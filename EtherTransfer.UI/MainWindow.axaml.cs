using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.Input.Platform;
using Avalonia.VisualTree;
using EtherTransfer.Core;
using EtherTransfer.Core.Models;
using EtherTransfer.Services.DeviceManager;
using EtherTransfer.Services;
using EtherTransfer.Network.NetworkInterfaces;
using EtherTransfer.Network.UdpDiscovery;

namespace EtherTransfer.UI;

public enum LogCategory
{
    Default,
    Info,
    Success,
    Warning,
    Error,
    Special
}

public class LogMessage
{
    public string Text { get; set; } = string.Empty;
    public LogCategory Category { get; set; } = LogCategory.Default;

    public bool IsError => Category == LogCategory.Error;
    public bool IsWarning => Category == LogCategory.Warning;
    public bool IsSuccess => Category == LogCategory.Success;
    public bool IsInfo => Category == LogCategory.Info;
    public bool IsSpecial => Category == LogCategory.Special;
    public bool IsDefault => Category == LogCategory.Default;
}

/// <summary>
/// v3 主窗：设备列 + 会话列（文本气泡 / 文件卡片 / 消息记录 / 多选删除）+ 融合输入条。
/// 设计依据：UI设计/桌面端/主界面-两栏-融合输入.html
/// </summary>
public partial class MainWindow : Window, INotifyPropertyChanged
{
    public ObservableCollection<DiscoveredDevice> DiscoveredDevices { get; } = new();
    public ObservableCollection<LogMessage> DebugMessages { get; } = new();
    public ObservableCollection<PayloadItem> SelectedPayloads { get; } = new();
    public ObservableCollection<ChatEntry> ChatEntries { get; } = new();

    private readonly DeviceService _deviceService;
    private readonly TransferService _transferService;
    private readonly MessageStore _messageStore;

    /// <summary>失败的发出文件卡片 → 原始路径（仅内存，重启用）。点击卡片重试时重新加入输入条。</summary>
    private readonly Dictionary<string, string> _failedFilePaths = new();

    // ---------- 设备互通测试（设备列表红绿灯） ----------
    /// <summary>
    /// 每台设备的互通测试结论。设备对象在列表刷新时会被整体重建，结论按“设备键”缓存在这里回填。
    /// </summary>
    private sealed class ProbeState
    {
        public bool? Ok { get; set; }
        public string LastReason { get; set; } = "";
        public DateTimeOffset LastAttempt { get; set; } = DateTimeOffset.MinValue;
    }

    private readonly Dictionary<string, ProbeState> _probeStates = new();
    private readonly SemaphoreSlim _probeGate = new(1, 1);
    private CancellationTokenSource? _probeCts;
    private CancellationTokenSource? _passCts;
    private DateTimeOffset _lastBroadcastCheck = DateTimeOffset.MinValue;
    private bool _busySkipLogged;
    private int _probeKickPending;

