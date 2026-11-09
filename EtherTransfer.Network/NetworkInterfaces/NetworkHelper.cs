using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace EtherTransfer.Network.NetworkInterfaces;

public class InterfaceAddressInfo
{
    public IPAddress LocalAddress { get; }
    public IPAddress BroadcastAddress { get; }
    public IPAddress Mask { get; }
    public bool IsWifi { get; }
    public string InterfaceId { get; }
    public string InterfaceName { get; }

    public bool Contains(IPAddress target)
    {
        var t = target.GetAddressBytes();
        var l = LocalAddress.GetAddressBytes();
        var m = Mask.GetAddressBytes();
        return t.Length == 4 && Enumerable.Range(0, 4).All(i => (t[i] & m[i]) == (l[i] & m[i]));
    }

    public InterfaceAddressInfo(IPAddress localAddress, IPAddress broadcastAddress, IPAddress mask, bool isWifi,
        string interfaceId = "", string interfaceName = "")
    {
        LocalAddress = localAddress;
        BroadcastAddress = broadcastAddress;
        Mask = mask;
        IsWifi = isWifi;
        InterfaceId = interfaceId;
        InterfaceName = interfaceName;
    }
}

public static class NetworkHelper
{
    public static bool IsUsableLanInterface(NetworkInterfaceInfo ni) =>
        ni.IsPhysical && !ni.IsVirtual && (ni.IsEthernet || ni.IsWifi) &&
        ni.OperationalStatus == OperationalStatus.Up;

    public static bool HasActiveWifi => CrossPlatformNetworkDetector.GetInterfaces()
        .Any(ni => IsUsableLanInterface(ni) && ni.IsWifi && ni.Ipv4Addresses.Count > 0);

    public static bool IsWiredAddress(string address) => GetLanInterfaces()
        .Any(i => !i.IsWifi && i.Contains(IPAddress.Parse(address)));

