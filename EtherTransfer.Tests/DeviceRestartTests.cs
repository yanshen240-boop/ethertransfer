using System.Reflection;
using System.Net;
using EtherTransfer.Core.Models;
using EtherTransfer.Network.UdpDiscovery;
using EtherTransfer.Services.DeviceManager;

namespace EtherTransfer.Tests;
public class DeviceRestartTests
{
    private static void Announce(DeviceService service,string device,string session,string ip,int port,long sequence,string type="HELLO")
    {
        var message=new DiscoveryMessage { DeviceId=device,SessionId=session,ComputerName="同名电脑",TcpPort=port,SequenceNumber=sequence,Type=type };
        typeof(DeviceService).GetMethod("ApplyAnnouncement",BindingFlags.Instance|BindingFlags.NonPublic)!
            .Invoke(service,[new PeerDiscoveredEventArgs(message,IPAddress.Parse(ip))]);
    }
    [Test]
    public void RestartReplacesOldSessionAndOldByeCannotRemoveReplacement()
    {
        using var service=new DeviceService();
        Announce(service,"laptop","old","10.0.0.2",55000,10);
        Announce(service,"laptop","old","169.254.1.2",55000,11);
        Announce(service,"laptop","new","10.0.0.2",55001,1);
        Announce(service,"laptop","old","10.0.0.2",0,12,"BYE");
        Announce(service,"laptop","old","169.254.1.2",55000,99);
        var device=service.GetActiveDevices().Single();
        Assert.That(device.SessionId,Is.EqualTo("new"));
        Assert.That(device.Port,Is.EqualTo(55001));
    }
    [Test]
    public void ThreeDevicesRemainDistinctEvenWhenNamesMatch()
    {
        using var service=new DeviceService();
        for(int i=1;i<=3;i++)Announce(service,"device"+i,"session"+i,"10.0.0."+i,55000,1);
        Announce(service,"device2","restart2","10.0.0.2",55123,1);
        Assert.That(service.GetActiveDevices().Count(),Is.EqualTo(3));
        Assert.That(service.GetActiveDevices().Single(d=>d.DeviceId=="device2").Port,Is.EqualTo(55123));
    }
    [Test]
    public void LegacyRestartOnSameAddressDoesNotDisplayDuplicates()
    {
        using var service=new DeviceService();
        Announce(service,"","old","10.0.0.2",55000,10);
        Announce(service,"","new","10.0.0.2",55100,1);
        Announce(service,"","old","10.0.0.2",0,11,"BYE");
        Assert.That(service.GetActiveDevices().Single().Port,Is.EqualTo(55100));
    }
    [Test]
    public void LegacyDualInterfacePeerStillAppearsOnceAndUpgradeReplacesBothAddresses()
    {
        using var service=new DeviceService();
        Announce(service,"","legacy","10.0.0.2",55000,1);
        Announce(service,"","legacy","169.254.1.2",55000,2);
        Assert.That(service.GetActiveDevices().Count(),Is.EqualTo(1));
        Announce(service,"stable","upgraded","10.0.0.2",55001,1);
        Assert.That(service.GetActiveDevices().Single().SessionId,Is.EqualTo("upgraded"));
        Announce(service,"","legacy","169.254.1.2",55000,3);
        Assert.That(service.GetActiveDevices().Single().SessionId,Is.EqualTo("upgraded"));
    }
}