    /// <summary>正在做互通测试（刷新按钮置灰，标题旁显示“检测中…”）。</summary>
    private bool _isProbing;
    public bool IsProbing
    {
        get => _isProbing;
        private set
        {
            if (_isProbing == value) return;
            _isProbing = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DevicesCountText));
        }
    }

    /// <summary>全通时的巡检间隔：不打扰对端，够快发现掉线。</summary>
    private static readonly TimeSpan ProbeIntervalHealthy = TimeSpan.FromSeconds(30);
    /// <summary>有设备不通时的巡检间隔：给“手机刚亮屏”留一个快速恢复窗口。</summary>
    private static readonly TimeSpan ProbeIntervalUnhealthy = TimeSpan.FromSeconds(8);
    /// <summary>启动后先让设备发现跑一轮，再开始巡检。</summary>
    private static readonly TimeSpan ProbeStartupDelay = TimeSpan.FromSeconds(3);
    /// <summary>UDP 广播双向测试的限频（TCP 全断时才做，避免广播风暴）。</summary>
    private static readonly TimeSpan BroadcastCheckInterval = TimeSpan.FromSeconds(60);

    private static string ProbeKey(DiscoveredDevice d) =>
        string.IsNullOrEmpty(d.DeviceId) ? "addr:" + d.Address : "id:" + d.DeviceId;

    private ProbeState GetProbeState(string key)
    {
        if (!_probeStates.TryGetValue(key, out var state))
        {
            state = new ProbeState();
            _probeStates[key] = state;
        }
        return state;
    }

    /// <summary>设备名（日志用）：按设备键反查当前列表。</summary>
    private string ProbeDisplayName(string key)
        => DiscoveredDevices.FirstOrDefault(d => ProbeKey(d) == key)?.Name
           ?? (key.StartsWith("id:", StringComparison.Ordinal) ? key[3..] : key);

    /// <summary>
    /// 互通测试主循环：启动后延迟一会儿开始，之后按“全通 30 秒 / 有不通 8 秒”自适应巡检。
    /// 传输进行中自动让路，不和对端抢唯一的接受窗口。
    /// </summary>
    private async Task RunConnectivityProbeLoopAsync(CancellationToken ct)
    {
        try { await Task.Delay(ProbeStartupDelay, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            TimeSpan next;
            try
            {
                await ProbeAllDevicesAsync(manual: false, ct).ConfigureAwait(false);
                next = await Dispatcher.UIThread.InvokeAsync(NextProbeDelay);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 本轮被手动刷新打断：立刻接着跑下一轮，让点击的响应不被拖慢。
                next = TimeSpan.FromMilliseconds(200);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                EtherTransfer.Core.AppLog.Write(LogLevel.Warning, "probe.loop", $"互通测试循环异常：{ex.Message}");
                next = ProbeIntervalUnhealthy;
            }

            try { await Task.Delay(next, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private TimeSpan NextProbeDelay()
    {
        if (DiscoveredDevices.Count == 0) return TimeSpan.FromSeconds(12);
        bool anyFailed = _probeStates.Values.Any(s => s.Ok == false);
        return anyFailed ? ProbeIntervalUnhealthy : ProbeIntervalHealthy;
    }

    /// <summary>
    /// 新设备刚出现时立刻补一轮检测（否则要等下一个巡检周期，最长 30 秒才有灯）。
    /// 用一次性的调度标记合并短时间内多次发现事件。
    /// </summary>
    private void RequestProbeSoon()
    {
        if (Interlocked.Exchange(ref _probeKickPending, 1) == 1) return;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(700).ConfigureAwait(false);
                var token = _probeCts?.Token ?? CancellationToken.None;
                if (token.IsCancellationRequested) return;
                await ProbeAllDevicesAsync(manual: false, token).ConfigureAwait(false);
            }
            catch { }
            finally { Interlocked.Exchange(ref _probeKickPending, 0); }
        });
    }

    /// <summary>
    /// 对当前列表里的每台设备做一次互通测试。manual = 用户点了刷新：
    /// 走深探测（唤醒 + 长超时），并且按钮显示“检测中…”。
    /// </summary>
    private async Task ProbeAllDevicesAsync(bool manual, CancellationToken ct)
    {
        if (manual)
        {
            // 手动测试要立刻生效：先掐掉正在跑的那一轮，再排队拿锁。
            try { _passCts?.Cancel(); } catch { }
            await _probeGate.WaitAsync(ct).ConfigureAwait(false);
        }
        else
        {
            if (!await _probeGate.WaitAsync(0, ct).ConfigureAwait(false)) return;
        }

        using var passCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _passCts = passCts;
        try
        {
            if (!manual && _transferService.IsBusy)
            {
                if (!_busySkipLogged)
                {
                    _busySkipLogged = true;
                    OnDebugLog(this, new StructuredLogMessage("probe.skip.busy",
                        "传输进行中，本轮互通测试跳过（避免与对端抢连接）。", LogLevel.Info));
                }
                return;
            }
            _busySkipLogged = false;

            var targets = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var list = new List<(string Key, string Name, List<PeerEndpoint> Endpoints)>();
                foreach (var d in DiscoveredDevices)
                {
                    var eps = _deviceService.GetEndpointsFor(d);
                    if (eps.Count == 0) continue;
                    list.Add((ProbeKey(d), d.Name, eps));
                }
                return list;
            });

            if (targets.Count == 0)
            {
                if (manual)
                {
                    OnDebugLog(this, new StructuredLogMessage("probe.none",
                        "互通测试：当前没有可测试的设备。", LogLevel.Warning));
                }
                return;
            }

            if (manual)
            {
                await Dispatcher.UIThread.InvokeAsync(() => { IsProbing = true; });
                OnDebugLog(this, new StructuredLogMessage("probe.manual",
                    $"开始互通测试：共 {targets.Count} 台设备（{string.Join("、", targets.Select(t => t.Name))}）。",
                    LogLevel.Info));
            }

            int okCount = 0;
            var failed = new List<string>();

            foreach (var target in targets)
            {
                passCts.Token.ThrowIfCancellationRequested();
                var (ok, reason) = await ProbeDeviceAsync(target.Key, target.Endpoints, manual, passCts.Token)
                    .ConfigureAwait(false);
                if (ok) okCount++;
                else failed.Add(string.IsNullOrEmpty(reason) ? target.Name : $"{target.Name}（{reason}）");
            }

            // TCP 全断 → 可能是 AP 隔离：再验一次 UDP 广播双向可达性，通就说明“能传”
            if (okCount == 0)
            {
                await TryBroadcastFallbackAsync(manual, passCts.Token).ConfigureAwait(false);
            }

            if (manual)
            {
                var summary = okCount == targets.Count
                    ? $"互通测试完成：{okCount}/{targets.Count} 台均可传输。"
                    : $"互通测试完成：{okCount}/{targets.Count} 台可传输；不通：{string.Join("；", failed)}";
                OnDebugLog(this, new StructuredLogMessage(
                    failed.Count == 0 ? "probe.summary.ok" : "probe.summary.failed",
                    summary, failed.Count == 0 ? LogLevel.Info : LogLevel.Warning));
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_passCts, passCts)) _passCts = null;
            if (manual) await Dispatcher.UIThread.InvokeAsync(() => { IsProbing = false; });
            _probeGate.Release();
        }
    }

    /// <summary>
    /// 单台设备：按链路逐条试探（首选链路在前）。快速探测全失败时再深探测一遍，
    /// 因为手机息屏时单次 SYN 丢掉很常见，只失败一次不足以判定“不能传输”。
    /// </summary>
    private async Task<(bool ok, string reason)> ProbeDeviceAsync(
        string key, List<PeerEndpoint> endpoints, bool manual, CancellationToken ct)
    {
        var reason = "";
        var attempts = new List<string>();

        foreach (var ep in endpoints)
        {
            ct.ThrowIfCancellationRequested();
            var (ok, why, ms) = await _transferService.ProbeEndpointAsync(ep, manual, ct).ConfigureAwait(false);
            if (ok)
            {
                await ApplyProbeResultAsync(key, true, $"{ep.LinkLabel} {ms} ms").ConfigureAwait(false);
                return (true, "");
            }
            reason = why;
            attempts.Add($"{ep.LinkLabel}:{why}");
        }

        if (!manual)
        {
            // 深探测：发唤醒包、给足超时，专门对付“射频在省电”的手机。
            foreach (var ep in endpoints)
            {
                ct.ThrowIfCancellationRequested();
                var (ok, why, ms) = await _transferService.ProbeEndpointAsync(ep, deep: true, ct).ConfigureAwait(false);
                if (ok)
                {
                    await ApplyProbeResultAsync(key, true, $"{ep.LinkLabel} {ms} ms（唤醒后）").ConfigureAwait(false);
                    return (true, "");
                }
                reason = why;
                attempts.Add($"{ep.LinkLabel}(深):{why}");
            }
        }

        if (attempts.Count > 0) reason = string.Join("；", attempts);
        await ApplyProbeResultAsync(key, false, reason).ConfigureAwait(false);
        return (false, reason);
    }

    /// <summary>结论落地：更新缓存与红绿灯，只在状态变化时写日志（避免 30 秒一条刷屏）。</summary>
    private async Task ApplyProbeResultAsync(string key, bool ok, string reason)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var state = GetProbeState(key);
            var changed = state.Ok != ok;
            state.Ok = ok;
            state.LastReason = reason;
            state.LastAttempt = DateTimeOffset.UtcNow;

            foreach (var d in DiscoveredDevices.Where(d => ProbeKey(d) == key))
            {
                d.ConnectivityDetail = reason;
                d.Connectivity = ok ? DeviceConnectivity.Ok : DeviceConnectivity.Failed;
            }

            if (changed)
            {
                var name = ProbeDisplayName(key);
                OnDebugLog(this, new StructuredLogMessage(ok ? "probe.ok" : "probe.failed",
                    ok ? $"互通测试通过：{name}（{reason}）"
                       : $"互通测试未通过：{name} → {reason}",
                    ok ? LogLevel.Info : LogLevel.Warning));
            }
        });
    }

    /// <summary>
    /// TCP 全部不通时的兜底判定：验证 UDP 广播双向是否能通。
    /// 能通说明发送时会自动切到广播通道，设备其实是“能传”的，不能标成不可用。
    /// </summary>
    private async Task TryBroadcastFallbackAsync(bool manual, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (!manual && now - _lastBroadcastCheck < BroadcastCheckInterval) return;
        _lastBroadcastCheck = now;
        if (!_transferService.IsBroadcastChannelReady) return;

        bool reachable;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            reachable = await _transferService.ProbeBroadcastAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            OnDebugLog(this, new StructuredLogMessage("probe.broadcast.failed",
                $"UDP 广播双向测试失败：{ex.Message}", LogLevel.Warning));
            return;
        }

        if (!reachable) return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            foreach (var d in DiscoveredDevices)
            {
                d.ConnectivityDetail = "走 UDP 广播通道（AP 隔离）";
                d.Connectivity = DeviceConnectivity.Ok;
                GetProbeState(ProbeKey(d)).Ok = true;
            }
            OnDebugLog(this, new StructuredLogMessage("probe.broadcast.ok",
                "TCP 直连不通，但 UDP 广播双向可达（AP 隔离）：设备仍可传输，按广播通道放行。",
                LogLevel.Warning));
        });
    }


    /// <summary>本次发送中、等待落定状态的文件消息。</summary>
    private readonly List<ChatMessage> _pendingOutMessages = new();
    private TransferRequestMessage? _incomingRequest;

    private DiscoveredDevice? _selectedDevice;
    public DiscoveredDevice? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            _selectedDevice = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(CanSend));
            OnPropertyChanged(nameof(SelectedDeviceOS));
            OnPropertyChanged(nameof(SelectedDeviceNameText));
            OnPropertyChanged(nameof(SelectedDeviceMetaText));
            OnPropertyChanged(nameof(IsToolsEnabled));
            OnPropertyChanged(nameof(ThreadEmptyTitle));
            OnPropertyChanged(nameof(ShowThreadEmpty));

            // 记住最后一次有设备的会话键（设备全部离线后"清空当前会话记录"仍可用）
            var key = SessionKeyFor(value);
            if (key.Length > 0)
            {
                _lastSessionKey = key;
            }

            // 设备消失（0 台在线）时渲染键变空 → 右侧立即切回"未选择设备"空态
            if (key != _renderedSessionKey)
            {
                RebuildThread();
            }
        }
    }

    /// <summary>设备 → 会话键；无设备时返回空串（表示不渲染任何会话）。</summary>
    private static string SessionKeyFor(DiscoveredDevice? device)
        => device == null ? string.Empty : MessageStore.KeyFor(device.DeviceId, device.Name);

    public bool HasSelection => SelectedDevice != null;

    /// <summary>当前会话键：选中设备 → 设备键；设备离开后停留在最后一个会话。</summary>
    private string CurrentSessionKey => SelectedDevice != null
        ? MessageStore.KeyFor(SelectedDevice.DeviceId, SelectedDevice.Name)
        : _lastSessionKey;

    private string _lastSessionKey = "";

    public bool HasSelectedFiles => SelectedPayloads.Count > 0;

    private string _textDraft = "";
    public string TextDraft
    {
        get => _textDraft;
        set { _textDraft = value ?? ""; OnPropertyChanged(); OnPropertyChanged(nameof(CanSend)); }
    }

    private bool _isSendingFiles;

    public string SelectionSummaryText => HasSelectedFiles
        ? $"{SelectedPayloads.Count} 项 · {EtherTransfer.Core.FormatHelper.FormatSize(SelectedPayloads.Sum(p => p.TotalSize))}"
        : "";

    /// <summary>一个发送按钮：有附件 或 文本非空 即可发送。</summary>
    public bool CanSend => HasSelection && !_isSendingFiles && !IsSelectionMode &&
                           (HasSelectedFiles || !string.IsNullOrWhiteSpace(TextDraft));

    private TransferDialog? _activeDialog;

    private string _customDeviceName = "";
    public string CustomDeviceName { get => _customDeviceName; set { _customDeviceName = value; OnPropertyChanged(); } }

    private readonly EthernetLinkMonitor _linkMonitor;
    public EthernetLinkState LinkState => _linkMonitor.CurrentState;
    public string? LinkErrorMessage => _linkMonitor.LastErrorMessage;
    public bool HasActiveVpn => _deviceService != null && _deviceService.HasActiveVpn;

    public bool IsNoCable => !IsReady && LinkState == EthernetLinkState.NoCable;
    public bool IsConfiguring => !IsReady && LinkState == EthernetLinkState.Configuring;
    public bool IsConfigError => !IsReady && LinkState == EthernetLinkState.ConfigError;
    public bool IsReady => LinkState == EthernetLinkState.Ready || NetworkHelper.HasActiveWifi;

    public bool IsReadyAndEmpty => IsReady && DiscoveredDevices.Count == 0 && !HasActiveVpn;
    public bool IsReadyAndHasDevicesOrScanning => IsReady && DiscoveredDevices.Count > 0;

    // 链路条
    public string OnlineCountText => $"{DiscoveredDevices.Count} 台在线";
    public string DevicesCountText => IsProbing ? "检测中…" : $"{DiscoveredDevices.Count} 台";

    private string _wiredAddressText = "";
    public string WiredAddressText { get => _wiredAddressText; private set { _wiredAddressText = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasWiredAddress)); } }
    public bool HasWiredAddress => !string.IsNullOrEmpty(_wiredAddressText);

    private string _wifiAddressText = "";
    public string WifiAddressText { get => _wifiAddressText; private set { _wifiAddressText = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasWifiAddress)); } }
    public bool HasWifiAddress => !string.IsNullOrEmpty(_wifiAddressText);

    public string LinkStatusText => IsReady ? "就绪" : IsConfiguring ? "配置中" : IsConfigError ? "异常" : IsNoCable ? "未连接" : "扫描中";

    // 会话头
    public string SelectedDeviceOS => SelectedDevice?.OS ?? string.Empty;
    public string SelectedDeviceNameText => SelectedDevice?.Name ?? "未选择设备";
    public string SelectedDeviceMetaText => SelectedDevice == null
        ? "在左侧选择一台设备开始会话"
        : $"在线 · {SelectedDevice.ConnectionLabel} · {SelectedDevice.Address}";

    // 多选
    private bool _isSelectionMode;
    public bool IsSelectionMode
    {
        get => _isSelectionMode;
        set
        {
            _isSelectionMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectionToggleText));
            OnPropertyChanged(nameof(IsToolsEnabled));
            if (value)
            {
                foreach (var e in ChatEntries.OfType<ChatMessageEntry>()) e.IsSelected = false;
            }
            RaiseSelectionCounters();
        }
    }
    public string SelectionToggleText => IsSelectionMode ? "取消" : "选择";
    public bool IsToolsEnabled => HasSelection && !IsSelectionMode;

    public int SelectedCount => ChatEntries.OfType<ChatMessageEntry>().Count(e => e.IsSelected);
    public bool HasSelectedMessages => SelectedCount > 0;
    public string SelectedCountText => $"已选 {SelectedCount} 条";

    public string SelectAllText
    {
        get
        {
            var total = ChatEntries.OfType<ChatMessageEntry>().Count();
            return total > 0 && SelectedCount >= total ? "全不选" : "全选";
        }
    }

    private void SelectAll_Click(object? sender, RoutedEventArgs e)
    {
        var entries = ChatEntries.OfType<ChatMessageEntry>().ToList();
        var selectAll = SelectedCount < entries.Count;
        foreach (var entry in entries) entry.IsSelected = selectAll;
        RaiseSelectionCounters();
    }

    private void RaiseSelectionCounters()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelectedMessages));
        OnPropertyChanged(nameof(SelectedCountText));
        OnPropertyChanged(nameof(SelectAllText));
    }

    // 搜索
    private bool _isSearchOpen;
    public bool IsSearchOpen
    {
        get => _isSearchOpen;
        set { _isSearchOpen = value; OnPropertyChanged(); if (!value) SearchQuery = ""; }
    }

    private string _searchQuery = "";
    public string SearchQuery
    {
        get => _searchQuery;
        set { _searchQuery = value ?? ""; OnPropertyChanged(); RebuildThread(); }
    }

    // 本机名称编辑
    private bool _isEditingName;
    public bool IsEditingName
    {
        get => _isEditingName;
        set { _isEditingName = value; OnPropertyChanged(); }
    }

    // 空会话
    public bool ShowThreadEmpty => ChatEntries.Count == 0;
    public string ThreadEmptyTitle => HasSelection ? "还没有消息" : "未选择设备";

    private bool _isStartupFatalError;
    public bool IsStartupFatalError { get => _isStartupFatalError; set { _isStartupFatalError = value; OnPropertyChanged(); } }

    private string _startupFatalErrorMessage = "";
    public string StartupFatalErrorMessage { get => _startupFatalErrorMessage; set { _startupFatalErrorMessage = value; OnPropertyChanged(); } }

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        WindowsTitleBarTheme.EnableDarkMode(this);

        // Enter 发送：用隧道阶段抢在 TextBox 类处理器（插换行）之前
        ComposerBox.AddHandler(KeyDownEvent, ComposerBoxTunnelKeyDown, RoutingStrategies.Tunnel);

        var settings = EtherTransfer.Core.SettingsManager.Load();
        if (string.IsNullOrWhiteSpace(settings.CustomDeviceName))
        {
            settings.CustomDeviceName = Environment.MachineName;
            EtherTransfer.Core.SettingsManager.Save(settings);
        }

        CustomDeviceName = settings.CustomDeviceName;

        _messageStore = MessageStore.Load();

        _linkMonitor = new EthernetLinkMonitor(new EtherTransfer.Network.NetworkInterfaces.DefaultNetworkInterfaceProvider());
        _linkMonitor.StateChanged += OnLinkStateChanged;
        _linkMonitor.Start();

        OnPropertyChanged(nameof(LinkState));
        OnPropertyChanged(nameof(IsNoCable));
        OnPropertyChanged(nameof(IsConfiguring));
        OnPropertyChanged(nameof(IsConfigError));
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(LinkErrorMessage));

        TransferService? tempService = null;
        try
        {
            tempService = new TransferService(CustomDeviceName, 55000);
            tempService.Start();
        }
        catch
        {
            // 55000 被占用时换随机端口重来。上一实例可能已经把 UDP 广播接收端拉起来了，
            // 必须先释放，否则第二个实例会与它抢 39000，导致广播报文被两个接收端分流。
            tempService?.Dispose();
            tempService = new TransferService(CustomDeviceName, 0);
            tempService.Start();
        }
        _transferService = tempService;
        _transferService.DebugLog += OnDebugLog;
        _transferService.ProgressUpdated += OnProgressUpdated;
        _transferService.TransferFinished += OnTransferFinished;
        _transferService.OnIncomingTransfer = HandleIncomingTransferAsync;
        _transferService.OnIncomingText = HandleIncomingTextAsync;

        // 服务在订阅日志之前就已启动，广播通道的启动结果单独记一条，否则这个关键状态没有痕迹。
        OnDebugLog(this, new StructuredLogMessage("transfer.channel",
            _transferService.IsBroadcastChannelReady
                ? $"UDP 广播通道已就绪：{_transferService.UdpPort}（AP 隔离时自动接管收发，广播测试端口 {EtherTransfer.Core.Models.UdpBroadcastProtocol.ProbePort}）"
                : $"UDP 广播通道未启动（{_transferService.UdpPort} 端口不可用），仅支持 TCP 直连",
            _transferService.IsBroadcastChannelReady ? LogLevel.Info : LogLevel.Warning));

        int actualTcpPort = _transferService.TcpPort;

        // 防火墙规则要在拿到真实 TCP 端口之后再补（55000 被占用时会退回随机端口）。
        _ = Task.Run(() =>
        {
            FirewallHelper.EnsureFirewallRule((msg, level) =>
            {
                OnDebugLog(this, new StructuredLogMessage("firewall.init", msg, level));
            }, actualTcpPort, 50000, _transferService.UdpPort,
                EtherTransfer.Core.Models.UdpBroadcastProtocol.ProbePort);
        });

        _deviceService = new DeviceService();
        _deviceService.DevicesChanged += OnDevicesChanged;
        _deviceService.NetworkChanged += (_, _) => _ = Dispatcher.UIThread.InvokeAsync(() =>
        {
            OnPropertyChanged(nameof(HasActiveVpn));
            OnPropertyChanged(nameof(IsReady));
            OnPropertyChanged(nameof(IsNoCable));
            OnPropertyChanged(nameof(IsConfiguring));
            OnPropertyChanged(nameof(IsConfigError));
            OnPropertyChanged(nameof(LinkStatusText));
            RefreshLinkBar();
        });
        _deviceService.DebugLog += OnDebugLog;

        ComposerGrip.DragDelta += (_, e) =>
        {
            ComposerBox.Height = Math.Clamp(ComposerBox.Height + e.Vector.Y, 96, 200);
        };

        // 点选/拖选文本时，SelectableTextBlock 会发起 BringIntoView 把滚动位置拽走一截；
        // 会话列表自身已无需要滚到可视区的场景（ScrollIntoView 已移除），统一拦截。
        // 注意：该事件是冒泡路由，只挂 Tunnel 收不到，必须 Tunnel|Bubble 且 handledEventsToo。
        ThreadScroll.AddHandler(Control.RequestBringIntoViewEvent,
            (_, e) => e.Handled = true,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);

        // 拖选文本期间压制悬停工具条。SelectableTextBlock 在 PointerPressed 里把事件标记为已处理，
        // 必须用 handledEventsToo 才能收到；标记打到 ThreadList 上，压掉拖动经过的所有行的工具条。
        ThreadList.AddHandler(PointerPressedEvent,
            (_, e) =>
            {
                if (e.Pointer.Type == PointerType.Mouse) ThreadList.Classes.Add("msgdragging");

                // 点中消息文本时把键盘焦点给窗口本身（不是文本块、也不是滚动容器）：
                // 文本块或滚动容器获得焦点都会被"滚进可视区"，导致跳动/闪烁；
                // 窗口获得焦点既能接收 Ctrl+C，又不会产生任何滚动。
                if (e.Source is Visual src &&
                    src.GetSelfAndVisualAncestors().OfType<PersistentSelectableTextBlock>().Any())
                {
                    Focus();
                }
            },
            RoutingStrategies.Bubble, handledEventsToo: true);
        ThreadList.AddHandler(PointerReleasedEvent,
            (_, _) => ThreadList.Classes.Remove("msgdragging"),
            RoutingStrategies.Bubble, handledEventsToo: true);
        ThreadList.AddHandler(PointerCaptureLostEvent,
            (_, _) => ThreadList.Classes.Remove("msgdragging"),
            RoutingStrategies.Bubble, handledEventsToo: true);

        HookWindowScrollPinning();

        _ = StartDeviceServiceAsync(actualTcpPort);
        RefreshLinkBar();

        // 连通性探测循环：启动即对在网设备发送静默测试报文，驱动设备列表红绿灯。
        _probeCts = new CancellationTokenSource();
        _ = RunConnectivityProbeLoopAsync(_probeCts.Token);
    }

    private async Task StartDeviceServiceAsync(int actualTcpPort)
    {
        try
        {
            await _deviceService.StartAsync(CustomDeviceName, actualTcpPort);
            _ = Dispatcher.UIThread.InvokeAsync(() => OnPropertyChanged(nameof(HasActiveVpn)));
        }
        catch (Exception ex)
        {
            _ = Dispatcher.UIThread.InvokeAsync(() =>
            {
                var msg = $"致命错误：无法绑定 UDP 设备发现端口 50000，可能已有其他程序占用该端口。\n\n{ex.Message}";
                OnDebugLog(this, new StructuredLogMessage("startup.fatal", msg, LogLevel.Error));

                IsStartupFatalError = true;
                StartupFatalErrorMessage = msg;
            });
        }
    }

    private void OnLinkStateChanged(object? sender, EthernetLinkState newState)
    {
        _ = Dispatcher.UIThread.InvokeAsync(() =>
        {
            OnPropertyChanged(nameof(LinkState));
            OnPropertyChanged(nameof(IsNoCable));
            OnPropertyChanged(nameof(IsConfiguring));
            OnPropertyChanged(nameof(IsConfigError));
            OnPropertyChanged(nameof(IsReady));
            OnPropertyChanged(nameof(LinkErrorMessage));
            OnPropertyChanged(nameof(HasActiveVpn));
            OnPropertyChanged(nameof(LinkStatusText));
            RefreshLinkBar();
        });
    }

    /// <summary>链路条：显示本机在有线 / Wi-Fi 上的地址。</summary>
    private void RefreshLinkBar()
    {
        try
        {
            var ifaces = NetworkHelper.GetLanInterfaces().ToList();
            WiredAddressText = ifaces.FirstOrDefault(i => !i.IsWifi)?.LocalAddress.ToString() ?? "";
            WifiAddressText = ifaces.FirstOrDefault(i => i.IsWifi)?.LocalAddress.ToString() ?? "";
        }
        catch
        {
            WiredAddressText = "";
            WifiAddressText = "";
        }
    }

    public void RetryConfig_Click(object? sender, RoutedEventArgs e)
    {
        _linkMonitor.ManualRetry();
    }

    /// <summary>
    /// 刷新按钮 = 一次“现在到底能不能传”的现场检查：
    /// 补发一轮发现广播（让对端立刻回灌状态）→ 对每台设备做互通测试 → 更新红绿灯。
    /// </summary>
    private async void RefreshScan_Click(object? sender, RoutedEventArgs e)
    {
        _linkMonitor.ManualRetry();

        try { await _deviceService.AnnounceNowAsync().ConfigureAwait(true); }
        catch (Exception ex)
        {
            OnDebugLog(this, new StructuredLogMessage("ui.rescan.announce.failed",
                $"补发发现广播失败：{ex.Message}", LogLevel.Warning));
        }

        OnDebugLog(this, new StructuredLogMessage("ui.rescan",
            "重新扫描：已补发发现广播，开始设备互通测试…", LogLevel.Info));

        try
        {
            await ProbeAllDevicesAsync(manual: true, _probeCts?.Token ?? CancellationToken.None);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            OnDebugLog(this, new StructuredLogMessage("ui.rescan.failed",
                $"互通测试出错：{ex.Message}", LogLevel.Warning));
        }
    }

    // ---------- 会话（消息流） ----------

    /// <summary>各会话上次读到的滚动偏移；-1 表示原本停在底部（切回时跟随最新）。</summary>
    private readonly Dictionary<string, double> _sessionScrollOffsets = new();

    /// <summary>当前消息列表实际渲染的会话 key，离开该会话时据此记录滚动位置。</summary>
    private string _renderedSessionKey = string.Empty;

    private void RebuildThread()
    {
        // 记住即将离开的会话读到哪儿：在底部记 -1（表示跟随最新），否则记具体偏移
        if (!string.IsNullOrEmpty(_renderedSessionKey) && ThreadScroll.Viewport.Height > 0)
        {
            bool atBottom = ThreadScroll.Offset.Y + ThreadScroll.Viewport.Height
                            >= ThreadScroll.Extent.Height - 4;
            _sessionScrollOffsets[_renderedSessionKey] = atBottom ? -1 : ThreadScroll.Offset.Y;
        }

        ChatEntries.Clear();

        // 没有在线设备时不渲染任何历史会话，右侧显示"未选择设备"空态
        var sessionKey = SessionKeyFor(SelectedDevice);
        var session = sessionKey.Length == 0 ? null : _messageStore.FindSession(sessionKey);
        if (session != null)
        {
            var filter = SearchQuery.Trim();
            DateTimeOffset? lastDay = null;

            foreach (var m in session.Messages)
            {
                if (filter.Length > 0 &&
                    m.Text.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 &&
                    m.FileName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                var time = DateTimeOffset.FromUnixTimeMilliseconds(m.TimeUnixMs).LocalDateTime;
                if (lastDay == null || time.Date != lastDay.Value.Date)
                {
                    ChatEntries.Add(new DaySeparatorEntry(FormatDay(time)));
                    lastDay = time.Date;
                }

                ChatEntries.Add(new ChatMessageEntry(m));
            }
        }

        OnPropertyChanged(nameof(ShowThreadEmpty));
        OnPropertyChanged(nameof(ThreadEmptyTitle));
        RaiseSelectionCounters();

        _renderedSessionKey = sessionKey;

        // 无设备时没有会话可滚，直接返回
        if (sessionKey.Length == 0)
        {
            return;
        }

        // 切回历史会话时恢复原阅读位置；首次进入或原本在底部则滚到最新
        if (_sessionScrollOffsets.TryGetValue(sessionKey, out var saved) && saved >= 0)
        {
            RestoreThreadOffset(saved);
        }
        else
        {
            ScrollThreadToEnd();
        }
    }

    /// <summary>恢复会话的滚动位置（等布局完成后再设，防止范围未定时被钳制）。</summary>
    private void RestoreThreadOffset(double offset)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ThreadScroll.UpdateLayout();
            ThreadScroll.Offset = new Vector(ThreadScroll.Offset.X, offset);
            Dispatcher.UIThread.Post(() =>
            {
                ThreadScroll.Offset = new Vector(ThreadScroll.Offset.X, offset);
            }, DispatcherPriority.Background);
        }, DispatcherPriority.Loaded);
    }

    /// <summary>切出窗口前的滚动位置（用于重新激活时的兜底还原）。</summary>
    private double _offsetBeforeDeactivate = -1;

    /// <summary>
    /// 窗口失活/重新激活时保持会话滚动位置。
    /// 现象：选中过文本后切走再切回来，视图会跳到选中处——原因是窗口重新激活时
    /// Avalonia 把焦点恢复到上次聚焦的文本块，滚动容器随之把它滚回可视区。
    /// 处理：失活时记下偏移，激活后连续两帧还原，压过焦点恢复引发的滚动。
    /// </summary>
    private void HookWindowScrollPinning()
    {
        // 窗口自身可聚焦：点击消息文字时焦点收到窗口上（不交给文本块/滚动容器，避免被滚进可视区）
        Focusable = true;

        // Ctrl+C 兜底（文本块无焦点时也能复制；有焦点时由控件自身处理）
        AddHandler(KeyDownEvent, HandleCopyHotkey, RoutingStrategies.Tunnel);

        Deactivated += (_, _) => _offsetBeforeDeactivate = ThreadScroll.Offset.Y;

        Activated += (_, _) =>
        {
            if (_offsetBeforeDeactivate < 0) return;

            var target = _offsetBeforeDeactivate;
            Dispatcher.UIThread.Post(() =>
            {
                ThreadScroll.UpdateLayout();
                ThreadScroll.Offset = new Vector(ThreadScroll.Offset.X, target);
                Dispatcher.UIThread.Post(() =>
                {
                    ThreadScroll.Offset = new Vector(ThreadScroll.Offset.X, target);
                    Dispatcher.UIThread.Post(() =>
                    {
                        ThreadScroll.Offset = new Vector(ThreadScroll.Offset.X, target);
                    }, DispatcherPriority.Render);
                }, DispatcherPriority.Background);
            }, DispatcherPriority.Loaded);
        };
    }

    /// <summary>窗口级 Ctrl+C：复制会话区里最近点中的文本块所选内容（文本块本身不夺取焦点）。</summary>
    private async void HandleCopyHotkey(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.C || !e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;

        // 输入框里的复制交给 TextBox 自己处理，别抢
        if (FocusManager?.GetFocusedElement() is TextBox) return;

        if (PersistentSelectableTextBlock.LastActive?.TryGetTarget(out var target) != true || target == null) return;

        var text = target.SelectedText;
        if (string.IsNullOrEmpty(text)) return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null) return;

        await clipboard.SetTextAsync(text);
        e.Handled = true;
    }

    private static string FormatDay(DateTime time)
    {
        var today = DateTime.Now.Date;
        if (time.Date == today) return $"今天 {time:HH:mm}";
        if (time.Date == today.AddDays(-1)) return $"昨天 {time:HH:mm}";
        return $"{time:M月d日 HH:mm}";
    }

    private void ScrollThreadToEnd()
    {
        // 等一轮布局完成后再滚；Offset 直接设成 Extent 高度（超出会被钳到最大值），
        // 比 ScrollToEnd() 更稳——布局/范围尚未算完时也能落到正确位置。
        Dispatcher.UIThread.Post(() =>
        {
            ThreadScroll.UpdateLayout();
            ThreadScroll.Offset = new Vector(ThreadScroll.Offset.X, ThreadScroll.Extent.Height);

            // 再等一帧复查：滚动范围可能在这一帧才最终确定
            Dispatcher.UIThread.Post(() =>
            {
                ThreadScroll.Offset = new Vector(ThreadScroll.Offset.X, ThreadScroll.Extent.Height);
            }, DispatcherPriority.Background);
        }, DispatcherPriority.Loaded);
    }

    private void AppendMessages(string key, string deviceName, IEnumerable<ChatMessage> messages)
    {
        _messageStore.AppendRange(key, deviceName, messages);
        _messageStore.Save();
        if (key == _lastSessionKey || key == CurrentSessionKey)
        {
            RebuildThread();
        }
    }

    /// <summary>来源设备 → 会话键：优先用发现列表里的 DeviceId，找不到就用名字兜底。</summary>
    private string ResolveIncomingKey(string senderName)
    {
        var device = _deviceService.GetActiveDevices().FirstOrDefault(d => d.Name == senderName);
        return device != null
            ? MessageStore.KeyFor(device.DeviceId, device.Name)
            : $"name:{senderName}";
    }

    // ---------- 融合发送 ----------

    private async void ExecuteSendButton_Click(object? sender, RoutedEventArgs e)
    {
        if (SelectedDevice == null)
        {
            _ = new ErrorDialog("请先在左侧选择一台设备。").ShowDialog(this);
            return;
        }

        // 正式发送前掐掉正在跑的互通测试：对端（尤其是手机）通常只有一个接受窗口，
        // 让探测连接和文件连接撞在一起，对端就可能回“设备正忙”，传输直接失败。
        try { _passCts?.Cancel(); } catch { }

        var text = (TextDraft ?? "").Trim();
        bool hasFiles = SelectedPayloads.Count > 0;

        if (!hasFiles && text.Length == 0) return;

        if (text.Length > EtherTransfer.Transfer.TransferReceiver.MaxTextLength)
        {
            _ = new ErrorDialog($"文本过长：{text.Length} 字，上限 {EtherTransfer.Transfer.TransferReceiver.MaxTextLength} 字。").ShowDialog(this);
            return;
        }

        if (_transferService.IsBusy)
        {
            _ = new ErrorDialog("当前设备正在处理另一项传输，请完成或取消后再发送。").ShowDialog(this);
            return;
        }

        if (!TryResolveLiveEndpoints(SelectedDevice, out var endpoints, out var routeError))
        {
            OnDebugLog(this, new StructuredLogMessage("send.failed", routeError, LogLevel.Error));
            _ = new ErrorDialog(routeError).ShowDialog(this);
            return;
        }

        var targetKey = MessageStore.KeyFor(SelectedDevice.DeviceId, SelectedDevice.Name);
        var targetName = SelectedDevice.Name;

        _isSendingFiles = true;
        OnPropertyChanged(nameof(CanSend));

        try
        {
            if (hasFiles)
            {
                var payloads = SelectedPayloads.ToList();

                // 先落"发送中"卡片，发完再更新状态。
                var pending = new List<ChatMessage>();
                foreach (var p in payloads)
                {
                    var sizeText = p.IsFolder ? $"{p.DisplaySize} · {p.FileCount} 个文件" : p.DisplaySize;
                    var msg = ChatMessage.CreateFile(ChatDirection.Out, p.Name, sizeText, ChatMessageState.Sending);
                    pending.Add(msg);

                    if (p.Type == PayloadItemType.File && System.IO.File.Exists(p.Path))
                    {
                        _failedFilePaths[msg.Id] = p.Path;
                    }
                }

                AppendMessages(targetKey, targetName, pending);
                _pendingOutMessages.AddRange(pending);

                SelectedPayloads.Clear();
                UpdateSelectionUI();

                await SendFilesWithDialogAsync(endpoints, payloads);
            }

            if (text.Length > 0)
            {
                OnDebugLog(this, new StructuredLogMessage("text.route",
                    $"发送文本到 {targetName}，候选链路：{string.Join("、", endpoints.Select(x => x.ToString()))}", LogLevel.Info));

                var result = await _transferService.SendTextAsync(endpoints, text, CancellationToken.None);
                if (result.Success)
                {
                    AppendMessages(targetKey, targetName, new[] { ChatMessage.CreateText(ChatDirection.Out, text) });
                    TextDraft = "";
                    OnDebugLog(this, new StructuredLogMessage("text.sent",
                        $"文本已发送给 {targetName}（{text.Length} 字，链路 {result.Endpoint?.LinkLabel ?? "未知"}，{result.ElapsedMs} ms）。", LogLevel.Info));
                }
                else
                {
                    OnDebugLog(this, new StructuredLogMessage("text.failed", $"发送文本失败：{result.ErrorMessage}", LogLevel.Error));
                    _ = new ErrorDialog($"发送文本失败：{result.ErrorMessage}").ShowDialog(this);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            OnDebugLog(this, new StructuredLogMessage("send.failed", $"发送失败：{ex.Message}", LogLevel.Error));
            _ = new ErrorDialog($"发送失败：{ex.Message}").ShowDialog(this);
        }
        finally
        {
            _isSendingFiles = false;
            OnPropertyChanged(nameof(CanSend));
        }
    }

    /// <summary>沿用原文件传输流程：TransferSession + TransferDialog（进度 / 结果弹窗）。</summary>
    private async Task SendFilesWithDialogAsync(List<PeerEndpoint> endpoints, List<PayloadItem> payloads)
    {
        var session = new TransferSession();
        foreach (var payload in payloads)
        {
            if (payload.Type == PayloadItemType.Folder)
            {
                session.ContainsFolders = true;
                session.PayloadFolderCount++;
            }
            else
            {
                session.PayloadFileCount++;
            }
            session.AddFiles(payload.DeepScannedFiles);
        }

        if (session.Files.Count == 0) return;

        var cts = new CancellationTokenSource();
        var dialog = TransferDialog.CreateSender(SelectedDevice?.Name ?? "", cts);
        _activeDialog = dialog;

        // 主窗口保持可见：进度/完成弹窗浮在主窗口之上，不再隐藏-恢复切换。
        dialog.Closed += (_, _) =>
        {
            _ = Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_activeDialog == dialog)
                {
                    _activeDialog = null;
                }
            });
        };

        dialog.Show();

        try
        {
            await _transferService.TransmitSessionAsync(endpoints, session, cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            dialog.IsFailureMode = true;
            dialog.FailureTitle = "无法发送";
            dialog.FailureMessage = ex.Message;
        }
    }

    /// <summary>发送完成 / 被取消 / 中断：把"发送中"卡片落成最终状态；接收侧落收到的文件记录。</summary>
    private void HandleTransferFinishedForChat(TransferResult result)
    {
        var isSender = _activeDialog?.IsSender ?? true;

        if (isSender && _pendingOutMessages.Count > 0)
        {
            foreach (var msg in _pendingOutMessages)
            {
                if (result.Success)
                {
                    msg.State = ChatMessageState.Sent;
                }
                else if (result.CompletedElementsCount > 0 && result.TotalElements > 1)
                {
                    msg.State = result.CompletedElementNames.Contains(msg.FileName)
                        ? ChatMessageState.Sent
                        : ChatMessageState.Failed;
                }
                else
                {
                    msg.State = ChatMessageState.Failed;
                }
            }

            _messageStore.Save();
            _pendingOutMessages.Clear();
            RebuildThread();
        }
        else if (!isSender && _incomingRequest != null)
        {
            if (result.Success || result.CompletedElementsCount > 0)
            {
                var request = _incomingRequest;
                var key = ResolveIncomingKey(request.SenderName);
                var names = result.CompletedElementNames;

                var incoming = names.Count > 0 && names.Count <= 8
                    ? names.Select(n => ChatMessage.CreateFile(ChatDirection.In, n, "", ChatMessageState.Received)).ToList()
                    : new List<ChatMessage> { ChatMessage.CreateFile(ChatDirection.In, $"{Math.Max(names.Count, request.TotalFiles)} 个文件",
                        EtherTransfer.Core.FormatHelper.FormatSize(request.TotalSize), ChatMessageState.Received) };

                AppendMessages(key, request.SenderName, incoming);
            }

            _incomingRequest = null;
        }
    }

    /// <summary>
    /// 解析当前所选设备在各条链路上的端点（有线在前）。
    /// 文件与文本走同一套解析，保证"笔记本优先走网线、手机走 Wi-Fi"的规则一致。
    /// </summary>
    private bool TryResolveLiveEndpoints(DiscoveredDevice device, out List<PeerEndpoint> endpoints, out string error)
    {
        endpoints = new List<PeerEndpoint>();
        error = "";

        var live = _deviceService.GetActiveDevices()
            .FirstOrDefault(d => d.SessionId == device.SessionId ||
                                 (!string.IsNullOrEmpty(device.DeviceId) && d.DeviceId == device.DeviceId));

        if (live == null)
        {
            error = "所选设备已不在网络中，无法连接。";
            return false;
        }

        endpoints = _deviceService.GetEndpointsFor(live);
        if (endpoints.Count == 0)
        {
            error = "所选设备已不在网络中，无法连接。";
            return false;
        }

        return true;
    }

    /// <summary>
    /// 输入框按键处理。必须走隧道（Tunnel）路由：TextBox 的类处理器会先于
    /// 普通实例处理器执行，把 Enter 吃成换行并标记 Handled，导致冒泡阶段
    /// 收不到（实测 Enter 只换行不发送）。隧道阶段抢先处理后即不会插入换行。
    /// Enter 发送；Shift+Enter 换行。
    /// </summary>
    private void ComposerBoxTunnelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if ((e.KeyModifiers & KeyModifiers.Shift) != 0) return;

        e.Handled = true;
        ExecuteSendButton_Click(sender, e);
    }

    // ---------- 接收 ----------

    /// <summary>
    /// 收到文本消息：落进会话流（不再弹窗），回执表示"已送达并已展示"。
    /// </summary>
    private Task<(bool accepted, string reason)> HandleIncomingTextAsync(TextMessageRequest message, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<(bool, string)>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (ct.IsCancellationRequested)
        {
            tcs.TrySetResult((false, "接收方已断开。"));
            return tcs.Task;
        }

        // 连通性探测：自动应答，不进会话、不显示。
        if (message.IsProbe)
        {
            OnDebugLog(this, new StructuredLogMessage("probe.received",
                $"收到 {message.SenderName} 的连通性探测，已自动应答（不显示）。", LogLevel.Info));
            tcs.TrySetResult((true, ""));
            return tcs.Task;
        }

        _ = Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (ct.IsCancellationRequested)
            {
                tcs.TrySetResult((false, "接收方已断开。"));
                return;
            }

            try
            {
                OnDebugLog(this, new StructuredLogMessage("text.received",
                    $"收到来自 {message.SenderName} 的文本消息（{message.Text.Length} 字）。", LogLevel.Info));

                var key = ResolveIncomingKey(message.SenderName);
                AppendMessages(key, message.SenderName,
                    new[] { ChatMessage.CreateText(ChatDirection.In, message.Text, message.SentAtUnixMs) });

                // 已落会话即视为送达。
                tcs.TrySetResult((true, ""));
            }
            catch (Exception ex)
            {
                tcs.TrySetResult((false, $"接收方未能展示文本：{ex.Message}"));
            }
        });

        return tcs.Task;
    }

    // ---------- 传输完成（弹窗状态 + 会话记录） ----------

    private void OnTransferFinished(object? sender, EtherTransfer.Core.Models.TransferResult result)
    {
        _ = Dispatcher.UIThread.InvokeAsync(() =>
        {
            HandleTransferFinishedForChat(result);

            // 传输失败后立刻复测一次：状态灯要当场反映“现在到底通不通”，
            // 不能让用户看着绿灯却传不过去、要等下一个巡检周期才变红。
            if (!result.Success) RequestProbeSoon();

            if (_activeDialog != null && _activeDialog.IsVisible)
            {
                if (result.Success)
                {
                    _activeDialog.IsSuccessMode = true;
                    _activeDialog.IsPartialSuccessMode = false;
                    _activeDialog.IsFailureMode = false;
                    _activeDialog.SetTransferElements(result.CompletedElementNames, null);
                }
                else if (result.TotalElements > 1 && result.CompletedElementsCount > 0)
                {
                    bool isCancelledByUser = _activeDialog.IsCancelledByUser;
                    string peerName = _activeDialog.PeerDeviceName;

                    string cancelTitle = "传输已取消";
                    string cancelReason;
                    string cancelDetail;

                    if (isCancelledByUser)
                    {
                        cancelReason = "您取消了本次传输。";
                        cancelDetail = _activeDialog.IsSender
                            ? "已完成的项目已发送，其余项目已停止传输："
                            : "未完成的文件已清除，已完成的项目已保存：";
                    }
                    else if (!IsReady)
                    {
                        cancelTitle = "传输失败";
                        cancelReason = "连接中断（网线已拔出）。";
                        cancelDetail = _activeDialog.IsSender
                            ? "连接中断前已完成的项目已发送："
                            : "未完成的文件已清除，已完成的项目已保存：";
                    }
                    else if (_activeDialog.IsSender)
                    {
                        cancelTitle = "传输中断";
                        cancelReason = string.IsNullOrWhiteSpace(result.ErrorMessage)
                            ? "与接收方的连接中断。"
                            : $"与接收方的连接中断：{result.ErrorMessage}";
                        cancelDetail = "已完成的项目已发送，其余项目已停止传输：";
                    }
                    else
                    {
                        cancelTitle = "传输中断";
                        cancelReason = string.IsNullOrWhiteSpace(result.ErrorMessage)
                            ? "与发送方的连接中断。"
                            : $"与发送方的连接中断：{result.ErrorMessage}";
                        cancelDetail = "未完成的文件已清除，已完成的项目已保存：";
                    }

                    _activeDialog.IsSuccessMode = true;
                    _activeDialog.IsPartialSuccessMode = true;
                    _activeDialog.IsFailureMode = false;
                    _activeDialog.PartialFailureTitle = cancelTitle;
                    _activeDialog.PartialFailureReason = cancelReason;
                    _activeDialog.PartialFailureDetail = cancelDetail;
                    _activeDialog.TransferFinalSizeText = $"已完成 {result.CompletedElementsCount}/{result.TotalElements} 项";
                    _activeDialog.SetTransferElements(result.CompletedElementNames, result.FailedElementNames);
                }
                else
                {
                    OnDebugLog(this, new StructuredLogMessage("transfer.failed", $"传输失败：{result.ErrorMessage}", LogLevel.Error));

                    bool isDeclined = !string.IsNullOrEmpty(result.ErrorMessage) &&
                        (result.ErrorMessage.Contains("declined", StringComparison.OrdinalIgnoreCase) ||
                         result.ErrorMessage.Contains("拒绝", StringComparison.OrdinalIgnoreCase));

                    bool isUnreachable = !string.IsNullOrEmpty(result.ErrorMessage) &&
                        (result.ErrorMessage.Contains("链路", StringComparison.OrdinalIgnoreCase) ||
                         result.ErrorMessage.Contains("连接", StringComparison.OrdinalIgnoreCase) ||
                         result.ErrorMessage.Contains("超时", StringComparison.OrdinalIgnoreCase) ||
                         result.ErrorMessage.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
                         result.ErrorMessage.Contains("Connection", StringComparison.OrdinalIgnoreCase) ||
                         result.ErrorMessage.Contains("network", StringComparison.OrdinalIgnoreCase));

                    bool isCancelledByUser = _activeDialog.IsCancelledByUser;
                    string peerName = _activeDialog.PeerDeviceName;

                    _activeDialog.IsSuccessMode = false;
                    _activeDialog.IsFailureMode = true;

                    if (isCancelledByUser)
                    {
                        _activeDialog.FailureTitle = "传输已取消";
                        _activeDialog.FailureMessage = "您取消了本次传输。";
                        _activeDialog.FailureSubDetail = _activeDialog.IsSender
                            ? (!string.IsNullOrWhiteSpace(peerName) ? $"没有文件发送到 {peerName}。" : "没有发送任何文件。")
                            : "没有文件保存到您的设备，临时数据已安全清理。";
                    }
                    else if (isDeclined)
                    {
                        _activeDialog.FailureTitle = "传输被拒绝";
                        _activeDialog.FailureMessage = _activeDialog.IsSender
                            ? (!string.IsNullOrWhiteSpace(peerName) ? $"接收方“{peerName}”拒绝了本次传输。" : "接收方拒绝了本次传输。")
                            : "您拒绝了本次传输。";
                        _activeDialog.FailureSubDetail = !string.IsNullOrWhiteSpace(result.ErrorMessage)
                            ? result.ErrorMessage
                            : (_activeDialog.IsSender
                                ? (!string.IsNullOrWhiteSpace(peerName) ? $"没有文件发送到 {peerName}。" : "您的设备没有发送任何文件。")
                                : "没有文件保存到您的设备。");
                    }
                    else if (!IsReady)
                    {
                        _activeDialog.FailureTitle = "传输失败";
                        _activeDialog.FailureMessage = "连接中断（网线已拔出）。";
                        _activeDialog.FailureSubDetail = _activeDialog.IsSender
                            ? (!string.IsNullOrWhiteSpace(peerName) ? $"没有文件发送到 {peerName}。" : "没有发送任何文件，原始文件未被修改。")
                            : "没有文件保存到您的设备，临时数据已安全清理。";
                    }
                    else if (isUnreachable)
                    {
                        _activeDialog.FailureTitle = "无法建立连接";
                        _activeDialog.FailureMessage = _activeDialog.IsSender
                            ? (!string.IsNullOrWhiteSpace(peerName) ? $"无法与“{peerName}”建立连接。" : "无法与对方建立连接。")
                            : "与发送方的连接中断。";
                        _activeDialog.FailureSubDetail = string.IsNullOrWhiteSpace(result.ErrorMessage)
                            ? "已尝试全部可用链路（有线 / Wi-Fi）。"
                            : result.ErrorMessage;
                    }
                    else
                    {
                        _activeDialog.FailureTitle = "传输中断";
                        _activeDialog.FailureMessage = _activeDialog.IsSender
                            ? (!string.IsNullOrWhiteSpace(peerName) ? $"与“{peerName}”的传输中断。" : "与接收方的传输中断。")
                            : "与发送方的传输中断。";
                        _activeDialog.FailureSubDetail = string.IsNullOrWhiteSpace(result.ErrorMessage)
                            ? (_activeDialog.IsSender ? "没有发送任何文件，原始文件未被修改。" : "没有文件保存到您的设备，临时数据已安全清理。")
                            : result.ErrorMessage;
                    }
                }
            }
        });
    }

    private void OnDevicesChanged(object? sender, EventArgs e)
    {
        _ = Dispatcher.UIThread.InvokeAsync(() =>
        {
            var selectedSession = SelectedDevice?.SessionId;
            var selectedIdentity = SelectedDevice?.DeviceId;
            var activeDevices = _deviceService.GetActiveDevices();
            DiscoveredDevices.Clear();
            bool hasUntestedDevice = false;
            foreach (var device in activeDevices)
            {
                // 回填互通测试缓存：列表重建后红绿灯不丢
                if (_probeStates.TryGetValue(ProbeKey(device), out var probeState) && probeState.Ok is bool known)
                {
                    device.Connectivity = known ? DeviceConnectivity.Ok : DeviceConnectivity.Failed;
                    device.ConnectivityDetail = probeState.LastReason;
                }
                else
                {
                    hasUntestedDevice = true;
                }
                DiscoveredDevices.Add(device);
            }

            // 新设备（还没结论）：立刻补一轮互通测试，别让用户盯着灰灯等 30 秒
            if (hasUntestedDevice) RequestProbeSoon();

            SelectedDevice = DiscoveredDevices.FirstOrDefault(d => d.SessionId == selectedSession ||
                (!string.IsNullOrEmpty(selectedIdentity) && d.DeviceId == selectedIdentity))
                ?? (DiscoveredDevices.Count == 1 ? DiscoveredDevices[0] : null);

            OnPropertyChanged(nameof(HasActiveVpn));
            OnPropertyChanged(nameof(OnlineCountText));
            OnPropertyChanged(nameof(DevicesCountText));
            OnPropertyChanged(nameof(IsReadyAndEmpty));
            OnPropertyChanged(nameof(IsReadyAndHasDevicesOrScanning));
            RefreshLinkBar();
        });
    }

    private void OnDebugLog(object? sender, StructuredLogMessage logMsg)
    {
        // 同时落盘，便于事后排查（界面关闭后日志不再丢失）。
        EtherTransfer.Core.AppLog.Write(logMsg.Level, logMsg.EventId, logMsg.Message);

        _ = Dispatcher.UIThread.InvokeAsync(() =>
        {
            var cleanedMessage = logMsg.Message.Trim();
            var category = LogCategory.Default;

            if (logMsg.Level == LogLevel.Error)
            {
                category = LogCategory.Error;
            }
            else if (logMsg.Level == LogLevel.Warning)
            {
                category = LogCategory.Warning;
            }
            else if (logMsg.EventId.StartsWith("device.new") || logMsg.EventId.StartsWith("ethernet.ready"))
            {
                category = LogCategory.Success;
            }
            else if (logMsg.Level == LogLevel.Info)
            {
                if (cleanedMessage.Contains("settled", StringComparison.OrdinalIgnoreCase) ||
                    cleanedMessage.Contains("已稳定", StringComparison.OrdinalIgnoreCase) ||
                    cleanedMessage.Contains("scanning", StringComparison.OrdinalIgnoreCase) ||
                    cleanedMessage.Contains("扫描", StringComparison.OrdinalIgnoreCase) ||
                    cleanedMessage.Contains("listening", StringComparison.OrdinalIgnoreCase) ||
                    cleanedMessage.Contains("监听", StringComparison.OrdinalIgnoreCase) ||
                    cleanedMessage.Contains("offline", StringComparison.OrdinalIgnoreCase) ||
                    cleanedMessage.Contains("离线", StringComparison.OrdinalIgnoreCase))
                {
                    category = LogCategory.Info;
                }
                else if (cleanedMessage.Contains("network interface", StringComparison.OrdinalIgnoreCase) ||
                    cleanedMessage.Contains("网络接口", StringComparison.OrdinalIgnoreCase))
                {
                    category = LogCategory.Special;
                }
            }

            DebugMessages.Add(new LogMessage { Text = cleanedMessage, Category = category });

            if (DebugMessages.Count > 100)
            {
                DebugMessages.RemoveAt(0);
            }
        });
    }

    private DebugWindow? _debugWindow;

    private void ShowDebugLog_Click(object? sender, RoutedEventArgs e)
    {
        if (_debugWindow == null)
        {
            _debugWindow = new DebugWindow();
            _debugWindow.DataContext = this;
            _debugWindow.Closed += (s, args) => _debugWindow = null;
            _debugWindow.Show(this);
        }
        else
        {
            _debugWindow.Activate();
        }
    }

    private void OnProgressUpdated(object? sender, EtherTransfer.Transfer.TransferProgressEventArgs e)
    {
        _activeDialog?.UpdateProgress(e);
    }

    // ---------- 附件（输入条 chips） ----------

    private void UpdateSelectionUI()
    {
        OnPropertyChanged(nameof(HasSelectedFiles));
        OnPropertyChanged(nameof(SelectionSummaryText));
        OnPropertyChanged(nameof(CanSend));
    }

    private void ClearSelectionButton_Click(object? sender, RoutedEventArgs e)
    {
        SelectedPayloads.Clear();
        UpdateSelectionUI();
        OnDebugLog(this, new StructuredLogMessage("ui.selection.cleared", "已清空选择。", LogLevel.Info));
    }

    private void RemovePayload_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.CommandParameter is string id)
        {
            var item = SelectedPayloads.FirstOrDefault(p => p.Id == id);
            if (item != null)
            {
                SelectedPayloads.Remove(item);
                UpdateSelectionUI();
                OnDebugLog(this, new StructuredLogMessage("ui.selection.removed", $"已从选择中移除 {item.Name}。", LogLevel.Info));
            }
        }
    }

    private void PayloadArea_DragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File)
            ? e.DragEffects & DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void PayloadArea_Drop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        try
        {
            var items = e.DataTransfer.TryGetFiles();
            if (items == null) return;

            var paths = items.Select(item => item.TryGetLocalPath())
                .OfType<string>()
                .Where(path => System.IO.File.Exists(path) || System.IO.Directory.Exists(path))
                .ToList();
            if (paths.Count == 0)
            {
                OnDebugLog(this, new StructuredLogMessage("ui.error", "无法获取拖入项目的本地文件或文件夹路径。", LogLevel.Warning));
                return;
            }

            await AddPathsAsync(paths);
        }
        catch (Exception ex)
        {
            OnDebugLog(this, new StructuredLogMessage("ui.drop_error", $"添加拖入项目失败：{ex.Message}", LogLevel.Error));
        }
    }

    private async Task AddPathsAsync(List<string> paths)
    {
        OnDebugLog(this, new StructuredLogMessage("ui.scanning", $"正在并行扫描 {paths.Count} 个项目...", LogLevel.Info));

        using var cts = new CancellationTokenSource();
        var scanTasksList = new ObservableCollection<ScanProgressViewModel>();
        var scanDialog = new ScanDialog(scanTasksList, cts);
        _ = scanDialog.ShowDialog(this);

        var scanTasks = paths.Select(p =>
        {
            var vm = new ScanProgressViewModel { FolderName = System.IO.Path.GetFileName(p) ?? p };
            _ = Dispatcher.UIThread.InvokeAsync(() => scanTasksList.Add(vm));

            var progress = new Progress<int>(count =>
            {
                _ = Dispatcher.UIThread.InvokeAsync(() => vm.FilesFound = count);
            });

            return _transferService.ScanItemAsync(p, progress, cts.Token).ContinueWith(t =>
            {
                _ = Dispatcher.UIThread.InvokeAsync(() =>
                {
                    vm.IsComplete = true;
                });
                return t.IsCanceled || t.IsFaulted ? null : t.Result;
            });
        });

        var payloads = await Task.WhenAll(scanTasks);
        bool wasCancelled = cts.IsCancellationRequested;
        scanDialog.Close();

        if (wasCancelled)
        {
            return;
        }

        foreach (var payload in payloads)
        {
            if (payload != null)
            {
                SelectedPayloads.Add(payload);
            }
        }
        UpdateSelectionUI();
    }

    private async void AddFilesButton_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择要添加的文件",
            AllowMultiple = true
        });

        if (files.Count > 0)
        {
            var paths = new List<string>();
            foreach (var f in files)
            {
                if (f.TryGetLocalPath() is string localPath)
                    paths.Add(localPath);
            }

            if (paths.Count == 0)
            {
                OnDebugLog(this, new StructuredLogMessage("ui.error", "无法获取所选文件的本地路径。", LogLevel.Error));
                return;
            }

            await AddPathsAsync(paths);
        }
    }

    private async void AddFolderButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择要添加的文件夹",
                AllowMultiple = true
            });

            if (folders.Count > 0)
            {
                var paths = new List<string>();
                foreach (var f in folders)
                {
                    var localPath = f.TryGetLocalPath() ?? f.Path.LocalPath;
                    if (!string.IsNullOrEmpty(localPath))
                    {
                        paths.Add(localPath);
                    }
                }

                if (paths.Count == 0)
                {
                    OnDebugLog(this, new StructuredLogMessage("ui.error", "无法获取所选文件夹的本地路径，它们是否是网络驱动器或虚拟驱动器？", LogLevel.Warning));
                    return;
                }

                await AddPathsAsync(paths);
            }
        }
        catch (Exception ex)
        {
            OnDebugLog(this, new StructuredLogMessage("ui.picker_crash", $"文件夹选择器异常：{ex.Message}\n{ex.StackTrace}", LogLevel.Error));
        }
    }

    // ---------- 会话头操作 ----------

    private void ToggleSelection_Click(object? sender, RoutedEventArgs e)
    {
        IsSelectionMode = !IsSelectionMode;
    }

    private void ToggleSearch_Click(object? sender, RoutedEventArgs e)
    {
        IsSearchOpen = !IsSearchOpen;
        if (IsSearchOpen)
        {
            Dispatcher.UIThread.Post(() => SearchBox.Focus());
        }
    }

    private void MoreMenu_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;

        var flyout = new MenuFlyout();
        var clearItem = new MenuItem { Header = "清空当前会话记录" };
        clearItem.Click += async (_, _) =>
        {
            if (string.IsNullOrEmpty(_lastSessionKey)) return;

            var confirmed = await ConfirmDialog.ShowAsync(
                this,
                "将删除与该设备的全部会话记录。已接收的文件不受影响。",
                "清空会话记录？",
                "清空",
                "取消");
            if (!confirmed) return;

            _messageStore.ClearSession(_lastSessionKey);
            _messageStore.Save();
            RebuildThread();
            OnDebugLog(this, new StructuredLogMessage("chat.cleared", "已清空当前会话记录。", LogLevel.Info));
        };
        flyout.Items.Add(clearItem);
        flyout.ShowAt(button);
    }

    private void MessageCheckbox_Tapped(object? sender, TappedEventArgs e)
    {
        if (sender is Border { DataContext: ChatMessageEntry entry } && IsSelectionMode)
        {
            entry.IsSelected = !entry.IsSelected;
            RaiseSelectionCounters();
        }
    }

    /// <summary>右键菜单"复制"：复制文本块当前选中的部分（无选区则不动作）。</summary>
    private void CopySelection_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi) return;

        var target = mi.GetSelfAndVisualAncestors()
            .OfType<ContextMenu>()
            .FirstOrDefault()?.PlacementTarget as SelectableTextBlock;

        if (target == null || target.SelectedText.Length == 0) return;

        target.Copy();
        OnDebugLog(this, new StructuredLogMessage("chat.copy",
            $"已复制所选文本（{target.SelectedText.Length} 字）。", LogLevel.Info));
    }

    private async void DeleteSelected_Click(object? sender, RoutedEventArgs e)
    {
        var ids = ChatEntries.OfType<ChatMessageEntry>().Where(x => x.IsSelected).Select(x => x.Id).ToList();
        if (ids.Count == 0) return;

        _messageStore.DeleteMessages(CurrentSessionKey, ids);
        _messageStore.Save();
        RebuildThread();
        RaiseSelectionCounters();
        OnDebugLog(this, new StructuredLogMessage("chat.deleted", $"已删除 {ids.Count} 条会话记录。", LogLevel.Info));
        await Task.CompletedTask;
    }

    // ---------- 悬停工具 ----------

    private ChatMessageEntry? FindEntry(string id) =>
        ChatEntries.OfType<ChatMessageEntry>().FirstOrDefault(x => x.Id == id);

    private async void CopyMessage_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: string id }) return;
        var entry = FindEntry(id);
        if (entry == null || !entry.IsText) return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null)
        {
            await clipboard.SetTextAsync(entry.Text);
            OnDebugLog(this, new StructuredLogMessage("chat.copied", "已复制消息文本。", LogLevel.Info));
        }
    }

    private void ResendMessage_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: string id }) return;
        var entry = FindEntry(id);
        if (entry == null || !entry.IsText) return;

        TextDraft = entry.Text;
        Dispatcher.UIThread.Post(() => ComposerBox.Focus());
    }

    private void DeleteMessage_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: string id }) return;
        _messageStore.DeleteMessages(CurrentSessionKey, new[] { id });
        _messageStore.Save();
        RebuildThread();
    }

    /// <summary>失败的文件卡片：点此重试 —— 把原文件重新加入输入条。</summary>
    private async void FailedCard_Tapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (sender is not Border { Tag: string id }) return;
        var entry = FindEntry(id);
        if (entry == null || !entry.IsFile || !entry.IsFailed) return;

        if (_failedFilePaths.TryGetValue(id, out var path) && System.IO.File.Exists(path))
        {
            _failedFilePaths.Remove(id);
            _messageStore.DeleteMessages(CurrentSessionKey, new[] { id });
            _messageStore.Save();
            RebuildThread();
            await AddPathsAsync(new List<string> { path });
        }
        else
        {
            _ = new ErrorDialog("原始文件路径已失效，请重新添加文件后再发送。").ShowDialog(this);
        }
    }

    // ---------- 清理 ----------

    private async void ClearCache_Click(object? sender, RoutedEventArgs e)
    {
        var confirmed = await ConfirmDialog.ShowAsync(
            this,
            "将删除本机全部会话记录（文本与文件名 / 大小 / 状态）。已接收的文件不受影响。",
            "清理会话记录？",
            "清理",
            "取消");

        if (!confirmed) return;

        _messageStore.ClearAll();
        RebuildThread();
        OnDebugLog(this, new StructuredLogMessage("chat.cleared", "已清理全部会话记录。", LogLevel.Info));
    }

    // ---------- 本机名称 ----------

    private void EditName_Click(object? sender, RoutedEventArgs e)
    {
        IsEditingName = true;
        Dispatcher.UIThread.Post(() =>
        {
            SelfNameBox.Focus();
            SelfNameBox.SelectAll();
        });
    }

    private void SelfNameBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitDeviceName();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            IsEditingName = false;
        }
    }

    private void SelfNameBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (IsEditingName) CommitDeviceName();
    }

    private void CommitDeviceName()
    {
        IsEditingName = false;

        if (string.IsNullOrWhiteSpace(CustomDeviceName))
        {
            CustomDeviceName = Environment.MachineName;
        }

        var settings = EtherTransfer.Core.SettingsManager.Load();
        settings.CustomDeviceName = CustomDeviceName;
        EtherTransfer.Core.SettingsManager.Save(settings);

        _deviceService.UpdateComputerName(CustomDeviceName);
        OnDebugLog(this, new StructuredLogMessage("ui.name_saved", $"已保存并广播新名称：{CustomDeviceName}", LogLevel.Info));
    }

    // ---------- 接收文件请求 ----------

    private Task<(bool accept, string savePath, CancellationToken cancelToken)> HandleIncomingTransferAsync(TransferRequestMessage request, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<(bool, string, CancellationToken)>(TaskCreationOptions.RunContinuationsAsynchronously);
        TransferDialog? requestDialog = null;

        var registration = ct.Register(() =>
        {
            _ = Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (requestDialog != null && requestDialog.IsReceiverMode)
                {
                    requestDialog.IsFailureMode = true;
                    requestDialog.FailureTitle = "传输失败";
                    requestDialog.FailureMessage = "发送方已断开连接。";
                    requestDialog.FailureSubDetail = "没有文件保存到您的设备。";
                }
                tcs.TrySetResult((false, "", default));
            });
        });

        _ = Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (ct.IsCancellationRequested || tcs.Task.IsCompleted) return;

            string sizeStr = EtherTransfer.Core.FormatHelper.FormatSize(request.TotalSize);
            string text;

            if (request.PayloadFolderCount > 0 && request.PayloadFileCount > 0)
            {
                text = $"{request.SenderName} 想向您发送 {request.PayloadFolderCount} 个文件夹和 {request.PayloadFileCount} 个文件（{sizeStr}）。";
            }
            else if (request.PayloadFolderCount > 0)
            {
                text = $"{request.SenderName} 想向您发送 {request.PayloadFolderCount} 个文件夹，共 {request.TotalFiles} 个文件（{sizeStr}）。";
            }
            else
            {
                text = $"{request.SenderName} 想向您发送 {request.PayloadFileCount} 个文件（{sizeStr}）。";
            }

            var cancelCts = new CancellationTokenSource();

            var dialog = TransferDialog.CreateReceiver(request.SenderName, text, request.TotalSize, tcs, cancelCts);
            requestDialog = dialog;
            _activeDialog = dialog;
            _incomingRequest = request;

            // 主窗口保持可见：接收进度/完成弹窗浮在主窗口之上。
            dialog.Closed += (_, _) =>
            {
                _ = Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (_activeDialog == dialog)
                    {
                        _activeDialog = null;
                    }
                });
            };

            dialog.Show();
            dialog.AcceptIncomingTransfer();
        });

        _ = tcs.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
        return tcs.Task;
    }

    // ---------- 生命周期 ----------

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (IsSelectionMode)
        {
            if (e.Key == Key.Escape)
            {
                IsSelectionMode = false;
                e.Handled = true;
            }
            else if (e.Key == Key.A && (e.KeyModifiers & KeyModifiers.Control) != 0)
            {
                foreach (var entry in ChatEntries.OfType<ChatMessageEntry>()) entry.IsSelected = true;
                RaiseSelectionCounters();
                e.Handled = true;
            }
            else if (e.Key == Key.Delete)
            {
                DeleteSelected_Click(this, e);
                e.Handled = true;
            }
        }

        base.OnKeyDown(e);
    }

    private bool _isShuttingDown = false;

    public void Shutdown()
    {
        if (_isShuttingDown) return;
        _isShuttingDown = true;

        try
        {
            _messageStore.Save();
            _probeCts?.Cancel();
            _deviceService.Dispose();
            _transferService.Dispose();
            _linkMonitor.Dispose();
        }
        catch { }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        Shutdown();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        Shutdown();
        base.OnClosed(e);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        FitToWorkArea();

        this.Topmost = true;
        this.Topmost = false;
        this.Activate();
        this.Focus();
    }

    /// <summary>
    /// 窗口尺寸自适应所在屏幕的工作区，并处理高缩放小屏：
    /// 设计尺寸 580×860（小屏 650×840），在笔记本小屏上会超出可视高度，
    /// 这里按工作区（去掉任务栏）收敛并重新居中。
    ///
    /// DPI 补偿：1080p 屏幕开 125% 缩放时，界面按系统缩放渲染会整体偏大
    /// （1 设计 DIP = 1.25 物理像素，窗口几乎占满整屏）。此时把内容按 1/s
    /// 缩小渲染，等效回到 100% DPI 的物理观感（1 设计 DIP = 1 物理像素），
    /// 文字仍按最终尺寸矢量渲染，保持清晰。2K 及以上屏幕不补偿，保留缩放优势。
    /// </summary>
    private void FitToWorkArea()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen == null) return;

        const double designW = 650, designH = 890;   // 统一窗口设计尺寸
        const double designMinW = 520, designMinH = 640;

        var s = RenderScaling > 0 ? RenderScaling : 1.0;
        var area = screen.WorkingArea;

        double baseW = designW;
        double baseH = designH;

        bool compensate = s > 1.05 && baseH * s > area.Height * 0.85;
        RootScaler.LayoutTransform = compensate ? new ScaleTransform(1 / s, 1 / s) : null;

        // 补偿时窗口 DIP = 物理像素 / s，最小尺寸同步折算，否则会被 XAML 最小值撑爆屏幕
        MinWidth = designMinW / (compensate ? s : 1.0);
        MinHeight = designMinH / (compensate ? s : 1.0);

        // 布局引擎眼里的可用空间（DIP）：补偿模式下 1 设计 DIP = 1 物理像素
        double workW = compensate ? area.Width : area.Width / s;
        double workH = compensate ? area.Height : area.Height / s;

        double maxH = Math.Max(480, workH - 32);
        double maxW = Math.Max(420, workW - 32);

        double newH = Math.Min(baseH, maxH);
        double newW = Math.Min(baseW, maxW);

        if (compensate)
        {
            newW /= s;
            newH /= s;
        }

        if (Math.Abs(newH - Height) > 0.5 || Math.Abs(newW - Width) > 0.5)
        {
            Height = newH;
            Width = newW;
        }

        // 居中到工作区。注意：PixelPoint 是物理像素，Width/Height 是 DIP，需要乘 scale
        int wPhys = (int)(Width * s);
        int hPhys = (int)(Height * s);
        int x = area.X + Math.Max(0, (area.Width - wPhys) / 2);
        int y = area.Y + Math.Max(0, (area.Height - hPhys) / 2);
        Position = new PixelPoint(x, y);
    }

    public new event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
