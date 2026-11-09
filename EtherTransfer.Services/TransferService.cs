using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EtherTransfer.Core;
using EtherTransfer.Core.Models;
using EtherTransfer.Network;
using EtherTransfer.Network.UdpTransfer;
using EtherTransfer.Transfer;

namespace EtherTransfer.Services;

public class TransferService : IDisposable
{
    private readonly TcpServer _server;
    private readonly UdpBroadcastReceiver _broadcastReceiver;
    private readonly UdpBroadcastProbe _broadcastProbe;
    private readonly NetworkConfig _config = NetworkConfig.Default;
    private readonly CancellationTokenSource _cts;
    private readonly string _computerName;
    private int _transferActive;
    public bool IsBusy => Volatile.Read(ref _transferActive) != 0;

    public int TcpPort => _server.Port;

    /// <summary>UDP 广播传输端口（AP 隔离模式下与 TCP 并行监听）。</summary>
    public int UdpPort => _broadcastReceiver.Port;

    /// <summary>广播接收端是否已成功绑定端口；绑定失败时会话只能走 TCP。</summary>
    public bool IsBroadcastChannelReady { get; private set; }

    public event EventHandler<StructuredLogMessage>? DebugLog;
    public event EventHandler<TransferProgressEventArgs>? ProgressUpdated;
    public event EventHandler<TransferResult>? TransferFinished;

    public Func<TransferRequestMessage, CancellationToken, Task<(bool accept, string savePath, CancellationToken cancelToken)>>? OnIncomingTransfer { get; set; }

    /// <summary>收到文本消息时由界面展示，返回 false 表示未展示成功。</summary>
    public Func<TextMessageRequest, CancellationToken, Task<(bool accepted, string reason)>>? OnIncomingText { get; set; }

    private void Log(string msg, LogLevel level = LogLevel.Info, string eventId = "transfer.log") => DebugLog?.Invoke(this, new StructuredLogMessage(eventId, msg, level));

    public TransferService(string computerName, int tcpPort)
    {
        _computerName = computerName;
        _cts = new CancellationTokenSource();
        _server = new TcpServer(tcpPort);

        _server.DebugLog += (_, msg) => DebugLog?.Invoke(this, msg);
        _server.OnClientConnected = HandleIncomingClient;

        // 广播接收端：AP 隔离时对端根本连不上 TCP，必须独立于 TcpServer 常驻监听。
        // 界面处理程序通过代理在收到报文时才读取，避免构造顺序影响绑定。
        _broadcastReceiver = new UdpBroadcastReceiver(_config.UdpTransferPort);
        _broadcastReceiver.DebugLog += (_, msg) => DebugLog?.Invoke(this, msg);
        _broadcastReceiver.ProgressUpdated += (_, e) => ProgressUpdated?.Invoke(this, e);
        _broadcastReceiver.TransferFinished += (_, result) => TransferFinished?.Invoke(this, result);
        // 广播接收与 TCP 共用同一个「设备忙」标记：两个通道同时收会打乱界面上的传输状态。
        _broadcastReceiver.TransferBegan += (_, _) => Volatile.Write(ref _transferActive, 1);
        _broadcastReceiver.TransferEnded += (_, _) => Volatile.Write(ref _transferActive, 0);
        _broadcastReceiver.IsBusy = () => IsBusy;
        _broadcastReceiver.OnIncomingTransfer = (request, ct) =>
        {
            var handler = OnIncomingTransfer;
            return handler != null
                ? handler(request, ct)
                : Task.FromException<(bool, string, CancellationToken)>(
                    new InvalidOperationException("未绑定入站传输的界面处理程序。"));
        };
        _broadcastReceiver.OnIncomingText = (message, ct) =>
        {
            var handler = OnIncomingText;
            return handler != null
                ? handler(message, ct)
                : Task.FromResult((true, string.Empty));
        };

        _broadcastProbe = new UdpBroadcastProbe(_config.UdpProbePort);
        _broadcastProbe.DebugLog += (_, msg) => DebugLog?.Invoke(this, msg);
    }

    public void Start()
    {
        _server.Start();
        IsBroadcastChannelReady = _broadcastReceiver.Start();
        _broadcastProbe.Start();
    }

    public void Stop()
    {
        _cts.Cancel();
        _server.Stop();
        _broadcastReceiver.Stop();
        _broadcastProbe.Dispose();
    }

