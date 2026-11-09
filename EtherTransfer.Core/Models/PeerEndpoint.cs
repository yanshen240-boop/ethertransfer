namespace EtherTransfer.Core.Models;

/// <summary>
/// 发送端使用的一条“链路端点”：对端地址 + 该链路对应的本机源地址。
/// LocalAddress 决定 TCP 绑定在哪块网卡上，从而决定报文从哪条链路出去。
/// 双链路（有线 + 无线）环境下必须按端点绑定，否则会走错网卡形成静默黑洞。
/// </summary>
public class PeerEndpoint
{
    public string DeviceId { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>对端在这条链路上的 IPv4 地址。</summary>
    public string Address { get; set; } = string.Empty;

    public int Port { get; set; } = 55000;

    /// <summary>这条链路是否为有线（以太网）。</summary>
    public bool IsWired { get; set; }

    /// <summary>本机在这条链路上的源地址；为空表示交给系统选路（单链路场景）。</summary>
    public string LocalAddress { get; set; } = string.Empty;

    public string LocalInterfaceId { get; set; } = string.Empty;

    public DateTime LastSeen { get; set; }

    public string LinkLabel => IsWired ? "有线" : "Wi-Fi";

    public override string ToString()
    {
        var source = string.IsNullOrWhiteSpace(LocalAddress) ? string.Empty : $"（本机 {LocalAddress}）";
        return $"{LinkLabel} {Address}:{Port}{source}";
    }
}
