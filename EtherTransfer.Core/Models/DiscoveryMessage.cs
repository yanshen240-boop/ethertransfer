namespace EtherTransfer.Core.Models;

public class DiscoveryMessage
{
    public string Type { get; set; } = "HELLO";
    public string Id { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string ComputerName { get; set; } = string.Empty;
    public int TcpPort { get; set; }
    public string OS { get; set; } = string.Empty;
    public long SequenceNumber { get; set; }

    /// <summary>
    /// 发出这条报文的本机链路："Wired" / "Wifi"。
    /// 每条 HELLO 都由单独一块网卡发出，因此该字段是链路的权威来源，
    /// 不再用“子网是否包含”去猜（APIPA 是 /16，会把任意 169.254.x 都判成有线）。
    /// 旧版客户端不带该字段，接收端退回子网推断。
    /// </summary>
    public string Link { get; set; } = string.Empty;

    /// <summary>发出这条报文的本机 IPv4 地址。</summary>
    public string LocalAddress { get; set; } = string.Empty;

    /// <summary>发出这条报文的本机网卡 Id。</summary>
    public string LocalInterfaceId { get; set; } = string.Empty;
}