    private async void HandleIncomingClient(System.Net.Sockets.TcpClient client)
    {
        if (Interlocked.CompareExchange(ref _transferActive, 1, 0) != 0)
        {
            try
            {
                using (client)
                {
                    var stream = client.GetStream();
                    var json = await ProtocolHelper.ReceiveRawJsonAsync(stream, _cts.Token, 5000);
                    var isText = false;
                    if (json != null)
                    {
                        using var doc = JsonDocument.Parse(json);
                        isText = doc.RootElement.TryGetProperty("Type", out var typeElement) &&
                                 string.Equals(typeElement.GetString(), ProtocolMessageTypes.TextMessage, StringComparison.OrdinalIgnoreCase);
                    }

                    if (isText)
                    {
                        await ProtocolHelper.SendMessageAsync(stream, new TextMessageAck
                        {
                            Accepted = false,
                            Reason = "设备正在处理另一项传输，请稍后重试。"
                        }, _cts.Token, 3000);
                    }
                    else
                    {
                        await ProtocolHelper.SendMessageAsync(stream, new TransferResponseMessage
                        { Accepted = false, Reason = "设备正在处理另一项传输，请稍后重试。" }, _cts.Token, 3000);
                    }
                }
            }
            catch (Exception ex) { Log($"回复设备忙碌时连接已断开：{ex.Message}"); }
            return;
        }
        try
        {
            var receiver = new TransferReceiver();
            receiver.DebugLog += (_, msg) => DebugLog?.Invoke(this, msg);
            receiver.ProgressUpdated += (_, e) => ProgressUpdated?.Invoke(this, e);
            receiver.OnIncomingTransfer = OnIncomingTransfer;
            receiver.OnIncomingText = OnIncomingText;

            var result = await receiver.HandleClientAsync(client, _cts.Token);
            TransferFinished?.Invoke(this, result);
        }
        catch (Exception ex)
        {
            Log($"处理入站连接时发生未处理异常：{ex.Message}", LogLevel.Error);
            try { client.Dispose(); } catch { }
        }
        finally { Volatile.Write(ref _transferActive, 0); }
    }

    public Task<PayloadItem> ScanItemAsync(string path, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var sender = new TransferSender();
        sender.DebugLog += (_, msg) => DebugLog?.Invoke(this, msg);

        return sender.ScanItemAsync(path, progress, ct);
    }

    public Task TransmitSessionAsync(string targetIp, int targetPort, TransferSession session, CancellationToken userCt = default)
    {
        return TransmitCoreAsync(
            new[] { new PeerEndpoint { Address = targetIp, Port = targetPort } },
            session, userCt);
    }

    /// <summary>按链路端点发送：首选链路（通常是有线）在前，失败自动换另一条链路。</summary>
    public Task TransmitSessionAsync(IReadOnlyList<PeerEndpoint> endpoints, TransferSession session, CancellationToken userCt = default)
    {
        return TransmitCoreAsync(endpoints, session, userCt);
    }

    private async Task TransmitCoreAsync(IReadOnlyList<PeerEndpoint> endpoints, TransferSession session, CancellationToken userCt = default)
    {
        if (Interlocked.CompareExchange(ref _transferActive, 1, 0) != 0)
            throw new InvalidOperationException("设备正在处理另一项传输，请稍后重试。");
        try
        {
            var sender = new TransferSender();
            sender.DebugLog += (_, msg) => DebugLog?.Invoke(this, msg);
            sender.ProgressUpdated += (_, e) => ProgressUpdated?.Invoke(this, e);
    
            await Task.Run(async () =>
            {
                TransferResult result;
                try
                {
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, userCt);
                    result = await sender.TransmitSessionAsync(endpoints, _computerName, session, linkedCts.Token);

                    // 一条 TCP 链路都没建起来 —— 典型 AP 隔离现象：发现广播能收到，TCP 全超时。
                    // 这时改走 UDP 广播通道；已经建连后失败的不重试，避免重复传输。
                    if (!result.Success && !result.Connected && !linkedCts.IsCancellationRequested &&
                        _config.UdpBroadcastFallbackEnabled)
                    {
                        Log($"TCP 链路均不可达（{result.ErrorMessage}），改用 UDP 广播通道…", LogLevel.Warning);
                        result = await TransmitViaBroadcastAsync(session, linkedCts.Token).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    Log($"发送失败：{ex.Message}");
                    result = new TransferResult { Success = false, ErrorMessage = ex.Message };
                }
                TransferFinished?.Invoke(this, result);
            }, userCt);
        }
        finally { Volatile.Write(ref _transferActive, 0); }
    }

