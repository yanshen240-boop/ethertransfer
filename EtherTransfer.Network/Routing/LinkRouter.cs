using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using EtherTransfer.Core.Models;
using EtherTransfer.Network.NetworkInterfaces;

namespace EtherTransfer.Network.Routing;

public sealed class LinkConnectResult
{
    public TcpClient Client { get; init; } = null!;
    public PeerEndpoint Endpoint { get; init; } = null!;
    public long ElapsedMs { get; init; }
    public IReadOnlyList<string> Attempts { get; init; } = Array.Empty<string>();
}

/// <summary>单次快速建连的结果（连通性探测专用）。</summary>
public sealed class QuickConnectResult
{
    public TcpClient? Client { get; init; }
    public string Error { get; init; } = string.Empty;
    public long ElapsedMs { get; init; }
    public bool Ok => Client != null;
}

/// <summary>
/// 链路感知的建连器。
/// 双网卡（有线 + 无线）并存时，Windows 处于强主机模型：
/// 源地址/出接口绑定错 = 报文被静默丢弃（不会回 RST），只能等超时。
/// 因此这里坚持“先绑定该链路对应的本机源地址，再连接”，并按链路逐一尝试、失败自动换链路。
/// </summary>
public static class LinkRouter
{
    /// <summary>有线首轮：链路直连、RTT 通常 &lt;1ms，快速失败以便切换。</summary>
    public const int WiredFastProbeMs = 1500;

    /// <summary>有线次轮：可能是唯一可用链路，给足时间。</summary>
    public const int WiredSlowProbeMs = 4000;

    /// <summary>
    /// 无线首轮：手机常处于 Wi-Fi 省电（DTIM 缓冲）或刚被唤醒，
    /// 首包 RTT 可达数百毫秒，1.5 秒会误判为“不可达”。这里放宽到 4 秒。
    /// </summary>
    public const int WifiFastProbeMs = 4000;

    /// <summary>无线次轮：允许 Windows 的 SYN 重传两次后再判定失败。</summary>
    public const int WifiSlowProbeMs = 10000;

    /// <summary>兼容旧常量。</summary>
    public const int FastProbeMs = WiredFastProbeMs;

    /// <summary>兼容旧常量。</summary>
    public const int SlowProbeMs = WiredSlowProbeMs;

