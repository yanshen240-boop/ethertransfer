using System;
using System.Text;
using System.Text.Json;
using EtherTransfer.Core.Models;
using NUnit.Framework;

namespace EtherTransfer.Tests;

[TestFixture]
public class UdpBroadcastProtocolTests
{
    [Test]
    public void Build_ThenTryParse_RoundTripsHeaderFieldsAndBody()
    {
        var transferId = Guid.NewGuid();
        var body = Encoding.UTF8.GetBytes("{\"name\":\"a.zip\"}");

        var datagram = UdpBroadcastProtocol.Build(UdpBroadcastPacketTypes.Data, transferId, 1234, 5678, body);

        Assert.That(datagram.Length, Is.EqualTo(UdpBroadcastProtocol.HeaderSize + body.Length));
        Assert.That(Encoding.ASCII.GetString(datagram, 0, 4), Is.EqualTo("UFT1"));

        Assert.That(UdpBroadcastProtocol.TryParse(datagram, out var parsed), Is.True);
        Assert.That(parsed.Kind, Is.EqualTo(UdpBroadcastPacketTypes.Data));
        Assert.That(parsed.TransferId, Is.EqualTo(transferId));
        Assert.That(parsed.Arg1, Is.EqualTo(1234u));
        Assert.That(parsed.Arg2, Is.EqualTo(5678u));
        Assert.That(Encoding.UTF8.GetString(parsed.Body), Is.EqualTo("{\"name\":\"a.zip\"}"));
    }

    [Test]
    public void TryParse_RejectsForeignPayloadAndShortDatagram()
    {
        Assert.That(UdpBroadcastProtocol.TryParse(new byte[10], out _), Is.False);
        Assert.That(UdpBroadcastProtocol.TryParse(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK"), out _), Is.False);
    }

    [Test]
    public void Metadata_UsesSnakeCaseKeysExpectedByTheReferenceImplementation()
    {
        var metadata = new UdpBroadcastMetadata
        {
            Name = "test.zip",
            Path = "folder/test.zip",
            Size = 2048,
            Total = 3,
            Sha256 = new string('a', 64),
            SenderName = "PC",
            Kind = "file"
        };

        var json = JsonSerializer.Serialize(metadata);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.That(root.GetProperty("name").GetString(), Is.EqualTo("test.zip"));
        Assert.That(root.GetProperty("chunk_size").GetInt32(), Is.EqualTo(UdpBroadcastProtocol.ChunkSize));
        Assert.That(root.GetProperty("total").GetInt32(), Is.EqualTo(3));
        Assert.That(root.GetProperty("sha256").GetString(), Is.EqualTo(new string('a', 64)));
        Assert.That(root.GetProperty("size").GetInt64(), Is.EqualTo(2048));
    }

    [Test]
    public void Metadata_ParsesStartBodyFromReferenceSender()
    {
        // 方案里的 Python 发送端只写 name/size/chunk_size/total/sha256，必须仍能被解析。
        const string reference = "{\"name\":\"测试.txt\",\"size\":1200,\"chunk_size\":900,\"total\":2," +
                                 "\"sha256\":\"" + "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef" + "\"}";

        var metadata = UdpBroadcastProtocol.ParseMetadata(Encoding.UTF8.GetBytes(reference));

        Assert.That(metadata, Is.Not.Null);
        Assert.That(metadata!.Name, Is.EqualTo("测试.txt"));
        Assert.That(metadata.Size, Is.EqualTo(1200));
        Assert.That(metadata.Total, Is.EqualTo(2));
        Assert.That(metadata.IsText, Is.False);
    }

    [Test]
    public void TotalChunks_AndChunkLength_MatchChunkSizeMath()
    {
        Assert.That(UdpBroadcastProtocol.TotalChunks(0), Is.EqualTo(0));
        Assert.That(UdpBroadcastProtocol.TotalChunks(1), Is.EqualTo(1));
        Assert.That(UdpBroadcastProtocol.TotalChunks(900), Is.EqualTo(1));
        Assert.That(UdpBroadcastProtocol.TotalChunks(901), Is.EqualTo(2));

        Assert.That(UdpBroadcastProtocol.ChunkLength(1200, 0), Is.EqualTo(900));
        Assert.That(UdpBroadcastProtocol.ChunkLength(1200, 1), Is.EqualTo(300));
        Assert.That(UdpBroadcastProtocol.ChunkLength(1200, 2), Is.EqualTo(0));
    }

    [Test]
    public void AckBitmap_CoversFullWindowIncludingLastPartialWindow()
    {
        // 32 位位图在整窗口时应为 0xFFFFFFFF，尾窗口按剩余分片数截断。
        const int count = 32;
        var fullMask = count >= UdpBroadcastProtocol.WindowSize ? uint.MaxValue : (1u << count) - 1;
        Assert.That(fullMask, Is.EqualTo(uint.MaxValue));

        const int tail = 5;
        var tailMask = tail >= UdpBroadcastProtocol.WindowSize ? uint.MaxValue : (1u << tail) - 1;
        Assert.That(tailMask, Is.EqualTo(0b11111u));
    }
}
