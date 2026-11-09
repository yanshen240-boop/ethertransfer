package cn.ethertransfer.mobile;

import android.graphics.Color;
import android.view.Gravity;
import android.view.View;
import android.widget.FrameLayout;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;

/**
 * P13 设置（Tab 3）：本机 / 传输 / 存储 / 显示 / 关于。
 * <p>行 64 dp：图标 24（次要色）+ 标题 15 + 右值 13 + chevron 16；分组卡片圆角 12、1 dp 描边。</p>
 */
final class SettingsPage extends LinearLayout {
    private final MainActivity host;
    private final Ui ui;
    private final LinearLayout body;
    private String signature = "";

    SettingsPage(MainActivity host) {
        super(host);
        this.host = host;
        this.ui = host.ui();
        setOrientation(VERTICAL);
        setBackgroundColor(ui.bg);

        LinearLayout header = ui.row();
        header.setPadding(ui.dp(16), ui.dp(4), ui.dp(16), ui.dp(4));
        header.setMinimumHeight(ui.dp(56));
        header.setBackgroundColor(ui.surface);
        header.addView(ui.titleH1("设置"));
        addView(header, new LinearLayout.LayoutParams(-1, -2));
        addView(ui.divider(ui.border), new LinearLayout.LayoutParams(-1, Math.max(1, ui.dp(1))));

        ScrollView scroll = new ScrollView(host);
        scroll.setFillViewport(true);
        scroll.setClipToPadding(false);
        body = ui.col();
        body.setPadding(ui.dp(16), ui.dp(8), ui.dp(16), ui.dp(32));
        scroll.addView(body, new FrameLayout.LayoutParams(-1, -2));
        addView(scroll, new LinearLayout.LayoutParams(-1, 0, 1));
    }

    void refresh() {
        boolean notif = host.notificationsGranted();
        boolean battery = host.batteryOptimized();
        String sig = host.localName() + host.receiveLabel() + host.localIp() + notif + battery
            + host.receivedStats() + ChatStore.totalCount(host) + host.onlineCount()
            + host.autoReceive() + host.manualPeers();
        if (sig.equals(signature)) return;
        signature = sig;

        body.removeAllViews();
        if (!notif || battery) body.addView(permissionCard(notif, battery));

        body.addView(groupLabel("本机"));
        LinearLayout local = card();
        local.addView(row(R.drawable.ic_settings_edit, "本机名称", host.localName(), false, true, host::editName));
        local.addView(rowDivider());
        local.addView(row(R.drawable.ic_settings_storage, "接收位置", host.receiveLabel(), false, true, host::openReceiveLocation));
        local.addView(rowDivider());
        local.addView(row(R.drawable.ic_device_link_wifi, "本机地址", host.localIp(), false, false, null));
        body.addView(local);

        body.addView(groupLabel("传输"));
        LinearLayout transfer = card();
        transfer.addView(row(R.drawable.ic_common_sync, "后台常驻", "已开启", false, true, host::restartKeepAlive));
        transfer.addView(rowDivider());
        transfer.addView(row(R.drawable.ic_settings_notification, "通知权限",
            notif ? "已允许" : "去允许", false, true, host::requestNotifications));
        transfer.addView(rowDivider());
        transfer.addView(row(R.drawable.ic_settings_battery, "锁屏继续收发",
            battery ? "建议关闭电池优化" : "已就绪", false, true, host::openBatterySettings));
        transfer.addView(rowDivider());
        transfer.addView(row(R.drawable.ic_chat_download, "自动接收文件",
            host.autoReceive() ? "自动保存" : "接收前询问", false, true, host::toggleAutoReceive));
        transfer.addView(rowDivider());
        transfer.addView(row(R.drawable.ic_common_add, "手动添加设备",
            host.manualPeers().isEmpty() ? "未添加" : host.manualPeers().size() + " 个",
            false, true, host::showManualPeerDialog));
        body.addView(transfer);

        body.addView(groupLabel("存储"));
        LinearLayout storage = card();
        storage.addView(row(R.drawable.ic_chat_download, "已接收", host.receivedStats(), false, false, null));
        storage.addView(rowDivider());
        storage.addView(row(R.drawable.ic_common_delete, "清理会话记录", ChatStore.totalCount(host) + " 条", true, true,
            host::confirmClearAll));
        body.addView(storage);
        TextView note = ui.t("会话记录只存文字和文件名，不含文件本体；清理不会删除已收发的文件。", 12, ui.muted, false);
        note.setPadding(ui.dp(4), ui.dp(8), ui.dp(4), 0);
        note.setLineSpacing(ui.dp(4), 1f);
        body.addView(note);

        body.addView(groupLabel("显示"));
        LinearLayout display = card();
        display.addView(row(R.drawable.ic_settings_theme, "外观", "跟随系统", false, true, host::showThemeHint));
        body.addView(display);

        body.addView(groupLabel("关于"));
        LinearLayout about = card();
        about.addView(row(R.drawable.ic_common_info, "版本", host.appVersion(), false, false, null));
        about.addView(rowDivider());
        about.addView(row(R.drawable.ic_settings_log, "开发者日志", "", false, true, host::openLog));
        body.addView(about);
    }