    public static async Task<LinkConnectResult> ConnectAsync(
        IReadOnlyList<PeerEndpoint> endpoints,
        CancellationToken ct,
        Action<string>? log = null)
    {
        if (endpoints == null || endpoints.Count == 0)
            throw new InvalidOperationException("没有可用的链路端点。");

        var attempts = new List<string>();
        Exception? last = null;
        bool allTimedOut = true;

        for (int round = 0; round < 3; round++)
        {
            foreach (var ep in endpoints)
            {
                // 有线只有两轮（直连链路要么通要么断）；无线多给一轮，
                // 因为校园/企业 WLAN 对“已休眠或跨 AP 的客户端”常有较高丢包，
                // 单次 SYN 丢掉就整次失败，多试一轮显著提高成功率。
                if (ep.IsWired && round >= 2) continue;

                ct.ThrowIfCancellationRequested();

                var timeout = ep.IsWired
                    ? (round == 0 ? WiredFastProbeMs : WiredSlowProbeMs)
                    : (round == 0 ? WifiFastProbeMs : WifiSlowProbeMs);

                // 无线链路：每次尝试前都打一发 UDP 唤醒对端射频/AP 侧客户端表项。
                if (!ep.IsWired)
                    await WakePeerAsync(ep, log).ConfigureAwait(false);

                var sw = Stopwatch.StartNew();
                TcpClient? client = null;
                try
                {
                    client = CreateBoundClient(ep.LocalAddress, log);
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(timeout);
                    await client.ConnectAsync(IPAddress.Parse(ep.Address), ep.Port, cts.Token).ConfigureAwait(false);
                    sw.Stop();

                    var local = (client.Client.LocalEndPoint as IPEndPoint)?.ToString() ?? "未知";
                    log?.Invoke($"链路连通：{ep.LinkLabel} {ep.Address}:{ep.Port}，本机 {local}，握手 {sw.ElapsedMilliseconds} ms");
                    return new LinkConnectResult
                    {
                        Client = client,
                        Endpoint = ep,
                        ElapsedMs = sw.ElapsedMilliseconds,
                        Attempts = attempts
                    };
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    last = ex;

                    bool timedOut = ex is OperationCanceledException && !ct.IsCancellationRequested;
                    if (!timedOut) allTimedOut = false;

                    string reason = timedOut
                        ? $"{timeout} ms 内无响应（报文可能被丢弃）"
                        : ex.Message;

                    var line = $"{ep.LinkLabel} {ep.Address}:{ep.Port} → {reason}";
                    if (string.IsNullOrWhiteSpace(ep.LocalAddress) == false)
                        line += $"（本机源 {ep.LocalAddress}）";
                    attempts.Add(line);
                    log?.Invoke($"链路失败：{line}，用时 {sw.ElapsedMilliseconds} ms");

                    try { client?.Dispose(); } catch { }
                }
            }
        }

        var hint = allTimedOut
            ? "提示：对端的发现广播能收到，但 TCP 建连无响应。最常见原因是对端无线射频处于省电休眠（手机息屏/后台被冻结），广播只能短暂唤醒射频；也可能是无线网络开启了客户端隔离。让对端亮屏并保持应用在前台后再试。"
            : string.Empty;

        throw new IOException("所有链路都无法连接对端。" + string.Join("；", attempts) + hint, last);
    }

    /// <summary>
    /// 单次建连：只做一轮、不打唤醒包、不多轮重试。专供周期性连通性探测使用。
    /// 探测必须“便宜”：每几秒对手机做一次 3 轮建连 + 3 发唤醒广播，
    /// 会把对端射频和 AP 的客户端表项反复打醒，反而拖累真正的文件传输。
    /// </summary>
    public static async Task<QuickConnectResult> TryConnectOnceAsync(
        PeerEndpoint endpoint,
        int timeoutMs,
        bool wakePeer = false,
        Action<string>? log = null)
    {
        var sw = Stopwatch.StartNew();
        TcpClient? client = null;
        try
        {
            if (wakePeer && !endpoint.IsWired)
                await WakePeerAsync(endpoint, log).ConfigureAwait(false);

            client = CreateBoundClient(endpoint.LocalAddress, log);
            using var cts = new CancellationTokenSource(timeoutMs);
            await client.ConnectAsync(IPAddress.Parse(endpoint.Address), endpoint.Port, cts.Token).ConfigureAwait(false);
            sw.Stop();

            var local = (client.Client.LocalEndPoint as IPEndPoint)?.ToString() ?? "未知";
            log?.Invoke($"探测建连成功：{endpoint.LinkLabel} {endpoint.Address}:{endpoint.Port}，本机 {local}，握手 {sw.ElapsedMilliseconds} ms");
            return new QuickConnectResult { Client = client, ElapsedMs = sw.ElapsedMilliseconds };
        }
        catch (Exception ex)
        {
            sw.Stop();
            try { client?.Dispose(); } catch { }

            bool timedOut = ex is OperationCanceledException;
            var reason = timedOut ? $"{timeoutMs} ms 内无响应" : ex.Message;
            log?.Invoke($"探测建连失败：{endpoint.LinkLabel} {endpoint.Address}:{endpoint.Port} → {reason}，用时 {sw.ElapsedMilliseconds} ms");
            return new QuickConnectResult { Error = reason, ElapsedMs = sw.ElapsedMilliseconds };
        }
    }

