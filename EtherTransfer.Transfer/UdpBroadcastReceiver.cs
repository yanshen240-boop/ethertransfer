using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EtherTransfer.Core.Models;
using EtherTransfer.Network.NetworkInterfaces;
using EtherTransfer.Network.UdpTransfer;

namespace EtherTransfer.Transfer;

/// <summary>
/// AP 隔离网络下的广播接收端（对应方案里的 <c>recv</c> 子命令）。
/// <para>
/// 单任务：同一时刻只处理一个文件或一条文本；收到 START 后先落 .part 临时文件，
/// 数据全部到齐再算 SHA-256，校验一致才改名为最终文件并回 DONE。
/// </para>
/// 所有应答（READY / ACK / DONE / ERROR）都绑定到与对端同网段的本机地址再广播出去，
/// 双网卡机器上不会因为走错网卡而静默丢失。
/// </summary>
public sealed class UdpBroadcastReceiver : IDisposable
{
    private sealed class ReceiveState
    {
        public Guid TransferId;
        public UdpBroadcastMetadata Meta = new();
        public int Total;
        public long Size;
        public byte[] Received = Array.Empty<byte>();
        public int Count;
        public FileStream? File;
        public MemoryStream? Text;
        public string PartPath = string.Empty;
        public string FinalPath = string.Empty;
        public Dictionary<int, int> NewSinceAck = new();
        public CancellationTokenSource? Cancellation;
        public SpeedTracker Speed = new();
        public long LastPercent = -1;
        public IPAddress Peer = IPAddress.Any;

        public bool IsCancelled => Cancellation?.IsCancellationRequested == true;
        public CancellationToken Token => Cancellation?.Token ?? CancellationToken.None;
    }

    /// <summary>会话空闲上限：发送端消失（关窗、断网、进程被杀）后必须自动释放，否则接收端会永久占线。</summary>
    private const int ReceiveIdleTimeoutMs = 20_000;

    private readonly int _port;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _stateLock = new();

    private UdpBroadcastChannel? _listener;
    private UdpBroadcastChannel? _replyChannel;
    private string _replyChannelKey = string.Empty;
    private Task? _loop;

    private ReceiveState? _state;
    private Guid? _lastDoneId;

    /// <summary>已给出结论的传输：发送端会重发 START，不能每次都弹确认框（每个超时都占住接收端）。</summary>
    private readonly HashSet<Guid> _declinedIds = new();

    /// <summary>本接收端当前是否占用了「设备忙」标记。</summary>
    private bool _holdsBusy;
    private long _lastActivityMs = Environment.TickCount64;
    private HashSet<IPAddress> _localAddresses = new();
    private DateTime _localAddressesStamp = DateTime.MinValue;

    // 同一批发多个文件时，只询问用户一次，后续文件沿用已授权的目录。
    private string _approvedBatchId = string.Empty;
    private string _approvedSavePath = string.Empty;
    private CancellationToken _approvedCancelToken;

    public int Port => _port;

    /// <summary>上层的「设备正忙」判定：忙时直接回 ERROR，不让对端空等。</summary>
    public Func<bool>? IsBusy { get; set; }

    public Func<TransferRequestMessage, CancellationToken, Task<(bool accept, string savePath, CancellationToken cancelToken)>>? OnIncomingTransfer { get; set; }

    public Func<TextMessageRequest, CancellationToken, Task<(bool accepted, string reason)>>? OnIncomingText { get; set; }

    public event EventHandler<TransferProgressEventArgs>? ProgressUpdated;

    /// <summary>开始处理一个文件/文本（上层据此占用「设备忙」标记）。</summary>
    public event EventHandler? TransferBegan;

    /// <summary>本次会话结束（成功、失败或被取消都会触发，用于释放「设备忙」标记）。</summary>
    public event EventHandler? TransferEnded;

    public event EventHandler<TransferResult>? TransferFinished;
    public event EventHandler<StructuredLogMessage>? DebugLog;

    public UdpBroadcastReceiver(int port = UdpBroadcastProtocol.TransferPort)
    {
        _port = port;
    }

    private void Log(string message, LogLevel level = LogLevel.Info) =>
        DebugLog?.Invoke(this, new StructuredLogMessage("udp.receiver", message, level));

