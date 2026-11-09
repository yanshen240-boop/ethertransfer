using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EtherTransfer.Core.Models;

/// <summary>设备连通性状态：由启动后的静默探测更新，驱动设备列表的红绿灯。</summary>
public enum DeviceConnectivity
{
    /// <summary>尚未探测（灰色）。</summary>
    Unknown,
    /// <summary>探测连通（绿灯）。</summary>
    Ok,
    /// <summary>探测失败（红灯）。</summary>
    Failed
}

public class DiscoveredDevice : INotifyPropertyChanged
{
    public string DeviceId { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public bool IsWired { get; set; }
    public string ConnectionLabel => IsWired ? "有线" : "Wi-Fi";

    /// <summary>该端点被发现时，对端声明 / 推断出的链路类型："Wired" / "Wifi" / 空。</summary>
    public string Link { get; set; } = string.Empty;

    /// <summary>该链路上对端使用的本机地址；发送时据此绑定源地址。</summary>
    public string LocalAddress { get; set; } = string.Empty;

    /// <summary>该链路对应的本机网卡 Id。</summary>
    public string LocalInterfaceId { get; set; } = string.Empty;

    public int Port { get; set; } = 55000;
    public string OS { get; set; } = string.Empty;
    public DateTime LastSeen { get; set; }

    private DeviceConnectivity _connectivity = DeviceConnectivity.Unknown;

    /// <summary>连通性探测结果（红绿灯）。设备实例在列表刷新时会整体重建，探测缓存由界面层回填。</summary>
    public DeviceConnectivity Connectivity
    {
        get => _connectivity;
        set
        {
            if (_connectivity == value) return;
            _connectivity = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsConnectivityOk));
            OnPropertyChanged(nameof(IsConnectivityFailed));
            OnPropertyChanged(nameof(IsConnectivityUnknown));
            OnPropertyChanged(nameof(ConnectivityToolTip));
        }
    }

    public bool IsConnectivityOk => Connectivity == DeviceConnectivity.Ok;
    public bool IsConnectivityFailed => Connectivity == DeviceConnectivity.Failed;
    public bool IsConnectivityUnknown => Connectivity == DeviceConnectivity.Unknown;

    private string _connectivityDetail = "";

    /// <summary>最近一次互通测试的说明（哪条链路、多少毫秒 / 失败原因），用于状态灯提示。</summary>
    public string ConnectivityDetail
    {
        get => _connectivityDetail;
        set
        {
            if (_connectivityDetail == value) return;
            _connectivityDetail = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ConnectivityToolTip));
        }
    }

    public string ConnectivityToolTip => Connectivity switch
    {
        DeviceConnectivity.Ok => string.IsNullOrEmpty(ConnectivityDetail) ? "可传输" : $"可传输 · {ConnectivityDetail}",
        DeviceConnectivity.Failed => string.IsNullOrEmpty(ConnectivityDetail) ? "连不通（点刷新重测）" : $"连不通 · {ConnectivityDetail}",
        _ => "尚未检测（点右上角刷新立即检测）"
    };

    /// <summary>界面上的完整路径描述，例如“有线 169.254.149.222:55000”。</summary>
    public string EndpointLabel => $"{ConnectionLabel} {Address}:{Port}";

    public PeerEndpoint ToEndpoint() => new()
    {
        DeviceId = DeviceId,
        SessionId = SessionId,
        Name = Name,
        Address = Address,
        Port = Port,
        IsWired = IsWired,
        LocalAddress = LocalAddress,
        LocalInterfaceId = LocalInterfaceId,
        LastSeen = LastSeen
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
