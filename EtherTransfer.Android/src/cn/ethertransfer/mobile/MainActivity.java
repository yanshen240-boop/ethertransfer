package cn.ethertransfer.mobile;

import android.app.Activity;
import android.app.Dialog;
import android.app.NotificationManager;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.content.pm.PackageManager;
import android.database.Cursor;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.ColorDrawable;
import android.net.ConnectivityManager;
import android.net.LinkAddress;
import android.net.Network;
import android.net.NetworkCapabilities;
import android.net.Uri;
import android.net.wifi.WifiManager;
import android.os.Build;
import android.os.Bundle;
import android.os.CountDownTimer;
import android.os.Environment;
import android.os.PowerManager;
import android.os.SystemClock;
import android.provider.DocumentsContract;
import android.provider.MediaStore;
import android.provider.OpenableColumns;
import android.text.InputFilter;
import android.util.Log;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.view.Window;
import android.view.WindowInsets;
import android.view.WindowInsetsController;
import android.view.WindowManager;
import android.view.animation.DecelerateInterpolator;
import android.widget.EditText;
import android.widget.FrameLayout;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.BufferedInputStream;
import java.io.BufferedOutputStream;
import java.io.EOFException;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.ConnectException;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.MulticastSocket;
import java.net.NetworkInterface;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.NoRouteToHostException;
import java.net.ServerSocket;
import java.net.Socket;
import java.net.SocketTimeoutException;
import java.nio.charset.StandardCharsets;
import java.text.SimpleDateFormat;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Calendar;
import java.util.Collection;
import java.util.Comparator;
import java.util.Date;
import java.util.HashMap;
import java.util.HashSet;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;
import java.util.UUID;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicLong;

/**
 * 以太传输（安卓端 v4）：底部三页签 + 会话详情整页。
 * <p>导航：Tab1 会话列表 → 会话详情（L1）；Tab2 附近设备 → 设备浮层；Tab3 设置 → 二级页。
 * 传输非模态：悬浮条常驻，可展开整页，结束后进结果页。</p>
 * <p>网络栈与 v1–v3 完全一致（发现 / 收发 / 锁与前台服务），本版只重做界面与信息架构。</p>
 */
public class MainActivity extends Activity {
    static final int TAB_CHAT = 0, TAB_DEVICES = 1, TAB_SETTINGS = 2;
    private static final int REQ_FILES = 1, REQ_FOLDER = 2, REQ_NOTIFICATION = 3;
    private static final int DISCOVERY_PORT = 50000;
    /** 与 LANChat 相同的组播组：广播被 AP 过滤时，组播通常仍能穿透。 */
    private static final String MULTICAST_GROUP = "224.0.0.167";

    // ---------- 线程 / 网络状态 ----------
    private final ExecutorService workers = Executors.newCachedThreadPool();
    private final ScheduledExecutorService timer = Executors.newSingleThreadScheduledExecutor();
    private final Map<String, Peer> peers = new ConcurrentHashMap<>();
    private final Map<String, String> activeSessions = new ConcurrentHashMap<>();
    private final Set<String> retiredSessions = ConcurrentHashMap.newKeySet();
    /** 本轮在线已检查过补发的设备（下线后会移除，重新上线再触发一次）。 */
    private final Set<String> onlineHandled = ConcurrentHashMap.newKeySet();
    private String deviceId;
    private final String session = UUID.randomUUID().toString();
    private final AtomicLong sequence = new AtomicLong();
    private final AtomicBoolean busy = new AtomicBoolean();
    private final AtomicBoolean netStarted = new AtomicBoolean();
    private final AtomicBoolean cancelled = new AtomicBoolean();
    private volatile boolean running = true;
    private volatile boolean cancelledByUser;
    private volatile DatagramSocket discovery;
    private volatile ServerSocket server;
    private volatile Socket activeSocket;
    private volatile UdpReceiver udpReceiver;
    private volatile UdpProbe udpProbe;
    private volatile String deviceName;
    private volatile Uri saveTree;
    private boolean customTree;
    private static final String MEDIA_BASE = Environment.DIRECTORY_DOWNLOADS + "/EtherTransfer";
    private WifiManager.MulticastLock wifiLock;
    private WifiManager.WifiLock highPerfLock;
    private PowerManager.WakeLock cpuLock;
    private volatile CountDownLatch consent;
    private final Set<String> receivedPaths = new HashSet<>();

    // ---------- 传输状态（UI） ----------
    private volatile String activeMsgId;
    private volatile int activePercent;
    private volatile long batchTotal, batchDone, batchStartDone;
    private volatile String batchPeerName = "";
    private volatile boolean batchIncoming;
    private volatile String batchFileName = "";
    private volatile String batchDeviceId = "";
    private final List<ChatStore.Msg> batchMessages = java.util.Collections.synchronizedList(new ArrayList<>());
    private final Map<String, Uri> resendUris = new ConcurrentHashMap<>();
    private final Map<Uri, Long> sizeCache = new ConcurrentHashMap<>();
    private boolean resultShownForBatch;

    // ---------- 界面 ----------
    private Ui ui;
    private FrameLayout root, content, barHost;
    private FrameLayout pageHost;
    private LinearLayout tabBar;
    private ChatListPage chatListPage;
    private DevicesPage devicesPage;
    private SettingsPage settingsPage;
    private ChatPage chatPage;
    private View pushedPage;
    private int currentTab = TAB_CHAT;
    private String threadId = "";
    private TextView snackView;
    private LinearLayout transferBar;
    private TextView transferName, transferPct, transferCaption;
    private Ui.ProgressLine transferLine;
    private ImageView transferIcon;
    private int tickCount;
    private View splash;

    static final class Peer {
        String name, ip, id, sessionId, os = "PC";
        int port;
        long seen;
    }

    private static final class FileToSend {
        File cache;
        String name, path;
        List<FileToSend> children;
        long size() {
            if (children == null) return cache == null ? 0 : cache.length();
            long total = 0;
            for (FileToSend child : children) total += child.size();
            return total;
        }
        int count() { return children == null ? 1 : children.size(); }
        void cleanup() {
            if (cache != null) cache.delete();
            if (children != null) for (FileToSend child : children) child.cleanup();
        }
    }