    /// <summary>
    /// 直接用 UDP 广播通道发送（不尝试 TCP）。广播通道的元数据与校验规则与 TCP 会话一致，
    /// 区别只是报文全部走子网广播、靠位图 ACK 重传而不是 TCP 重传。
    /// </summary>
    public async Task<TransferResult> TransmitViaBroadcastAsync(TransferSession session, CancellationToken ct)
    {
        if (!IsBroadcastChannelReady)
            Log($"广播接收端未就绪，本机仍可尝试发送（UDP {_config.UdpTransferPort}）。", LogLevel.Warning);

        var sender = new UdpBroadcastSender();
        sender.DebugLog += (_, msg) => DebugLog?.Invoke(this, msg);
        sender.ProgressUpdated += (_, e) => ProgressUpdated?.Invoke(this, e);
        return await sender.TransmitSessionAsync(session, _computerName, ct).ConfigureAwait(false);
    }

    /// <summary>发送一条文本消息（同样按链路端点选择路径，有线优先、失败自动换链路）。</summary>
    public async Task<TextSendResult> SendTextAsync(IReadOnlyList<PeerEndpoint> endpoints, string text, CancellationToken userCt = default)
    {
        if (Interlocked.CompareExchange(ref _transferActive, 1, 0) != 0)
            throw new InvalidOperationException("设备正在处理另一项传输，请稍后重试。");

        try
        {
            var sender = new TransferSender();
            sender.DebugLog += (_, msg) => DebugLog?.Invoke(this, msg);

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, userCt);
            var result = await sender.SendTextAsync(endpoints, _computerName, text, linkedCts.Token).ConfigureAwait(false);

            // Endpoint 为空表示连 TCP 都没连上；拒绝/超时这类已建连的失败不改走广播。
            if (!result.Success && result.Endpoint == null && !linkedCts.IsCancellationRequested &&
                _config.UdpBroadcastFallbackEnabled)
            {
                Log("TCP 文本通道不可达，改用 UDP 广播通道…", LogLevel.Warning);
                var broadcast = new UdpBroadcastSender();
                broadcast.DebugLog += (_, msg) => DebugLog?.Invoke(this, msg);
                return await broadcast.SendTextAsync(text, _computerName, linkedCts.Token).ConfigureAwait(false);
            }

            return result;
        }
        finally { Volatile.Write(ref _transferActive, 0); }
    }

    /// <summary>
    /// 单端点互通探测（设备列表红绿灯）。deep = 深探测：多发唤醒包、给足超时，
    /// 用于“快速探测失败后的二次确认”和用户手动点击刷新。
    /// 不占用全局传输锁：探测报文极小，且对端会回“忙”表示链路可达。
    /// </summary>
    public async Task<(bool ok, string reason, long elapsedMs)> ProbeEndpointAsync(
        PeerEndpoint endpoint, bool deep, CancellationToken userCt = default)
    {
        var connectTimeout = endpoint.IsWired
            ? (deep ? 2500 : 1200)
            : (deep ? 6000 : 2500);
        var ackTimeout = endpoint.IsWired ? 4000 : (deep ? 8000 : 4500);

        var sender = new TransferSender();
        sender.DebugLog += (_, msg) => DebugLog?.Invoke(this, msg);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, userCt);
        var outcome = await sender.ProbeAsync(endpoint, _computerName, connectTimeout, ackTimeout,
            wakePeer: deep && !endpoint.IsWired, linkedCts.Token).ConfigureAwait(false);
        return (outcome.Ok, outcome.Reason, outcome.ElapsedMs);
    }

    /// <summary>
    /// 双向广播连通性测试（PING/PONG，端口 39001）：只有这一步通过，
    /// 才说明 AP 在两个方向上都在转发客户端之间的 UDP 广播。
    /// 收发共用本机监听端的套接字，PONG 才能回到请求方（另开端口会收不到）。
    /// </summary>
    public async Task<bool> ProbeBroadcastAsync(CancellationToken userCt = default)
    {
        // 必须 await：直接返回 Task 会让 using 立刻释放 CTS，探测中的 Task.Delay 注册就会抛 ObjectDisposedException。
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, userCt);
        return await _broadcastProbe.PingAsync(TimeSpan.FromSeconds(8), linkedCts.Token).ConfigureAwait(false);
    }

    public void Dispose()
    {
        Stop();
        _server.Dispose();
        _broadcastReceiver.Dispose();
        _cts.Dispose();
    }
}