    /// <summary>
    /// 无线链路建连前先发唤醒包，把正在省电的无线射频唤醒。
    /// 关键：必须走广播。手机息屏后单播 UDP 会被 AP 缓冲甚至丢弃（实测发现广播可达而单播不可达），
    /// 广播报文总能送达，射频被唤醒后紧随其后的 TCP 建连才有可能成功。
    /// 报文带独立 Id，对端会当作无关报文忽略，不会污染设备列表。
    /// </summary>
    private static async Task WakePeerAsync(PeerEndpoint ep, Action<string>? log)
    {
        try
        {
            if (!IPAddress.TryParse(ep.Address, out var peer) || peer.AddressFamily != AddressFamily.InterNetwork)
                return;

            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.EnableBroadcast = true;
            IPAddress? subnetBroadcast = null;
            if (!string.IsNullOrWhiteSpace(ep.LocalAddress) &&
                IPAddress.TryParse(ep.LocalAddress, out var local) &&
                local.AddressFamily == AddressFamily.InterNetwork)
            {
                try { udp.Client.Bind(new IPEndPoint(local, 0)); } catch { }
                subnetBroadcast = NetworkHelper.GetLanInterfaces()
                    .FirstOrDefault(i => i.LocalAddress.Equals(local))?.BroadcastAddress;
            }

            var payload = System.Text.Encoding.UTF8.GetBytes("{\"Id\":\"EtherTransfer-Wake\",\"Type\":\"WAKE\"}");
            if (subnetBroadcast != null)
                await udp.SendAsync(payload, payload.Length, new IPEndPoint(subnetBroadcast, 50000)).ConfigureAwait(false);
            await udp.SendAsync(payload, payload.Length, new IPEndPoint(IPAddress.Broadcast, 50000)).ConfigureAwait(false);
            await udp.SendAsync(payload, payload.Length, new IPEndPoint(peer, 50000)).ConfigureAwait(false);
            var via = subnetBroadcast != null ? $"{subnetBroadcast}、255.255.255.255 及单播 {ep.Address}:50000" : $"255.255.255.255 及单播 {ep.Address}:50000";
            log?.Invoke($"已广播唤醒报文（{via}），等待无线射频就绪。");
            await Task.Delay(300).ConfigureAwait(false);
        }
        catch
        {
            // 唤醒失败不影响后续正经的建连尝试。
        }
    }

    /// <summary>
    /// 绑定指定源地址；地址为空时退回系统选路（单链路 / 旧调用路径保持原有行为）。
    /// 绑定失败会明确记日志，不再静默退回（否则“链路固定”会悄悄失效）。
    /// </summary>
    public static TcpClient CreateBoundClient(string localAddress, Action<string>? log = null)
    {
        if (!string.IsNullOrWhiteSpace(localAddress))
        {
            if (!IPAddress.TryParse(localAddress, out var local) || local.AddressFamily != AddressFamily.InterNetwork)
            {
                log?.Invoke($"本机源地址“{localAddress}”不是有效 IPv4，本次改为系统选路。");
            }
            else
            {
                try
                {
                    return new TcpClient(new IPEndPoint(local, 0));
                }
                catch (Exception ex)
                {
                    log?.Invoke($"绑定本机源地址 {localAddress} 失败（{ex.Message}），本次改为系统选路。");
                }
            }
        }

        return new TcpClient(AddressFamily.InterNetwork);
    }

    /// <summary>
    /// 旧逻辑兼容：按“目标地址落在哪块网卡网段”选源地址。
    /// 仅在没有链路端点信息时使用，且不再无条件偏向有线。
    /// </summary>
    public static TcpClient CreateBoundClientLegacy(IPAddress targetAddress)
    {
        try
        {
            var candidates = NetworkInterfaces.NetworkHelper.GetLanInterfaces()
                .Where(i => i.Contains(targetAddress))
                .ToList();

            if (candidates.Count == 1)
                return new TcpClient(new IPEndPoint(candidates[0].LocalAddress, 0));
        }
        catch
        {
        }

        return new TcpClient();
    }
}
