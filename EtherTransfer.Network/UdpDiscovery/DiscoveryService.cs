using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EtherTransfer.Core;
using EtherTransfer.Core.Models;
using EtherTransfer.Network.NetworkInterfaces;

namespace EtherTransfer.Network.UdpDiscovery;

public class PeerDiscoveredEventArgs : EventArgs
{
    public DiscoveryMessage Message { get; }
    public IPAddress SourceAddress { get; }

    public PeerDiscoveredEventArgs(DiscoveryMessage message, IPAddress sourceAddress)
    {
        Message = message;
        SourceAddress = sourceAddress;
    }
}

public class DiscoveryService : IDisposable
{
    private const int DiscoveryPort = 50000;
    private const string AppId = "EtherTransferApp-V1";

    private CancellationTokenSource? _cts;
    private UdpClient? _globalListener;
    private string _computerName = string.Empty;
    private int _tcpPort;
    private readonly string _deviceId;
    private readonly string _sessionId = Guid.NewGuid().ToString();
    private readonly NetworkConfig _config = NetworkConfig.Default;
    private long _sequenceNumber = 0;
    private int _isStopped = 0;

    public DiscoveryService()
    {
        var settings = SettingsManager.Load();
        if (string.IsNullOrWhiteSpace(settings.DeviceId))
        {
            settings.DeviceId = Guid.NewGuid().ToString("N");
            SettingsManager.Save(settings);
        }
        _deviceId = settings.DeviceId;
    }

    public event EventHandler<PeerDiscoveredEventArgs>? PeerDiscovered;
    public event EventHandler<PeerDiscoveredEventArgs>? TransferCancelReceived;

    public event EventHandler<StructuredLogMessage>? DebugLog;

    private void Log(string msg, LogLevel level = LogLevel.Info, string eventId = "discovery.log")
    {
        DebugLog?.Invoke(this, new StructuredLogMessage(eventId, msg, level));
    }

    public async Task StartAsync(string computerName, int tcpPort, bool isRebind = false)
    {
        Stop(sendBye: false);
        _isStopped = 0;

        _computerName = computerName;
        _tcpPort = tcpPort;
        _cts = new CancellationTokenSource();

        try
        {
            _globalListener = new UdpClient();
            _globalListener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _globalListener.Client.Bind(new IPEndPoint(IPAddress.Any, _config.DiscoveryPort));

            if (!isRebind)
            {
                Log($"以“{computerName}”身份在端口 {_config.DiscoveryPort} 启动设备发现");
                Log("监听器已绑定到 0.0.0.0:" + _config.DiscoveryPort);
            }

            _ = Task.Run(() => ListenAsync(_globalListener, _cts.Token));
        }
        catch (Exception ex)
        {
            Log($"监听器绑定失败：{ex.Message}", LogLevel.Error, "discovery.bind.error");
            throw;
        }

        _ = Task.Run(() => BroadcastLoopAsync(tcpPort, _cts.Token));
    }

    public void UpdateComputerName(string newName)
    {
        _computerName = newName;
        Log($"设备发现名称已更新为“{newName}”");
    }

    /// <summary>
    /// 立即补发一轮 HELLO（点“刷新”时用）：让对端马上把最新状态回灌到设备列表，
    /// 不必等下一轮周期广播（默认 2 秒一轮，手机省电时可能更慢）。
    /// </summary>
    public async Task AnnounceNowAsync(int burst = 2)
    {
        var cts = _cts;
        if (cts == null || cts.IsCancellationRequested || _tcpPort <= 0) return;

        try
        {
            var ethInterfaces = NetworkHelper.GetLanInterfaces().ToList();
            if (ethInterfaces.Count == 0) return;

            for (int b = 0; b < burst; b++)
            {
                var seq = Interlocked.Increment(ref _sequenceNumber);
                var message = new DiscoveryMessage
                {
                    Type = "HELLO",
                    ComputerName = _computerName,
                    TcpPort = _tcpPort,
                    Id = AppId,
                    SessionId = _sessionId,
                    DeviceId = _deviceId,
                    OS = GetCurrentOS(),
                    SequenceNumber = seq
                };

                foreach (var netIf in ethInterfaces)
                {
                    if (cts.IsCancellationRequested) return;
                    try
                    {
                        ApplyLink(message, netIf);
                        var packet = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
                        using var sender = new UdpClient(netIf.LocalAddress.AddressFamily);
                        sender.Client.Bind(new IPEndPoint(netIf.LocalAddress, 0));
                        if (netIf.LocalAddress.AddressFamily == AddressFamily.InterNetwork)
                            sender.EnableBroadcast = true;
                        await sender.SendAsync(packet, packet.Length,
                            new IPEndPoint(netIf.BroadcastAddress, _config.DiscoveryPort)).ConfigureAwait(false);
                    }
                    catch { }
                }

                if (b < burst - 1) await Task.Delay(120, cts.Token).ConfigureAwait(false);
            }

            Log("已按请求补发发现广播（立即刷新设备列表）", LogLevel.Info, "discovery.announce");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log($"补发发现广播失败：{ex.Message}", LogLevel.Warning, "discovery.announce.failed");
        }
    }