    public static IEnumerable<InterfaceAddressInfo> GetLanInterfaces()
    {
        var interfaces = CrossPlatformNetworkDetector.GetInterfaces()
            .Where(ni => IsUsableLanInterface(ni));

        foreach (var ni in interfaces)
        {
            var origNi = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Id == ni.Id);
            if (origNi == null) continue;

            var ipProps = origNi.GetIPProperties();
            foreach (var ip in ipProps.UnicastAddresses)
            {
                if (ip.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    var ipBytes = ip.Address.GetAddressBytes();
                    var mask = ip.IPv4Mask;

                    if (mask == null || mask.GetAddressBytes().All(b => b == 0))
                    {
                        if (ipBytes[0] == 169 && ipBytes[1] == 254)
                        {
                            mask = IPAddress.Parse("255.255.0.0");
                        }
                        else
                        {
                            mask = IPAddress.Parse("255.255.255.0");
                        }
                    }

                    var maskBytes = mask.GetAddressBytes();

                    if (maskBytes.Length == 4 && ipBytes.Length == 4)
                    {
                        var broadcastBytes = new byte[4];
                        for (int i = 0; i < 4; i++)
                        {
                            broadcastBytes[i] = (byte)(ipBytes[i] | ~maskBytes[i]);
                        }

                        yield return new InterfaceAddressInfo(ip.Address, new IPAddress(broadcastBytes), mask,
                            ni.IsWifi, ni.Id, ni.Name);
                    }
                }
            }
        }
    }

    /// <summary>按本机地址找到它所属的链路（用于判断入站连接走的是网线还是 Wi-Fi）。</summary>
    public static InterfaceAddressInfo? FindByLocalAddress(IPAddress? address)
    {
        if (address == null || address.AddressFamily != AddressFamily.InterNetwork) return null;
        try
        {
            return GetLanInterfaces().FirstOrDefault(i => i.LocalAddress.Equals(address));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>把本机地址翻译成链路标签，仅用于日志与界面提示。</summary>
    public static string LinkLabelOf(IPAddress? address)
    {
        var found = FindByLocalAddress(address);
        if (found == null) return "未知链路";
        return found.IsWifi ? "Wi-Fi" : "有线";
    }

    /// <summary>
    /// 反查“对端这条链路”对应的本机网卡地址，用作发送时的绑定源地址。
    /// 规则：先在该链路类型的网卡里找“网段包含对端地址”的那块；只有一块同类网卡时直接用它；
    /// 都无法确定就返回空（交给系统选路）。**绝不会返回对端自己的地址**。
    /// </summary>
    public static (string localAddress, string interfaceId) ResolveLocalSourceFor(string peerAddress, bool wired)
    {
        try
        {
            var candidates = GetLanInterfaces()
                .Where(i => i.IsWifi != wired)
                .ToList();

            if (candidates.Count == 0) return (string.Empty, string.Empty);

            if (IPAddress.TryParse(peerAddress, out var peer))
            {
                var hit = candidates.FirstOrDefault(i => i.Contains(peer));
                if (hit != null) return (hit.LocalAddress.ToString(), hit.InterfaceId);
            }

            var distinct = candidates
                .GroupBy(i => i.LocalAddress.ToString())
                .Select(g => g.First())
                .ToList();

            return distinct.Count == 1
                ? (distinct[0].LocalAddress.ToString(), distinct[0].InterfaceId)
                : (string.Empty, string.Empty);
        }
        catch
        {
            return (string.Empty, string.Empty);
        }
    }

    public static List<string> DiagnoseInterfaces()
    {
        var results = new List<string>();

        var interfaces = CrossPlatformNetworkDetector.GetInterfaces()
            .Where(ni => ni.IsPhysical && !ni.IsVirtual && (ni.IsEthernet || ni.IsWifi));

        foreach (var ni in interfaces)
        {
            if (ni.OperationalStatus == OperationalStatus.Up)
            {
                var ipv4Addrs = ni.Ipv4Addresses.Select(a => a.ToString()).ToList();

                if (ipv4Addrs.Count > 0)
                {
                    results.Add($"[正常] {ni.Name}：已连通，IP = {string.Join(", ", ipv4Addrs)}");
                }
                else
                {
                    results.Add($"[警告] {ni.Name}：已连通但没有 IPv4 地址！");
                }
            }
            else if (ni.OperationalStatus == OperationalStatus.Down)
            {
                results.Add($"[信息] {ni.Name}：网络未连接");
            }
        }

        return results;
    }

    public static bool IsIpInActiveSubnets(string ipAddress)
    {
        if (!IPAddress.TryParse(ipAddress, out var targetIp)) return false;
        if (targetIp.AddressFamily != AddressFamily.InterNetwork) return false;

        var targetBytes = targetIp.GetAddressBytes();

        var interfaces = CrossPlatformNetworkDetector.GetInterfaces()
            .Where(ni => IsUsableLanInterface(ni));

        foreach (var ni in interfaces)
        {
            var origNi = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Id == ni.Id);
            if (origNi == null) continue;

            foreach (var ip in origNi.GetIPProperties().UnicastAddresses)
            {
                if (ip.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    var localBytes = ip.Address.GetAddressBytes();

                    if (targetBytes[0] == 169 && targetBytes[1] == 254 &&
                        localBytes[0] == 169 && localBytes[1] == 254)
                    {
                        return true;
                    }

                    if (ip.IPv4Mask != null)
                    {
                        var maskBytes = ip.IPv4Mask.GetAddressBytes();
                        if (maskBytes.Length == 4 && localBytes.Length == 4 && !maskBytes.All(b => b == 0))
                        {
                            bool matches = true;
                            for (int i = 0; i < 4; i++)
                            {
                                if ((localBytes[i] & maskBytes[i]) != (targetBytes[i] & maskBytes[i]))
                                {
                                    matches = false;
                                    break;
                                }
                            }
                            if (matches) return true;
                        }
                    }
                }
            }
        }
        return false;
    }
}

