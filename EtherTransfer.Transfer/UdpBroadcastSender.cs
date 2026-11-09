using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EtherTransfer.Core.Models;
using EtherTransfer.Network.UdpTransfer;

namespace EtherTransfer.Transfer;

/// <summary>
/// AP 隔离网络下的广播发送端（对应方案里的 <c>send</c> 子命令）。
/// <para>
/// 流程：START（元数据）→ 等 READY → 按 32 分片窗口发 DATA、用累积位图 ACK 确认并重传 →
/// FIN → 等 DONE（期间按接收端报告的缺失窗口补发）。全程不使用 TCP 连接。
/// </para>
/// 每个文件一个独立的传输 ID；一次会话里多个文件按顺序排队发送，接收端逐个确认。
/// </summary>
public sealed class UdpBroadcastSender
{
    public event EventHandler<TransferProgressEventArgs>? ProgressUpdated;
    public event EventHandler<StructuredLogMessage>? DebugLog;

    private void Log(string message, LogLevel level = LogLevel.Info) =>
        DebugLog?.Invoke(this, new StructuredLogMessage("udp.sender", message, level));

    /// <summary>
    /// 按会话发送全部文件。<paramref name="channel"/> 为空时自动创建「绑定 Wi-Fi 源地址 + 子网广播目标」的通道；
    /// 传入现成通道时不会接管其释放（便于一次发送复用同一条通道）。
    /// </summary>
    public async Task<TransferResult> TransmitSessionAsync(
        TransferSession session,
        string senderName,
        CancellationToken ct,
        UdpBroadcastChannel? channel = null)
    {
        var files = session.Files;
        var result = new TransferResult
        {
            TotalElements = session.PayloadFolderCount + session.PayloadFileCount,
            AllElementNames = files.Select(f => f.RootName).Distinct().ToList(),
            Channel = TransferChannels.UdpBroadcast
        };

        if (files.Count == 0)
        {
            result.Success = true;
            return result;
        }

        var ownsChannel = channel == null;
        try
        {
            channel ??= UdpBroadcastChannel.CreateSender(UdpBroadcastProtocol.TransferPort, UdpBroadcastChannel.ResolveWifiAddress());
            if (channel.Targets.Count == 0)
                throw new IOException("没有可用的子网广播地址：请确认已连接 Wi-Fi 且已获取 IPv4 地址。");

            Log($"广播通道就绪：本机 {channel.LocalAddress}，广播目标 {channel.TargetList}");

            var watch = Stopwatch.StartNew();
            var speedTracker = new SpeedTracker();
            speedTracker.Start();

            long completedBytes = 0;
            long lastReportMs = 0;
            var totalBytes = session.TotalSize;
            var batchId = Guid.NewGuid().ToString("N");

            for (int i = 0; i < files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var item = files[i];
                var fileIndex = i + 1;
                var rootName = item.RootName;

                void ReportProgress(long fileBytes)
                {
                    var now = watch.ElapsedMilliseconds;
                    if (now - lastReportMs < 50 && fileBytes < item.Size) return;
                    lastReportMs = now;
                    var overall = completedBytes + fileBytes;
                    ProgressUpdated?.Invoke(this, new TransferProgressEventArgs
                    {
                        CurrentFile = rootName,
                        BytesSent = overall,
                        TotalBytes = totalBytes,
                        SpeedMbPerSec = speedTracker.CalculateSpeed(overall),
                        CurrentElementIndex = fileIndex,
                        TotalElements = files.Count
                    });
                }

                if (!File.Exists(item.AbsolutePath))
                {
                    Log($"跳过（文件不存在）：{item.RelativePath}", LogLevel.Warning);
                    continue;
                }

                await using (var source = new FileStream(
                                 item.AbsolutePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                                 1, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    var size = source.Length;
                    var digest = await ComputeSha256Async(source, ct).ConfigureAwait(false);
                    source.Position = 0;

                    var metadata = new UdpBroadcastMetadata
                    {
                        Name = Path.GetFileName(item.RelativePath),
                        Path = NormalizeRelativePath(item),
                        RootName = rootName,
                        Size = size,
                        Total = UdpBroadcastProtocol.TotalChunks(size),
                        Sha256 = digest,
                        SenderName = senderName,
                        Kind = "file",
                        BatchId = batchId,
                        BatchIndex = i,
                        BatchCount = files.Count,
                        LastWriteTimeUnixMs = new DateTimeOffset(File.GetLastWriteTimeUtc(item.AbsolutePath)).ToUnixTimeMilliseconds()
                    };

                    Log($"开始广播发送：{item.RelativePath}，{size:N0} 字节，{metadata.Total:N0} 个分片，SHA-256 {digest}");
                    await SendStreamAsync(channel, source, metadata, size, ReportProgress, ct).ConfigureAwait(false);
                }

                // 进度按「已完成 + 本文件已确认」上报，因此先报再累加，避免总数虚高。
                ReportProgress(item.Size);
                completedBytes += item.Size;
                if (!result.CompletedElementNames.Contains(rootName))
                    result.CompletedElementNames.Add(rootName);
            }

            result.Success = true;
            Log($"广播发送完成：{result.CompletedElementNames.Count} 项，{completedBytes / 1024 / 1024} MB，耗时 {watch.Elapsed.TotalSeconds:F1} 秒。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
            Log($"广播发送失败：{ex.Message}", LogLevel.Error);
        }
        finally
        {
            if (ownsChannel) channel?.Dispose();
        }

        result.FailedElementNames = result.AllElementNames
            .Where(name => !result.CompletedElementNames.Contains(name))
            .ToList();
        return result;
    }

    /// <summary>
    /// 发送一条纯文本消息：复用同一套可靠通道，只是把正文当成分片负载（kind=text）。
    /// AP 隔离下 TCP 完全不可用时，文字消息也能照常送达。
    /// </summary>
    public async Task<TextSendResult> SendTextAsync(string text, string senderName, CancellationToken ct)
    {
        var result = new TextSendResult();
        var watch = Stopwatch.StartNew();

        var body = Encoding.UTF8.GetBytes(text);
        var metadata = new UdpBroadcastMetadata
        {
            Name = $"消息-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            Path = string.Empty,
            Size = body.Length,
            Total = UdpBroadcastProtocol.TotalChunks(body.Length),
            Sha256 = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant(),
            SenderName = senderName,
            Kind = "text",
            LastWriteTimeUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        UdpBroadcastChannel? channel = null;
        try
        {
            channel = UdpBroadcastChannel.CreateSender(UdpBroadcastProtocol.TransferPort, UdpBroadcastChannel.ResolveWifiAddress());
            if (channel.Targets.Count == 0)
                throw new IOException("没有可用的子网广播地址：请确认已连接 Wi-Fi 且已获取 IPv4 地址。");

            Log($"通过广播通道发送文本（{text.Length} 字），目标 {channel.TargetList}");
            using var source = new MemoryStream(body, writable: false);
            await SendStreamAsync(channel, source, metadata, body.Length, _ => { }, ct).ConfigureAwait(false);

            watch.Stop();
            result.Success = true;
            result.ElapsedMs = watch.ElapsedMilliseconds;
            Log($"文本已通过广播通道送达，往返 {watch.ElapsedMilliseconds} ms。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            watch.Stop();
            result.Success = false;
            result.ErrorMessage = ex.Message;
            Log($"广播发送文本失败：{ex.Message}", LogLevel.Error);
        }
        finally
        {
            channel?.Dispose();
        }

        return result;
    }

    private async Task SendStreamAsync(
        UdpBroadcastChannel channel,
        Stream source,
        UdpBroadcastMetadata metadata,
        long size,
        Action<long> onConfirmed,
        CancellationToken ct)
    {
        var total = metadata.Total;
        var transferId = Guid.NewGuid();
        var startPacket = UdpBroadcastProtocol.BuildStart(transferId, metadata);

        await WaitForReadyAsync(channel, transferId, startPacket, metadata.Path, ct).ConfigureAwait(false);

        for (int baseSequence = 0; baseSequence < total; baseSequence += UdpBroadcastProtocol.WindowSize)
        {
            ct.ThrowIfCancellationRequested();
            var count = Math.Min(UdpBroadcastProtocol.WindowSize, total - baseSequence);
            await SendWindowAsync(channel, transferId, source, size, total, baseSequence, count, ct).ConfigureAwait(false);

            var confirmed = Math.Min(size, (long)(baseSequence + count) * UdpBroadcastProtocol.ChunkSize);
            onConfirmed(confirmed);
        }

        await WaitForDoneAsync(channel, transferId, source, size, total, ct).ConfigureAwait(false);
    }

    /// <summary>反复广播 START，直到接收端回 READY（或报告 ERROR）。</summary>
    private async Task WaitForReadyAsync(
        UdpBroadcastChannel channel,
        Guid transferId,
        byte[] startPacket,
        string label,
        CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(UdpBroadcastProtocol.StartHandshakeSeconds);
        var attempt = 0;

        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            attempt++;

            await channel.SendAsync(startPacket, ct).ConfigureAwait(false);

            var windowEnd = DateTimeOffset.UtcNow.AddMilliseconds(UdpBroadcastProtocol.StartRetryIntervalMs);
            while (DateTimeOffset.UtcNow < windowEnd)
            {
                var datagram = await channel.ReceiveAsync(windowEnd - DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
                if (datagram == null) break;
                if (!UdpBroadcastProtocol.TryParse(datagram.Payload, out var packet)) continue;
                if (packet.TransferId != transferId) continue;

                if (packet.Kind == UdpBroadcastPacketTypes.Ready)
                {
                    Log($"接收端已就绪（来自 {datagram.Source}），开始发送 {label}");
                    return;
                }

                if (packet.Kind == UdpBroadcastPacketTypes.Error)
                    throw new IOException(UdpBroadcastProtocol.DecodeError(packet.Body));
            }

            if (attempt % 5 == 0)
                Log($"等待接收端 READY（第 {attempt} 次未收到）：{label}", LogLevel.Warning);
        }

        throw new TimeoutException("没有收到接收端的 READY：请确认对端已启动、广播地址正确，且 AP 允许客户端之间转发 UDP 广播。");
    }

    /// <summary>一个 32 分片窗口：补发缺失分片，用累积位图 ACK 判定窗口完成。</summary>
    private async Task SendWindowAsync(
        UdpBroadcastChannel channel,
        Guid transferId,
        Stream source,
        long size,
        int total,
        int baseSequence,
        int count,
        CancellationToken ct)
    {
        var fullMask = count >= UdpBroadcastProtocol.WindowSize ? uint.MaxValue : (1u << count) - 1;
        uint ackMask = 0;
        var buffer = new byte[UdpBroadcastProtocol.ChunkSize];

        for (int attempt = 1; attempt <= UdpBroadcastProtocol.MaxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var missing = fullMask & ~ackMask;

            for (int bit = 0; bit < count; bit++)
            {
                if ((missing & (1u << bit)) == 0) continue;

                var sequence = baseSequence + bit;
                var length = ReadChunk(source, size, sequence, buffer);
                var packet = UdpBroadcastProtocol.Build(
                    UdpBroadcastPacketTypes.Data, transferId, (uint)sequence, (uint)total,
                    buffer.AsSpan(0, length));

                await channel.SendAsync(packet, ct).ConfigureAwait(false);
                PauseBetweenChunks(ct);
            }

            var windowEnd = DateTimeOffset.UtcNow.AddMilliseconds(UdpBroadcastProtocol.WindowTimeoutMs);
            while (ackMask != fullMask && DateTimeOffset.UtcNow < windowEnd)
            {
                var datagram = await channel.ReceiveAsync(windowEnd - DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
                if (datagram == null) break;
                if (!UdpBroadcastProtocol.TryParse(datagram.Payload, out var packet)) continue;
                if (packet.TransferId != transferId) continue;

                if (packet.Kind == UdpBroadcastPacketTypes.Ack && packet.Arg1 == (uint)baseSequence)
                {
                    ackMask |= packet.Arg2 & fullMask;
                }
                else if (packet.Kind == UdpBroadcastPacketTypes.Error)
                {
                    throw new IOException(UdpBroadcastProtocol.DecodeError(packet.Body));
                }
            }

            if (ackMask == fullMask) return;

            Log($"分片组 {baseSequence}-{baseSequence + count - 1} 第 {attempt}/{UdpBroadcastProtocol.MaxRetries} 次重传，" +
                $"已确认 {BitOperations.PopCount(ackMask)}/{count}", LogLevel.Warning);
        }

        throw new TimeoutException($"分片组 {baseSequence}-{baseSequence + count - 1} 多次重传仍未全部确认。");
    }

    /// <summary>数据发完后等接收端校验完成；期间接收端会用 ACK 报告缺失窗口，按报告补发。</summary>
    private async Task WaitForDoneAsync(
        UdpBroadcastChannel channel,
        Guid transferId,
        Stream source,
        long size,
        int total,
        CancellationToken ct)
    {
        for (int attempt = 1; attempt <= UdpBroadcastProtocol.FinMaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            await channel.SendAsync(
                UdpBroadcastProtocol.Build(UdpBroadcastPacketTypes.Fin, transferId, (uint)total, 0), ct).ConfigureAwait(false);

            var repair = new Dictionary<int, uint>();
            var finished = false;
            var windowEnd = DateTimeOffset.UtcNow.AddMilliseconds(UdpBroadcastProtocol.FinRetryIntervalMs);

            while (DateTimeOffset.UtcNow < windowEnd)
            {
                var datagram = await channel.ReceiveAsync(windowEnd - DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
                if (datagram == null) break;
                if (!UdpBroadcastProtocol.TryParse(datagram.Payload, out var packet)) continue;
                if (packet.TransferId != transferId) continue;

                if (packet.Kind == UdpBroadcastPacketTypes.Done)
                {
                    finished = true;
                    Log($"接收端 {datagram.Source} 已完成接收与 SHA-256 校验。");
                    break;
                }

                if (packet.Kind == UdpBroadcastPacketTypes.Ack)
                {
                    repair[(int)packet.Arg1] = packet.Arg2;
                }
                else if (packet.Kind == UdpBroadcastPacketTypes.Error)
                {
                    throw new IOException(UdpBroadcastProtocol.DecodeError(packet.Body));
                }
            }

            if (finished) return;

            foreach (var (baseSequence, mask) in repair)
            {
                var count = Math.Min(UdpBroadcastProtocol.WindowSize, total - baseSequence);
                var fullMask = count >= UdpBroadcastProtocol.WindowSize ? uint.MaxValue : (1u << count) - 1;
                if (count <= 0 || (mask & fullMask) == fullMask) continue;

                Log($"接收端报告分片组 {baseSequence} 有缺失，补发中…", LogLevel.Warning);
                await SendWindowAsync(channel, transferId, source, size, total, baseSequence, count, ct).ConfigureAwait(false);
            }

            if (attempt % 5 == 0)
                Log($"等待完成确认（第 {attempt}/{UdpBroadcastProtocol.FinMaxAttempts} 次）", LogLevel.Warning);
        }

        throw new TimeoutException("数据已发送完毕，但没有收到最终 DONE：接收端可能已保存文件，请检查对端日志。");
    }

    private static int ReadChunk(Stream source, long size, int sequence, byte[] buffer)
    {
        var length = UdpBroadcastProtocol.ChunkLength(size, sequence);
        if (length <= 0) return 0;

        var offset = (long)sequence * UdpBroadcastProtocol.ChunkSize;
        source.Position = offset;
        var read = 0;
        while (read < length)
        {
            var count = source.Read(buffer, read, length - read);
            if (count <= 0) throw new IOException("读取待发送分片时文件提前结束（文件可能正在被修改）。");
            read += count;
        }

        return read;
    }

    private static async Task<string> ComputeSha256Async(Stream source, CancellationToken ct)
    {
        using var sha = SHA256.Create();
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            source.Position = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                if (read <= 0) break;
                sha.TransformBlock(buffer, 0, read, null, 0);
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// 分片间节流（对应方案的 SEND_GAP）。Task.Delay 的精度是 15 ms 量级，
    /// 对 0.4 ms 的间隔没有意义，这里用极短自旋保住节流精度，避免把 AP 的广播队列打爆。
    /// </summary>
    private static void PauseBetweenChunks(CancellationToken ct)
    {
        if (UdpBroadcastProtocol.SendGapMs <= 0) return;
        if ((ct.IsCancellationRequested)) return;

        var until = Stopwatch.GetTimestamp() +
                    (long)(UdpBroadcastProtocol.SendGapMs / 1000.0 * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < until)
        {
            Thread.SpinWait(64);
        }
    }

    /// <summary>相对路径要保留目录结构（文件夹传输），同时保证不含绝对路径与反斜杠。</summary>
    private static string NormalizeRelativePath(FileSelectionItem item)
    {
        var relative = string.IsNullOrWhiteSpace(item.RelativePath) ? item.RootName : item.RelativePath;
        relative = relative.Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrEmpty(relative)) relative = Path.GetFileName(item.AbsolutePath);
        return relative;
    }
}