    public void Stop(bool sendBye = false)
    {

        _cts?.Cancel();

        if (sendBye)
        {
            if (Interlocked.Exchange(ref _isStopped, 1) == 0)
            {
                SendBye();
            }
        }

        try
        {
            _globalListener?.Dispose();
            _globalListener = null;
        }
        catch { }
    }

    private static void ApplyLink(DiscoveryMessage message, InterfaceAddressInfo netIf)
    {
        // 每条报文由单独一块网卡发出，因此这里写入的是该链路的权威身份，
        // 接收端不需要再用“子网包含”去猜。旧客户端会忽略这些字段。
        message.Link = netIf.IsWifi ? "Wifi" : "Wired";
        message.LocalAddress = netIf.LocalAddress.ToString();
        message.LocalInterfaceId = netIf.InterfaceId;
    }

    private void SendBye()
    {
        try
        {
            var seq = Interlocked.Increment(ref _sequenceNumber);
            var message = new DiscoveryMessage
            {
                Type = "BYE",
                ComputerName = _computerName,
                TcpPort = 0,
                Id = AppId,
                SessionId = _sessionId,
                DeviceId = _deviceId,
                OS = GetCurrentOS(),
                SequenceNumber = seq
            };
            var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));

            List<InterfaceAddressInfo> ethInterfaces = new List<InterfaceAddressInfo>();
            try
            {
                ethInterfaces = NetworkHelper.GetLanInterfaces().ToList();
            }
            catch (Exception ex)
            {
                Log($"发送下线广播时获取网卡信息失败：{ex.Message}", LogLevel.Warning, "discovery.sendbye.error");
            }

