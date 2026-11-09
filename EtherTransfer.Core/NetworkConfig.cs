using System;

namespace EtherTransfer.Core;

public class NetworkConfig
{
    public int DiscoveryPort { get; set; } = 50000;
    public int BroadcastIntervalMs { get; set; } = 2000;

    /// <summary>
    /// AP 隔离网络下的 UDP 广播传输端口（文件/文本）与双向广播测试端口。
    /// 与方案保持一致：39000 传文件，39001 只做连通性测试。
    /// </summary>
    public int UdpTransferPort { get; set; } = 39000;
    public int UdpProbePort { get; set; } = 39001;

    /// <summary>TCP 建连全部失败时是否自动改用 UDP 广播通道。</summary>
    public bool UdpBroadcastFallbackEnabled { get; set; } = true;

    public TimeSpan PeerStaleThreshold { get; set; } = TimeSpan.FromSeconds(45);

    public TimeSpan ConfigRetryCooldown { get; set; } = TimeSpan.FromSeconds(15);

    public TimeSpan IPWaitLoopDelay { get; set; } = TimeSpan.FromMilliseconds(500);
    public int IPWaitLoopMaxAttempts { get; set; } = 12;

    public int ProcessTimeoutMs { get; set; } = 10000;

    public static NetworkConfig Default { get; } = new NetworkConfig();
}

