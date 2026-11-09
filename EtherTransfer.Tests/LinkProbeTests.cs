using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using EtherTransfer.Core.Models;
using EtherTransfer.Transfer;
using NUnit.Framework;

namespace EtherTransfer.Tests;

/// <summary>
/// 设备互通测试（设备列表红绿灯）走的轻量探测通道：
/// 一次建连 + 一条 IsProbe 文本 + 一个回执，不建会话、不做多轮重试。
/// 这里锁定三件事：通的判通、不通的给原因、对端“忙”也要判通（能应答就是链路没断）。
/// </summary>
[TestFixture]
public class LinkProbeTests
{
    private static (TcpListener listener, int port) StartListener()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return (listener, ((IPEndPoint)listener.LocalEndpoint).Port);
    }

    private static PeerEndpoint Loopback(int port) => new() { Address = "127.0.0.1", Port = port, IsWired = true };

    [Test]
    public async Task Probe_LivePeer_IsReachable_AndCarriesProbeFlag()
    {
        var (listener, port) = StartListener();
        TextMessageRequest? seen = null;

        var receiver = new TransferReceiver
        {
            OnIncomingText = (msg, ct) =>
            {
                seen = msg;
                return Task.FromResult((true, string.Empty));
            }
        };

        var receiverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            return await receiver.HandleClientAsync(client, CancellationToken.None);
        });

        try
        {
            var outcome = await new TransferSender().ProbeAsync(
                Loopback(port), "测试主机", 1500, 3000, wakePeer: false, CancellationToken.None);

            Assert.That(outcome.Ok, Is.True, outcome.Reason);
            Assert.That(seen, Is.Not.Null);
            Assert.That(seen!.IsProbe, Is.True, "探测报文必须带 IsProbe，接收端才不会把它显示成聊天消息");
            Assert.That(seen.Text, Is.EqualTo("__LINK_PROBE__"));
        }
        finally
        {
            listener.Stop();
            try { await receiverTask; } catch { }
        }
    }

    [Test]
    public async Task Probe_BusyPeer_StillCountsAsReachable()
    {
        var (listener, port) = StartListener();
        var receiver = new TransferReceiver
        {
            // 模拟对端正在传输：回“忙”说明 TCP 建连成功，链路本身是通的
            OnIncomingText = (_, _) => Task.FromResult((false, "设备正在处理另一项传输，请稍后重试。"))
        };

        var receiverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            return await receiver.HandleClientAsync(client, CancellationToken.None);
        });

        try
        {
            var outcome = await new TransferSender().ProbeAsync(
                Loopback(port), "测试主机", 1500, 3000, wakePeer: false, CancellationToken.None);

            Assert.That(outcome.Ok, Is.True, outcome.Reason);
        }
        finally
        {
            listener.Stop();
            try { await receiverTask; } catch { }
        }
    }

    [Test]
    public async Task Probe_ClosedPort_FailsWithReason()
    {
        int port;
        using (var temp = new TcpListener(IPAddress.Loopback, 0))
        {
            temp.Start();
            port = ((IPEndPoint)temp.LocalEndpoint).Port;
        }

        var outcome = await new TransferSender().ProbeAsync(
            Loopback(port), "测试主机", 800, 1500, wakePeer: false, CancellationToken.None);

        Assert.That(outcome.Ok, Is.False);
        Assert.That(outcome.Reason, Is.Not.Empty);
        Assert.That(outcome.ElapsedMs, Is.GreaterThanOrEqualTo(0));
    }
}
