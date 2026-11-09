using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using EtherTransfer.Network.NetworkInterfaces;

namespace EtherTransfer.Network.UdpTransfer;

public sealed class UdpBroadcastDatagram
{
    public IPAddress Source { get; init; } = IPAddress.Any;
    public byte[] Payload { get; init; } = Array.Empty<byte>();
}

/// <summary>
/// AP 隔离网络下的广播收发通道。与 TCP 链路不同，这里不建立任何连接：
/// 发送端按方案绑定本机 Wi-Fi 源地址（决定报文从哪块网卡出去），目标固定为子网定向广播地址。
/// 对端是否收到完全取决于 AP 是否转发客户端之间的广播，所以只做「尽力送达 + 位图重传」。
/// </summary>
public sealed class UdpBroadcastChannel : IDisposable
{
    private const int ReceiveBufferSize = 4 * 1024 * 1024;
    private const int SendBufferSize = 1024 * 1024;

    private readonly UdpClient _socket;
    private readonly List<IPEndPoint> _targets;

    public int Port { get; }

    /// <summary>本机在这条链路（Wi-Fi）上的源地址；监听端为空。</summary>
    public string LocalAddress { get; }

    public bool IsListener { get; }

    public IReadOnlyList<IPEndPoint> Targets => _targets;

    public string TargetList => _targets.Count == 0
        ? "未找到广播地址"
        : string.Join("、", _targets.Select(t => t.ToString()));

    private UdpBroadcastChannel(UdpClient socket, List<IPEndPoint> targets, int port, string localAddress, bool isListener)
    {
        _socket = socket;
        _targets = targets;
        Port = port;
        LocalAddress = localAddress;
        IsListener = isListener;
    }

    /// <summary>接收端：绑定 0.0.0.0 接收任意网卡进来的广播。</summary>
    public static UdpBroadcastChannel CreateListener(int port)
    {
        var socket = new UdpClient(AddressFamily.InterNetwork);
        socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
        TrySetBuffer(socket, ReceiveBufferSize, sendBuffer: false);
        socket.Client.Bind(new IPEndPoint(IPAddress.Any, port));
        return new UdpBroadcastChannel(socket, new List<IPEndPoint>(), port, string.Empty, isListener: true);
    }

    /// <summary>
    /// 发送端：绑定指定本机地址后向该链路（及其它链路）的子网广播地址发送。
    /// 绑定源地址是必须的 —— 有线/无线双网卡并存时，不绑定会让报文从错误的网卡出去，形成静默黑洞。
    /// </summary>
    public static UdpBroadcastChannel CreateSender(int port, string? localAddress)
    {
        var socket = new UdpClient(AddressFamily.InterNetwork);
        socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
        socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        TrySetBuffer(socket, SendBufferSize, sendBuffer: true);
        TrySetBuffer(socket, ReceiveBufferSize, sendBuffer: false);

        var bound = string.Empty;
        if (!string.IsNullOrWhiteSpace(localAddress) &&
            IPAddress.TryParse(localAddress, out var local) &&
            local.AddressFamily == AddressFamily.InterNetwork)
        {
            try
            {
                socket.Client.Bind(new IPEndPoint(local, 0));
                bound = localAddress;
            }
            catch (SocketException)
            {
                // 地址已失效（切换网络）：退回系统选路，仍然发广播，只是可能走错网卡。
                socket.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
            }
        }
        else
        {
            socket.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        }

        var targets = BuildTargets(bound, port);
        return new UdpBroadcastChannel(socket, targets, port, bound, isListener: false);
    }

    /// <summary>
    /// 按方案计算子网定向广播地址（本机地址 | ~掩码），并补上受限广播 255.255.255.255。
    /// 只有一块网卡时两者等价；多网卡时逐个网卡发一遍，保证每个广播域都覆盖到。
    /// </summary>
    private static List<IPEndPoint> BuildTargets(string boundAddress, int port)
    {
        var targets = new List<IPEndPoint>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            var interfaces = NetworkHelper.GetLanInterfaces().ToList();

            // 绑定成功时只发该链路；未绑定时把 Wi-Fi 优先的全部发一遍。
            var selected = string.IsNullOrEmpty(boundAddress)
                ? interfaces.OrderByDescending(i => i.IsWifi).ToList()
                : interfaces.Where(i => i.LocalAddress.ToString() == boundAddress).ToList();

            foreach (var netIf in selected)
            {
                var key = netIf.BroadcastAddress.ToString();
                if (seen.Add(key)) targets.Add(new IPEndPoint(netIf.BroadcastAddress, port));
            }
        }
        catch
        {
            // 网卡枚举失败时退化为受限广播。
        }

