namespace EtherTransfer.Core.Models;

/// <summary>传输实际走的通道，用于日志与结果提示。</summary>
public static class TransferChannels
{
    public const string Tcp = "TCP 直连";
    public const string UdpBroadcast = "UDP 广播";
}

public class TransferResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public int TotalElements { get; set; }
    public int CompletedElementsCount => CompletedElementNames.Count;
    public List<string> CompletedElementNames { get; set; } = new();
    public List<string> AllElementNames { get; set; } = new();
    public List<string> FailedElementNames { get; set; } = new();

    /// <summary>实际使用的通道（TCP 直连 / UDP 广播）。</summary>
    public string Channel { get; set; } = TransferChannels.Tcp;

    /// <summary>
    /// 是否已与对端建立过连接。AP 隔离时 TCP 会在建连阶段全部超时，此时为 false，
    /// 上层据此决定是否改走 UDP 广播通道（已建连后的失败不重试，避免重复传输）。
    /// </summary>
    public bool Connected { get; set; }
}
