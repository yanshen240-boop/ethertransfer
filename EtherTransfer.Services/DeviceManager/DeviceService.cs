using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Net.NetworkInformation;
using EtherTransfer.Core;
using EtherTransfer.Core.Models;
using EtherTransfer.Network.UdpDiscovery;
using EtherTransfer.Network.NetworkInterfaces;

namespace EtherTransfer.Services.DeviceManager;

public class DeviceService : IDisposable
{
    private readonly DiscoveryService _discoveryService;
    private readonly ConcurrentDictionary<string, DiscoveredDevice> _devices = new();
    private readonly ConcurrentDictionary<string, long> _offlineSessions = new();
    private readonly ConcurrentDictionary<string, long> _highestSequenceBySession = new();
    private CancellationTokenSource? _cts;

    private readonly Dictionary<string, DateTime> _lastRefreshAttempt = new();
    private HashSet<string> _lastKnownIps = new();
    private string _computerName = string.Empty;
    private int _tcpPort;
    private CancellationTokenSource? _debounceCts;

    public event EventHandler? DevicesChanged;
    public event EventHandler? NetworkChanged;
    public event EventHandler<PeerDiscoveredEventArgs>? TransferCancelReceived;
    public event EventHandler<StructuredLogMessage>? DebugLog;

    private void Log(string msg, LogLevel level = LogLevel.Info, string eventId = "device.log")
    {
        DebugLog?.Invoke(this, new StructuredLogMessage(eventId, msg, level));
    }

    public DeviceService()
    {
        _discoveryService = new DiscoveryService();
        _discoveryService.PeerDiscovered += OnPeerDiscovered;
        _discoveryService.TransferCancelReceived += (_, e) => TransferCancelReceived?.Invoke(this, e);
        _discoveryService.DebugLog += (_, msg) => DebugLog?.Invoke(this, msg);
    }

    public Task SendTransferCancelAsync(IPAddress targetAddress)
    {
        return _discoveryService.SendTransferCancelAsync(targetAddress);
    }

    /// <summary>点“刷新”时立刻补发发现广播，让对端马上回灌状态（不等下一轮周期广播）。</summary>
    public Task AnnounceNowAsync() => _discoveryService.AnnounceNowAsync();