        if (targets.Count == 0 && seen.Add(IPAddress.Broadcast.ToString()))
        {
            targets.Add(new IPEndPoint(IPAddress.Broadcast, port));
        }

        return targets;
    }

    /// <summary>判断某个源地址是否就是本机（广播应答可能来自本机的监听端，必须排除）。</summary>
    public static bool IsLocalAddress(IPAddress address)
    {
        try
        {
            return NetworkHelper.GetLanInterfaces().Any(i => i.LocalAddress.Equals(address));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>优先 Wi-Fi 链路的本机地址（AP 隔离只会发生在无线侧）。</summary>
    public static string ResolveWifiAddress()
    {
        try
        {
            var interfaces = NetworkHelper.GetLanInterfaces().ToList();
            var wifi = interfaces.FirstOrDefault(i => i.IsWifi) ?? interfaces.FirstOrDefault();
            return wifi?.LocalAddress.ToString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 诊断 / 自测用通道：目标固定为一个单播地址。本机发出的广播不会被自己收到，
    /// 所以回环自测不能走「算子网广播地址」那条路，只能用这条。
    /// </summary>
    public static UdpBroadcastChannel CreateDiagnostic(int port, IPAddress target)
    {
        var socket = new UdpClient(AddressFamily.InterNetwork);
        socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
        TrySetBuffer(socket, SendBufferSize, sendBuffer: true);
        TrySetBuffer(socket, ReceiveBufferSize, sendBuffer: false);
        socket.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

        return new UdpBroadcastChannel(socket, new List<IPEndPoint> { new IPEndPoint(target, port) },
            port, string.Empty, isListener: false);
    }

    public async Task SendAsync(byte[] payload, CancellationToken ct = default)
    {
        if (_targets.Count == 0)
            throw new SocketException((int)SocketError.NetworkUnreachable);

        SocketException? last = null;
        bool sent = false;
        foreach (var target in _targets)
        {
            try
            {
                await _socket.SendAsync(payload.AsMemory(), target, ct).ConfigureAwait(false);
                sent = true;
            }
            catch (SocketException ex)
            {
                last = ex;
            }
        }

        if (!sent && last != null) throw last;
    }

    /// <summary>带超时的接收；超时返回 null（不是异常），便于窗口循环里直接判空。</summary>
    public async Task<UdpBroadcastDatagram?> ReceiveAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        if (timeout <= TimeSpan.Zero) return null;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);
        try
        {
            var result = await _socket.ReceiveAsync(linked.Token).ConfigureAwait(false);
            return new UdpBroadcastDatagram
            {
                Source = result.RemoteEndPoint.Address,
                Payload = result.Buffer
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (SocketException)
        {
            // ICMP 端口不可达 / 网卡切换等瞬时错误按「这一轮没收到」处理。
            return null;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }

    /// <summary>把一段报文广播到所有链路（PONG 回包用）。</summary>
    public static async Task BroadcastOnAllLinksAsync(byte[] payload, int port, CancellationToken ct = default)
    {
        foreach (var netIf in NetworkHelper.GetLanInterfaces().ToList())
        {
            try
            {
                using var channel = CreateSender(port, netIf.LocalAddress.ToString());
                await channel.SendAsync(payload, ct).ConfigureAwait(false);
            }
            catch
            {
                // 某条链路发不出去不影响其它链路。
            }
        }
    }

    private static void TrySetBuffer(UdpClient socket, int size, bool sendBuffer)
    {
        try
        {
            if (sendBuffer)
                socket.Client.SendBufferSize = size;
            else
                socket.Client.ReceiveBufferSize = size;
        }
        catch
        {
            // 系统限制（部分 Android 内核）时保持默认值。
        }
    }

    public void Dispose()
    {
        try { _socket.Dispose(); } catch { }
    }
}
