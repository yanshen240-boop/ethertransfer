using System;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EtherTransfer.Core.Models;

/// <summary>
/// AP 隔离网络下的 UDP 广播传输报文类型，与方案里的 T_START / T_READY / T_DATA / T_ACK / T_FIN / T_DONE / T_ERROR 一一对应。
/// </summary>
public static class UdpBroadcastPacketTypes
{
    public const byte Start = 1;
    public const byte Ready = 2;
    public const byte Data = 3;
    public const byte Ack = 4;
    public const byte Fin = 5;
    public const byte Done = 6;
    public const byte Error = 7;
}

public sealed class UdpBroadcastPacket
{
    public byte Kind { get; init; }
    public Guid TransferId { get; init; }

    /// <summary>DATA 为分片序号，ACK 为窗口基序号，READY/FIN 为分片总数。</summary>
    public uint Arg1 { get; init; }

    /// <summary>DATA 为分片总数，ACK 为窗口内 32 位确认位图。</summary>
    public uint Arg2 { get; init; }

    public byte[] Body { get; init; } = Array.Empty<byte>();
}

/// <summary>
/// START 报文携带的文件元数据。字段名保持方案里的 snake_case，额外的字段（path/root/sender/kind/mtime）
/// 对只认识 name/size/chunk_size/total/sha256 的实现是无害的。
/// </summary>
public sealed class UdpBroadcastMetadata
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
    [JsonPropertyName("root")] public string RootName { get; set; } = string.Empty;
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("chunk_size")] public int ChunkSize { get; set; } = UdpBroadcastProtocol.ChunkSize;
    [JsonPropertyName("total")] public int Total { get; set; }
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = string.Empty;
    [JsonPropertyName("sender")] public string SenderName { get; set; } = string.Empty;

    /// <summary>
    /// 一次发送动作的分批标识：广播通道按文件逐个协商 START，
    /// 带上批次号后接收端可以对同一批只询问用户一次，后续文件沿用已授权的目录。
    /// </summary>
    [JsonPropertyName("batch")] public string BatchId { get; set; } = string.Empty;

    [JsonPropertyName("batch_index")] public int BatchIndex { get; set; }
    [JsonPropertyName("batch_count")] public int BatchCount { get; set; }

    /// <summary>file = 普通文件；text = 纯文本消息（正文即 UTF-8 文本，复用同一套可靠通道）。</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = "file";

    [JsonPropertyName("mtime")] public long? LastWriteTimeUnixMs { get; set; }

    [JsonIgnore]
    public bool IsText => string.Equals(Kind, "text", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// UFT1 报文格式（与方案完全一致）：
/// <code>
/// 0        4  5                 21        25        29
/// +--------+--+-----------------+---------+---------+--------+
/// | "UFT1" |类型|   传输 ID(16B)  | arg1    | arg2    |  body  |
/// +--------+--+-----------------+---------+---------+--------+
/// </code>
/// 多字节整数一律大端；分片 900 字节、窗口 32 个分片、每 8 个新分片回一次累积位图 ACK。
/// </summary>
public static class UdpBroadcastProtocol
{
    public const int TransferPort = 39000;
    public const int ProbePort = 39001;

    /// <summary>分片大小：留足余量，确保单个数据报远低于常见 MTU。</summary>
    public const int ChunkSize = 900;

    /// <summary>发送窗口：ACK 位图正好 32 位。</summary>
    public const int WindowSize = 32;

    /// <summary>接收端每收到 8 个新分片回一次 ACK，避免 ACK 风暴。</summary>
    public const int AckEvery = 8;

    public const int MaxRetries = 20;
    public const int WindowTimeoutMs = 450;

    /// <summary>分片间节流间隔（毫秒），防止把 AP 的广播队列打爆。</summary>
    public static readonly double SendGapMs = 0.4;

    public const long MaxFileSize = 20L * 1024 * 1024 * 1024;

    public const int HeaderSize = 29;

    public const int MaxErrorBodyBytes = 800;

    /// <summary>START 重发间隔；对端可能要等用户确认接收（与界面的 120 秒确认超时对齐）。</summary>
    public const int StartRetryIntervalMs = 600;
    public const int StartHandshakeSeconds = 120;

    /// <summary>FIN 重发间隔与次数，与方案一致。</summary>
    public const int FinRetryIntervalMs = 800;
    public const int FinMaxAttempts = 20;

    private static readonly byte[] Magic = { (byte)'U', (byte)'F', (byte)'T', (byte)'1' };

    private static readonly JsonSerializerOptions MetadataJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static int TotalChunks(long size) => size <= 0 ? 0 : (int)((size + ChunkSize - 1) / ChunkSize);

    public static int ChunkLength(long size, int sequence)
    {
        var offset = (long)sequence * ChunkSize;
        var remaining = size - offset;
        if (remaining <= 0) return 0;
        return (int)Math.Min(ChunkSize, remaining);
    }

    public static byte[] Build(byte kind, Guid transferId, uint arg1 = 0, uint arg2 = 0, ReadOnlySpan<byte> body = default)
    {
        var buffer = new byte[HeaderSize + body.Length];
        Magic.CopyTo(buffer, 0);
        buffer[4] = kind;
        transferId.TryWriteBytes(buffer.AsSpan(5, 16));
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(21, 4), arg1);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(25, 4), arg2);
        if (body.Length > 0) body.CopyTo(buffer.AsSpan(HeaderSize));
        return buffer;
    }

    public static byte[] BuildStart(Guid transferId, UdpBroadcastMetadata metadata) =>
        Build(UdpBroadcastPacketTypes.Start, transferId, 0, 0,
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(metadata, MetadataJsonOptions)));

    public static byte[] BuildError(Guid transferId, string message)
    {
        var body = Encoding.UTF8.GetBytes(message);
        if (body.Length > MaxErrorBodyBytes) body = body.AsSpan(0, MaxErrorBodyBytes).ToArray();
        return Build(UdpBroadcastPacketTypes.Error, transferId, 0, 0, body);
    }

    public static bool TryParse(ReadOnlySpan<byte> datagram, out UdpBroadcastPacket packet)
    {
        packet = null!;
        if (datagram.Length < HeaderSize) return false;
        if (!datagram.Slice(0, 4).SequenceEqual(Magic)) return false;

        packet = new UdpBroadcastPacket
        {
            Kind = datagram[4],
            TransferId = new Guid(datagram.Slice(5, 16)),
            Arg1 = BinaryPrimitives.ReadUInt32BigEndian(datagram.Slice(21, 4)),
            Arg2 = BinaryPrimitives.ReadUInt32BigEndian(datagram.Slice(25, 4)),
            Body = datagram.Slice(HeaderSize).ToArray()
        };
        return true;
    }

    public static UdpBroadcastMetadata? ParseMetadata(ReadOnlySpan<byte> body)
    {
        if (body.Length == 0) return null;
        try
        {
            return JsonSerializer.Deserialize<UdpBroadcastMetadata>(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string DecodeError(ReadOnlySpan<byte> body) =>
        body.Length == 0 ? "对端报告未知错误。" : Encoding.UTF8.GetString(body);
}