    public async Task StartAsync(string computerName, int tcpPort)
    {
        _computerName = computerName;
        _tcpPort = tcpPort;
        _cts = new CancellationTokenSource();
        await _discoveryService.StartAsync(computerName, tcpPort);

        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAddressChanged;

        _ = Task.Run(() => CleanupLoopAsync(_cts.Token));
        _lastKnownIps = GetCurrentLocalIps();

        var diags = NetworkHelper.DiagnoseInterfaces();
        foreach (var diag in diags)
        {
            Log(diag, LogLevel.Info, "network.diagnostic");
        }
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;

        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(300, token);
                if (token.IsCancellationRequested) return;

                bool vpnStateChanged = false;
                bool oldVpnState = HasActiveVpn;

                var currentIps = GetCurrentLocalIps();
                bool ipSetChanged = !_lastKnownIps.SetEquals(currentIps);
                _lastKnownIps = currentIps;

                if (oldVpnState != HasActiveVpn)
                {
                    vpnStateChanged = true;
                }

                if (!ipSetChanged && !vpnStateChanged)
                {

                    return;
                }

                var diags = NetworkHelper.DiagnoseInterfaces();
                foreach (var diag in diags)
                {
                    Log(diag, LogLevel.Info, "network.diagnostic");
                }

                NetworkChanged?.Invoke(this, EventArgs.Empty);

                _discoveryService.Stop(sendBye: false);
                await _discoveryService.StartAsync(_computerName, _tcpPort, isRebind: true);
            }
            catch { }
        });
    }

    public void UpdateComputerName(string newName)
    {
        _computerName = newName;
        _discoveryService.UpdateComputerName(newName);
    }

    public void Stop()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAddressChanged;
        _cts?.Cancel();
        _discoveryService.Stop(sendBye: true);
    }

    /// <summary>
    /// 反查“对端这条链路”对应的本机网卡地址（实现在 NetworkHelper，便于单元测试）。
    /// </summary>
    private static (string localAddress, string interfaceId) ResolveLocalSource(string peerAddress, bool wired)
        => NetworkHelper.ResolveLocalSourceFor(peerAddress, wired);

    /// <summary>把过期超过该窗口的端点视为不可用，避免“陈旧的网线端点”压过“新鲜的 Wi-Fi 端点”。</summary>
    private static readonly TimeSpan EndpointFreshWindow = TimeSpan.FromSeconds(15);

    private static string DeviceKeyOf(DiscoveredDevice d) =>
        !string.IsNullOrEmpty(d.DeviceId) ? d.DeviceId :
        !string.IsNullOrEmpty(d.SessionId) ? d.SessionId : d.Address;

    public IEnumerable<DiscoveredDevice> GetActiveDevices()
    {
        return _devices.Values
            .GroupBy(DeviceKeyOf)
            .Select(PickPrimaryEndpoint)
            .ToList();
    }

    private static DiscoveredDevice PickPrimaryEndpoint(IEnumerable<DiscoveredDevice> group)
    {
        var all = group.ToList();
        var now = DateTime.UtcNow;
        var fresh = all.Where(d => now - d.LastSeen <= EndpointFreshWindow).ToList();
        var pool = fresh.Count > 0 ? fresh : all;

        return pool.OrderByDescending(d => d.IsWired).ThenByDescending(d => d.LastSeen).First();
    }

    /// <summary>
    /// 取同一台设备在两条链路上的全部端点（首选链路在前），发送端据此逐条尝试并在失败时换链路。
    /// </summary>
    public List<PeerEndpoint> GetEndpointsFor(DiscoveredDevice? selected)
    {
        if (selected == null) return new List<PeerEndpoint>();

        var key = DeviceKeyOf(selected);
        var now = DateTime.UtcNow;
        return _devices.Values
            .Where(d => DeviceKeyOf(d) == key || (!string.IsNullOrEmpty(selected.SessionId) && d.SessionId == selected.SessionId))
            .GroupBy(d => d.Address + "|" + d.Port)
            .Select(g => g.OrderByDescending(d => d.LastSeen).First())
            // 首选“新鲜的有线”，其次“最新的端点”：避免一条已经失效的网线端点把整次传输拖成超时。
            .OrderByDescending(d => d.IsWired && (now - d.LastSeen) <= EndpointFreshWindow)
            .ThenByDescending(d => d.LastSeen)
            .Select(d => d.ToEndpoint())
            .ToList();
    }

    public bool HasActiveVpn { get; private set; }

    private HashSet<string> GetCurrentLocalIps()
    {
        var ips = new HashSet<string>();
        var interfaces = CrossPlatformNetworkDetector.GetInterfaces().ToList();

        bool vpnActive = interfaces.Any(n =>
            n.IsVirtual &&
            n.OperationalStatus == OperationalStatus.Up &&
            (n.Description.Contains("cisco", StringComparison.OrdinalIgnoreCase) ||
             n.Description.Contains("anyconnect", StringComparison.OrdinalIgnoreCase) ||
             n.Description.Contains("globalprotect", StringComparison.OrdinalIgnoreCase) ||
             n.Description.Contains("zscaler", StringComparison.OrdinalIgnoreCase) ||
             n.Description.Contains("fortinet", StringComparison.OrdinalIgnoreCase) ||
             n.Description.Contains("pulse secure", StringComparison.OrdinalIgnoreCase) ||
             n.Description.Contains("checkpoint", StringComparison.OrdinalIgnoreCase) ||
             n.Description.Contains("sonicwall", StringComparison.OrdinalIgnoreCase) ||
             n.Description.Contains("f5", StringComparison.OrdinalIgnoreCase) ||
             n.Description.Contains("big-ip", StringComparison.OrdinalIgnoreCase) ||
             n.Description.Contains("netmotion", StringComparison.OrdinalIgnoreCase)));

        if (vpnActive && !HasActiveVpn)
        {
            Log("检测到企业 VPN 正在运行。如果无法发现其他设备，可能是您的 IT 管理员屏蔽了局域网设备发现。", LogLevel.Warning, "network.vpn_warning");
        }

        HasActiveVpn = vpnActive;

        foreach (var ni in interfaces.Where(n => NetworkHelper.IsUsableLanInterface(n)))
        {
            foreach (var addr in ni.Ipv4Addresses)
            {
                ips.Add(addr.ToString());
            }
        }
        return ips;
    }

    private void OnPeerDiscovered(object? sender, PeerDiscoveredEventArgs e)
    {
        var sourceIp = e.SourceAddress.ToString();

        if (_lastKnownIps.Contains(sourceIp))
        {
            return;
        }

        if (!NetworkHelper.IsIpInActiveSubnets(sourceIp))
        {
            return;
        }

        ApplyAnnouncement(e);
    }

    private void ApplyAnnouncement(PeerDiscoveredEventArgs e)
    {
        var sourceIp = e.SourceAddress.ToString();
        var sessionId = e.Message.SessionId;
        if (string.IsNullOrEmpty(sessionId))
        {
            sessionId = sourceIp;
        }

        var msgSeq = e.Message.SequenceNumber;

        if (e.Message.Type == "BYE")
        {

            _offlineSessions.AddOrUpdate(sessionId, msgSeq, (_, old) => Math.Max(old, msgSeq));

            bool removedAny = false;

            if (_devices.TryRemove(sessionId, out var removed))
            {
                Log($"设备已离线：{removed.Name}（{sourceIp}）", LogLevel.Info, "device.offline");
                removedAny = true;
            }

            var matchingKeys = _devices.Where(kvp =>
                kvp.Value.SessionId == sessionId).Select(kvp => kvp.Key).ToList();

            foreach (var k in matchingKeys)
            {
                if (_devices.TryRemove(k, out var r))
                {
                    Log($"设备已离线：{r.Name}（{r.Address}）", LogLevel.Info, "device.offline");
                    removedAny = true;
                }
            }

            if (removedAny)
            {
                DevicesChanged?.Invoke(this, EventArgs.Empty);
            }
            return;
        }

        if (_offlineSessions.TryGetValue(sessionId, out var byeSeq))
        {
            if (msgSeq > 0 && msgSeq <= byeSeq)
            {

                return;
            }
            else if (msgSeq > byeSeq)
            {

                _offlineSessions.TryRemove(sessionId, out _);
            }
        }

        var previousSessions = _devices.Values.Where(d => d.SessionId != sessionId &&
            ((!string.IsNullOrEmpty(e.Message.DeviceId) && d.DeviceId == e.Message.DeviceId) ||
             (d.Address == sourceIp && (string.IsNullOrEmpty(e.Message.DeviceId) || string.IsNullOrEmpty(d.DeviceId)))))
            .Select(d => d.SessionId).Distinct().ToList();
        foreach (var oldSession in previousSessions)
        {
            _offlineSessions[oldSession] = long.MaxValue;
            foreach (var old in _devices.Where(p => p.Value.SessionId == oldSession).ToList())
                _devices.TryRemove(old.Key, out _);
        }

        if (msgSeq > 0)
        {
            var endpointKey = sessionId + "|" + sourceIp;
            var lastSeq = _highestSequenceBySession.GetOrAdd(endpointKey, 0);
            if (msgSeq < lastSeq && _devices.Values.Any(d => d.SessionId == sessionId))
            {
                return;
            }
            _highestSequenceBySession[endpointKey] = Math.Max(lastSeq, msgSeq);
        }

        var isNew = false;
        var updated = false;
        var devicePort = e.Message.TcpPort > 0 ? e.Message.TcpPort : 55000;

        // 链路身份优先取报文自带的声明（每个网卡各发一份 HELLO）；
        // 旧版客户端不带该字段时才退回“子网包含”推断（APIPA /16 会误判，仅作兜底）。
        var declaredLink = e.Message.Link;
        bool linkIsWired = declaredLink.Equals("Wired", StringComparison.OrdinalIgnoreCase)
            || (string.IsNullOrEmpty(declaredLink) && NetworkHelper.IsWiredAddress(sourceIp));
        var linkLabel = linkIsWired ? "Wired" : "Wifi";

        // 注意：报文里的 e.Message.LocalAddress 是“对端自己的地址”，不能拿来当本机源地址。
        // 本机源地址要按“该链路对应的本机网卡”反查，否则会绑定到一个不属于本机的地址。
        var (localSource, localInterfaceId) = ResolveLocalSource(sourceIp, linkIsWired);

        _devices.AddOrUpdate(sessionId + "|" + sourceIp,
            _ =>
            {
                updated = true;
                isNew = true;
                return new DiscoveredDevice
                {
                    SessionId = sessionId,
                    DeviceId = e.Message.DeviceId,
                    Name = e.Message.ComputerName,
                    Address = sourceIp,
                    IsWired = linkIsWired,
                    Link = linkLabel,
                    LocalAddress = localSource,
                    LocalInterfaceId = localInterfaceId,
                    Port = devicePort,
                    OS = e.Message.OS,
                    LastSeen = DateTime.UtcNow
                };
            },
            (_, existing) =>
            {
                existing.LastSeen = DateTime.UtcNow;
                if (existing.IsWired != linkIsWired ||
                    existing.Link != linkLabel ||
                    existing.LocalAddress != localSource ||
                    existing.LocalInterfaceId != localInterfaceId)
                {
                    existing.IsWired = linkIsWired;
                    existing.Link = linkLabel;
                    existing.LocalAddress = localSource;
                    existing.LocalInterfaceId = localInterfaceId;
                    updated = true;
                }
                if (existing.Name != e.Message.ComputerName || existing.OS != e.Message.OS || existing.Address != sourceIp || existing.Port != devicePort)
                {
                    if (existing.Address != sourceIp)
                    {
                        Log($"设备 IP 已变更：{existing.Name} 从 {existing.Address} 变为 {sourceIp}", LogLevel.Info, "device.ip_changed");
                    }
                    existing.Name = e.Message.ComputerName;
                    existing.OS = e.Message.OS;
                    existing.Address = sourceIp;
                    existing.Port = devicePort;
                    updated = true;
                }
                return existing;
            });

        if (isNew)
        {
            Log($"发现新设备：{e.Message.ComputerName}（{sourceIp}，链路={linkLabel}）", LogLevel.Info, "device.new");
        }

        if (updated)
        {
            DevicesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task CleanupLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var now = DateTime.UtcNow;
                var staleThreshold = NetworkConfig.Default.PeerStaleThreshold;
                var removedAny = false;

                var currentIps = GetCurrentLocalIps();
                if (!_lastKnownIps.SetEquals(currentIps))
                {
                    _lastKnownIps = currentIps;
                    var diags = NetworkHelper.DiagnoseInterfaces();
                    foreach (var diag in diags)
                    {
                        Log(diag, LogLevel.Info, "network.diagnostic");
                    }
                    NetworkChanged?.Invoke(this, EventArgs.Empty);
                }

                var keysToRemove = _devices.Where(kvp =>
                    now - kvp.Value.LastSeen > staleThreshold ||
                    !NetworkHelper.IsIpInActiveSubnets(kvp.Value.Address)).Select(kvp => kvp.Key).ToList();
                foreach (var key in keysToRemove)
                {
                    if (_devices.TryRemove(key, out var removed))
                    {
                        Log($"已移除离线/过期设备：{removed.Name}（{removed.Address}）", LogLevel.Info, "device.removed.stale");
                        removedAny = true;
                    }
                }

                if (removedAny)
                {
                    DevicesChanged?.Invoke(this, EventArgs.Empty);
                }

                await Task.Delay(1000, cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        Stop();
        _debounceCts?.Dispose();
        _cts?.Dispose();
        _discoveryService?.Dispose();
    }
}

