using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using EtherTransfer.Core.Models;
using EtherTransfer.Network.UdpTransfer;
using EtherTransfer.Transfer;
using NUnit.Framework;

namespace EtherTransfer.Tests;

/// <summary>
/// 广播通道的端到端自测：本机发出的广播不会被自己收到，所以这里用回环单播跑同一套协议状态机，
/// 验证「分片窗口 + 位图 ACK + 重传 + SHA-256 + .part 改名」这条链路本身正确。
/// </summary>
[TestFixture]
public class UdpBroadcastTransferTests
{
    private static int GetFreeUdpPort()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    private static byte[] MakePayload(int length, int seed)
    {
        var payload = new byte[length];
        new Random(seed).NextBytes(payload);
        return payload;
    }

    private static UdpBroadcastMetadata MetadataFor(byte[] payload, string name) => new()
    {
        Name = name,
        Path = name,
        RootName = name,
        Size = payload.Length,
        Total = UdpBroadcastProtocol.TotalChunks(payload.Length),
        Sha256 = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
        SenderName = "测试机",
        Kind = "file",
        BatchId = "batch-1",
        BatchCount = 1
    };

    [Test]
    public async Task Receiver_AssemblesFileAndMovesPartFileIntoPlace()
    {
        var directory = Path.Combine(Path.GetTempPath(), "et-udp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        var port = GetFreeUdpPort();
        using var receiver = new UdpBroadcastReceiver(port);
        receiver.OnIncomingTransfer = (_, _) =>
            Task.FromResult((true, directory, CancellationToken.None));

        Assert.That(receiver.Start(), Is.True);

        var payload = MakePayload(5000, 11);
        var metadata = MetadataFor(payload, "sample.bin");
        var transferId = Guid.NewGuid();
        var target = new IPEndPoint(IPAddress.Loopback, port);

        using var client = new UdpClient(AddressFamily.InterNetwork);
        await client.SendAsync(UdpBroadcastProtocol.BuildStart(transferId, metadata), target);

        // START 与第一片之间留一点时间，让接收端先把 .part 建好（握手是单向的）。
        await Task.Delay(200);

        for (int sequence = 0; sequence < metadata.Total; sequence++)
        {
            var offset = sequence * UdpBroadcastProtocol.ChunkSize;
            var length = UdpBroadcastProtocol.ChunkLength(payload.Length, sequence);
            var packet = UdpBroadcastProtocol.Build(
                UdpBroadcastPacketTypes.Data, transferId, (uint)sequence, (uint)metadata.Total,
                payload.AsSpan(offset, length));
            await client.SendAsync(packet, target);
            await Task.Delay(5);
        }

        await client.SendAsync(
            UdpBroadcastProtocol.Build(UdpBroadcastPacketTypes.Fin, transferId, (uint)metadata.Total, 0), target);

        var finalPath = Path.Combine(directory, "sample.bin");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!File.Exists(finalPath) && DateTime.UtcNow < deadline) await Task.Delay(50);

        Assert.That(File.Exists(finalPath), Is.True, "接收端应把 .part 改名为最终文件");
        Assert.That(File.ReadAllBytes(finalPath), Is.EqualTo(payload));

        var leftovers = Directory.GetFiles(directory, "*.part");
        Assert.That(leftovers, Is.Empty);

        Directory.Delete(directory, recursive: true);
    }

    [Test]
    public async Task Sender_CompletesHandshakeWindowsAndDoneAgainstLoopbackPeer()
    {
        var directory = Path.Combine(Path.GetTempPath(), "et-udp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var filePath = Path.Combine(directory, "payload.bin");
        var payload = MakePayload(4200, 23);
        await File.WriteAllBytesAsync(filePath, payload);

        var port = GetFreeUdpPort();
        var assembled = new ConcurrentDictionary<int, byte[]>();
        var dataPackets = 0;
        var totalChunks = UdpBroadcastProtocol.TotalChunks(payload.Length);
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
        var peerLoop = Task.Run(async () =>
        {
            while (!done.Task.IsCompleted)
            {
                UdpReceiveResult result;
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
                    result = await peer.ReceiveAsync(timeout.Token);
                }
                catch (OperationCanceledException) { continue; }
                catch (ObjectDisposedException) { break; }

                if (!UdpBroadcastProtocol.TryParse(result.Buffer, out var packet)) continue;

                switch (packet.Kind)
                {
                    case UdpBroadcastPacketTypes.Start:
                        await peer.SendAsync(
                            UdpBroadcastProtocol.Build(UdpBroadcastPacketTypes.Ready, packet.TransferId, (uint)totalChunks, 0),
                            result.RemoteEndPoint);
                        break;

                    case UdpBroadcastPacketTypes.Data:
                        var received = Interlocked.Increment(ref dataPackets);
                        assembled[(int)packet.Arg1] = packet.Body;

                        // 首轮 ACK 全部丢掉，验证发送端会按窗口超时重传。
                        if (received <= totalChunks) break;

                        var windowBase = (int)packet.Arg1 / UdpBroadcastProtocol.WindowSize * UdpBroadcastProtocol.WindowSize;
                        var width = Math.Min(UdpBroadcastProtocol.WindowSize, totalChunks - windowBase);
                        var mask = (uint)((1L << width) - 1);
                        await peer.SendAsync(
                            UdpBroadcastProtocol.Build(UdpBroadcastPacketTypes.Ack, packet.TransferId, (uint)windowBase, mask),
                            result.RemoteEndPoint);
                        break;

                    case UdpBroadcastPacketTypes.Fin:
                        await peer.SendAsync(
                            UdpBroadcastProtocol.Build(UdpBroadcastPacketTypes.Done, packet.TransferId),
                            result.RemoteEndPoint);
                        done.TrySetResult(true);
                        break;
                }
            }
        });

        var session = new TransferSession();
        session.PayloadFileCount = 1;
        session.AddFiles(new[]
        {
            new FileSelectionItem
            {
                AbsolutePath = filePath,
                RelativePath = "payload.bin",
                RootName = "payload.bin",
                Size = payload.Length
            }
        });

        using var channel = UdpBroadcastChannel.CreateDiagnostic(port, IPAddress.Loopback);
        var sender = new UdpBroadcastSender();
        var transfer = await sender.TransmitSessionAsync(session, "测试机", CancellationToken.None, channel);

        Assert.That(transfer.Success, Is.True, transfer.ErrorMessage);
        Assert.That(transfer.Channel, Is.EqualTo(TransferChannels.UdpBroadcast));
        Assert.That(await done.Task.WaitAsync(TimeSpan.FromSeconds(5)), Is.True);

        // 丢掉第一个 ACK 后，整窗口应被重发，因此收到的 DATA 多于分片数。
        Assert.That(dataPackets, Is.GreaterThan(totalChunks));

        var rebuilt = new byte[payload.Length];
        foreach (var entry in assembled)
        {
            var offset = entry.Key * UdpBroadcastProtocol.ChunkSize;
            entry.Value.CopyTo(rebuilt, offset);
        }
        Assert.That(rebuilt, Is.EqualTo(payload));

        done.TrySetResult(true);
        await peerLoop;
        Directory.Delete(directory, recursive: true);
    }
}