    private TextView groupLabel(String text) {
        TextView v = ui.t(text, 11, ui.muted, false);
        v.setLetterSpacing(0.08f);
        v.setPadding(ui.dp(4), ui.dp(20), ui.dp(4), ui.dp(8));
        return v;
    }

    private LinearLayout card() {
        LinearLayout card = ui.col();
        card.setBackground(ui.box(ui.card, 12, 1, ui.cardBorder));
        return card;
    }

    private View rowDivider() {
        LinearLayout wrap = ui.col();
        wrap.setPadding(ui.dp(52), 0, 0, 0);
        wrap.addView(ui.divider(ui.border));
        return wrap;
    }

    private View row(int iconRes, String title, String value, boolean dangerText, boolean chevron,
                     Runnable action) {
        LinearLayout row = ui.row();
        row.setMinimumHeight(ui.dp(64));
        row.setPadding(ui.dp(16), ui.dp(10), ui.dp(16), ui.dp(10));
        row.addView(ui.icon(iconRes, 24, ui.fg2), new LinearLayout.LayoutParams(ui.dp(24), ui.dp(24)));
        TextView label = ui.t(title, 15, dangerText ? ui.danger : ui.fg, false);
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(0, -2, 1);
        lp.leftMargin = ui.dp(12);
        row.addView(label, lp);
        if (value != null && !value.isEmpty()) {
            TextView v = ui.t(value, 13, dangerText ? ui.danger : ui.muted, false);
            v.setSingleLine(true);
            v.setEllipsize(android.text.TextUtils.TruncateAt.MIDDLE);
            v.setMaxWidth(ui.dp(170));
            v.setGravity(Gravity.END);
            row.addView(v);
        }
        if (chevron) {
            LinearLayout.LayoutParams cp = new LinearLayout.LayoutParams(ui.dp(16), ui.dp(16));
            cp.leftMargin = ui.dp(6);
            row.addView(ui.chevron(), cp);
        }
        if (action != null) {
            ui.tap(row, action);
            row.setContentDescription(title + (value == null || value.isEmpty() ? "" : "，" + value));
        }
        return row;
    }

    /** P18：权限与系统引导，聚合为一张卡，最多两条。 */
    private View permissionCard(boolean notif, boolean battery) {
        LinearLayout card = ui.col();
        card.setBackground(ui.box(ui.warningBg, 16, 1, 0x33B45309));
        card.setPadding(ui.dp(16), ui.dp(14), ui.dp(16), ui.dp(6));
        LinearLayout head = ui.row();
        head.addView(ui.icon(R.drawable.ic_system_permission, 20, ui.warning),
            new LinearLayout.LayoutParams(ui.dp(20), ui.dp(20)));
        TextView title = ui.t("建议完成 " + ((notif ? 0 : 1) + (battery ? 1 : 0)) + " 项设置",
            15, ui.fg, true);
        LinearLayout.LayoutParams tp = new LinearLayout.LayoutParams(0, -2, 1);
        tp.leftMargin = ui.dp(10);
        head.addView(title, tp);
        card.addView(head);

        if (!notif) card.addView(guideRow("允许通知", "锁屏时才能收到传输提醒", host::requestNotifications));
        if (battery) card.addView(guideRow("关闭电池优化", "锁屏后仍可继续收发", host::openBatterySettings));
        return card;
    }

    private View guideRow(String title, String detail, Runnable action) {
        LinearLayout row = ui.row();
        row.setMinimumHeight(ui.dp(52));
        row.setPadding(0, ui.dp(8), 0, ui.dp(8));
        LinearLayout info = ui.col();
        info.addView(ui.t(title, 14, ui.fg, false));
        info.addView(ui.t(detail, 12, ui.muted, false));
        row.addView(info, new LinearLayout.LayoutParams(0, -2, 1));
        row.addView(ui.textButton("去设置", ui.accent, action));
        return row;
    }
}
