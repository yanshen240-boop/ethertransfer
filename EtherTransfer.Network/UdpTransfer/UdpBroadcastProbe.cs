using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EtherTransfer.Core.Models;

namespace EtherTransfer.Network.UdpTransfer;

/// <summary>
/// 双向广播连通性测试（对应方案里的 probe-send / probe-listen，端口 39001）。
/// <para>
/// 关键点：PING 与 PONG 必须走**同一个**套接字。方案里 probe-send 自己绑定 39001 才能收到对端
/// 广播回来的 PONG；如果本机监听端占着 39001、发送端另开一个临时端口，PONG 会送到监听端而不是
/// 请求方，探测永远失败（设备灯常红）。
/// </para>
/// </summary>
public sealed class UdpBroadcastProbe : IDisposable
{
    private const string PingPrefix = "PING:";
    private const string PongPrefix = "PONG:";

    private readonly int _port;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pending = new();

    private UdpBroadcastChannel? _listener;
    private Task? _loop;

    public event EventHandler<StructuredLogMessage>? DebugLog;

    public UdpBroadcastProbe(int port = UdpBroadcastProtocol.ProbePort)
    {
        _port = port;
    }

    private void Log(string message, LogLevel level = LogLevel.Info) =>
        DebugLog?.Invoke(this, new StructuredLogMessage("udp.probe", message, level));

    /// <summary>启动监听端：收到 PING 立刻广播 PONG。端口被占用时返回 false，不影响文件传输。</summary>
    public bool Start()
    {
        try
        {
            _listener = UdpBroadcastChannel.CreateListener(_port);
        }
        catch (Exception ex)
        {
            Log($"广播测试监听端启动失败（{_port} 端口）：{ex.Message}", LogLevel.Warning);
            _listener = null;
            return false;
        }

        _loop = Task.Run(() => ListenLoopAsync(_cts.Token));
        Log($"广播测试监听端已启动：UDP {_port}，等待对端 PING。");
        return true;
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        var listener = _listener;
        if (listener == null) return;

        while (!ct.IsCancellationRequested)
        {
            var datagram = await listener.ReceiveAsync(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            if (datagram == null) continue;

            // 本机发出的广播会回环给自己：既不该给自己的 PING 回 PONG，也不能把自己的 PONG 当成功。
            if (UdpBroadcastChannel.IsLocalAddress(datagram.Source)) continue;

            var text = Encoding.UTF8.GetString(datagram.Payload);

            if (text.StartsWith(PingPrefix, StringComparison.Ordinal))
            {
                var pong = Encoding.UTF8.GetBytes(PongPrefix + text.Substring(PingPrefix.Length));
                await UdpBroadcastChannel.BroadcastOnAllLinksAsync(pong, _port, ct).ConfigureAwait(false);
                Log($"收到来自 {datagram.Source} 的 PING，已广播 PONG。");
            }
            else if (text.StartsWith(PongPrefix, StringComparison.Ordinal))
            {
                var token = text.Substring(PongPrefix.Length);
                if (_pending.TryRemove(token, out var completion)) completion.TrySetResult(true);
            }
        }
    }

    /// <summary>
    /// 发起测试：最多 5 轮发送 PING，每轮等 1 秒的 PONG。
    /// 收到非本机来源的 PONG 即说明两段广播都通，广播通道可用。
    /// </summary>
    public async Task<bool> PingAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (_listener == null) return false;

        var token = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[token] = completion;

        try
        {
            var payload = Encoding.UTF8.GetBytes(PingPrefix + token);
            var deadline = DateTimeOffset.UtcNow + timeout;

            for (int attempt = 1; attempt <= 5 && DateTimeOffset.UtcNow < deadline; attempt++)
            {
                await UdpBroadcastChannel.BroadcastOnAllLinksAsync(payload, _port, ct).ConfigureAwait(false);

                var winner = await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromSeconds(1), ct))
                    .ConfigureAwait(false);
                if (winner == completion.Task && completion.Task.Result) return true;
            }

            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
        finally
        {
            _pending.TryRemove(token, out _);
        }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _listener?.Dispose(); } catch { }
        try { _cts.Dispose(); } catch { }
        GC.SuppressFinalize(this);
    }
}