    // ==================================================================
    // 生命周期
    // ==================================================================
    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        getWindow().setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE);
        // 从图标/通知拉起时同步点亮屏幕，避免冷启动落在息屏状态（Android 8+ 才有效）
        if (Build.VERSION.SDK_INT >= 27) setTurnScreenOn(true);
        SharedPreferences prefs = prefs();
        deviceName = prefs.getString("name", Build.MODEL);
        deviceId = prefs.getString("deviceId", null);
        if (deviceId == null) {
            deviceId = UUID.randomUUID().toString();
            prefs.edit().putString("deviceId", deviceId).apply();
        }
        // Android 11+ 不允许授权「下载」根目录，默认写 MediaStore 的 Download/EtherTransfer（免授权）。
        customTree = prefs.getBoolean("customTree", false);
        if (customTree) {
            String stored = prefs.getString("tree", null);
            if (stored != null) saveTree = Uri.parse(stored);
        }

        ui = new Ui(this);
        getWindow().setStatusBarColor(ui.surface);
        getWindow().setNavigationBarColor(ui.surface);

        buildShell();
        applyImmersive();
        Logs.add(Logs.INFO, "启动 " + deviceName + " · " + appVersion());

        WifiManager manager = (WifiManager) getApplicationContext().getSystemService(WIFI_SERVICE);
        wifiLock = manager.createMulticastLock("EtherTransfer-discovery");
        wifiLock.acquire();
        // 电脑发起的连接是被动入站：息屏后 Wi-Fi 省电 / 进程冻结会导致 SYN 被丢弃。
        try {
            highPerfLock = manager.createWifiLock(WifiManager.WIFI_MODE_FULL_HIGH_PERF, "EtherTransfer-wifi");
            highPerfLock.setReferenceCounted(false);
            highPerfLock.acquire();
        } catch (Exception ignored) { }
        try {
            PowerManager power = (PowerManager) getSystemService(POWER_SERVICE);
            cpuLock = power.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "EtherTransfer:cpu");
            cpuLock.setReferenceCounted(false);
            cpuLock.acquire();
        } catch (Exception ignored) { }
        startKeepAlive();

        workers.execute(this::startNetworking);
        timer.scheduleWithFixedDelay(this::tick, 200, 500, TimeUnit.MILLISECONDS);
    }

    private SharedPreferences prefs() { return getPreferences(0); }

    Ui ui() { return ui; }

    // ==================================================================
    // 外壳：内容层 + 页签栏 + 传输条 + Snackbar
    // ==================================================================
    private void buildShell() {
        root = new FrameLayout(this);
        root.setBackgroundColor(ui.surface);
        root.setClipChildren(false);

        // 页面层单独留出摄像头挖孔的安全区；浮层直接挂在 root 上，因此能盖满整屏
        pageHost = new FrameLayout(this);
        pageHost.setPadding(0, ui.dp(24), 0, 0);
        root.addView(pageHost, new FrameLayout.LayoutParams(-1, -1));
        // 沉浸式下 adjustResize 不生效，键盘高度要自己从 insets 里取
        root.setOnApplyWindowInsetsListener((view, insets) -> {
            int bottom = 0;
            if (Build.VERSION.SDK_INT >= 30) {
                bottom = insets.getInsets(WindowInsets.Type.ime()).bottom;
            } else {
                bottom = insets.getSystemWindowInsetBottom();
            }
            if (pageHost.getPaddingBottom() != bottom) {
                pageHost.setPadding(0, ui.dp(24), 0, bottom);
            }
            return insets;
        });

        content = new FrameLayout(this);
        pageHost.addView(content, new FrameLayout.LayoutParams(-1, -1));

        chatListPage = new ChatListPage(this);
        devicesPage = new DevicesPage(this);
        settingsPage = new SettingsPage(this);
        content.addView(chatListPage, new FrameLayout.LayoutParams(-1, -1));
        content.addView(devicesPage, new FrameLayout.LayoutParams(-1, -1));
        content.addView(settingsPage, new FrameLayout.LayoutParams(-1, -1));

        tabBar = buildTabBar();
        FrameLayout.LayoutParams tlp = new FrameLayout.LayoutParams(-1, -2);
        tlp.gravity = Gravity.BOTTOM;
        pageHost.addView(tabBar, tlp);

        barHost = new FrameLayout(this);
        barHost.setClickable(false);
        FrameLayout.LayoutParams blp = new FrameLayout.LayoutParams(-1, -1);
        blp.bottomMargin = ui.dp(64);
        pageHost.addView(barHost, blp);
        transferBar = buildTransferBar();
        barHost.addView(transferBar, new FrameLayout.LayoutParams(-1, -2, Gravity.BOTTOM));

        snackView = ui.t("", 13, ui.surface, false);
        snackView.setBackground(ui.box(0xF209090B, 12, 0, 0));
        snackView.setPadding(ui.dp(16), ui.dp(14), ui.dp(16), ui.dp(14));
        snackView.setVisibility(View.GONE);
        snackView.setElevation(ui.dp(6));
        FrameLayout.LayoutParams slp = new FrameLayout.LayoutParams(-2, -2);
        slp.gravity = Gravity.BOTTOM | Gravity.CENTER_HORIZONTAL;
        slp.bottomMargin = ui.dp(80);
        slp.leftMargin = ui.dp(16);
        slp.rightMargin = ui.dp(16);
        root.addView(snackView, slp);

        setContentView(root);
        openTab(TAB_CHAT);
        refreshAll();

        splash = buildSplash();
        root.addView(splash, new FrameLayout.LayoutParams(-1, -1));
        root.postDelayed(this::hideSplash, 720);
        root.postDelayed(() -> applyDeepLink(getIntent()), 900);
    }

    /**
     * 逐屏走查入口：{@code adb shell am start -n cn.ethertransfer.mobile/.MainActivity
     * --es page chat --es device <id>}。只读导航，不改变任何数据。
     */
    private void applyDeepLink(Intent intent) {
        if (intent == null) return;
        String page = intent.getStringExtra("page");
        if (page == null || page.isEmpty()) return;
        String device = intent.getStringExtra("device");
        switch (page) {
            case "devices": openTab(TAB_DEVICES); break;
            case "settings": openTab(TAB_SETTINGS); break;
            case "chat": if (device != null && !device.isEmpty()) openChat(device); break;
            case "search": openSearch(device); break;
            case "log": openLog(); break;
            case "receive": openReceiveLocation(); break;
            case "transfer": openTransferPage(); break;
            default: break;
        }
    }

    @Override protected void onNewIntent(Intent intent) {
        super.onNewIntent(intent);
        setIntent(intent);
        applyDeepLink(intent);
    }

    /** P00 启动与自检：品牌标记 + 名称 + 一行说明 + 4 dp 不确定进度 + 版本号。 */
    private View buildSplash() {
        FrameLayout page = new FrameLayout(this);
        page.setBackgroundColor(ui.surface);

        LinearLayout center = ui.col();
        center.setGravity(Gravity.CENTER_HORIZONTAL);
        FrameLayout mark = new FrameLayout(this);
        mark.setBackground(ui.box(ui.accent, 10, 0, 0));
        mark.addView(ui.icon(R.drawable.ic_brand_mark, 22, ui.onAccent),
            new FrameLayout.LayoutParams(ui.dp(22), ui.dp(22), Gravity.CENTER));
        LinearLayout.LayoutParams mp = new LinearLayout.LayoutParams(ui.dp(36), ui.dp(36));
        mp.gravity = Gravity.CENTER_HORIZONTAL;
        center.addView(mark, mp);

        TextView name = ui.t("以太传输", 20, ui.fg, true);
        LinearLayout.LayoutParams np = new LinearLayout.LayoutParams(-2, -2);
        np.topMargin = ui.dp(10);
        np.gravity = Gravity.CENTER_HORIZONTAL;
        center.addView(name, np);

        TextView sub = ui.t("局域网直连 · 不经过服务器", 12, ui.muted, false);
        LinearLayout.LayoutParams sp = new LinearLayout.LayoutParams(-2, -2);
        sp.topMargin = ui.dp(6);
        sp.gravity = Gravity.CENTER_HORIZONTAL;
        center.addView(sub, sp);

        Ui.ProgressLine line = new Ui.ProgressLine(this, ui, 4, ui.accent, ui.track);
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(ui.dp(160), ui.dp(4));
        lp.topMargin = ui.dp(24);
        lp.gravity = Gravity.CENTER_HORIZONTAL;
        center.addView(line, lp);
        android.animation.ValueAnimator anim =
            android.animation.ValueAnimator.ofFloat(0f, 0.88f);
        anim.setDuration(680);
        anim.addUpdateListener(a -> line.setPercent((float) a.getAnimatedValue()));
        anim.start();

        TextView status = ui.t("正在配置网络…", 13, ui.fg2, false);
        LinearLayout.LayoutParams stp = new LinearLayout.LayoutParams(-2, -2);
        stp.topMargin = ui.dp(16);
        stp.gravity = Gravity.CENTER_HORIZONTAL;
        center.addView(status, stp);
        page.addView(center, new FrameLayout.LayoutParams(-2, -2, Gravity.CENTER));

        TextView version = ui.t("以太传输 " + appVersion(), 11, ui.muted, false);
        FrameLayout.LayoutParams vp = new FrameLayout.LayoutParams(-2, -2,
            Gravity.BOTTOM | Gravity.CENTER_HORIZONTAL);
        vp.bottomMargin = ui.dp(32);
        page.addView(version, vp);
        return page;
    }

    private void hideSplash() {
        if (splash == null) return;
        final View view = splash;
        splash = null;
        view.animate().alpha(0f).setDuration(220).withEndAction(() -> root.removeView(view)).start();
    }

    private LinearLayout buildTabBar() {
        LinearLayout bar = ui.row();
        bar.setBackgroundColor(ui.surface);
        bar.setPadding(0, ui.dp(6), 0, ui.dp(8));
        bar.setElevation(ui.dp(8));
        bar.addView(tabItem(TAB_CHAT, R.drawable.ic_tab_chat, R.drawable.ic_tab_chat_active, "会话"),
            new LinearLayout.LayoutParams(0, -1, 1));
        bar.addView(tabItem(TAB_DEVICES, R.drawable.ic_tab_device, R.drawable.ic_tab_device_active, "设备"),
            new LinearLayout.LayoutParams(0, -1, 1));
        bar.addView(tabItem(TAB_SETTINGS, R.drawable.ic_tab_settings, R.drawable.ic_tab_settings_active, "设置"),
            new LinearLayout.LayoutParams(0, -1, 1));
        // 顶部 1 dp 描边
        LinearLayout wrap = ui.col();
        wrap.addView(ui.divider(ui.border), new LinearLayout.LayoutParams(-1, Math.max(1, ui.dp(1))));
        wrap.addView(bar, new LinearLayout.LayoutParams(-1, ui.dp(64)));
        wrap.setBackgroundColor(ui.surface);
        return wrap;
    }

    private final List<LinearLayout> tabItems = new ArrayList<>();

    private View tabItem(int index, int icon, int iconActive, String label) {
        LinearLayout item = new LinearLayout(this);
        item.setOrientation(LinearLayout.VERTICAL);
        item.setGravity(Gravity.CENTER);
        item.setContentDescription(label);
        ImageView image = ui.icon(icon, 24, ui.muted);
        LinearLayout.LayoutParams ip = new LinearLayout.LayoutParams(ui.dp(24), ui.dp(24));
        item.addView(image, ip);
        TextView text = ui.t(label, 11, ui.muted, false);
        LinearLayout.LayoutParams tp = new LinearLayout.LayoutParams(-2, -2);
        tp.topMargin = ui.dp(4);
        item.addView(text, tp);
        ui.tap(item, () -> openTab(index));
        item.setTag(new Object[]{image, text, icon, iconActive});
        tabItems.add(item);
        return item;
    }

    private void updateTabs() {
        for (int i = 0; i < tabItems.size(); i++) {
            LinearLayout item = tabItems.get(i);
            Object[] tag = (Object[]) item.getTag();
            ImageView image = (ImageView) tag[0];
            TextView text = (TextView) tag[1];
            boolean on = i == currentTab && pushedPage == null;
            image.setImageResource((Integer) (on ? tag[3] : tag[2]));
            image.setImageTintList(android.content.res.ColorStateList.valueOf(on ? ui.accent : ui.muted));
            text.setTextColor(on ? ui.accent : ui.muted);
            text.setTypeface(Typeface.create("sans-serif", on ? Typeface.BOLD : Typeface.NORMAL));
            item.setAlpha(pushedPage == null ? 1f : 0.4f);
        }
    }

    void openTab(int index) {
        closeChat();
        closePushed();
        currentTab = index;
        chatListPage.setVisibility(index == TAB_CHAT ? View.VISIBLE : View.GONE);
        devicesPage.setVisibility(index == TAB_DEVICES ? View.VISIBLE : View.GONE);
        settingsPage.setVisibility(index == TAB_SETTINGS ? View.VISIBLE : View.GONE);
        tabBar.setVisibility(View.VISIBLE);
        FrameLayout.LayoutParams blp = (FrameLayout.LayoutParams) barHost.getLayoutParams();
        blp.bottomMargin = ui.dp(64);
        barHost.setLayoutParams(blp);
        updateTabs();
        refreshAll();
    }

    private void showOnly(View page) {
        chatListPage.setVisibility(page == chatListPage ? View.VISIBLE : View.GONE);
        devicesPage.setVisibility(page == devicesPage ? View.VISIBLE : View.GONE);
        settingsPage.setVisibility(page == settingsPage ? View.VISIBLE : View.GONE);
        if (pushedPage != null && pushedPage != page) pushedPage.setVisibility(View.GONE);
    }

    // ==================================================================
    // 导航：会话详情 / 二级页 / 返回
    // ==================================================================
    void openChat(String deviceId) {
        if (deviceId == null || deviceId.isEmpty()) return;
        this.threadId = deviceId;
        markRead(deviceId);
        closePushed();
        closeChat();
        chatPage = new ChatPage(this, deviceId);
        content.addView(chatPage, new FrameLayout.LayoutParams(-1, -1));
        tabBar.setVisibility(View.GONE);
        FrameLayout.LayoutParams blp = (FrameLayout.LayoutParams) barHost.getLayoutParams();
        blp.bottomMargin = 0;
        barHost.setLayoutParams(blp);
        chatListPage.setVisibility(View.GONE);
        devicesPage.setVisibility(View.GONE);
        settingsPage.setVisibility(View.GONE);
        chatPage.setVisibility(View.VISIBLE);
        chatPage.setAlpha(0f);
        chatPage.setTranslationX(ui.dp(24));
        chatPage.animate().alpha(1f).translationX(0f).setDuration(240)
            .setInterpolator(new DecelerateInterpolator()).start();
        updateTabs();
        chatPage.refresh();
        updateTransferBar();
    }

    private void closeChat() {
        if (chatPage != null) {
            content.removeView(chatPage);
            chatPage = null;
        }
    }

    private void closePushed() {
        if (pushedPage != null) {
            content.removeView(pushedPage);
            pushedPage = null;
        }
        // 从会话里打开的二级页关闭后，回到原会话而不是退回列表
        if (chatPage != null) {
            chatPage.setVisibility(View.VISIBLE);
            chatListPage.setVisibility(View.GONE);
            devicesPage.setVisibility(View.GONE);
            settingsPage.setVisibility(View.GONE);
            tabBar.setVisibility(View.GONE);
            FrameLayout.LayoutParams blp = (FrameLayout.LayoutParams) barHost.getLayoutParams();
            blp.bottomMargin = 0;
            barHost.setLayoutParams(blp);
        }
    }

    void goBack() {
        if (overlay != null) { if (!overlayBlocking) dismissOverlay(); return; }
        if (chatPage != null) {
            if (pushedPage != null) { closePushed(); updateTabs(); return; }
            if (chatPage.inSelection()) { chatPage.exitSelection(); return; }
            closeChat();
            openTab(currentTab);
            return;
        }
        if (pushedPage != null) {
            closePushed();
            openTab(currentTab);
            return;
        }
        if (currentTab != TAB_CHAT) { openTab(TAB_CHAT); return; }
        super.onBackPressed();
    }

    @Override public void onBackPressed() { goBack(); }

    private void pushPage(View page) {
        closePushed();
        pushedPage = page;
        content.addView(page, new FrameLayout.LayoutParams(-1, -1));
        if (chatPage != null) chatPage.setVisibility(View.GONE);
        chatListPage.setVisibility(View.GONE);
        devicesPage.setVisibility(View.GONE);
        settingsPage.setVisibility(View.GONE);
        tabBar.setVisibility(View.GONE);
        FrameLayout.LayoutParams blp = (FrameLayout.LayoutParams) barHost.getLayoutParams();
        blp.bottomMargin = 0;
        barHost.setLayoutParams(blp);
        page.setAlpha(0f);
        page.setTranslationX(ui.dp(24));
        page.animate().alpha(1f).translationX(0f).setDuration(240)
            .setInterpolator(new DecelerateInterpolator()).start();
        updateTabs();
    }

    // ==================================================================
    // 刷新
    // ==================================================================
    void refreshAll() {
        if (!running) return;
        chatListPage.refresh();
        if (currentTab == TAB_DEVICES) devicesPage.refresh();
        if (currentTab == TAB_SETTINGS) settingsPage.refresh();
        if (chatPage != null) chatPage.refresh();
        updateTransferBar();
    }

    private void tick() {
        if (!running) return;
        tickCount++;
        long now = SystemClock.elapsedRealtime();
        peers.entrySet().removeIf(e -> now - e.getValue().seen > 12000);
        Set<String> live = new HashSet<>();
        for (Peer p : peers.values()) live.add(p.id);
        onlineHandled.retainAll(live);
        runOnUiThread(() -> {
            if (!running) return;
            if (chatPage != null && (busy.get() || tickCount % 2 == 0)) chatPage.refresh();
            if (tickCount % 4 == 0) {
                if (currentTab == TAB_CHAT) chatListPage.refresh();
                else if (currentTab == TAB_DEVICES) devicesPage.refresh();
                else if (currentTab == TAB_SETTINGS) settingsPage.refresh();
            }
            updateTransferBar();
            checkIncomingResult();
        });
    }

    // ==================================================================
    // 数据查询（页面调用）
    // ==================================================================
    String localName() { return deviceName; }
    String localIp() {
        try {
            for (LinkAddress link : wifiAddresses()) {
                if (link.getAddress().getAddress().length == 4) return link.getAddress().getHostAddress();
            }
        } catch (Exception ignored) { }
        return "未连接局域网";
    }

    String lanIssue() {
        try {
            ConnectivityManager cm = (ConnectivityManager) getSystemService(CONNECTIVITY_SERVICE);
            for (Network n : cm.getAllNetworks()) {
                NetworkCapabilities caps = cm.getNetworkCapabilities(n);
                if (caps != null && caps.hasTransport(NetworkCapabilities.TRANSPORT_VPN))
                    return "VPN 正在阻止设备发现，断开 VPN 后重试";
            }
            wifiNetwork();
            return null;
        } catch (Exception e) {
            return "未检测到局域网连接，连接 Wi-Fi 或插网线";
        }
    }

    int onlineCount() { return peers.size(); }

    Collection<Peer> onlinePeers() { return new ArrayList<>(peers.values()); }

    Peer findPeer(String id) {
        if (id == null) return null;
        for (Peer p : peers.values()) if (id.equals(p.id)) return p;
        return null;
    }

    boolean peerOnline(String id) { return findPeer(id) != null; }

    String peerIp(String id) {
        Peer p = findPeer(id);
        if (p != null) return p.ip;
        PeerStore.Known k = PeerStore.get(this, id);
        return k == null ? "" : k.ip;
    }

    String peerTitle(String id) {
        Peer p = findPeer(id);
        if (p != null && p.name != null && !p.name.isEmpty()) return p.name;
        PeerStore.Known k = PeerStore.get(this, id);
        if (k != null && k.name != null && !k.name.isEmpty()) return k.name;
        return "未知设备";
    }

    Map<String, PeerStore.Known> knownPeers() { return PeerStore.all(this); }

    PeerStore.Known knownPeer(String id) { return PeerStore.get(this, id); }

    List<ChatStore.Conv> conversations() { return ChatStore.conversations(this); }

    int unread(String deviceId) {
        long read = prefs().getLong("read." + deviceId, 0);
        List<ChatStore.Msg> msgs = ChatStore.load(this, deviceId);
        int count = 0;
        for (ChatStore.Msg m : msgs) if (!m.out && !m.sys && m.time > read) count++;
        return count;
    }

    void markRead(String deviceId) {
        prefs().edit().putLong("read." + deviceId, System.currentTimeMillis()).apply();
    }

    String receiveLabel() {
        if (useMediaStore()) return "下载/EtherTransfer";
        if (saveTree == null) return "下载/EtherTransfer";
        String folder = DocumentsContract.getTreeDocumentId(saveTree);
        int colon = folder.indexOf(':');
        return colon >= 0 ? folder.substring(colon + 1) : folder;
    }

    private String statsCache = "";
    private long statsAt;

    String receivedStats() {
        long now = System.currentTimeMillis();
        if (!statsCache.isEmpty() && now - statsAt < 3000) return statsCache;
        int count = 0;
        long bytes = 0;
        for (ChatStore.Conv conv : ChatStore.conversations(this)) {
            for (ChatStore.Msg m : ChatStore.load(this, conv.deviceId)) {
                if (m.file && !m.out && ChatStore.RECEIVED.equals(m.state)) { count++; bytes += m.size; }
            }
        }
        statsCache = count == 0 ? "还没有接收过文件"
            : count + " 个文件 · " + ChatStore.sizeText(bytes);
        statsAt = now;
        return statsCache;
    }

    String appVersion() {
        try {
            return getPackageManager().getPackageInfo(getPackageName(), 0).versionName
                + " (" + getPackageManager().getPackageInfo(getPackageName(), 0).versionCode + ")";
        } catch (Exception e) { return "0.8.0"; }
    }

    long querySize(Uri uri) {
        if (uri == null) return -1;
        try (Cursor cursor = getContentResolver().query(uri, new String[]{OpenableColumns.SIZE}, null, null, null)) {
            if (cursor != null && cursor.moveToFirst() && !cursor.isNull(0)) return cursor.getLong(0);
        } catch (Exception ignored) { }
        return -1;
    }

    String displayName(Uri uri) {
        try (Cursor cursor = getContentResolver().query(uri, new String[]{OpenableColumns.DISPLAY_NAME}, null, null, null)) {
            if (cursor != null && cursor.moveToFirst() && !cursor.isNull(0)) return cursor.getString(0);
        } catch (Exception ignored) { }
        return "文件";
    }

    // ==================================================================
    // Snackbar / 对话框 / 浮层
    // ==================================================================
    void snack(String message) {
        if (message == null || message.isEmpty()) return;
        runOnUiThread(() -> {
            if (snackView == null) return;
            snackView.setText(message);
            snackView.setVisibility(View.VISIBLE);
            snackView.setAlpha(0f);
            snackView.setTranslationY(ui.dp(16));
            snackView.animate().alpha(1f).translationY(0f).setDuration(160).start();
            snackView.removeCallbacks(hideSnack);
            snackView.postDelayed(hideSnack, 2600);
        });
    }

    private final Runnable hideSnack = new Runnable() {
        @Override public void run() {
            if (snackView == null) return;
            snackView.animate().alpha(0f).translationY(ui.dp(16)).setDuration(160)
                .withEndAction(() -> snackView.setVisibility(View.GONE)).start();
        }
    };

    void confirm(String title, String body, String actionLabel, Runnable action) {
        LinearLayout card = ui.col();
        card.setBackground(ui.box(ui.card, 24, 0, 0));
        card.setPadding(ui.dp(24), ui.dp(24), ui.dp(24), ui.dp(16));
        TextView head = ui.t(title, 17, ui.fg, true);
        card.addView(head);
        if (body != null && !body.isEmpty()) {
            TextView text = ui.t(body, 13, ui.fg2, false);
            text.setLineSpacing(ui.dp(4), 1f);
            LinearLayout.LayoutParams tp = new LinearLayout.LayoutParams(-1, -2);
            tp.topMargin = ui.dp(10);
            card.addView(text, tp);
        }
        LinearLayout actions = ui.row();
        actions.setGravity(Gravity.END);
        LinearLayout.LayoutParams ap = new LinearLayout.LayoutParams(-1, -2);
        ap.topMargin = ui.dp(20);
        card.addView(actions, ap);
        boolean danger = actionLabel != null && (actionLabel.contains("删除") || actionLabel.contains("清理"));
        actions.addView(ui.textButton("取消", ui.fg2, this::dismissModal));
        TextView ok = ui.textButton(actionLabel, danger ? ui.danger : ui.accent, () -> {
            dismissModal();
            action.run();
        });
        actions.addView(ok);
        showModal(card);
    }

    // ---------- 应用内浮层（不用 Dialog，避免隐藏系统栏后的窗口边界） ----------
    private View overlay;
    /** 阻断式浮层（如接收确认）：点遮罩与返回键都不关闭，必须选一项。 */
    private boolean overlayBlocking;

    private void showOverlay(View panel, boolean bottom) {
        showOverlay(panel, bottom, false);
    }

    private void showOverlay(View panel, boolean bottom, boolean blocking) {
        dismissOverlay();
        FrameLayout scrim = new FrameLayout(this);
        scrim.setBackgroundColor(ui.scrim);
        scrim.setClickable(true);
        scrim.setOnClickListener(v -> { if (!blocking) dismissOverlay(); });
        FrameLayout.LayoutParams lp = bottom
            ? new FrameLayout.LayoutParams(-1, -2, Gravity.BOTTOM)
            : new FrameLayout.LayoutParams(ui.dp(312), -2, Gravity.CENTER);
        scrim.addView(panel, lp);
        root.addView(scrim, new FrameLayout.LayoutParams(-1, -1));
        overlay = scrim;
        overlayBlocking = blocking;
        panel.setAlpha(0f);
        if (bottom) {
            panel.setTranslationY(ui.dp(48));
            panel.animate().translationY(0f).alpha(1f).setDuration(220)
                .setInterpolator(new DecelerateInterpolator()).start();
        } else {
            panel.setScaleX(0.96f);
            panel.setScaleY(0.96f);
            panel.animate().scaleX(1f).scaleY(1f).alpha(1f).setDuration(160).start();
        }
    }

    private void dismissOverlay() {
        if (overlay == null) return;
        final View view = overlay;
        overlay = null;
        overlayBlocking = false;
        view.animate().alpha(0f).setDuration(140).withEndAction(() -> root.removeView(view)).start();
    }

    private void showModal(View card) {
        FrameLayout wrap = new FrameLayout(this);
        wrap.setBackground(ui.box(ui.card, 24, 0, 0));
        wrap.addView(card, new FrameLayout.LayoutParams(-1, -2));
        showOverlay(wrap, false);
    }

    private void dismissModal() { dismissOverlay(); }

    /** BottomSheet：遮罩 40% 黑，圆角 20（仅上两角），顶部把手。 */
    void showSheet(LinearLayout body, boolean withCancel) {
        showOverlay(buildSheetPanel(body, withCancel), true, false);
    }

    /** 阻断式 BottomSheet：必须点按钮才能关闭（接收传输请求用）。 */
    private void showBlockingSheet(LinearLayout body) {
        showOverlay(buildSheetPanel(body, false), true, true);
    }

    private LinearLayout buildSheetPanel(LinearLayout body, boolean withCancel) {
        LinearLayout sheet = ui.col();
        sheet.setBackground(ui.topRounded(ui.card, 20, 1, ui.cardBorder));
        sheet.setPadding(ui.dp(4), ui.dp(8), ui.dp(4), ui.dp(12));
        sheet.setClickable(true);

        View handle = new View(this);
        handle.setBackground(ui.pill(ui.track));
        LinearLayout.LayoutParams hp = new LinearLayout.LayoutParams(ui.dp(32), ui.dp(4));
        hp.gravity = Gravity.CENTER_HORIZONTAL;
        hp.bottomMargin = ui.dp(6);
        sheet.addView(handle, hp);
        sheet.addView(body, new LinearLayout.LayoutParams(-1, -2));

        if (withCancel) {
            View divider = new View(this);
            divider.setBackgroundColor(ui.border);
            LinearLayout.LayoutParams dp = new LinearLayout.LayoutParams(-1, Math.max(1, ui.dp(1)));
            dp.topMargin = ui.dp(8);
            sheet.addView(divider, dp);
            LinearLayout cancel = sheetRow(R.drawable.ic_nav_close, "取消", ui.fg2, this::dismissSheet);
            sheet.addView(cancel);
        }
        return sheet;
    }

    private void dismissSheet() { dismissOverlay(); }

    LinearLayout sheetRow(int iconRes, String label, int color, Runnable action) {
        LinearLayout row = ui.row();
        row.setMinimumHeight(ui.dp(56));
        row.setPadding(ui.dp(16), 0, ui.dp(16), 0);
        row.addView(ui.icon(iconRes, 24, color), new LinearLayout.LayoutParams(ui.dp(24), ui.dp(24)));
        TextView text = ui.t(label, 15, color, false);
        LinearLayout.LayoutParams tp = new LinearLayout.LayoutParams(0, -2, 1);
        tp.leftMargin = ui.dp(16);
        row.addView(text, tp);
        ui.tap(row, action);
        return row;
    }

    LinearLayout sheetTitle(String title, String detail) {
        LinearLayout box = ui.col();
        box.setPadding(ui.dp(16), ui.dp(4), ui.dp(16), ui.dp(10));
        box.addView(ui.t(title, 15, ui.fg, true));
        if (detail != null && !detail.isEmpty()) {
            TextView d = ui.t(detail, 12, ui.muted, false);
            d.setSingleLine(true);
            d.setEllipsize(android.text.TextUtils.TruncateAt.MIDDLE);
            LinearLayout.LayoutParams dp = new LinearLayout.LayoutParams(-1, -2);
            dp.topMargin = ui.dp(4);
            box.addView(d, dp);
        }
        return box;
    }

    // ==================================================================
    // 各类浮层
    // ==================================================================
    void showGlobalMenu() {
        LinearLayout body = ui.col();
        body.addView(sheetTitle("以太传输", onlineCount() + " 台设备在线"));
        body.addView(sheetRow(R.drawable.ic_common_refresh, "重新扫描设备", ui.fg2, () -> { dismissSheet(); rescan(); }));
        body.addView(sheetRow(R.drawable.ic_settings_log, "开发者日志", ui.fg2, () -> { dismissSheet(); openLog(); }));
        body.addView(sheetRow(R.drawable.ic_common_delete, "清理全部会话记录", ui.danger, () -> { dismissSheet(); confirmClearAll(); }));
        showSheet(body, true);
    }

    void showThreadMenu(String deviceId) {
        String name = peerTitle(deviceId);
        LinearLayout body = ui.col();
        body.addView(sheetTitle(name, ChatStore.count(this, deviceId) + " 条记录"));
        body.addView(sheetRow(R.drawable.ic_common_check, "标记为已读", ui.fg2, () -> {
            dismissSheet(); markRead(deviceId); refreshAll(); snack("已标记为已读");
        }));
        body.addView(sheetRow(R.drawable.ic_device_other, "查看设备信息", ui.fg2, () -> {
            dismissSheet(); showDeviceSheet(deviceId);
        }));
        body.addView(sheetRow(R.drawable.ic_common_delete, "清空会话记录", ui.danger, () -> {
            dismissSheet();
            confirm("清理会话记录？",
                "将删除与「" + name + "」的全部会话记录（只含文字和文件名）。已接收的文件不会删除。",
                "清理", () -> {
                    ChatStore.clear(this, deviceId, 0);
                    refreshAll();
                    snack("已清空与「" + name + "」的记录");
                });
        }));
        showSheet(body, true);
    }

    void showMessageSheet(String deviceId, ChatStore.Msg msg) {
        LinearLayout body = ui.col();
        String detail = msg.file ? msg.fileName : (msg.text == null ? "" : msg.text);
        body.addView(sheetTitle(msg.file ? "文件消息" : "文本消息", detail));
        if (!msg.file && msg.text != null && !msg.text.isEmpty()) {
            body.addView(sheetRow(R.drawable.ic_chat_copy, "复制文本", ui.fg2, () -> {
                dismissSheet(); copyText(msg.text);
            }));
        }
        if (msg.out && ChatStore.FAILED.equals(msg.state)) {
            body.addView(sheetRow(R.drawable.ic_common_sync, "重新发送", ui.fg2, () -> {
                dismissSheet(); retryMessage(msg);
            }));
        }
        body.addView(sheetRow(R.drawable.ic_chat_select, "多选", ui.fg2, () -> {
            dismissSheet();
            if (chatPage != null) chatPage.enterSelection(msg.msgId);
        }));
        body.addView(sheetRow(R.drawable.ic_common_delete, "删除", ui.danger, () -> {
            dismissSheet();
            confirm("删除这条记录？", "只删除会话记录，不影响已收发的文件。", "删除", () -> {
                ChatStore.deleteMessages(this, deviceId, java.util.Collections.singletonList(msg.msgId));
                resendUris.remove(msg.msgId);
                refreshAll();
                snack("已删除 1 条记录");
            });
        }));
        showSheet(body, true);
    }

    void showDeviceSheet(String deviceId) {
        Peer peer = findPeer(deviceId);
        PeerStore.Known known = knownPeer(deviceId);
        String name = peerTitle(deviceId);
        String ip = peer != null ? peer.ip : (known == null ? "" : known.ip);
        boolean online = peer != null;
        boolean wired = ip != null && ip.startsWith("169.254.");

        LinearLayout body = ui.col();
        body.setPadding(ui.dp(12), 0, ui.dp(12), 0);

        LinearLayout head = ui.row();
        FrameLayout avatar = ui.avatar(peer != null && "Android".equals(peer.os)
            ? R.drawable.ic_device_phone : R.drawable.ic_device_desktop, ui.wifiBg, ui.wifiFg, 48);
        if (!online) avatar.setAlpha(0.55f);
        head.addView(avatar, new LinearLayout.LayoutParams(ui.dp(48), ui.dp(48)));
        LinearLayout names = ui.col();
        LinearLayout.LayoutParams np = new LinearLayout.LayoutParams(0, -2, 1);
        np.leftMargin = ui.dp(12);
        head.addView(names, np);
        names.addView(ui.t(name, 17, ui.fg, true));
        names.addView(ui.t(online ? "Android 设备 · 在线" : "离线设备", 13, ui.muted, false));
        body.addView(head);

        ui.space(body, 12);
        body.addView(infoRow("地址", ip == null || ip.isEmpty() ? "—" : ip));
        body.addView(infoRow("链路", wired ? "有线（更快）" : "Wi-Fi"));
        body.addView(infoRow("连通状态", online ? "探测正常 · 可连接" : "无法连接，可能已离线或防火墙拦截"));
        if (known != null) body.addView(infoRow("最近在线", when(known.lastSeen)));

        ui.space(body, 12);
        LinearLayout actions = ui.row();
        TextView chat = ui.primary("发消息", () -> { dismissSheet(); openChat(deviceId); });
        LinearLayout.LayoutParams cp = new LinearLayout.LayoutParams(0, ui.dp(48), 1);
        actions.addView(chat, cp);
        TextView sendFile = ui.secondary("发送文件", () -> {
            dismissSheet();
            openChat(deviceId);
            pickFiles();
        });
        LinearLayout.LayoutParams fp = new LinearLayout.LayoutParams(0, ui.dp(48), 1);
        fp.leftMargin = ui.dp(12);
        actions.addView(sendFile, fp);
        body.addView(actions);

        ui.space(body, 4);
        body.addView(sheetRow(R.drawable.ic_common_delete, "清空会话记录", ui.danger, () -> {
            dismissSheet();
            showThreadMenu(deviceId);
        }));
        showSheet(body, true);
    }

    private LinearLayout infoRow(String key, String value) {
        LinearLayout row = ui.row();
        row.setMinimumHeight(ui.dp(44));
        row.addView(ui.t(key, 13, ui.muted, false), new LinearLayout.LayoutParams(0, -2, 1));
        TextView v = ui.t(value, 13, ui.fg, false);
        v.setGravity(Gravity.END);
        row.addView(v);
        return row;
    }

    // ==================================================================
    // 设置项动作
    // ==================================================================
    /** 手动添加设备：借鉴 LANChat 的自定义 IP —— 广播被拦、跨 VLAN 时的兜底发现方式。 */
    void showManualPeerDialog() {
        LinearLayout card = ui.col();
        card.setBackground(ui.box(ui.card, 24, 0, 0));
        card.setPadding(ui.dp(24), ui.dp(20), ui.dp(24), ui.dp(12));
        card.addView(ui.t("手动添加设备", 17, ui.fg, true));
        TextView hint = ui.t("广播被路由器拦截、或两台设备不在同一网段时，直接填对方 IP：会单播探测，对方收到后即时上线。",
            12, ui.muted, false);
        hint.setLineSpacing(ui.dp(3), 1f);
        LinearLayout.LayoutParams hp = new LinearLayout.LayoutParams(-1, -2);
        hp.topMargin = ui.dp(6);
        card.addView(hint, hp);

        EditText input = new EditText(this);
        input.setSingleLine(true);
        input.setTextSize(15);
        input.setTextColor(ui.fg);
        input.setHint("192.168.1.24 或 192.168.1.24:50000");
        input.setHintTextColor(ui.muted);
        input.setBackground(ui.box(ui.card, 12, 1, ui.cardBorder));
        input.setPadding(ui.dp(12), ui.dp(10), ui.dp(12), ui.dp(10));
        LinearLayout.LayoutParams ip = new LinearLayout.LayoutParams(-1, ui.dp(48));
        ip.topMargin = ui.dp(14);
        card.addView(input, ip);

        Set<String> peers = manualPeers();
        if (!peers.isEmpty()) {
            TextView label = ui.t("已添加", 11, ui.muted, false);
            label.setPadding(ui.dp(2), ui.dp(14), ui.dp(2), ui.dp(4));
            card.addView(label);
            for (String peer : peers) {
                LinearLayout line = ui.row();
                line.setMinimumHeight(ui.dp(40));
                line.addView(ui.t(peer, 14, ui.fg, false),
                    new LinearLayout.LayoutParams(0, -2, 1));
                line.addView(ui.iconButton(R.drawable.ic_nav_close, ui.muted, () -> {
                    removeManualPeer(peer);
                    dismissModal();
                    showManualPeerDialog();
                }), new LinearLayout.LayoutParams(ui.dp(32), ui.dp(32)));
                card.addView(line);
            }
        }

        LinearLayout actions = ui.row();
        actions.setGravity(Gravity.END);
        LinearLayout.LayoutParams ap = new LinearLayout.LayoutParams(-1, -2);
        ap.topMargin = ui.dp(16);
        card.addView(actions, ap);
        actions.addView(ui.textButton("取消", ui.fg2, this::dismissModal));
        actions.addView(ui.textButton("添加", ui.accent, () -> {
            String value = input.getText().toString().trim();
            if (value.isEmpty()) { snack("请填写对方 IP"); return; }
            String host = value;
            int port = 0;
            int colon = value.lastIndexOf(':');
            if (colon > 0) {
                host = value.substring(0, colon);
                try { port = Integer.parseInt(value.substring(colon + 1)); }
                catch (Exception e) { snack("端口格式不对"); return; }
            }
            try { InetAddress.getByName(host); }
            catch (Exception e) { snack("IP 格式不对"); return; }
            dismissModal();
            addManualPeer(host, port);
        }));
        showModal(card);
        input.requestFocus();
    }

    void editName() {
        LinearLayout card = ui.col();
        card.setBackground(ui.box(ui.card, 24, 0, 0));
        card.setPadding(ui.dp(24), ui.dp(24), ui.dp(24), ui.dp(16));
        card.addView(ui.t("本机名称", 17, ui.fg, true));
        TextView hint = ui.t("对方设备上会看到这个名字", 12, ui.muted, false);
        LinearLayout.LayoutParams hp = new LinearLayout.LayoutParams(-1, -2);
        hp.topMargin = ui.dp(6);
        card.addView(hint, hp);

        EditText input = new EditText(this);
        input.setText(deviceName);
        input.setSingleLine(true);
        input.setTextSize(15);
        input.setTextColor(ui.fg);
        input.setSelectAllOnFocus(true);
        input.setFilters(new InputFilter[]{new InputFilter.LengthFilter(24)});
        input.setBackground(ui.box(ui.card, 12, 1, ui.cardBorder));
        input.setPadding(ui.dp(12), ui.dp(10), ui.dp(12), ui.dp(10));
        LinearLayout.LayoutParams ip = new LinearLayout.LayoutParams(-1, ui.dp(48));
        ip.topMargin = ui.dp(16);
        card.addView(input, ip);

        LinearLayout actions = ui.row();
        actions.setGravity(Gravity.END);
        LinearLayout.LayoutParams ap = new LinearLayout.LayoutParams(-1, -2);
        ap.topMargin = ui.dp(16);
        card.addView(actions, ap);
        actions.addView(ui.textButton("取消", ui.fg2, this::dismissModal));
        actions.addView(ui.textButton("保存", ui.accent, () -> {
            String value = input.getText().toString().trim();
            if (value.isEmpty()) { snack("名称不能为空"); return; }
            deviceName = value;
            prefs().edit().putString("name", value).apply();
            dismissModal();
            Logs.add(Logs.INFO, "本机名称改为 " + value);
            broadcast();
            refreshAll();
            snack("本机名称已保存");
        }));
        showModal(card);
        input.requestFocus();
    }

    void openReceiveLocation() {
        pushPage(buildReceiveLocationPage());
    }

    private View buildReceiveLocationPage() {
        LinearLayout page = ui.col();
        page.setBackgroundColor(ui.bg);

        LinearLayout bar = ui.row();
        bar.setBackgroundColor(ui.surface);
        bar.setPadding(ui.dp(4), ui.dp(4), ui.dp(16), ui.dp(4));
        bar.setMinimumHeight(ui.dp(56));
        bar.addView(ui.iconButton(R.drawable.ic_nav_back, ui.fg2, this::goBack),
            new LinearLayout.LayoutParams(ui.dp(48), ui.dp(48)));
        bar.addView(ui.t("接收位置", 17, ui.fg, true), new LinearLayout.LayoutParams(0, -2, 1));
        page.addView(bar);
        page.addView(ui.divider(ui.border), new LinearLayout.LayoutParams(-1, Math.max(1, ui.dp(1))));

        ScrollView scroll = new ScrollView(this);
        LinearLayout body = ui.col();
        body.setPadding(ui.dp(16), ui.dp(16), ui.dp(16), ui.dp(24));

        boolean custom = !useMediaStore();

        LinearLayout optionDefault = radioRow("默认位置", "Download/EtherTransfer", "推荐 · 免授权",
            !custom, () -> {
                if (busy.get()) { snack("请在当前传输结束后更改接收位置"); return; }
                resetReceiveFolder();
                refreshAll();
                rebuildReceiveLocationPage();
            });
        body.addView(optionDefault);
        ui.space(body, 8);
        LinearLayout optionCustom = radioRow("自定义文件夹",
            custom ? receiveLabel() : "未选择", custom ? "已启用" : null, custom, () -> {
                if (busy.get()) { snack("请在当前传输结束后更改接收位置"); return; }
                chooseReceiveFolder();
            });
        body.addView(optionCustom);

        ui.space(body, 16);
        LinearLayout note = ui.col();
        note.setBackground(ui.box(ui.card, 12, 1, ui.cardBorder));
        note.setPadding(ui.dp(16), ui.dp(14), ui.dp(16), ui.dp(14));
        TextView text = ui.t("安卓不允许应用直接写入「下载」根目录，默认位置可免授权保存，卸载应用也不会丢。\n自定义文件夹需要系统授权；如果提示「无法使用此文件夹」，请先进入某个子文件夹再选择。",
            12, ui.muted, false);
        text.setLineSpacing(ui.dp(5), 1f);
        note.addView(text);
        body.addView(note);

        ui.space(body, 16);
        body.addView(ui.t("此位置已接收 " + receivedStats(), 12, ui.muted, false));

        scroll.addView(body, new FrameLayout.LayoutParams(-1, -2));
        page.addView(scroll, new LinearLayout.LayoutParams(-1, 0, 1));
        return page;
    }

    private void rebuildReceiveLocationPage() {
        if (pushedPage != null) {
            content.removeView(pushedPage);
            pushedPage = null;
            openReceiveLocation();
        }
    }

    private LinearLayout radioRow(String title, String value, String badge, boolean checked, Runnable action) {
        LinearLayout row = ui.row();
        row.setBackground(ui.box(ui.card, 12, 1, checked ? ui.accent : ui.cardBorder));
        row.setPadding(ui.dp(16), ui.dp(14), ui.dp(16), ui.dp(14));
        row.setMinimumHeight(ui.dp(80));
        LinearLayout info = ui.col();
        LinearLayout nameLine = ui.row();
        nameLine.addView(ui.t(title, 15, ui.fg, true));
        if (badge != null) {
            LinearLayout.LayoutParams bp = new LinearLayout.LayoutParams(-2, -2);
            bp.leftMargin = ui.dp(8);
            nameLine.addView(ui.badge(badge, checked ? ui.accent : ui.wifiFg,
                checked ? ui.wifiBg : ui.ghost), bp);
        }
        info.addView(nameLine);
        TextView v = ui.t(value, 12, ui.fg2, false);
        v.setSingleLine(true);
        v.setEllipsize(android.text.TextUtils.TruncateAt.MIDDLE);
        LinearLayout.LayoutParams vp = new LinearLayout.LayoutParams(-1, -2);
        vp.topMargin = ui.dp(4);
        info.addView(v, vp);
        row.addView(info, new LinearLayout.LayoutParams(0, -2, 1));
        FrameLayout circle = new FrameLayout(this);
        circle.setBackground(checked ? ui.box(ui.accent, 12, 0, 0) : ui.box(0, 12, 1.5f, ui.cardBorder));
        if (checked) {
            circle.addView(ui.icon(R.drawable.ic_common_check, 14, ui.onAccent),
                new FrameLayout.LayoutParams(ui.dp(14), ui.dp(14), Gravity.CENTER));
        }
        LinearLayout.LayoutParams cp = new LinearLayout.LayoutParams(ui.dp(24), ui.dp(24));
        cp.leftMargin = ui.dp(12);
        row.addView(circle, cp);
        ui.tap(row, action);
        return row;
    }

    void confirmClearAll() {
        int count = ChatStore.totalCount(this);
        if (count == 0) { snack("会话记录已经是空的"); return; }
        confirm("清理会话记录？",
            "将删除全部 " + count + " 条会话记录（只含文字和文件名）。已接收的文件不会删除。",
            "清理", () -> {
                int removed = ChatStore.clearAll(this, 0);
                File[] leftovers = getCacheDir().listFiles((dir, name) -> name.startsWith("ethertransfer-"));
                if (leftovers != null) for (File f : leftovers) f.delete();
                refreshAll();
                snack("已清理 " + removed + " 条记录");
            });
    }

    void showThemeHint() {
        snack("外观跟随系统深浅色，暂不提供手动开关");
    }

    void restartKeepAlive() {
        startKeepAlive();
        snack("后台常驻已开启，锁屏仍可收发");
    }

    boolean notificationsGranted() {
        if (Build.VERSION.SDK_INT < 33) {
            NotificationManager nm = (NotificationManager) getSystemService(NOTIFICATION_SERVICE);
            return nm.areNotificationsEnabled();
        }
        return checkSelfPermission("android.permission.POST_NOTIFICATIONS") == PackageManager.PERMISSION_GRANTED;
    }

    void requestNotifications() {
        if (Build.VERSION.SDK_INT >= 33
            && checkSelfPermission("android.permission.POST_NOTIFICATIONS") != PackageManager.PERMISSION_GRANTED) {
            requestPermissions(new String[]{"android.permission.POST_NOTIFICATIONS"}, REQ_NOTIFICATION);
            return;
        }
        try {
            Intent intent = new Intent(android.provider.Settings.ACTION_APP_NOTIFICATION_SETTINGS)
                .putExtra(android.provider.Settings.EXTRA_APP_PACKAGE, getPackageName());
            startActivity(intent);
        } catch (Exception e) { snack("无法打开通知设置：" + e.getMessage()); }
    }

    boolean batteryOptimized() {
        try {
            PowerManager pm = (PowerManager) getSystemService(POWER_SERVICE);
            return !pm.isIgnoringBatteryOptimizations(getPackageName());
        } catch (Exception e) { return false; }
    }

    void openBatterySettings() {
        try {
            startActivity(new Intent(android.provider.Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS));
        } catch (Exception e) {
            try { startActivity(new Intent(android.provider.Settings.ACTION_SETTINGS)); }
            catch (Exception ignored) { snack("无法打开系统设置"); }
        }
    }

    @Override public void onRequestPermissionsResult(int request, String[] permissions, int[] results) {
        super.onRequestPermissionsResult(request, permissions, results);
        if (request == REQ_NOTIFICATION) {
            settingsPage.refresh();
            snack(notificationsGranted() ? "通知已允许" : "通知未允许，锁屏时可能收不到提醒");
        }
    }

    // ==================================================================
    // P17 开发者日志
    // ==================================================================
    void openLog() {
        pushPage(buildLogPage());
    }

    private View buildLogPage() {
        LinearLayout page = ui.col();
        page.setBackgroundColor(ui.surface);

        LinearLayout bar = ui.row();
        bar.setBackgroundColor(ui.surface);
        bar.setPadding(ui.dp(4), ui.dp(4), ui.dp(8), ui.dp(4));
        bar.setMinimumHeight(ui.dp(56));
        bar.addView(ui.iconButton(R.drawable.ic_nav_back, ui.fg2, this::goBack),
            new LinearLayout.LayoutParams(ui.dp(48), ui.dp(48)));
        bar.addView(ui.t("开发者日志", 17, ui.fg, true), new LinearLayout.LayoutParams(0, -2, 1));
        bar.addView(ui.textButton("复制全部", ui.accent, () -> {
            copyText(Logs.dump());
            snack("日志已复制（" + Logs.dump().length() + " 字）");
        }));
        page.addView(bar);
        page.addView(ui.divider(ui.border), new LinearLayout.LayoutParams(-1, Math.max(1, ui.dp(1))));

        ScrollView scroll = new ScrollView(this);
        LinearLayout list = ui.col();
        list.setPadding(ui.dp(12), ui.dp(8), ui.dp(12), ui.dp(24));
        list.setBackgroundColor(ui.night ? 0xFF000000 : ui.surface);
        for (Logs.Line line : Logs.lines()) {
            TextView v = ui.t(line.text, 12, logColor(line.level), false);
            v.setTypeface(Typeface.MONOSPACE);
            v.setLineSpacing(ui.dp(4), 1f);
            v.setPadding(0, ui.dp(2), 0, ui.dp(2));
            v.setTextIsSelectable(true);
            list.addView(v, new LinearLayout.LayoutParams(-1, -2));
        }
        scroll.addView(list, new FrameLayout.LayoutParams(-1, -2));
        page.addView(scroll, new LinearLayout.LayoutParams(-1, 0, 1));

        LinearLayout foot = ui.row();
        foot.setPadding(ui.dp(16), 0, ui.dp(16), 0);
        foot.setMinimumHeight(ui.dp(44));
        foot.addView(ui.t("共 " + Logs.size() + " 条 · 自动滚动", 11, ui.muted, false),
            new LinearLayout.LayoutParams(0, -2, 1));
        page.addView(ui.divider(ui.border), new LinearLayout.LayoutParams(-1, Math.max(1, ui.dp(1))));
        page.addView(foot, new LinearLayout.LayoutParams(-1, ui.dp(44)));
        scroll.post(() -> scroll.fullScroll(View.FOCUS_DOWN));
        return page;
    }

    private int logColor(int level) {
        switch (level) {
            case Logs.ERROR: return ui.night ? 0xFFF87171 : 0xFFDC2626;
            case Logs.WARNING: return ui.night ? 0xFFFBBF24 : 0xFFB45309;
            case Logs.SUCCESS: return ui.night ? 0xFF34D399 : 0xFF059669;
            case Logs.INFO: return ui.night ? 0xFF38BDF8 : 0xFF1D4ED8;
            case Logs.SPECIAL: return ui.night ? 0xFFC084FC : 0xFF6D28D9;
            default: return ui.night ? 0xFFE4E4E7 : 0xFF1F2937;
        }
    }

    // ==================================================================
    // P09 传输进行中 / 悬浮条 / P11 结果
    // ==================================================================
    private LinearLayout buildTransferBar() {
        LinearLayout bar = ui.row();
        bar.setBackground(ui.box(ui.card, 12, 1, ui.cardBorder));
        bar.setPadding(ui.dp(12), ui.dp(10), ui.dp(12), ui.dp(10));
        bar.setMinimumHeight(ui.dp(64));
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(-1, -2);
        lp.leftMargin = ui.dp(12);
        lp.rightMargin = ui.dp(12);
        lp.bottomMargin = ui.dp(8);
        bar.setLayoutParams(lp);
        bar.setElevation(ui.dp(6));
        bar.setVisibility(View.GONE);

        FrameLayout iconBox = new FrameLayout(this);
        iconBox.setBackground(ui.box(ui.wifiBg, 10, 0, 0));
        transferIcon = ui.icon(R.drawable.ic_transfer_up, 20, ui.accent);
        iconBox.addView(transferIcon, new FrameLayout.LayoutParams(ui.dp(20), ui.dp(20), Gravity.CENTER));
        bar.addView(iconBox, new LinearLayout.LayoutParams(ui.dp(36), ui.dp(36)));

        LinearLayout info = ui.col();
        LinearLayout.LayoutParams ip = new LinearLayout.LayoutParams(0, -2, 1);
        ip.leftMargin = ui.dp(10);
        bar.addView(info, ip);
        LinearLayout line1 = ui.row();
        transferName = ui.t("", 13, ui.fg, true);
        transferName.setSingleLine(true);
        transferName.setEllipsize(android.text.TextUtils.TruncateAt.MIDDLE);
        line1.addView(transferName, new LinearLayout.LayoutParams(0, -2, 1));
        transferPct = ui.t("0%", 13, ui.accent, true);
        line1.addView(transferPct);
        info.addView(line1);
        transferCaption = ui.t("", 11, ui.muted, false);
        transferCaption.setSingleLine(true);
        LinearLayout.LayoutParams cp = new LinearLayout.LayoutParams(-1, -2);
        cp.topMargin = ui.dp(2);
        info.addView(transferCaption, cp);
        transferLine = new Ui.ProgressLine(this, ui, 4, ui.accent, ui.track);
        LinearLayout.LayoutParams pp = new LinearLayout.LayoutParams(-1, ui.dp(4));
        pp.topMargin = ui.dp(6);
        info.addView(transferLine, pp);

        ui.tap(bar, this::openTransferPage);
        return bar;
    }

    private void updateTransferBar() {
        if (transferBar == null) return;
        boolean active = busy.get();
        transferBar.setVisibility(active ? View.VISIBLE : View.GONE);
        if (!active) return;
        // 会话页的输入区占了底部，悬浮条要抬到输入区上方，否则会压住发送键
        FrameLayout.LayoutParams hostParams = (FrameLayout.LayoutParams) barHost.getLayoutParams();
        int margin = chatPage != null && chatPage.getVisibility() == View.VISIBLE
            ? chatPage.bottomBlockHeight() : ui.dp(64);
        if (hostParams.bottomMargin != margin) {
            hostParams.bottomMargin = margin;
            barHost.setLayoutParams(hostParams);
        }
        int percent = overallPercent();
        transferPct.setText(batchTotal > 0 ? percent + "%" : "…");
        transferLine.setPercent(percent / 100f);
        transferIcon.setImageResource(batchIncoming ? R.drawable.ic_transfer_down : R.drawable.ic_transfer_up);
        transferIcon.setImageTintList(android.content.res.ColorStateList.valueOf(ui.accent));
        transferName.setText(batchFileName == null || batchFileName.isEmpty()
            ? (batchIncoming ? "正在接收" : "文本消息") : batchFileName);
        String direction = batchIncoming ? "正在接收" : "正在发送";
        String peer = batchPeerName == null || batchPeerName.isEmpty() ? "" : " · " + batchPeerName;
        String size = batchTotal > 0
            ? ChatStore.sizeText(batchDone) + " / " + ChatStore.sizeText(batchTotal) : "";
        transferCaption.setText(direction + peer + (size.isEmpty() ? "" : " · " + size));
    }

    private int overallPercent() {
        if (batchTotal > 0) return (int) Math.min(100, batchDone * 100 / batchTotal);
        if (batchDone > 0) return 100;
        return activePercent;
    }

    void openTransferPage() {
        if (!busy.get()) return;
        pushPage(buildTransferPage());
    }

    private View buildTransferPage() {
        LinearLayout page = ui.col();
        page.setBackgroundColor(ui.surface);

        LinearLayout bar = ui.row();
        bar.setPadding(ui.dp(4), ui.dp(4), ui.dp(4), ui.dp(4));
        bar.setMinimumHeight(ui.dp(56));
        bar.addView(ui.iconButton(R.drawable.ic_nav_back, ui.fg2, this::goBack),
            new LinearLayout.LayoutParams(ui.dp(48), ui.dp(48)));
        LinearLayout titles = ui.col();
        titles.addView(ui.t(batchIncoming ? "接收中" : "发送中", 17, ui.fg, true));
        titles.addView(ui.t((batchIncoming ? "来自 " : "正在发送到 ") + batchPeerName, 12, ui.muted, false));
        LinearLayout.LayoutParams tp = new LinearLayout.LayoutParams(0, -2, 1);
        tp.leftMargin = ui.dp(4);
        bar.addView(titles, tp);
        page.addView(bar);
        page.addView(ui.divider(ui.border), new LinearLayout.LayoutParams(-1, Math.max(1, ui.dp(1))));

        LinearLayout body = ui.col();
        body.setPadding(ui.dp(16), ui.dp(24), ui.dp(16), ui.dp(16));

        int percent = overallPercent();
        TextView big = ui.t(percent + "%", 28, ui.fg, true);
        big.setGravity(Gravity.CENTER);
        body.addView(big, new LinearLayout.LayoutParams(-1, -2));
        String detail = (batchTotal > 0
            ? ChatStore.sizeText(batchDone) + " / " + ChatStore.sizeText(batchTotal) : "正在准备")
            + (batchTotal > 0 && batchDone > 0
            ? " · 剩余 " + ChatStore.sizeText(Math.max(0, batchTotal - batchDone)) : "");
        TextView sub = ui.t(detail, 12, ui.muted, false);
        sub.setGravity(Gravity.CENTER);
        LinearLayout.LayoutParams sp = new LinearLayout.LayoutParams(-1, -2);
        sp.topMargin = ui.dp(6);
        body.addView(sub, sp);

        Ui.ProgressLine line = new Ui.ProgressLine(this, ui, 6, ui.accent, ui.track);
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(-1, ui.dp(6));
        lp.topMargin = ui.dp(16);
        body.addView(line, lp);
        line.setPercent(percent / 100f);
        final int[] last = {percent};
        final Runnable[] poll = new Runnable[1];
        poll[0] = () -> {
            if (!busy.get()) return;
            int now = overallPercent();
            if (now != last[0]) {
                last[0] = now;
                big.setText(now + "%");
                line.setPercent(now / 100f);
                sub.setText((batchTotal > 0
                    ? ChatStore.sizeText(batchDone) + " / " + ChatStore.sizeText(batchTotal) : "正在准备"));
            }
            page.postDelayed(poll[0], 400);
        };
        page.postDelayed(poll[0], 400);

        ui.space(body, 16);
        body.addView(ui.t("本批 " + batchCount() + " 项", 11, ui.muted, false));
        ui.space(body, 8);
        for (ChatStore.Msg m : batchSnapshot()) {
            body.addView(transferFileRow(m));
        }

        ScrollView scroll = new ScrollView(this);
        scroll.addView(body, new FrameLayout.LayoutParams(-1, -2));
        page.addView(scroll, new LinearLayout.LayoutParams(-1, 0, 1));

        LinearLayout footer = ui.row();
        footer.setGravity(Gravity.END);
        footer.setPadding(ui.dp(16), ui.dp(8), ui.dp(16), ui.dp(16));
        footer.addView(ui.textButton("取消传输", ui.danger, () -> confirm("取消这次传输？",
            "已传输的部分不会保留，需要重新发送。", "取消传输", this::cancelTransfer)));
        page.addView(ui.divider(ui.border), new LinearLayout.LayoutParams(-1, Math.max(1, ui.dp(1))));
        page.addView(footer);
        return page;
    }

    private View transferFileRow(ChatStore.Msg m) {
        LinearLayout row = ui.row();
        row.setMinimumHeight(ui.dp(56));
        row.setPadding(0, ui.dp(8), 0, ui.dp(8));
        FrameLayout tile = new FrameLayout(this);
        tile.setBackground(ui.box(ui.ghost, 10, 0, 0));
        tile.addView(ui.icon(fileIcon(m.fileName), 18, ui.fg2),
            new FrameLayout.LayoutParams(ui.dp(18), ui.dp(18), Gravity.CENTER));
        row.addView(tile, new LinearLayout.LayoutParams(ui.dp(36), ui.dp(36)));
        LinearLayout info = ui.col();
        LinearLayout.LayoutParams ip = new LinearLayout.LayoutParams(0, -2, 1);
        ip.leftMargin = ui.dp(10);
        row.addView(info, ip);
        TextView name = ui.t(m.fileName == null || m.fileName.isEmpty() ? m.text : m.fileName, 14, ui.fg, false);
        name.setSingleLine(true);
        name.setEllipsize(android.text.TextUtils.TruncateAt.MIDDLE);
        info.addView(name);
        String state = m.file ? m.stateText() : "文本消息";
        TextView meta = ui.t(state, 12, ChatStore.FAILED.equals(m.state) ? ui.danger : ui.muted, false);
        info.addView(meta);
        if (ChatStore.SENT.equals(m.state) || ChatStore.RECEIVED.equals(m.state)) {
            row.addView(ui.icon(R.drawable.ic_transfer_success, 16, ui.success),
                new LinearLayout.LayoutParams(ui.dp(16), ui.dp(16)));
        } else if (ChatStore.FAILED.equals(m.state)) {
            row.addView(ui.icon(R.drawable.ic_transfer_error, 16, ui.danger),
                new LinearLayout.LayoutParams(ui.dp(16), ui.dp(16)));
        }
        return row;
    }

    private void cancelTransfer() {
        cancelledByUser = true;
        cancelled.set(true);
        Socket socket = activeSocket;
        if (socket != null) try { socket.close(); } catch (IOException ignored) { }
        Logs.add(Logs.WARNING, "用户取消了传输");
    }

    private void checkIncomingResult() {
        if (resultShownForBatch) return;
        if (busy.get()) return;
        List<ChatStore.Msg> snapshot;
        synchronized (batchMessages) {
            if (batchMessages.isEmpty()) return;
            snapshot = new ArrayList<>(batchMessages);
        }
        resultShownForBatch = true;
        batchMessages.clear();
        // 只对「发出去的文件」弹结果页；纯文本由消息流 + Snackbar 交代，避免小题大做
        boolean hasFile = false;
        for (ChatStore.Msg m : snapshot) if (m.file) hasFile = true;
        if (batchIncoming || !hasFile) return;

        String deviceKey = batchDeviceId;
        List<ChatStore.Msg> latest = ChatStore.load(this, deviceKey);
        List<ChatStore.Msg> done = new ArrayList<>();
        for (ChatStore.Msg original : snapshot) {
            for (ChatStore.Msg candidate : latest) {
                if (candidate.msgId.equals(original.msgId)) { done.add(candidate); break; }
            }
        }
        if (!done.isEmpty()) showResultPage(done);
    }

    private void showResultPage(List<ChatStore.Msg> msgs) {
        int ok = 0, failed = 0;
        long bytes = 0;
        for (ChatStore.Msg m : msgs) {
            if (ChatStore.FAILED.equals(m.state)) failed++;
            else if (m.file) { ok++; bytes += m.size; }
        }
        boolean allOk = failed == 0;

        LinearLayout page = ui.col();
        page.setBackgroundColor(ui.surface);
        LinearLayout bar = ui.row();
        bar.setPadding(ui.dp(4), ui.dp(4), ui.dp(16), ui.dp(4));
        bar.setMinimumHeight(ui.dp(56));
        bar.addView(ui.iconButton(R.drawable.ic_nav_close, ui.fg2, this::goBack),
            new LinearLayout.LayoutParams(ui.dp(48), ui.dp(48)));
        bar.addView(ui.t("传输结果", 17, ui.fg, true), new LinearLayout.LayoutParams(0, -2, 1));
        page.addView(bar);

        LinearLayout body = ui.col();
        body.setPadding(ui.dp(24), ui.dp(56), ui.dp(24), ui.dp(24));
        ImageView mark = ui.icon(allOk ? R.drawable.ic_transfer_success : R.drawable.ic_transfer_warning,
            56, allOk ? ui.success : ui.warning);
        LinearLayout.LayoutParams mp = new LinearLayout.LayoutParams(ui.dp(56), ui.dp(56));
        mp.gravity = Gravity.CENTER_HORIZONTAL;
        body.addView(mark, mp);

        TextView title = ui.t(allOk ? "传输完成" : (cancelledByUser ? "传输已取消" : "部分文件未发送"),
            24, ui.fg, true);
        title.setGravity(Gravity.CENTER);
        LinearLayout.LayoutParams tp = new LinearLayout.LayoutParams(-1, -2);
        tp.topMargin = ui.dp(16);
        body.addView(title, tp);

        String summary = cancelledByUser ? "已取消，未完成的文件需要重新发送"
            : (allOk ? msgs.size() + " 项 · " + ChatStore.sizeText(bytes) + " 已发送"
            : failed + " 个失败，" + ok + " 个成功");
        TextView sub = ui.t(summary, 13, ui.muted, false);
        sub.setGravity(Gravity.CENTER);
        LinearLayout.LayoutParams sp = new LinearLayout.LayoutParams(-1, -2);
        sp.topMargin = ui.dp(8);
        body.addView(sub, sp);

        ui.space(body, 24);
        LinearLayout card = ui.col();
        card.setBackground(ui.box(ui.card, 12, 1, ui.cardBorder));
        card.setPadding(ui.dp(12), ui.dp(4), ui.dp(12), ui.dp(4));
        for (ChatStore.Msg m : msgs) card.addView(transferFileRow(m));
        body.addView(card);

        ui.space(body, 24);
        body.addView(ui.primary("完成", this::goBack), new LinearLayout.LayoutParams(-1, ui.dp(48)));
        ui.space(body, 8);
        if (!allOk) {
            body.addView(ui.secondary("重试失败项", () -> {
                snack("请在会话里点失败的卡片重新加入待发送");
                goBack();
            }), new LinearLayout.LayoutParams(-1, ui.dp(48)));
        } else {
            body.addView(ui.secondary("查看文件", this::openDownloadFolder),
                new LinearLayout.LayoutParams(-1, ui.dp(48)));
        }

        ScrollView scroll = new ScrollView(this);
        scroll.addView(body, new FrameLayout.LayoutParams(-1, -2));
        page.addView(scroll, new LinearLayout.LayoutParams(-1, 0, 1));
        pushPage(page);
    }

    private void openDownloadFolder() {
        try {
            Intent intent = new Intent(Intent.ACTION_VIEW);
            intent.setDataAndType(MediaStore.Downloads.EXTERNAL_CONTENT_URI, "vnd.android.document/directory");
            startActivity(intent);
        } catch (Exception e) {
            snack("文件已保存到 下载/EtherTransfer");
        }
    }

    int transferPercentFor(String msgId) {
        if (msgId != null && msgId.equals(activeMsgId)) return activePercent;
        return 0;
    }

    private List<ChatStore.Msg> batchSnapshot() {
        synchronized (batchMessages) { return new ArrayList<>(batchMessages); }
    }

    private int batchCount() {
        synchronized (batchMessages) { return batchMessages.size(); }
    }

    boolean transferring() { return busy.get(); }

    // ==================================================================
    // 文件选择
    // ==================================================================
    void pickFiles() {
        if (busy.get()) { snack("请等待当前传输结束"); return; }
        Intent intent = new Intent(Intent.ACTION_OPEN_DOCUMENT);
        intent.setType("*/*");
        intent.addCategory(Intent.CATEGORY_OPENABLE);
        intent.putExtra(Intent.EXTRA_ALLOW_MULTIPLE, true);
        intent.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION
            | Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION);
        startActivityForResult(intent, REQ_FILES);
    }

    void chooseReceiveFolder() {
        if (busy.get()) { snack("请在当前传输结束后更改接收目录"); return; }
        Intent intent = new Intent(Intent.ACTION_OPEN_DOCUMENT_TREE);
        intent.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION
            | Intent.FLAG_GRANT_WRITE_URI_PERMISSION
            | Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION);
        startActivityForResult(intent, REQ_FOLDER);
    }

    void resetReceiveFolder() {
        customTree = false;
        saveTree = null;
        prefs().edit().putBoolean("customTree", false).remove("tree").apply();
        Logs.add(Logs.INFO, "接收位置恢复为 下载/EtherTransfer");
        snack("接收位置已恢复为「下载/EtherTransfer」");
    }

    @Override protected void onActivityResult(int request, int result, Intent data) {
        super.onActivityResult(request, result, data);
        if (request == REQ_FOLDER) {
            if (result != RESULT_OK || data == null || data.getData() == null) return;
            Uri tree = data.getData();
            try {
                getContentResolver().takePersistableUriPermission(tree,
                    data.getFlags() & (Intent.FLAG_GRANT_READ_URI_PERMISSION | Intent.FLAG_GRANT_WRITE_URI_PERMISSION));
                saveTree = tree;
                customTree = true;
                prefs().edit().putString("tree", tree.toString()).putBoolean("customTree", true).apply();
                Logs.add(Logs.INFO, "接收位置改为 " + receiveLabel());
                snack("接收位置已更新");
                rebuildReceiveLocationPage();
                refreshAll();
            } catch (Exception e) { snack("无法保存文件夹权限：" + e.getMessage()); }
            return;
        }
        if (request == REQ_FILES) {
            if (result != RESULT_OK || data == null) return;
            List<Uri> picked = new ArrayList<>();
            ClipData clip = data.getClipData();
            if (clip != null) {
                for (int i = 0; i < clip.getItemCount(); i++) picked.add(clip.getItemAt(i).getUri());
            } else if (data.getData() != null) {
                picked.add(data.getData());
            }
            for (Uri uri : picked) {
                try {
                    getContentResolver().takePersistableUriPermission(
                        uri, Intent.FLAG_GRANT_READ_URI_PERMISSION);
                } catch (Exception ignored) { }
            }
            if (picked.isEmpty()) return;
            if (chatPage == null) {
                snack("请先选择一台设备");
                return;
            }
            chatPage.addFiles(picked.toArray(new Uri[0]));
        }
    }

    // ==================================================================
    // 复制 / 重发
    // ==================================================================
    void copyText(String content) {
        try {
            ClipboardManager clipboard = (ClipboardManager) getSystemService(CLIPBOARD_SERVICE);
            clipboard.setPrimaryClip(ClipData.newPlainText("EtherTransfer", content));
            snack("已复制到剪贴板");
        } catch (Exception e) { snack("复制失败：" + e.getMessage()); }
    }

    void retryMessage(ChatStore.Msg msg) {
        Uri uri = resendUris.get(msg.msgId);
        if (uri == null) { snack("原文件引用已失效，请重新添加后发送"); return; }
        if (busy.get()) { snack("请等待当前传输结束"); return; }
        if (chatPage == null) return;
        chatPage.addFiles(uri);
        ChatStore.deleteMessages(this, threadId, java.util.Collections.singletonList(msg.msgId));
        resendUris.remove(msg.msgId);
        refreshAll();
        snack("失败文件已重新加入待发送");
    }

    void forgetResend(String msgId) { resendUris.remove(msgId); }

    int fileIcon(String name) {
        String n = name == null ? "" : name.toLowerCase(Locale.US);
        if (n.matches(".*\\.(png|jpg|jpeg|gif|webp|bmp|heic)$")) return R.drawable.ic_file_image;
        if (!n.contains(".")) return R.drawable.ic_file_folder;
        return R.drawable.ic_file_document;
    }

    void rescan() {
        devicesPage.setScanning(true);
        workers.execute(() -> {
            broadcast();
            runOnUiThread(() -> {
                devicesPage.setScanning(false);
                refreshAll();
                snack(onlineCount() == 0 ? "没有发现设备，检查两台设备是否在同一网络" : "已发现 " + onlineCount() + " 台设备");
            });
        });
    }

    void openSearchPicker() {
        pushPage(new SearchPage(this, null));
    }

    void openSearch(String deviceId) {
        pushPage(new SearchPage(this, deviceId));
    }

    // ==================================================================
    // 静态时间格式化
    // ==================================================================
    static String when(long time) {
        if (time <= 0) return "";
        Calendar now = Calendar.getInstance();
        Calendar that = Calendar.getInstance();
        that.setTimeInMillis(time);
        boolean sameYear = now.get(Calendar.YEAR) == that.get(Calendar.YEAR);
        int diff = now.get(Calendar.DAY_OF_YEAR) - that.get(Calendar.DAY_OF_YEAR);
        if (sameYear && diff == 0) return new SimpleDateFormat("HH:mm", Locale.US).format(new Date(time));
        if (sameYear && diff == 1) return "昨天";
        if (sameYear) return (that.get(Calendar.MONTH) + 1) + "月" + that.get(Calendar.DAY_OF_MONTH) + "日";
        return that.get(Calendar.YEAR) + "/" + (that.get(Calendar.MONTH) + 1) + "/" + that.get(Calendar.DAY_OF_MONTH);
    }

    static String dayLabel(long time) {
        Calendar now = Calendar.getInstance();
        Calendar that = Calendar.getInstance();
        that.setTimeInMillis(time);
        boolean sameYear = now.get(Calendar.YEAR) == that.get(Calendar.YEAR);
        int diff = now.get(Calendar.DAY_OF_YEAR) - that.get(Calendar.DAY_OF_YEAR);
        if (sameYear && diff == 0) return "今天";
        if (sameYear && diff == 1) return "昨天";
        return (that.get(Calendar.MONTH) + 1) + "月" + that.get(Calendar.DAY_OF_MONTH) + "日";
    }

    static String timeOnly(long time) {
        return new SimpleDateFormat("HH:mm", Locale.US).format(new Date(time));
    }

    // ==================================================================
    // 发送（融合输入：附件 + 文本，一次出队）
    // ==================================================================
    private volatile long fileBaseDone;

    void send(String deviceId, List<Uri> items, String text, Runnable onDone) {
        final Peer target = findPeer(deviceId);
        if (target == null) { snack("设备已离线，请重新连接"); return; }
        if (!busy.compareAndSet(false, true)) { snack("已有传输正在进行"); return; }
        cancelled.set(false);
        cancelledByUser = false;
        resultShownForBatch = false;
        batchIncoming = false;
        batchPeerName = target.name;
        batchDeviceId = deviceId;
        batchTotal = 0;
        batchDone = 0;
        fileBaseDone = 0;
        activePercent = 0;
        batchFileName = "";
        synchronized (batchMessages) { batchMessages.clear(); }

        final List<ChatStore.Msg> fileMsgs = new ArrayList<>();
        for (Uri uri : items) {
            String name = displayName(uri);
            long size = Math.max(0, querySize(uri));
            ChatStore.Msg m = ChatStore.Msg.file(true, name, size, ChatStore.SENDING);
            m.src = uri.toString();
            resendUris.put(m.msgId, uri);
            fileMsgs.add(m);
        }
        final ChatStore.Msg textMsg = text == null || text.isEmpty() ? null
            : ChatStore.Msg.text(true, text, ChatStore.SENDING);
        List<ChatStore.Msg> all = new ArrayList<>(fileMsgs);
        if (textMsg != null) all.add(textMsg);
        ChatStore.appendAll(this, deviceId, all);
        synchronized (batchMessages) { batchMessages.addAll(all); }
        activeMsgId = fileMsgs.isEmpty() ? null : fileMsgs.get(0).msgId;
        runOnUiThread(this::refreshAll);

        workers.execute(() -> {
            boolean ok = true;
            String error = null;
            try {
                if (!items.isEmpty()) {
                    List<FileToSend> staged = new ArrayList<>();
                    try {
                        log("正在准备 " + items.size() + " 个文件…");
                        for (Uri uri : items) {
                            FileToSend item = new FileToSend();
                            item.name = displayName(uri);
                            item.path = item.name;
                            staged.add(item);
                            stageFile(uri, item);
                            batchTotal += item.size();
                        }
                        sendBatch(target, staged, batchTotal, fileMsgs, deviceId);
                    } finally {
                        for (FileToSend item : staged) item.cleanup();
                    }
                }
                if (textMsg != null) {
                    sendTextSocket(target, text);
                    ChatStore.updateState(this, deviceId, textMsg.msgId, ChatStore.SENT);
                }
            } catch (Exception e) {
                ok = false;
                error = e.getMessage();
                for (ChatStore.Msg m : fileMsgs) ChatStore.updateState(this, deviceId, m.msgId, ChatStore.FAILED);
                if (textMsg != null) ChatStore.updateState(this, deviceId, textMsg.msgId, ChatStore.FAILED);
                Logs.add(Logs.ERROR, "发送失败：" + e);
            } finally {
                activeMsgId = null;
                finishTransfer();
                final boolean success = ok;
                final String err = error;
                runOnUiThread(() -> {
                    refreshAll();
                    if (success) {
                        if (onDone != null) onDone.run();
                        snack("已发送给 " + target.name);
                    } else {
                        snack(cancelledByUser ? "传输已取消" : "发送未完成：" + err);
                    }
                });
            }
        });
    }

    /**
     * 按 TCP 优先发送：AP 隔离网络里单播 TCP 根本建不起来（连接超时 / 不可达），
     * 这时不报错，直接改走 UDP 广播通道 —— 前提是路由器放行客户端之间的广播。
     */
    private void sendBatch(Peer target, List<FileToSend> files, long total,
                           List<ChatStore.Msg> fileMsgs, String deviceKey) throws Exception {
        try {
            sendBatchTcp(target, files, total, fileMsgs, deviceKey);
        } catch (ConnectException | NoRouteToHostException | SocketTimeoutException unreachable) {
            checkCancelled();
            Logs.add(Logs.WARNING, "TCP 不可达（" + unreachable.getMessage() + "），改用 UDP 广播通道");
            sendBatchBroadcast(files, total, fileMsgs, deviceKey);
        }
    }

    private void sendBatchTcp(Peer target, List<FileToSend> files, long total,
                              List<ChatStore.Msg> fileMsgs, String deviceKey) throws Exception {
        try (Socket socket = new Socket()) {
            checkCancelled();
            activeSocket = socket;
            wifiNetwork().bindSocket(socket);
            socket.connect(new InetSocketAddress(target.ip, target.port), 5000);
            socket.setSoTimeout(180000);
            OutputStream out = new BufferedOutputStream(socket.getOutputStream(), 128 * 1024);
            InputStream in = socket.getInputStream();
            JSONArray names = new JSONArray();
            int count = 0;
            for (FileToSend file : files) { names.put(file.name); count += file.count(); }
            Wire.message(out, new JSONObject().put("Type", "TRANSFER_REQUEST").put("SenderName", deviceName)
                .put("TotalFiles", count).put("TotalSize", total).put("ContainsFolders", false)
                .put("PayloadFolderCount", 0).put("PayloadFileCount", files.size())
                .put("RootElementNames", names).toString());
            log("等待 " + target.name + " 接受…");
            JSONObject response = new JSONObject(Wire.message(in));
            if (!response.optBoolean("Accepted"))
                throw new IOException(response.optString("Reason", "对方拒绝了传输"));
            long base = 0;
            for (int i = 0; i < files.size(); i++) {
                FileToSend file = files.get(i);
                checkCancelled();
                fileBaseDone = base;
                activeMsgId = i < fileMsgs.size() ? fileMsgs.get(i).msgId : null;
                activePercent = 0;
                Wire.message(out, "{\"Type\":\"FILE_BEGIN\"}");
                Wire.message(out, new JSONObject().put("RelativePath", file.path)
                    .put("RootName", file.name).put("Size", file.cache.length()).toString());
                streamFile(out, file);
                base += file.cache.length();
                batchDone = base;
                activeMsgId = null;
                if (i < fileMsgs.size())
                    ChatStore.updateState(this, deviceKey, fileMsgs.get(i).msgId, ChatStore.SENT);
                runOnUiThread(this::refreshAll);
            }
            Wire.message(out, "{\"Type\":\"TRANSFER_END\"}");
            log("已发送 " + count + " 个文件");
        }
    }

    private void streamFile(OutputStream out, FileToSend file) throws Exception {
        try (InputStream source = new FileInputStream(file.cache)) {
            byte[] buffer = new byte[128 * 1024];
            long done = 0;
            int n;
            while ((n = source.read(buffer)) != -1) {
                checkCancelled();
                out.write(buffer, 0, n);
                done += n;
                progress(file.path, done, file.cache.length());
            }
        }
    }

    private void sendTextSocket(Peer target, String content) throws Exception {
        try {
            sendTextSocketTcp(target, content);
        } catch (ConnectException | NoRouteToHostException | SocketTimeoutException unreachable) {
            checkCancelled();
            Logs.add(Logs.WARNING, "TCP 文本通道不可达，改用 UDP 广播通道");
            sendTextBroadcast(content);
        }
    }

    private void sendTextSocketTcp(Peer target, String content) throws Exception {
        try (Socket socket = new Socket()) {
            checkCancelled();
            activeSocket = socket;
            wifiNetwork().bindSocket(socket);
            socket.connect(new InetSocketAddress(target.ip, target.port), 8000);
            socket.setSoTimeout(20000);
            OutputStream out = new BufferedOutputStream(socket.getOutputStream(), 8192);
            InputStream in = socket.getInputStream();
            Wire.message(out, new JSONObject()
                .put("Type", "TEXT_MESSAGE")
                .put("SenderName", deviceName)
                .put("Text", content)
                .put("SentAtUnixMs", System.currentTimeMillis())
                .put("MessageId", UUID.randomUUID().toString().replace("-", ""))
                .toString());
            JSONObject ack = new JSONObject(Wire.message(in));
            if (!ack.optBoolean("Accepted"))
                throw new IOException(ack.optString("Reason", "对方未接收这条文本"));
        }
    }

    /**
     * 广播发送一批文件：每个文件一次独立握手，但共用 batch 标识，
     * 接收端因此只询问一次保存位置，后续文件直接沿用。
     */
    private void sendBatchBroadcast(List<FileToSend> files, long total,
                                    List<ChatStore.Msg> fileMsgs, String deviceKey) throws Exception {
        checkCancelled();
        Logs.add(Logs.INFO, "开始广播发送 " + files.size() + " 个文件");
        UdpChannel channel = UdpChannel.sender(wifiNetwork(), UdpWire.TRANSFER_PORT, udpBroadcastTargets());
        try {
            String batch = UUID.randomUUID().toString().replace("-", "");
            long base = 0;
            for (int i = 0; i < files.size(); i++) {
                final FileToSend file = files.get(i);
                checkCancelled();
                fileBaseDone = base;
                activeMsgId = i < fileMsgs.size() ? fileMsgs.get(i).msgId : null;
                activePercent = 0;
                UdpSender.sendFile(channel, file.cache, file.name, file.path, file.name, deviceName,
                    batch, i, files.size(),
                    done -> progress(file.path, done, file.cache.length()),
                    this::checkCancelled);
                base += file.cache.length();
                batchDone = base;
                activeMsgId = null;
                if (i < fileMsgs.size())
                    ChatStore.updateState(this, deviceKey, fileMsgs.get(i).msgId, ChatStore.SENT);
                runOnUiThread(this::refreshAll);
            }
        } finally {
            channel.close();
        }
        log("已通过广播通道发送 " + files.size() + " 个文件");
    }

    private void sendTextBroadcast(String content) throws Exception {
        try (UdpChannel channel = UdpChannel.sender(wifiNetwork(), UdpWire.TRANSFER_PORT, udpBroadcastTargets())) {
            UdpSender.sendText(channel, content, deviceName, this::checkCancelled);
        }
        log("已通过广播通道发送文本（" + content.length() + " 字）");
    }

    /** 按链路前缀算子网定向广播地址（与方案一致：不要照抄示例地址，必须按掩码算）。 */
    private List<InetAddress> udpBroadcastTargets() throws IOException {
        List<InetAddress> targets = new ArrayList<>();
        for (LinkAddress link : wifiAddresses()) {
            byte[] own = link.getAddress().getAddress();
            if (own.length != 4) continue;
            byte[] broadcast = own.clone();
            int bits = link.getPrefixLength();
            for (int i = 0; i < 4; i++) {
                int mask = bits >= 8 ? 255 : bits <= 0 ? 0 : (255 << (8 - bits)) & 255;
                bits -= 8;
                broadcast[i] = (byte) ((broadcast[i] & 255) | (~mask & 255));
            }
            targets.add(InetAddress.getByAddress(broadcast));
        }
        return targets;
    }

    /**
     * AP 隔离模式常驻监听：TCP 端口之外再开一个 UDP 广播接收端。
     * 与 TCP 复用同一套界面流程（自动接收 / 接收确认浮层 / 进度条 / 结果页）。
     */
    private void startBroadcastReceiver() {
        try {
            List<InetAddress> targets = udpBroadcastTargets();
            if (targets.isEmpty()) {
                Logs.add(Logs.WARNING, "未获取到 Wi-Fi 广播地址，AP 隔离通道未启用");
                return;
            }

            udpReceiver = new UdpReceiver(UdpWire.TRANSFER_PORT, wifiNetwork(), targets, getCacheDir(),
                new UdpReceiver.Host() {
                    @Override public boolean accept(JSONObject request, String ip) throws Exception {
                        batchIncoming = true;
                        batchPeerName = request.optString("SenderName", "对方");
                        batchTotal = request.optLong("TotalSize", 0);
                        batchDone = 0;
                        fileBaseDone = 0;
                        activePercent = 0;
                        batchDeviceId = resolveDeviceId(ip);
                        return askReceiveBroadcast(request);
                    }

                    @Override public void progress(String fileName, long done, long size) {
                        MainActivity.this.progress(fileName, done, size);
                        runOnUiThread(MainActivity.this::updateTransferBar);
                    }

                    @Override public void completed(JSONObject meta, File part, String ip) throws Exception {
                        String path = meta.optString("path", meta.optString("name", "received"));
                        long size = meta.optLong("size", 0);
                        String deviceKey = resolveDeviceId(ip);
                        PeerStore.remember(MainActivity.this, deviceKey,
                            meta.optString("sender", "对方"), ip, "PC");

                        ChatStore.Msg msg = ChatStore.Msg.file(false, path, size, ChatStore.RECEIVING);
                        ChatStore.append(MainActivity.this, deviceKey, msg);
                        activeMsgId = msg.msgId;
                        runOnUiThread(MainActivity.this::refreshAll);

                        importBroadcastFile(part, path, size);

                        ChatStore.updateState(MainActivity.this, deviceKey, msg.msgId, ChatStore.RECEIVED);
                        batchDone = size;
                        activeMsgId = null;
                        runOnUiThread(() -> {
                            refreshAll();
                            snack("接收完成：" + path + "，已保存到「" + receiveLabel() + "」");
                        });
                    }

                    @Override public void textReceived(JSONObject meta, String content, String ip) throws Exception {
                        String deviceKey = resolveDeviceId(ip);
                        String sender = meta.optString("sender", "对方");
                        ChatStore.append(MainActivity.this, deviceKey,
                            ChatStore.Msg.text(false, content, ChatStore.RECEIVED));
                        PeerStore.remember(MainActivity.this, deviceKey, sender, ip, "PC");
                        runOnUiThread(() -> {
                            if (threadId.equals(deviceKey)) markRead(deviceKey);
                            refreshAll();
                            snack("收到 " + sender + " 的文本消息");
                        });
                    }

                    @Override public void log(String message, boolean warning) {
                        Logs.add(warning ? Logs.WARNING : Logs.INFO, message);
                    }

                    @Override public List<InetAddress> localAddresses() {
                        List<InetAddress> list = new ArrayList<>();
                        try {
                            for (LinkAddress link : wifiAddresses()) list.add(link.getAddress());
                        } catch (Exception ignored) { }
                        return list;
                    }
                });
            udpReceiver.start();
            Logs.add(Logs.SUCCESS, "UDP 广播通道已启动：" + UdpWire.TRANSFER_PORT
                + "（AP 隔离可用，广播测试端口 " + UdpWire.PROBE_PORT + "）");

            udpProbe = new UdpProbe(UdpWire.PROBE_PORT, wifiNetwork(), targets);
            udpProbe.start();
        } catch (Exception e) {
            Logs.add(Logs.WARNING, "UDP 广播通道启动失败：" + e.getMessage());
        }
    }

    /** 广播通道的接收确认：没有 activeSocket 可判断，自动接收时只要进程活着就收。 */
    private boolean askReceiveBroadcast(JSONObject request) throws Exception {
        if (!useMediaStore() && saveTree == null)
            throw new IOException("接收目录不可用，请在「接收位置」里点「恢复默认」");
        if (autoReceive()) return running;
        return awaitReceiveConsent(request);
    }

    /** 把广播收到的临时文件导入接收位置（媒体库「下载」或用户选的 SAF 目录）。 */
    private void importBroadcastFile(File part, String path, long size) throws Exception {
        receivedPaths.clear();
        Map<String, Uri> directories = new HashMap<>();
        if (!useMediaStore()) {
            Uri base = DocumentsContract.buildDocumentUriUsingTree(saveTree,
                DocumentsContract.getTreeDocumentId(saveTree));
            Uri root = DocumentsContract.createDocument(getContentResolver(), base,
                DocumentsContract.Document.MIME_TYPE_DIR, "EtherTransfer-" + System.currentTimeMillis());
            if (root == null) throw new IOException("无法创建接收文件夹，请重新选择");
            directories.put("", root);
        }
        try (InputStream in = new BufferedInputStream(new FileInputStream(part), 128 * 1024)) {
            receiveFile(in, directories, path, size, new HashMap<>());
        }
    }

    private void progress(String name, long done, long size) {
        batchFileName = name;
        if (size > 0) activePercent = (int) Math.min(100, done * 100 / size);
        batchDone = fileBaseDone + done;
    }

    private void stageFile(Uri uri, FileToSend item) throws Exception {
        checkCancelled();
        item.cache = File.createTempFile("ethertransfer-", ".send", getCacheDir());
        try (InputStream in = getContentResolver().openInputStream(uri);
             OutputStream out = new FileOutputStream(item.cache)) {
            if (in == null) throw new IOException("无法读取文件：" + item.path);
            byte[] buffer = new byte[128 * 1024];
            int count;
            while ((count = in.read(buffer)) != -1) {
                checkCancelled();
                out.write(buffer, 0, count);
            }
        }
    }

    private void checkCancelled() throws IOException {
        if (!running || cancelled.get() || Thread.currentThread().isInterrupted())
            throw new IOException("已取消");
    }

    /**
     * 离线补发（借鉴 LANChat 的 pending 队列）：设备重新上线时，把该会话里失败过的
     * 发出消息按原顺序重发一次。纯文本直接重发；文件依赖持久化的源 URI（重启后仍有效）。
     */
    private void resendPending(String deviceId) {
        if (!running) return;
        List<ChatStore.Msg> pending = new ArrayList<>();
        for (ChatStore.Msg m : ChatStore.load(this, deviceId))
            if (m.out && ChatStore.FAILED.equals(m.state)) pending.add(m);
        if (pending.isEmpty()) return;
        final Peer target = findPeer(deviceId);
        if (target == null) { Logs.add(Logs.WARNING, "补发跳过：设备不在线"); return; }
        if (!busy.compareAndSet(false, true)) { Logs.add(Logs.WARNING, "补发跳过：正在传输"); return; }

        Logs.add(Logs.INFO, "「" + target.name + "」上线，准备补发 " + pending.size() + " 条未送达消息");
        resultShownForBatch = true;   // 补发不弹整页结果，只在完成后提示
        cancelled.set(false);
        batchIncoming = false;
        batchPeerName = target.name;
        batchDeviceId = deviceId;
        batchTotal = 0;
        batchDone = 0;
        fileBaseDone = 0;
        activePercent = 0;

        workers.execute(() -> {
            int done = 0;
            try {
                for (ChatStore.Msg m : pending) {
                    if (!running) break;
                    try {
                        if (!m.file) {
                            sendTextSocket(target, m.text);
                        } else {
                            if (m.src == null || m.src.isEmpty()) continue;
                            FileToSend item = new FileToSend();
                            item.name = m.fileName;
                            item.path = m.fileName;
                            stageFile(Uri.parse(m.src), item);
                            batchTotal = item.size();
                            List<ChatStore.Msg> one = new ArrayList<>();
                            one.add(m);
                            sendBatch(target, java.util.Collections.singletonList(item),
                                item.size(), one, deviceId);
                            item.cleanup();
                        }
                        ChatStore.updateState(this, deviceId, m.msgId, ChatStore.SENT);
                        done++;
                    } catch (Exception e) {
                        Logs.add(Logs.WARNING, "补发中断，稍后再试：" + e.getMessage());
                        break;
                    }
                }
            } finally {
                finishTransfer();
                final int count = done;
                runOnUiThread(() -> {
                    refreshAll();
                    if (count > 0) snack("已自动补发 " + count + " 条未送达消息");
                });
            }
        });
    }

    private void finishTransfer() {
        Socket socket = activeSocket;
        activeSocket = null;
        if (socket != null) try { socket.close(); } catch (IOException ignored) { }
        busy.set(false);
        runOnUiThread(this::updateTransferBar);
    }

    private void log(String message) {
        Logs.add(Logs.INFO, message);
    }

    // ==================================================================
    // 网络（发现 / 监听 / 接收）——与 v1–v3 一致
    // ==================================================================
    private Network wifiNetwork() throws IOException {
        ConnectivityManager cm = (ConnectivityManager) getSystemService(CONNECTIVITY_SERVICE);
        for (Network n : cm.getAllNetworks()) {
            NetworkCapabilities caps = cm.getNetworkCapabilities(n);
            if (caps != null && caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI)) return n;
        }
        throw new IOException("请先连接 Wi-Fi");
    }

    private List<LinkAddress> wifiAddresses() throws IOException {
        ConnectivityManager cm = (ConnectivityManager) getSystemService(CONNECTIVITY_SERVICE);
        LinkPropertiesHolder holder = new LinkPropertiesHolder(cm.getLinkProperties(wifiNetwork()));
        if (holder.links == null) throw new IOException("Wi-Fi 尚未分配 IP");
        return holder.links;
    }

    /** 避免直接 import android.net.LinkProperties 与局部变量重名。 */
    private static final class LinkPropertiesHolder {
        final List<LinkAddress> links;
        LinkPropertiesHolder(android.net.LinkProperties properties) {
            links = properties == null ? null : properties.getLinkAddresses();
        }
    }

    private boolean localSource(InetAddress address) throws IOException {
        byte[] target = address.getAddress();
        if (target.length != 4) return false;
        for (LinkAddress link : wifiAddresses()) {
            byte[] own = link.getAddress().getAddress();
            if (own.length != 4 || Arrays.equals(target, own)) continue;
            int bits = link.getPrefixLength();
            boolean matches = true;
            for (int i = 0; i < 4; i++) {
                int mask = bits >= 8 ? 255 : bits <= 0 ? 0 : (255 << (8 - bits)) & 255;
                if ((target[i] & mask) != (own[i] & mask)) matches = false;
                bits -= 8;
            }
            if (matches) return true;
        }
        return false;
    }

    private JSONObject announcement(String type) throws Exception {
        return new JSONObject().put("Id", "EtherTransferApp-V1").put("Type", type)
            .put("SessionId", session).put("DeviceId", deviceId)
            .put("ComputerName", deviceName).put("TcpPort", server.getLocalPort())
            .put("OS", "Android").put("SequenceNumber", sequence.incrementAndGet());
    }

    /**
     * 发现广播（借鉴 LANChat 的「智能路由遍历」）：一次性向三类目标发送 HELLO ——
     * <ol>
     *   <li>本网段的定向广播地址（按链路前缀算，普通路由器最稳）；</li>
     *   <li>受限广播 255.255.255.255 与组播 224.0.0.167（AP 隔离广播时组播仍可能放行）；</li>
     *   <li>手动添加的设备（单播，跨 VLAN / 热点隔离时唯一可行）。</li>
     * </ol>
     * 目标地址没有对应网卡时内核会立刻丢弃，不会造成风暴。
     */
    private void broadcast() {
        try {
            byte[] payload = announcement("HELLO").toString().getBytes(StandardCharsets.UTF_8);
            try (DatagramSocket socket = new DatagramSocket(null)) {
                wifiNetwork().bindSocket(socket);
                socket.setBroadcast(true);
                socket.bind(new InetSocketAddress(0));
                Set<InetAddress> targets = new LinkedHashSet<>();
                for (LinkAddress link : wifiAddresses()) {
                    byte[] own = link.getAddress().getAddress();
                    if (own.length != 4) continue;
                    byte[] broadcast = own.clone();
                    int bits = link.getPrefixLength();
                    for (int i = 0; i < 4; i++) {
                        int mask = bits >= 8 ? 255 : bits <= 0 ? 0 : (255 << (8 - bits)) & 255;
                        bits -= 8;
                        broadcast[i] = (byte) ((broadcast[i] & 255) | (~mask & 255));
                    }
                    targets.add(InetAddress.getByAddress(broadcast));
                }
                targets.add(InetAddress.getByName("255.255.255.255"));
                targets.add(InetAddress.getByName(MULTICAST_GROUP));
                for (InetAddress target : targets) {
                    try {
                        socket.send(new DatagramPacket(payload, payload.length, target, DISCOVERY_PORT));
                    } catch (Exception ignored) { }
                }
                for (String peer : manualPeers()) {
                    try {
                        String host = peer;
                        int port = DISCOVERY_PORT;
                        int colon = peer.lastIndexOf(':');
                        if (colon > 0) {
                            host = peer.substring(0, colon);
                            port = Integer.parseInt(peer.substring(colon + 1));
                        }
                        socket.send(new DatagramPacket(payload, payload.length,
                            InetAddress.getByName(host), port));
                    } catch (Exception ignored) { }
                }
            }
        } catch (Exception e) {
            Logs.add(Logs.WARNING, "设备发现失败：" + e.getMessage());
        }
    }

    // ---------- 手动添加的设备（IP[:端口]） ----------
    Set<String> manualPeers() {
        Set<String> stored = prefs().getStringSet("manualPeers", null);
        return stored == null ? new LinkedHashSet<>() : new LinkedHashSet<>(stored);
    }

    void addManualPeer(String host, int port) {
        String value = port > 0 && port != DISCOVERY_PORT ? host + ":" + port : host;
        Set<String> peers = manualPeers();
        peers.add(value);
        prefs().edit().putStringSet("manualPeers", peers).apply();
        Logs.add(Logs.INFO, "手动添加设备 " + value + "，立即单播探测");
        broadcast();
        snack("已添加 " + value + "，正在探测");
    }

    void removeManualPeer(String value) {
        Set<String> peers = manualPeers();
        peers.remove(value);
        prefs().edit().putStringSet("manualPeers", peers).apply();
        snack("已移除 " + value);
    }

    /** 发现端口：绑定 50000 并加入组播组；组播失败时降级为普通 UDP（只靠广播）。 */
    private DatagramSocket openDiscoverySocket() throws IOException {
        MulticastSocket socket = new MulticastSocket(null);
        socket.setReuseAddress(true);
        socket.bind(new InetSocketAddress(DISCOVERY_PORT));
        try {
            NetworkInterface nif = wifiInterface();
            if (nif != null) {
                socket.setNetworkInterface(nif);
                socket.joinGroup(new InetSocketAddress(
                    InetAddress.getByName(MULTICAST_GROUP), DISCOVERY_PORT), nif);
                Logs.add(Logs.INFO, "已加入组播 " + MULTICAST_GROUP + "（" + nif.getName() + "）");
            }
        } catch (Exception e) {
            Logs.add(Logs.WARNING, "加入组播失败，改用广播发现：" + e.getMessage());
        }
        return socket;
    }

    private NetworkInterface wifiInterface() {
        try {
            ConnectivityManager cm = (ConnectivityManager) getSystemService(CONNECTIVITY_SERVICE);
            android.net.LinkProperties properties = cm.getLinkProperties(wifiNetwork());
            String name = properties == null ? null : properties.getInterfaceName();
            return name == null ? null : NetworkInterface.getByName(name);
        } catch (Exception e) {
            return null;
        }
    }

    private void startNetworking() {
        // 同一进程只允许一套网络栈：多个 ServerSocket 会让积压队列填满、SYN 被内核丢弃。
        if (!netStarted.compareAndSet(false, true)) return;
        try {
            discovery = openDiscoverySocket();
            server = new ServerSocket(0);
            workers.execute(this::listenDiscovery);
            timer.scheduleWithFixedDelay(this::broadcast, 0, 2, TimeUnit.SECONDS);
            Logs.add(Logs.SUCCESS, "TCP 监听端口 " + server.getLocalPort() + "，发现端口 " + DISCOVERY_PORT);
            startBroadcastReceiver();
            while (running) {
                // accept 循环里只允许 accept() 这一个阻塞调用，其余全部进独立任务。
                final Socket socket = server.accept();
                workers.execute(() -> {
                    try {
                        handleInbound(socket);
                    } catch (Throwable t) {
                        Log.e("EtherTransfer", "inbound failed", t);
                        Logs.add(Logs.ERROR, "连接处理失败：" + t.getMessage());
                        try { socket.close(); } catch (IOException ignored) { }
                    }
                });
            }
        } catch (Exception e) {
            if (server != null) try { server.close(); } catch (IOException ignored) { }
            if (discovery != null) try { discovery.close(); } catch (Exception ignored) { }
            if (running) Logs.add(Logs.ERROR, "网络服务异常：" + e.getMessage());
        }
    }

    private void handleInbound(Socket socket) {
        String remote = socket.getInetAddress() == null ? "未知" : socket.getInetAddress().getHostAddress();
        Logs.add(Logs.INFO, "入站连接 " + remote);
        boolean local;
        try { local = localSource(socket.getInetAddress()); }
        catch (Exception e) { local = false; }
        if (!local) {
            Logs.add(Logs.WARNING, "拒绝来源不在同一网段的连接：" + remote);
            try (Socket rejected = socket) {
                rejected.setSoTimeout(2000);
                Wire.message(rejected.getOutputStream(), new JSONObject()
                    .put("Type", "TRANSFER_RESPONSE").put("Accepted", false)
                    .put("Reason", "手机端：连接来源不在同一网段，已拒绝").toString());
            } catch (Exception ignored) { }
            return;
        }
        if (!busy.compareAndSet(false, true)) {
            try (Socket rejected = socket) {
                rejected.setSoTimeout(3000);
                Wire.message(rejected.getInputStream());
                Wire.message(rejected.getOutputStream(), new JSONObject().put("Type", "TRANSFER_RESPONSE")
                    .put("Accepted", false).put("Reason", "设备正在处理另一项传输，请稍后重试。").toString());
            } catch (Exception ignored) { }
            return;
        }
        cancelled.set(false);
        cancelledByUser = false;
        resultShownForBatch = false;
        activeSocket = socket;
        try {
            receive(socket, remote);
        } catch (Exception e) {
            Logs.add(Logs.ERROR, "接收未完成：" + e.getMessage());
            runOnUiThread(() -> snack("接收未完成：" + e.getMessage()));
        } finally {
            finishTransfer();
            runOnUiThread(this::refreshAll);
        }
    }

    private void listenDiscovery() {
        byte[] bytes = new byte[65507];
        while (running) {
            try {
                DatagramPacket packet = new DatagramPacket(bytes, bytes.length);
                discovery.receive(packet);
                if (!localSource(packet.getAddress())) continue;
                JSONObject msg = new JSONObject(new String(packet.getData(), 0, packet.getLength(), StandardCharsets.UTF_8));
                if (!"EtherTransferApp-V1".equals(msg.optString("Id"))) continue;
                if (msg.optString("SessionId").equals(session)) continue;
                String ip = packet.getAddress().getHostAddress();
                String peerSession = msg.optString("SessionId", ip);
                String id = msg.optString("DeviceId");
                if (id.isEmpty()) id = ip;
                final String peerId = id;
                final String key = peerId + "|" + ip;
                if (retiredSessions.contains(peerSession)) continue;
                if ("BYE".equals(msg.optString("Type"))) {
                    Peer old = peers.get(key);
                    if (old != null && old.sessionId.equals(peerSession)) peers.remove(key);
                } else if ("HELLO".equals(msg.optString("Type")) || "HELLO_ACK".equals(msg.optString("Type"))) {
                    int port = msg.optInt("TcpPort");
                    if (port < 1 || port > 65535) continue;
                    for (Peer old : new ArrayList<>(peers.values())) {
                        if (old.ip.equals(ip) && !old.sessionId.equals(peerSession) && !old.id.equals(peerId)) {
                            retiredSessions.add(old.sessionId);
                            peers.entrySet().removeIf(entry -> entry.getValue().sessionId.equals(old.sessionId));
                            onlineHandled.remove(old.id);
                        }
                    }
                    String previous = activeSessions.put(peerId, peerSession);
                    if (previous != null && !previous.equals(peerSession)) {
                        // 同一设备换了会话（重启 / 重连）：清掉补发标记，让新一轮上线重新触发补发
                        retiredSessions.add(previous);
                        peers.entrySet().removeIf(entry -> entry.getValue().id.equals(peerId));
                        onlineHandled.remove(peerId);
                    }
                    Peer peer = new Peer();
                    peer.sessionId = peerSession;
                    peer.name = msg.optString("ComputerName", ip);
                    peer.ip = ip;
                    peer.id = peerId;
                    peer.port = port;
                    peer.seen = SystemClock.elapsedRealtime();
                    peer.os = msg.optString("OS", "PC");
                    boolean fresh = peers.put(key, peer) == null;
                    if (fresh && onlineHandled.add(peerId)) {
                        final String target = peerId;
                        workers.execute(() -> resendPending(target));
                    }
                    PeerStore.remember(this, peerId, peer.name, ip, peer.os);
                    if ("HELLO".equals(msg.optString("Type"))) {
                        byte[] reply = announcement("HELLO_ACK").toString().getBytes(StandardCharsets.UTF_8);
                        try (DatagramSocket socket = new DatagramSocket(null)) {
                            wifiNetwork().bindSocket(socket);
                            socket.bind(new InetSocketAddress(0));
                            socket.send(new DatagramPacket(reply, reply.length, packet.getAddress(), DISCOVERY_PORT));
                        }
                    }
                }
            } catch (Exception e) {
                if (!running) return;
            }
        }
    }

    /** 由来源 IP 反查设备 id（发现协议里带 DeviceId；未知设备回退为稳定别名）。 */
    private String resolveDeviceId(String ip) {
        for (Peer p : peers.values()) if (p.ip != null && p.ip.equals(ip)) return p.id;
        String fallback = "ip-" + ip.replace('.', '_').replace(':', '_');
        for (PeerStore.Known k : PeerStore.all(this).values()) {
            if (ip.equals(k.ip)) return k.id;
        }
        return fallback;
    }

    /** 自动接收（对应 LANChat 的 auto_download 开关）：默认开启，关闭后走 P10 阻断式确认。 */
    boolean autoReceive() {
        return prefs().getBoolean("autoReceive", true);
    }

    void toggleAutoReceive() {
        boolean next = !autoReceive();
        prefs().edit().putBoolean("autoReceive", next).apply();
        settingsPage.refresh();
        snack(next ? "已开启自动接收：对方发来文件会直接保存" : "已改为手动确认：接收前会询问");
    }

    private boolean askReceive(JSONObject request) throws Exception {
        if (!useMediaStore() && saveTree == null)
            throw new IOException("接收目录不可用，请在「接收位置」里点「恢复默认」");
        if (autoReceive()) return running && activeSocket != null && !activeSocket.isClosed();
        return awaitReceiveConsent(request);
    }

    /**
     * P10 接收传输请求（阻断式）：发送方 / 文件数 / 总大小 / 接收位置都要可见，
     * 用户必须选「接受」或「拒绝」。超时 120 s 视为拒绝，绝不让对方悬空等待。
     */
    private boolean awaitReceiveConsent(JSONObject request) throws Exception {
        final CountDownLatch latch = new CountDownLatch(1);
        final AtomicBoolean accepted = new AtomicBoolean(false);
        consent = latch;
        final String sender = request.optString("SenderName", "对方");
        final int count = request.optInt("TotalFiles", 0);
        final long size = request.optLong("TotalSize", 0);
        runOnUiThread(() -> showReceiveRequest(sender, count, size, accepted, latch));
        boolean released = latch.await(120, TimeUnit.SECONDS);
        consent = null;
        if (!released) {
            // 超时必须收掉浮层，否则它会一直盖在界面上挡住后续操作。
            runOnUiThread(this::dismissSheet);
            throw new IOException("等待用户确认接收超时");
        }
        return accepted.get() && running;
    }

    private void showReceiveRequest(String sender, int count, long size,
                                    AtomicBoolean accepted, CountDownLatch latch) {
        LinearLayout body = ui.col();
        body.setPadding(ui.dp(12), 0, ui.dp(12), ui.dp(8));

        LinearLayout head = ui.row();
        head.addView(ui.avatar(R.drawable.ic_device_desktop, ui.wifiBg, ui.wifiFg, 44),
            new LinearLayout.LayoutParams(ui.dp(44), ui.dp(44)));
        LinearLayout names = ui.col();
        LinearLayout.LayoutParams np = new LinearLayout.LayoutParams(0, -2, 1);
        np.leftMargin = ui.dp(12);
        head.addView(names, np);
        names.addView(ui.t(sender, 17, ui.fg, true));
        names.addView(ui.t("正在向你发送", 13, ui.muted, false));
        body.addView(head);

        ui.space(body, 14);
        body.addView(infoRow("内容", count + " 个文件 · " + ChatStore.sizeText(size)));
        body.addView(infoRow("接收位置", receiveLabel()));
        ui.space(body, 6);

        LinearLayout actions = ui.row();
        LinearLayout.LayoutParams ap = new LinearLayout.LayoutParams(-1, ui.dp(48));
        ap.topMargin = ui.dp(12);
        body.addView(actions, ap);
        TextView reject = ui.secondary("拒绝", () -> {
            accepted.set(false);
            dismissSheet();
            latch.countDown();
        });
        actions.addView(reject, new LinearLayout.LayoutParams(0, ui.dp(48), 1));
        TextView accept = ui.primary("接受", () -> {
            accepted.set(true);
            dismissSheet();
            latch.countDown();
        });
        LinearLayout.LayoutParams accp = new LinearLayout.LayoutParams(0, ui.dp(48), 1);
        accp.leftMargin = ui.dp(12);
        actions.addView(accept, accp);

        TextView note = ui.t("等待你的确认，对方仍在连接", 11, ui.muted, false);
        note.setGravity(Gravity.CENTER);
        LinearLayout.LayoutParams noteParams = new LinearLayout.LayoutParams(-1, -2);
        noteParams.topMargin = ui.dp(10);
        body.addView(note, noteParams);

        showBlockingSheet(body);
    }

    private void receive(Socket socket, String remoteIp) throws Exception {
        final String senderDeviceId = resolveDeviceId(remoteIp);
        final List<ChatStore.Msg> recvMsgs = new ArrayList<>();
        final Map<String, Long> recvSizes = new HashMap<>();
        batchIncoming = true;
        batchTotal = 0;
        batchDone = 0;
        fileBaseDone = 0;
        activePercent = 0;
        synchronized (batchMessages) { batchMessages.clear(); }
        try (Socket connection = socket) {
            socket.setSoTimeout(30000);
            InputStream in = new BufferedInputStream(socket.getInputStream(), 128 * 1024);
            OutputStream out = socket.getOutputStream();
            JSONObject request = new JSONObject(Wire.message(in));
            if ("TEXT_MESSAGE".equals(request.optString("Type"))) {
                String sender = request.optString("SenderName", "对方");
                String content = request.optString("Text", "");
                if (content.isEmpty()) throw new IOException("文本消息内容为空");
                if (content.length() > 20000) throw new IOException("文本过长，已拒绝");
                ChatStore.append(this, senderDeviceId, ChatStore.Msg.text(false, content, ChatStore.RECEIVED));
                PeerStore.remember(this, senderDeviceId, sender, remoteIp, "PC");
                Wire.message(out, new JSONObject().put("Type", "TEXT_ACK").put("Accepted", true)
                    .put("Reason", "").put("ReceivedAtUnixMs", System.currentTimeMillis()).toString());
                runOnUiThread(() -> {
                    if (threadId.equals(senderDeviceId)) markRead(senderDeviceId);
                    refreshAll();
                    snack("收到 " + sender + " 的文本消息");
                });
                Logs.add(Logs.SUCCESS, "收到来自 " + sender + " 的文本消息");
                return;
            }
            if (!"TRANSFER_REQUEST".equals(request.optString("Type"))
                || request.getInt("TotalFiles") < 0
                || request.getInt("TotalFiles") > 1000000
                || request.getLong("TotalSize") < 0) throw new IOException("传输请求无效");

            batchPeerName = request.optString("SenderName", peerTitle(senderDeviceId));
            batchDeviceId = senderDeviceId;
            batchTotal = request.optLong("TotalSize", 0);
            PeerStore.remember(this, senderDeviceId, batchPeerName, remoteIp, "PC");

            boolean accept;
            try {
                accept = askReceive(request);
            } catch (Exception consentError) {
                try {
                    Wire.message(out, new JSONObject().put("Type", "TRANSFER_RESPONSE").put("Accepted", false)
                        .put("Reason", "手机端：" + consentError.getMessage()).toString());
                } catch (Exception ignored) { }
                throw consentError;
            }
            Wire.message(out, new JSONObject().put("Type", "TRANSFER_RESPONSE")
                .put("Accepted", accept).put("Reason", accept ? "" : "用户已拒绝").toString());
            if (!accept) { log("已拒绝传输"); return; }

            JSONArray roots = request.optJSONArray("RootElementNames");
            if (roots != null) {
                for (int i = 0; i < roots.length(); i++)
                    recvMsgs.add(ChatStore.Msg.file(false, roots.optString(i), 0, ChatStore.RECEIVING));
                if (!recvMsgs.isEmpty()) {
                    ChatStore.appendAll(this, senderDeviceId, recvMsgs);
                    activeMsgId = recvMsgs.get(0).msgId;
                    synchronized (batchMessages) { batchMessages.addAll(recvMsgs); }
                    runOnUiThread(this::refreshAll);
                }
            }
            receivedPaths.clear();
            recvSizes.clear();
            Map<String, Uri> directories = new HashMap<>();
            if (!useMediaStore()) {
                Uri base = DocumentsContract.buildDocumentUriUsingTree(saveTree,
                    DocumentsContract.getTreeDocumentId(saveTree));
                Uri root = DocumentsContract.createDocument(getContentResolver(), base,
                    DocumentsContract.Document.MIME_TYPE_DIR, "EtherTransfer-" + System.currentTimeMillis());
                if (root == null) throw new IOException("无法创建接收文件夹，请重新选择");
                directories.put("", root);
            }
            int count = 0;
            while (true) {
                String type = new JSONObject(Wire.message(in)).getString("Type");
                if ("TRANSFER_END".equals(type)) break;
                if ("FILE_SKIP".equals(type)) continue;
                if ("FILE_BEGIN".equals(type)) {
                    JSONObject meta = new JSONObject(Wire.message(in));
                    receiveFile(in, directories, meta.getString("RelativePath"), meta.getLong("Size"), recvSizes);
                    count++;
                } else if ("FOLDER_BEGIN".equals(type)) {
                    JSONObject folder = new JSONObject(Wire.message(in));
                    if (!useMediaStore())
                        directory(directories, Wire.safePath(folder.getString("RootName")), 1);
                    count += Wire.folder(in, (path, size) -> {
                        try { receiveFile(in, directories, path, size, recvSizes); }
                        catch (IOException e) { throw e; }
                        catch (Exception e) { throw new IOException(e); }
                    });
                } else throw new IOException("不支持的传输标记：" + type);
                if (count > request.getInt("TotalFiles")) throw new IOException("文件数量超过传输请求");
                runOnUiThread(this::refreshAll);
            }
            for (ChatStore.Msg m : recvMsgs) {
                Long size = recvSizes.get(m.fileName);
                ChatStore.updateState(this, senderDeviceId, m.msgId, ChatStore.RECEIVED);
                if (size != null) ChatStore.updateSize(this, senderDeviceId, m.msgId, size);
            }
            final int receivedCount = count;
            ChatStore.append(this, senderDeviceId, ChatStore.Msg.system("已成功接收 " + receivedCount + " 个文件"));
            Logs.add(Logs.SUCCESS, "接收完成：" + receivedCount + " 个文件 → " + receiveLabel());
            runOnUiThread(() -> {
                refreshAll();
                snack("接收完成：" + receivedCount + " 个文件，已保存到「" + receiveLabel() + "」");
            });
        } catch (Exception e) {
            for (ChatStore.Msg m : recvMsgs) ChatStore.updateState(this, senderDeviceId, m.msgId, ChatStore.FAILED);
            ChatStore.append(this, senderDeviceId, ChatStore.Msg.system("接收未完成：" + e.getMessage()));
            throw e;
        } finally {
            activeMsgId = null;
            runOnUiThread(this::refreshAll);
        }
    }

    private Uri directory(Map<String, Uri> dirs, String[] parts, int length) throws Exception {
        String key = "";
        Uri parent = dirs.get("");
        for (int i = 0; i < length; i++) {
            key += "/" + parts[i];
            Uri child = dirs.get(key);
            if (child == null) {
                child = DocumentsContract.createDocument(getContentResolver(), parent,
                    DocumentsContract.Document.MIME_TYPE_DIR, parts[i]);
                if (child == null) throw new IOException("无法创建子文件夹");
                dirs.put(key, child);
            }
            parent = child;
        }
        return parent;
    }

    private void receiveFile(InputStream in, Map<String, Uri> dirs, String path, long size,
                             Map<String, Long> recvSizes) throws Exception {
        if (size < 0) throw new IOException("文件大小无效");
        String[] parts = Wire.safePath(path);
        String original = parts[parts.length - 1], finalName = original;
        String folderPath = path.substring(0, path.length() - original.length());
        for (int suffix = 1; !receivedPaths.add(folderPath + finalName); suffix++)
            finalName = original + " (" + suffix + ")";
        String rootName = path.contains("/") ? path.substring(0, path.indexOf('/')) : path;
        Long known = recvSizes.get(rootName);
        recvSizes.put(rootName, (known == null ? 0 : known) + size);

        fileBaseDone = batchDone;
        if (useMediaStore()) {
            receiveFileToDownloads(in, path, finalName, size);
            return;
        }
        Uri parent = directory(dirs, parts, parts.length - 1);
        Uri file = DocumentsContract.createDocument(getContentResolver(), parent,
            "application/octet-stream", UUID.randomUUID() + ".part");
        if (file == null) throw new IOException("无法创建接收文件");
        boolean complete = false;
        try {
            try (OutputStream out = getContentResolver().openOutputStream(file, "w")) {
                if (out == null) throw new IOException("无法写入接收文件");
                copyTo(in, out, size, path);
            }
            if (DocumentsContract.renameDocument(getContentResolver(), file, finalName) == null)
                throw new IOException("文件重命名失败");
            complete = true;
        } finally {
            if (!complete) try { DocumentsContract.deleteDocument(getContentResolver(), file); } catch (Exception ignored) { }
        }
    }

    /**
     * 直接写入系统「下载」目录（MediaStore.Downloads）。
     * Android 10 起应用可向「下载」贡献文件而无需存储权限，绕开 Android 11+ 的根目录授权限制。
     */
    private void receiveFileToDownloads(InputStream in, String path, String finalName, long size) throws Exception {
        String relative = MEDIA_BASE;
        int slash = path.lastIndexOf('/');
        if (slash > 0) relative = MEDIA_BASE + "/" + path.substring(0, slash);

        android.content.ContentValues values = new android.content.ContentValues();
        values.put(MediaStore.MediaColumns.DISPLAY_NAME, UUID.randomUUID() + ".part");
        values.put(MediaStore.MediaColumns.MIME_TYPE, mimeOf(finalName));
        values.put(MediaStore.MediaColumns.RELATIVE_PATH, relative);
        if (Build.VERSION.SDK_INT >= 29) values.put(MediaStore.MediaColumns.IS_PENDING, 1);

        Uri target = getContentResolver().insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values);
        if (target == null) throw new IOException("无法在「下载」目录创建文件（系统媒体库拒绝）");

        boolean complete = false;
        try {
            try (OutputStream out = getContentResolver().openOutputStream(target, "w")) {
                if (out == null) throw new IOException("无法写入接收文件");
                copyTo(in, out, size, path);
            }
            android.content.ContentValues finish = new android.content.ContentValues();
            finish.put(MediaStore.MediaColumns.IS_PENDING, 0);
            finish.put(MediaStore.MediaColumns.DISPLAY_NAME, finalName);
            int updated = getContentResolver().update(target, finish, null, null);
            if (updated <= 0) throw new IOException("文件已写入但无法重命名");
            complete = true;
        } finally {
            if (!complete) try { getContentResolver().delete(target, null, null); } catch (Exception ignored) { }
        }
    }

    private void copyTo(InputStream in, OutputStream out, long size, String path) throws IOException {
        byte[] buffer = new byte[128 * 1024];
        long done = 0;
        while (done < size) {
            int n = in.read(buffer, 0, (int) Math.min(buffer.length, size - done));
            if (n < 0) throw new EOFException("文件未接收完整");
            out.write(buffer, 0, n);
            done += n;
            progress(path, done, size);
        }
    }

    private static String mimeOf(String name) {
        int dot = name.lastIndexOf('.');
        String ext = dot < 0 ? "" : name.substring(dot + 1).toLowerCase(Locale.ROOT);
        switch (ext) {
            case "jpg": case "jpeg": return "image/jpeg";
            case "png": return "image/png";
            case "gif": return "image/gif";
            case "webp": return "image/webp";
            case "mp4": return "video/mp4";
            case "mp3": return "audio/mpeg";
            case "flac": return "audio/flac";
            case "wav": return "audio/wav";
            case "pdf": return "application/pdf";
            case "txt": case "md": return "text/plain";
            case "zip": return "application/zip";
            case "apk": return "application/vnd.android.package-archive";
            default: return "application/octet-stream";
        }
    }

    /** Android 10+ 默认直接写「下载」目录；只有用户显式指定文件夹时才用 SAF。 */
    private boolean useMediaStore() {
        return Build.VERSION.SDK_INT >= 29 && (!customTree || saveTree == null);
    }

    private void startKeepAlive() {
        try {
            Intent intent = new Intent(this, KeepAliveService.class);
            if (Build.VERSION.SDK_INT >= 26) startForegroundService(intent);
            else startService(intent);
        } catch (Exception e) {
            Logs.add(Logs.WARNING, "后台保活未启动：" + e.getMessage());
        }
    }

    // ==================================================================
    // 沉浸式 / 生命周期
    // ==================================================================
    private void applyImmersive() {
        if (Build.VERSION.SDK_INT >= 28) {
            WindowManager.LayoutParams attrs = getWindow().getAttributes();
            attrs.layoutInDisplayCutoutMode =
                WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_SHORT_EDGES;
            getWindow().setAttributes(attrs);
        }
        if (Build.VERSION.SDK_INT >= 30) {
            WindowInsetsController controller = getWindow().getInsetsController();
            if (controller != null) {
                controller.setSystemBarsBehavior(WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE);
                controller.hide(WindowInsets.Type.statusBars() | WindowInsets.Type.navigationBars());
            }
        } else {
            getWindow().getDecorView().setSystemUiVisibility(
                View.SYSTEM_UI_FLAG_LAYOUT_STABLE
                    | View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION
                    | View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN
                    | View.SYSTEM_UI_FLAG_HIDE_NAVIGATION
                    | View.SYSTEM_UI_FLAG_FULLSCREEN
                    | View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY);
        }
    }

    @Override public void onWindowFocusChanged(boolean hasFocus) {
        super.onWindowFocusChanged(hasFocus);
        if (hasFocus && running) applyImmersive();
    }

    @Override protected void onDestroy() {
        running = false;
        CountDownLatch pending = consent;
        if (pending != null) pending.countDown();
        finishTransfer();
        if (discovery != null) discovery.close();
        if (server != null) try { server.close(); } catch (IOException ignored) { }
        if (udpReceiver != null) udpReceiver.stop();
        if (udpProbe != null) udpProbe.stop();
        timer.shutdownNow();
        workers.shutdownNow();
        if (wifiLock != null && wifiLock.isHeld()) wifiLock.release();
        try { if (highPerfLock != null && highPerfLock.isHeld()) highPerfLock.release(); } catch (Exception ignored) { }
        try { if (cpuLock != null && cpuLock.isHeld()) cpuLock.release(); } catch (Exception ignored) { }
        try { stopService(new Intent(this, KeepAliveService.class)); } catch (Exception ignored) { }
        super.onDestroy();
    }
}