    public bool Start()
    {
        try
        {
            _listener = UdpBroadcastChannel.CreateListener(_port);
        }
        catch (Exception ex)
        {
            Log($"广播接收端启动失败（UDP {_port} 端口被占用或不可用）：{ex.Message}", LogLevel.Warning);
            _listener = null;
            return false;
        }

        _loop = Task.Run(() => ListenLoopAsync(_cts.Token));
        Log($"广播接收端已启动：UDP {_port}（AP 隔离模式下无需 TCP 即可接收）。");
        return true;
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        var listener = _listener;
        if (listener == null) return;

        while (!ct.IsCancellationRequested)
        {
            var datagram = await listener.ReceiveAsync(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            if (datagram == null)
            {
                ExpireIfIdle();
                continue;
            }

            // 定向广播会被内核回环给本机监听端：不排掉自己发的报文，
            // 发送端会把自己的文件再收一份，还会把接收状态占住。
            if (IsSelf(datagram.Source)) continue;

            if (!UdpBroadcastProtocol.TryParse(datagram.Payload, out var packet)) continue;
            if (_state != null && _state.TransferId == packet.TransferId)
                _lastActivityMs = Environment.TickCount64;

            try
            {
                switch (packet.Kind)
                {
                    case UdpBroadcastPacketTypes.Start:
                        await HandleStartAsync(packet, datagram.Source, ct).ConfigureAwait(false);
                        break;
                    case UdpBroadcastPacketTypes.Data:
                        HandleData(packet, datagram.Source);
                        break;
                    case UdpBroadcastPacketTypes.Fin:
                        await HandleFinAsync(packet, datagram.Source).ConfigureAwait(false);
                        break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log($"处理广播报文出错：{ex.Message}", LogLevel.Error);
                SendError(datagram.Source, packet.TransferId, ex.Message);
                ResetState(deletePartFile: false);
            }
        }
    }

    /// <summary>忙碌标记只在「进入/离开接收会话」时各触发一次，避免重复触发或误清。</summary>
    private void SetBusy(bool value)
    {
        if (_holdsBusy == value) return;
        _holdsBusy = value;

        if (value) TransferBegan?.Invoke(this, EventArgs.Empty);
        else TransferEnded?.Invoke(this, EventArgs.Empty);
    }

    private void RememberDeclined(Guid transferId)
    {
        if (_declinedIds.Count > 64) _declinedIds.Clear();
        _declinedIds.Add(transferId);
    }

    /// <summary>发送端彻底消失时释放会话，避免「正在接收另一个文件」把后续传输全部挡掉。</summary>
    private void ExpireIfIdle()
    {
        if (_state == null) return;
        if (Environment.TickCount64 - _lastActivityMs < ReceiveIdleTimeoutMs) return;

        Log($"广播接收超时（{ReceiveIdleTimeoutMs / 1000} 秒无数据），已释放会话并清理未完成文件。", LogLevel.Warning);
        ResetState(deletePartFile: true);
    }

    /// <summary>源地址是本机地址 => 这是内核回环回来的自己发出的广播。</summary>
    private bool IsSelf(IPAddress source)
    {
        if ((DateTime.UtcNow - _localAddressesStamp).TotalSeconds > 5)
        {
            try { _localAddresses = NetworkHelper.GetLanInterfaces().Select(i => i.LocalAddress).ToHashSet(); }
            catch { _localAddresses = new HashSet<IPAddress>(); }
            _localAddressesStamp = DateTime.UtcNow;
        }

        return _localAddresses.Contains(source);
    }

    private async Task HandleStartAsync(UdpBroadcastPacket packet, IPAddress peer, CancellationToken ct)
    {
        // 重复的 START（DONE 丢包导致发送端重试）直接再回一次 READY，让对端自己收尾。
        if (_lastDoneId == packet.TransferId)
        {
            SendReady(peer, packet.TransferId);
            return;
        }

        // 正在接收的这个传输自己重发的 START 必须回 READY。放在「忙」之前判断：
        // 接收会话本身就会把设备标记为忙，顺序反了会把对端的握手重试当成第三方请求拒掉，
        // 表现为「自己把自己卡死」——对端报忙、本端也发不出去。
        if (_state != null)
        {
            if (_state.TransferId == packet.TransferId)
                SendReady(peer, packet.TransferId);
            else
                SendError(peer, packet.TransferId, "接收端正在接收另一个文件，请稍后重试。");

            return;
        }

        if (_declinedIds.Contains(packet.TransferId))
        {
            SendError(peer, packet.TransferId, "同一个传输此前已拒绝或已超时，请重新发起。");
            return;
        }

        if (IsBusy?.Invoke() == true)
        {
            SendError(peer, packet.TransferId, "设备正在处理另一项传输，请稍后重试。");
            return;
        }

        var metadata = UdpBroadcastProtocol.ParseMetadata(packet.Body)
                       ?? throw new InvalidDataException("START 参数错误：元数据不是有效 JSON。");

        var total = ValidateMetadata(metadata);
        var state = new ReceiveState
        {
            TransferId = packet.TransferId,
            Meta = metadata,
            Total = total,
            Size = metadata.Size,
            Received = new byte[total],
            Peer = peer
        };
        state.Speed.Start();

        if (metadata.IsText)
        {
            state.Text = new MemoryStream(metadata.Size > 0 ? (int)metadata.Size : 0);
            Log($"收到广播文本：{metadata.SenderName}（{metadata.Size} 字节，{total} 个分片）");
        }
        else
        {
            string savePath;
            CancellationToken cancelToken;
            try
            {
                (savePath, cancelToken) = await RequestSavePathAsync(metadata, ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
                RememberDeclined(packet.TransferId);
                throw;
            }
            if (cancelToken.CanBeCanceled)
                state.Cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancelToken);

            var relative = string.IsNullOrWhiteSpace(metadata.Path) ? metadata.Name : metadata.Path;
            var safePath = PathSanitizer.SanitizeRelativePath(savePath, relative)
                           ?? PathSanitizer.SanitizeRelativePath(savePath, metadata.Name);
            if (safePath == null)
                throw new InvalidDataException($"START 参数错误：文件路径不安全（{relative}）。");

            state.FinalPath = PathSanitizer.ResolveCollision(safePath);
            var directory = Path.GetDirectoryName(state.FinalPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            state.PartPath = state.FinalPath + "." + packet.TransferId.ToString("N").Substring(0, 8) + ".part";
            state.File = new FileStream(
                state.PartPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None,
                1, FileOptions.Asynchronous | FileOptions.SequentialScan);
            state.File.SetLength(metadata.Size);

            Log($"开始接收广播文件：{relative}，{metadata.Size:N0} 字节，{total:N0} 个分片，来自 {peer}，" +
                $"保存到 {state.FinalPath}");
        }

        lock (_stateLock) _state = state;
        _lastActivityMs = Environment.TickCount64;

        // 文本是一次性小报文，不该把整台设备标成「忙」；只有需要用户确认、要传数据的文件会话才占位。
        if (!metadata.IsText) SetBusy(true);

        SendReady(peer, packet.TransferId);
    }

    /// <summary>校验 START 元数据，规则与方案一致（大小上限、分片参数、分片总数、摘要格式）。</summary>
    private static int ValidateMetadata(UdpBroadcastMetadata metadata)
    {
        if (metadata.Size < 0 || metadata.Size > UdpBroadcastProtocol.MaxFileSize)
            throw new InvalidDataException("START 参数错误：文件大小超出允许范围。");

        if (metadata.ChunkSize != UdpBroadcastProtocol.ChunkSize)
            throw new InvalidDataException("START 参数错误：分片参数不匹配。");

        var expectedTotal = UdpBroadcastProtocol.TotalChunks(metadata.Size);
        if (metadata.Total != expectedTotal)
            throw new InvalidDataException("START 参数错误：分片总数不正确。");

        if (string.IsNullOrWhiteSpace(metadata.Sha256) || metadata.Sha256.Length != 64)
            throw new InvalidDataException("START 参数错误：SHA-256 信息无效。");

        if (metadata.IsText && metadata.Size > TransferReceiver.MaxTextLength * 4)
            throw new InvalidDataException("START 参数错误：文本消息过长。");

        if (!metadata.IsText && string.IsNullOrWhiteSpace(metadata.Name) && string.IsNullOrWhiteSpace(metadata.Path))
            throw new InvalidDataException("START 参数错误：缺少文件名。");

        return expectedTotal;
    }

    private async Task<(string savePath, CancellationToken cancelToken)> RequestSavePathAsync(
        UdpBroadcastMetadata metadata, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(metadata.BatchId) &&
            metadata.BatchId == _approvedBatchId &&
            !string.IsNullOrEmpty(_approvedSavePath))
        {
            return (_approvedSavePath, _approvedCancelToken);
        }

        if (OnIncomingTransfer == null)
            throw new InvalidOperationException("未绑定入站传输的界面处理程序。");

        var request = new TransferRequestMessage
        {
            SenderName = string.IsNullOrWhiteSpace(metadata.SenderName) ? "对方" : metadata.SenderName,
            TotalFiles = Math.Max(1, metadata.BatchCount),
            TotalSize = metadata.Size,
            ContainsFolders = metadata.Path.Contains('/'),
            PayloadFolderCount = 0,
            PayloadFileCount = Math.Max(1, metadata.BatchCount),
            RootElementNames = new List<string> { RootOf(metadata) }
        };

        var (accept, savePath, cancelToken) = await OnIncomingTransfer(request, ct).ConfigureAwait(false);
        if (!accept) throw new IOException("接收方拒绝了本次传输。");
        if (string.IsNullOrWhiteSpace(savePath)) throw new IOException("接收方未指定保存目录。");

        if (!string.IsNullOrEmpty(metadata.BatchId))
        {
            _approvedBatchId = metadata.BatchId;
            _approvedSavePath = savePath;
            _approvedCancelToken = cancelToken;
        }

        return (savePath, cancelToken);
    }

    private void HandleData(UdpBroadcastPacket packet, IPAddress peer)
    {
        var state = _state;
        if (state == null || packet.TransferId != state.TransferId) return;

        if (state.IsCancelled)
        {
            SendError(peer, packet.TransferId, "接收方已取消本次传输。");
            ResetState(deletePartFile: true);
            return;
        }

        var sequence = (int)packet.Arg1;
        if (sequence < 0 || sequence >= state.Total) return;

        var expectedLength = UdpBroadcastProtocol.ChunkLength(state.Size, sequence);
        if (packet.Body.Length != expectedLength)
        {
            SendError(peer, packet.TransferId, $"分片 {sequence} 长度不正确");
            return;
        }

        var windowBase = sequence / UdpBroadcastProtocol.WindowSize * UdpBroadcastProtocol.WindowSize;
        var duplicate = state.Received[sequence] != 0;

        if (!duplicate)
        {
            var offset = (long)sequence * UdpBroadcastProtocol.ChunkSize;
            if (state.File != null)
            {
                state.File.Position = offset;
                state.File.Write(packet.Body, 0, packet.Body.Length);
            }
            else
            {
                state.Text?.Write(packet.Body, 0, packet.Body.Length);
            }

            state.Received[sequence] = 1;
            state.Count++;
            state.NewSinceAck.TryGetValue(windowBase, out var seen);
            state.NewSinceAck[windowBase] = seen + 1;
        }

        // 累积位图 ACK：重复分片也回一次，丢失的 ACK 能立刻恢复。
        state.NewSinceAck.TryGetValue(windowBase, out var sinceAck);
        if (duplicate || sinceAck >= UdpBroadcastProtocol.AckEvery || sequence == state.Total - 1)
        {
            SendAck(peer, packet.TransferId, windowBase, state);
            state.NewSinceAck[windowBase] = 0;
        }

        if (state.Total > 0)
        {
            var percent = (long)state.Count * 100 / state.Total;
            if (percent >= state.LastPercent + 5 || state.Count == state.Total)
            {
                state.LastPercent = percent;
                ReportProgress(state);
            }
        }
    }

    private async Task HandleFinAsync(UdpBroadcastPacket packet, IPAddress peer)
    {
        if (_lastDoneId == packet.TransferId)
        {
            SendDone(peer, packet.TransferId);
            return;
        }

        var state = _state;
        if (state == null || packet.TransferId != state.TransferId) return;

        if (state.Count != state.Total)
        {
            // 告诉发送端每个窗口里到底缺哪些分片。
            for (int windowBase = 0; windowBase < state.Total; windowBase += UdpBroadcastProtocol.WindowSize)
                SendAck(peer, packet.TransferId, windowBase, state);
            return;
        }

        if (state.Meta.IsText)
            await CompleteTextAsync(state, peer).ConfigureAwait(false);
        else
            CompleteFile(state, peer);
    }

    private void CompleteFile(ReceiveState state, IPAddress peer)
    {
        var file = state.File ?? throw new InvalidOperationException("文件流已关闭。");
        try
        {
            file.Flush();
            file.Flush(flushToDisk: true);

            string actualHash;
            file.Position = 0;
            using (var sha = SHA256.Create())
            {
                actualHash = Convert.ToHexString(sha.ComputeHash(file)).ToLowerInvariant();
            }

            if (!string.Equals(actualHash, state.Meta.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                file.Dispose();
                state.File = null;
                SendError(peer, state.TransferId, "SHA-256 校验失败；文件未保存为最终文件");
                Log($"SHA-256 校验失败：{state.Meta.Path}，保留 .part 文件供检查。", LogLevel.Error);
                ResetState(deletePartFile: false);
                return;
            }

            file.Dispose();
            state.File = null;
            File.Move(state.PartPath, state.FinalPath, overwrite: false);
            ApplyTimestamp(state.FinalPath, state.Meta.LastWriteTimeUnixMs);

            Log($"接收完成：{state.FinalPath}（SHA-256 {actualHash}）");
            _lastDoneId = state.TransferId;
            SendDone(peer, state.TransferId);

            TransferFinished?.Invoke(this, new TransferResult
            {
                Success = true,
                Channel = TransferChannels.UdpBroadcast,
                TotalElements = 1,
                AllElementNames = new List<string> { RootOf(state.Meta) },
                CompletedElementNames = new List<string> { RootOf(state.Meta) }
            });

            ResetState(deletePartFile: false);
        }
        catch (Exception)
        {
            try { file.Dispose(); } catch { }
            state.File = null;
            throw;
        }
    }

    private async Task CompleteTextAsync(ReceiveState state, IPAddress peer)
    {
        var buffer = state.Text ?? throw new InvalidOperationException("文本缓冲区已关闭。");
        var bytes = buffer.ToArray();

        var actualHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(actualHash, state.Meta.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            ResetState(deletePartFile: false);
            SendError(peer, state.TransferId, "SHA-256 校验失败：文本消息未送达");
            return;
        }

        var text = Encoding.UTF8.GetString(bytes);
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidDataException("文本消息内容为空。");
        if (text.Length > TransferReceiver.MaxTextLength)
            throw new InvalidDataException($"文本过长（{text.Length} 字，上限 {TransferReceiver.MaxTextLength} 字）。");

        var message = new TextMessageRequest
        {
            SenderName = string.IsNullOrWhiteSpace(state.Meta.SenderName) ? "对方" : state.Meta.SenderName,
            Text = text,
            SentAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            MessageId = Guid.NewGuid().ToString("N")
        };

        bool accepted = true;
        string reason = string.Empty;
        if (OnIncomingText != null)
            (accepted, reason) = await OnIncomingText(message, state.Token).ConfigureAwait(false);

        if (!accepted)
        {
            ResetState(deletePartFile: false);
            SendError(peer, state.TransferId, string.IsNullOrWhiteSpace(reason) ? "接收方未接收这条文本。" : reason);
            return;
        }

        Log($"已收到广播文本：{text.Length} 字，来自 {message.SenderName}");
        _lastDoneId = state.TransferId;
        SendDone(peer, state.TransferId);

        TransferFinished?.Invoke(this, new TransferResult
        {
            Success = true,
            Channel = TransferChannels.UdpBroadcast,
            TotalElements = 1,
            AllElementNames = new List<string> { "文本消息" },
            CompletedElementNames = new List<string> { "文本消息" }
        });

        ResetState(deletePartFile: false);
    }

    private void ReportProgress(ReceiveState state)
    {
        if (state.Size <= 0) return;

        var receivedBytes = Math.Min(state.Size, (long)state.Count * UdpBroadcastProtocol.ChunkSize);

        ProgressUpdated?.Invoke(this, new TransferProgressEventArgs
        {
            CurrentFile = RootOf(state.Meta),
            BytesSent = receivedBytes,
            TotalBytes = state.Size,
            SpeedMbPerSec = state.Speed.CalculateSpeed(receivedBytes),
            CurrentElementIndex = 1,
            TotalElements = Math.Max(1, state.Meta.BatchCount)
        });
    }

    private void SendReady(IPAddress peer, Guid transferId)
    {
        var total = _state != null && _state.TransferId == transferId ? _state.Total : 0;
        Send(peer, UdpBroadcastProtocol.Build(UdpBroadcastPacketTypes.Ready, transferId, (uint)total, 0));
    }

    private void SendDone(IPAddress peer, Guid transferId)
    {
        Send(peer, UdpBroadcastProtocol.Build(UdpBroadcastPacketTypes.Done, transferId));
    }

    private void SendError(IPAddress peer, Guid transferId, string message)
    {
        Log($"向 {peer} 报告错误：{message}", LogLevel.Warning);
        Send(peer, UdpBroadcastProtocol.BuildError(transferId, message));
    }

    private void SendAck(IPAddress peer, Guid transferId, int windowBase, ReceiveState state)
    {
        var count = Math.Min(UdpBroadcastProtocol.WindowSize, state.Total - windowBase);
        if (count <= 0) return;

        uint mask = 0;
        for (int bit = 0; bit < count; bit++)
        {
            if (state.Received[windowBase + bit] != 0) mask |= 1u << bit;
        }

        Send(peer, UdpBroadcastProtocol.Build(UdpBroadcastPacketTypes.Ack, transferId, (uint)windowBase, mask));
    }

    /// <summary>
    /// 应答一律走「绑定到对端同网段本机地址」的广播通道：监听端绑定的是 0.0.0.0，
    /// 直接用它发广播会交给系统选路，多网卡时可能从有线侧出去，对端永远收不到。
    /// </summary>
    private void Send(IPAddress peer, byte[] payload)
    {
        try
        {
            var channel = GetReplyChannel(peer);
            _ = channel.SendAsync(payload, _cts.Token).ContinueWith(
                t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }
        catch (Exception ex)
        {
            Log($"广播应答失败：{ex.Message}", LogLevel.Warning);
        }
    }

    private UdpBroadcastChannel GetReplyChannel(IPAddress peer)
    {
        string localAddress;
        try
        {
            var match = NetworkHelper.GetLanInterfaces().FirstOrDefault(i => i.Contains(peer));
            localAddress = match?.LocalAddress.ToString() ?? UdpBroadcastChannel.ResolveWifiAddress();
        }
        catch
        {
            localAddress = UdpBroadcastChannel.ResolveWifiAddress();
        }

        if (_replyChannel != null && _replyChannelKey == localAddress) return _replyChannel;

        _replyChannel?.Dispose();
        _replyChannel = UdpBroadcastChannel.CreateSender(_port, localAddress);
        _replyChannelKey = localAddress;
        return _replyChannel;
    }

    private void ResetState(bool deletePartFile)
    {
        ReceiveState? state;
        lock (_stateLock)
        {
            state = _state;
            _state = null;
        }

        if (state == null) return;

        try { state.File?.Dispose(); } catch { }
        try { state.Text?.Dispose(); } catch { }
        try { state.Cancellation?.Dispose(); } catch { }

        if (deletePartFile && !string.IsNullOrEmpty(state.PartPath))
        {
            try { if (File.Exists(state.PartPath)) File.Delete(state.PartPath); } catch { }
        }

        SetBusy(false);
    }

    private static string RootOf(UdpBroadcastMetadata metadata) =>
        string.IsNullOrWhiteSpace(metadata.RootName) ? metadata.Name : metadata.RootName;

    private static void ApplyTimestamp(string path, long? unixMs)
    {
        if (!unixMs.HasValue || unixMs.Value <= 0) return;
        try { File.SetLastWriteTimeUtc(path, DateTimeOffset.FromUnixTimeMilliseconds(unixMs.Value).UtcDateTime); }
        catch { }
    }

    public void Stop()
    {
        try { _cts.Cancel(); } catch { }
        ResetState(deletePartFile: false);
    }

    public void Dispose()
    {
        Stop();
        try { _listener?.Dispose(); } catch { }
        try { _replyChannel?.Dispose(); } catch { }
        try { _cts.Dispose(); } catch { }
        GC.SuppressFinalize(this);
    }
}
