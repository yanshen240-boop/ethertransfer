using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;
using EtherTransfer.Core.Models;
using EtherTransfer.Network.NetworkInterfaces;
using EtherTransfer.Services.DeviceManager;

namespace EtherTransfer.Tests;

public class LanNetworkTests
{
    [TestCase(true, false, true, false, OperationalStatus.Up, true)]
    [TestCase(false, true, true, false, OperationalStatus.Up, true)]
    [TestCase(false, true, true, true, OperationalStatus.Up, false)]
    [TestCase(true, false, false, true, OperationalStatus.Up, false)]
    [TestCase(false, true, true, false, OperationalStatus.Down, false)]
    [TestCase(false, false, true, false, OperationalStatus.Up, false)]
    public void LanSelectionIncludesWifiButExcludesVirtualAndDownAdapters(
        bool ethernet, bool wifi, bool physical, bool virtualAdapter, OperationalStatus status, bool expected)
    {
        var info = new NetworkInterfaceInfo("test", "test", "test", NetworkInterfaceType.Ethernet,
            status, physical, ethernet, wifi, virtualAdapter, [], []);
        Assert.That(NetworkHelper.IsUsableLanInterface(info), Is.EqualTo(expected));
    }

    [TestCase("10.12.99.5", true)]
    [TestCase("10.13.1.5", false)]
    [TestCase("::1", false)]
    public void RoutingUsesRealMaskInsteadOfAssuming24BitSubnets(string target, bool expected)
    {
        var address = new InterfaceAddressInfo(IPAddress.Parse("10.12.1.2"), IPAddress.Parse("10.12.255.255"),
            IPAddress.Parse("255.255.0.0"), true);
        Assert.That(address.Contains(IPAddress.Parse(target)), Is.EqualTo(expected));
    }

    [Test]
    public void SameSessionPrefersWiredAndDifferentDevicesWithSameNameStaySeparate()    {
        using var service = new DeviceService();
        var field = typeof(DeviceService).GetField("_devices", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var devices = (ConcurrentDictionary<string, DiscoveredDevice>)field.GetValue(service)!;
        devices["a|wired"] = new() { SessionId = "a", DeviceId = "device-a", Name = "电脑", Address = "169.254.1.2", IsWired = true, LastSeen = DateTime.UtcNow.AddSeconds(-1) };
        devices["a|wifi"] = new() { SessionId = "a", DeviceId = "device-a", Name = "电脑", Address = "192.168.1.2", LastSeen = DateTime.UtcNow };
        devices["b|wifi"] = new() { SessionId = "b", DeviceId = "device-b", Name = "电脑", Address = "192.168.1.3", LastSeen = DateTime.UtcNow };
        Assert.That(service.GetActiveDevices().Count(), Is.EqualTo(2));
        Assert.That(service.GetActiveDevices().Single(d => d.SessionId == "a").IsWired, Is.True);
        devices.TryRemove("a|wired", out _);
        Assert.That(service.GetActiveDevices().Single(d => d.SessionId == "a").Address, Is.EqualTo("192.168.1.2"));
    }

    [Test]
    public void ResolvedLocalSourceIsAlwaysOneOfOurOwnInterfaces()
    {
        // 回归：曾把“对端地址”当成“本机绑定地址”，导致 TCP 绑定失败后静默退回系统选路，
        // 于是“按链路绑定网卡”形同失效。
        foreach (var iface in NetworkHelper.GetLanInterfaces())
        {
            var bytes = iface.LocalAddress.GetAddressBytes();
            bytes[3] = (byte)(bytes[3] ^ 0x7B);
            var peer = new IPAddress(bytes).ToString();

            var (local, _) = NetworkHelper.ResolveLocalSourceFor(peer, !iface.IsWifi);

            if (string.IsNullOrEmpty(local)) continue;

            Assert.That(local, Is.Not.EqualTo(peer), "本机源地址不能等于对端地址");
            Assert.That(
                NetworkHelper.GetLanInterfaces().Any(i => i.LocalAddress.ToString() == local),
                Is.True,
                $"解析出的源地址 {local} 必须是本机某块网卡上的地址");
        }
    }
}