            for (int burst = 0; burst < 3; burst++)
            {
                foreach (var netIf in ethInterfaces)
                {
                    try
                    {
                        ApplyLink(message, netIf);
                        var packet = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
                        using var sender = new UdpClient(netIf.LocalAddress.AddressFamily);
                        sender.Client.Bind(new IPEndPoint(netIf.LocalAddress, 0));
                        if (netIf.LocalAddress.AddressFamily == AddressFamily.InterNetwork)
                        {
                            sender.EnableBroadcast = true;
                        }
                        var target = new IPEndPoint(netIf.BroadcastAddress, _config.DiscoveryPort);
                        sender.Send(packet, packet.Length, target);
                    }
                    catch { }
                }

                if (burst < 2) Thread.Sleep(20);
            }
        }
        catch { }
    }

    private async Task BroadcastLoopAsync(int tcpPort, CancellationToken ct)
    {
        try
        {
            int loopCount = 0;
            while (!ct.IsCancellationRequested)
            {

                var ethInterfaces = NetworkHelper.GetLanInterfaces().ToList();

                if (ethInterfaces.Count == 0)
                {
                    await Task.Delay(2000, ct);
                    continue;
                }

                var seq = Interlocked.Increment(ref _sequenceNumber);
                var message = new DiscoveryMessage
                {
                    Type = "HELLO",
                    ComputerName = _computerName,
                    TcpPort = tcpPort,
                    Id = AppId,
                    SessionId = _sessionId,
                DeviceId = _deviceId,
                    OS = GetCurrentOS(),
                    SequenceNumber = seq
                };

                foreach (var netIf in ethInterfaces)
                {
                    if (ct.IsCancellationRequested) break;

                    try
                    {
                        ApplyLink(message, netIf);
                        var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
                        using var sender = new UdpClient(netIf.LocalAddress.AddressFamily);
                        sender.Client.Bind(new IPEndPoint(netIf.LocalAddress, 0));
                        if (netIf.LocalAddress.AddressFamily == AddressFamily.InterNetwork)
                        {
                            sender.EnableBroadcast = true;
                        }
                        var target = new IPEndPoint(netIf.BroadcastAddress, _config.DiscoveryPort);
                        await sender.SendAsync(payload, payload.Length, target);
                    }
                    catch (SocketException sockEx) when (sockEx.SocketErrorCode == SocketError.AddressNotAvailable)
                    {

                    }
                    catch (Exception ex)
                    {
                        Log($"{netIf.LocalAddress} 发送失败：{ex.Message}", LogLevel.Warning, "discovery.send.failed");
                    }
                }

                loopCount++;

                int delayMs;
                if (loopCount <= 2)
                    delayMs = 250;
                else if (loopCount <= 4)
                    delayMs = 500;
                else if (loopCount <= 6)
                    delayMs = 1000;
                else
                    delayMs = _config.BroadcastIntervalMs;

                await Task.Delay(delayMs, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ListenAsync(UdpClient listener, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await listener.ReceiveAsync(ct);
                var json = Encoding.UTF8.GetString(result.Buffer);

                try
                {
                    var message = JsonSerializer.Deserialize<DiscoveryMessage>(json);

                    if (message != null && message.Id == AppId &&
                        !string.IsNullOrWhiteSpace(message.SessionId) && message.SessionId.Length <= 128 &&
                        !string.IsNullOrWhiteSpace(message.ComputerName) && message.ComputerName.Length <= 256 &&
                        (message.Type == "BYE" || message.Type == "TRANSFER_CANCEL" ||
                         message.TcpPort is > 0 and <= 65535))
                    {
                        if (message.SessionId != _sessionId)
                        {
                            var sourceIpStr = result.RemoteEndPoint.Address.ToString();
                            if (NetworkHelper.IsIpInActiveSubnets(sourceIpStr))
                            {
                                if (message.Type == "TRANSFER_CANCEL")
                                {
                                    TransferCancelReceived?.Invoke(this, new PeerDiscoveredEventArgs(message, result.RemoteEndPoint.Address));
                                }
                                else if (message.Type == "HELLO" || message.Type == "HELLO_ACK" || message.Type == "BYE")
                                {
                                    PeerDiscovered?.Invoke(this, new PeerDiscoveredEventArgs(message, result.RemoteEndPoint.Address));

                                    if (message.Type == "HELLO")
                                    {
                                        _ = SendDirectHelloAckAsync(result.RemoteEndPoint.Address);
                                    }
                                }
                            }
                        }
                    }
                }
                catch (JsonException)
                {

                }
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException ex) { Log($"套接字错误：{ex.Message}", LogLevel.Error, "discovery.listen.error"); }
        catch (ObjectDisposedException) { }
    }

    public async Task SendTransferCancelAsync(IPAddress targetAddress)
    {
        try
        {
            var msg = new DiscoveryMessage
            {
                Type = "TRANSFER_CANCEL",
                ComputerName = _computerName,
                TcpPort = _tcpPort,
                Id = AppId,
                SessionId = _sessionId,
                DeviceId = _deviceId,
                OS = GetCurrentOS()
            };

            var ethInterfaces = NetworkHelper.GetLanInterfaces().ToList();
            if (ethInterfaces.Count == 0) return;

            foreach (var netIf in ethInterfaces)
            {
                try
                {
                    ApplyLink(msg, netIf);
                    var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(msg));
                    using var sender = new UdpClient(netIf.LocalAddress.AddressFamily);
                    sender.EnableBroadcast = true;
                    sender.Client.Bind(new IPEndPoint(netIf.LocalAddress, 0));
                    await sender.SendAsync(payload, payload.Length, new IPEndPoint(targetAddress, _config.DiscoveryPort));
                    await sender.SendAsync(payload, payload.Length, new IPEndPoint(netIf.BroadcastAddress, _config.DiscoveryPort));
                }
                catch { }
            }
        }
        catch { }
    }

    private async Task SendDirectHelloAckAsync(IPAddress targetAddress)
    {
        try
        {
            var seq = Interlocked.Increment(ref _sequenceNumber);
            var reply = new DiscoveryMessage
            {
                Type = "HELLO_ACK",
                ComputerName = _computerName,
                TcpPort = _tcpPort,
                Id = AppId,
                SessionId = _sessionId,
                DeviceId = _deviceId,
                OS = GetCurrentOS(),
                SequenceNumber = seq
            };

            var ethInterfaces = NetworkHelper.GetLanInterfaces().Where(i => i.Contains(targetAddress)).ToList();
            if (ethInterfaces.Count == 0) return;

            foreach (var netIf in ethInterfaces)
            {
                try
                {
                    ApplyLink(reply, netIf);
                    var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(reply));
                    using var responder = new UdpClient(netIf.LocalAddress.AddressFamily);
                    responder.EnableBroadcast = true;
                    responder.Client.Bind(new IPEndPoint(netIf.LocalAddress, 0));
                    await responder.SendAsync(payload, payload.Length, new IPEndPoint(targetAddress, _config.DiscoveryPort));
                }
                catch { }
            }
        }
        catch { }
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            Stop(sendBye: true);
            _cts?.Dispose();
            _globalListener?.Dispose();
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private static string GetCurrentOS()
    {
        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows)) return "Windows";
        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX)) return "macOS";
        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux)) return "Linux";
        return "Unknown";
    }
}

