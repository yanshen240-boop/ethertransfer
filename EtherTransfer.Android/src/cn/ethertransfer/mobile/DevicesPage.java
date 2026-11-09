package cn.ethertransfer.mobile;

import android.graphics.Color;
import android.view.Gravity;
import android.view.View;
import android.view.animation.LinearInterpolator;
import android.view.animation.RotateAnimation;
import android.widget.FrameLayout;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;

import java.util.ArrayList;
import java.util.List;
import java.util.Map;

/**
 * P06 设备（附近，Tab 2）。
 * <p>本机摘要卡 88 dp + 附近设备行 76 dp（名称 / 链路胶囊 / 地址 / 连通点 / chevron）；
 * 离线设备不删除，55% 灰化放在末尾。空态给出三步排查。</p>
 */
final class DevicesPage extends LinearLayout {
    private final MainActivity host;
    private final Ui ui;
    private final TextView subtitle;
    private final LinearLayout body;
    private final ImageView refreshIcon;
    private String signature = "";
    private final FrameLayout refreshButton;

    DevicesPage(MainActivity host) {
        super(host);
        this.host = host;
        this.ui = host.ui();
        setOrientation(VERTICAL);
        setBackgroundColor(ui.surface);

        LinearLayout header = ui.row();
        header.setPadding(ui.dp(16), ui.dp(4), ui.dp(4), ui.dp(4));
        header.setMinimumHeight(ui.dp(56));
        LinearLayout titles = ui.col();
        titles.addView(ui.titleH1("设备"));
        subtitle = ui.t("正在发现", 12, ui.muted, false);
        LinearLayout.LayoutParams sp = new LinearLayout.LayoutParams(-1, -2);
        sp.topMargin = ui.dp(3);
        titles.addView(subtitle, sp);
        header.addView(titles, new LinearLayout.LayoutParams(0, -2, 1));

        refreshButton = new FrameLayout(host);
        refreshIcon = ui.icon(R.drawable.ic_common_refresh, 24, ui.fg2);
        FrameLayout.LayoutParams ip = new FrameLayout.LayoutParams(ui.dp(24), ui.dp(24));
        ip.gravity = Gravity.CENTER;
        refreshButton.addView(refreshIcon, ip);
        ui.tap(refreshButton, host::rescan);
        refreshButton.setContentDescription("重新扫描设备");
        header.addView(refreshButton, new LinearLayout.LayoutParams(ui.dp(48), ui.dp(48)));
        addView(header, new LinearLayout.LayoutParams(-1, -2));
        addView(ui.divider(ui.border), new LinearLayout.LayoutParams(-1, Math.max(1, ui.dp(1))));

        ScrollView scroll = new ScrollView(host);
        scroll.setFillViewport(true);
        scroll.setClipToPadding(false);
        body = ui.col();
        body.setPadding(ui.dp(16), ui.dp(12), ui.dp(16), ui.dp(24));
        scroll.addView(body, new FrameLayout.LayoutParams(-1, -2));
        addView(scroll, new LinearLayout.LayoutParams(-1, 0, 1));
    }

    void refresh() {
        List<MainActivity.Peer> online = new ArrayList<>(host.onlinePeers());
        online.sort((a, b) -> a.name.compareTo(b.name));
        Map<String, PeerStore.Known> known = host.knownPeers();
        List<PeerStore.Known> offline = new ArrayList<>();
        for (PeerStore.Known k : known.values()) {
            if (host.peerOnline(k.id)) continue;
            if (ChatStore.count(host, k.id) == 0) continue;
            offline.add(k);
        }
        offline.sort((a, b) -> Long.compare(b.lastSeen, a.lastSeen));

        subtitle.setText(online.isEmpty() ? "没有可用设备" : online.size() + " 台可用");

        StringBuilder sig = new StringBuilder();
        sig.append(host.localName()).append(host.localIp()).append(host.lanIssue());
        for (MainActivity.Peer p : online) sig.append(p.id).append(p.ip).append(p.name);
        for (PeerStore.Known k : offline) sig.append(k.id).append(k.lastSeen);
        if (sig.toString().equals(signature)) return;
        signature = sig.toString();

        body.removeAllViews();
        body.addView(localCard());
        ui.space(body, 4);
        body.addView(ui.sectionLabel("附近的设备"));

        if (online.isEmpty()) {
            boolean issue = host.lanIssue() != null;
            body.addView(emptyBlock(issue));
        } else {
            LinearLayout card = ui.col();
            card.setBackground(ui.box(ui.card, 12, 1, ui.cardBorder));
            for (int i = 0; i < online.size(); i++) {
                card.addView(deviceRow(online.get(i), true));
                if (i < online.size() - 1) {
                    LinearLayout wrap = ui.col();
                    wrap.setPadding(ui.dp(68), 0, 0, 0);
                    wrap.addView(ui.divider(ui.border));
                    card.addView(wrap);
                }
            }
            body.addView(card);
        }

        if (!offline.isEmpty()) {
            body.addView(ui.sectionLabel("离线设备"));
            LinearLayout card = ui.col();
            card.setBackground(ui.box(ui.card, 12, 1, ui.cardBorder));
            for (int i = 0; i < offline.size(); i++) {
                card.addView(offlineRow(offline.get(i)));
                if (i < offline.size() - 1) {
                    LinearLayout wrap = ui.col();
                    wrap.setPadding(ui.dp(68), 0, 0, 0);
                    wrap.addView(ui.divider(ui.border));
                    card.addView(wrap);
                }
            }
            body.addView(card);
        }

        ui.space(body, 16);
        TextView hint = ui.t("两台设备要在同一 Wi-Fi，或用网线直连。锁屏后仍可收发（前台服务 + Wi-Fi 锁常驻）。\n离线设备的历史消息保留，重新上线即可继续。",
            12, ui.muted, false);
        hint.setLineSpacing(ui.dp(4), 1f);
        body.addView(hint);
    }

    private View localCard() {
        LinearLayout card = ui.row();
        card.setBackground(ui.box(ui.wifiBg, 16, 1, ui.outBorder));
        card.setPadding(ui.dp(16), ui.dp(14), ui.dp(16), ui.dp(14));
        card.setMinimumHeight(ui.dp(88));
        card.addView(ui.avatar(R.drawable.ic_device_phone, ui.card, ui.accent, 40),
            new LinearLayout.LayoutParams(ui.dp(40), ui.dp(40)));

        LinearLayout info = ui.col();
        LinearLayout.LayoutParams ip = new LinearLayout.LayoutParams(0, -2, 1);
        ip.leftMargin = ui.dp(12);
        card.addView(info, ip);
        LinearLayout nameLine = ui.row();
        TextView name = ui.t(host.localName(), 15, ui.fg, true);
        name.setSingleLine(true);
        name.setEllipsize(android.text.TextUtils.TruncateAt.END);
        name.setMaxWidth(ui.dp(180));
        nameLine.addView(name, new LinearLayout.LayoutParams(-2, -2));
        LinearLayout.LayoutParams bp = new LinearLayout.LayoutParams(-2, -2);
        bp.leftMargin = ui.dp(6);
        nameLine.addView(ui.badge("本机", ui.wifiFg, ui.card), bp);
        nameLine.addView(new View(host), new LinearLayout.LayoutParams(0, 1, 1));
        info.addView(nameLine);
        TextView meta = ui.t("本机 · " + host.localIp(), 12, ui.fg2, false);
        LinearLayout.LayoutParams mp = new LinearLayout.LayoutParams(-1, -2);
        mp.topMargin = ui.dp(4);
        info.addView(meta, mp);
        TextView where = ui.t("接收位置 " + host.receiveLabel(), 12, ui.muted, false);
        LinearLayout.LayoutParams wp = new LinearLayout.LayoutParams(-1, -2);
        wp.topMargin = ui.dp(2);
        where.setSingleLine(true);
        where.setEllipsize(android.text.TextUtils.TruncateAt.MIDDLE);
        info.addView(where, wp);

        card.addView(ui.chevron(), new LinearLayout.LayoutParams(ui.dp(16), ui.dp(16)));
        ui.tap(card, () -> host.openTab(MainActivity.TAB_SETTINGS));
        card.setContentDescription("本机 " + host.localName() + "，点击打开设置");
        return card;
    }

    private View deviceRow(MainActivity.Peer peer, boolean online) {
        boolean wired = peer.ip != null && peer.ip.startsWith("169.254.");
        boolean isPhone = "Android".equals(peer.os);
        LinearLayout row = ui.row();
        row.setMinimumHeight(ui.dp(76));
        row.setPadding(ui.dp(16), ui.dp(12), ui.dp(16), ui.dp(12));
        row.addView(ui.avatar(isPhone ? R.drawable.ic_device_phone : R.drawable.ic_device_desktop,
            ui.wifiBg, ui.wifiFg, 40), new LinearLayout.LayoutParams(ui.dp(40), ui.dp(40)));

        LinearLayout info = ui.col();
        LinearLayout.LayoutParams ip = new LinearLayout.LayoutParams(0, -2, 1);
        ip.leftMargin = ui.dp(12);
        ip.rightMargin = ui.dp(8);
        row.addView(info, ip);
        LinearLayout nameLine = ui.row();
        TextView name = ui.t(peer.name, 15, ui.fg, true);
        name.setSingleLine(true);
        name.setEllipsize(android.text.TextUtils.TruncateAt.END);
        name.setMaxWidth(ui.dp(180));
        nameLine.addView(name, new LinearLayout.LayoutParams(-2, -2));
        LinearLayout.LayoutParams bp = new LinearLayout.LayoutParams(-2, -2);
        bp.leftMargin = ui.dp(6);
        nameLine.addView(ui.badge(wired ? "有线" : "Wi-Fi",
            wired ? ui.wireFg : ui.wifiFg, wired ? ui.wireBg : ui.wifiBg), bp);
        nameLine.addView(new View(host), new LinearLayout.LayoutParams(0, 1, 1));
        info.addView(nameLine);
        TextView meta = ui.t(peer.ip + " · 可连接", 12, ui.fg2, false);
        LinearLayout.LayoutParams mp = new LinearLayout.LayoutParams(-1, -2);
        mp.topMargin = ui.dp(4);
        info.addView(meta, mp);

        View dot = new View(host);
        dot.setBackground(ui.pill(ui.success));
        LinearLayout.LayoutParams dp = new LinearLayout.LayoutParams(ui.dp(8), ui.dp(8));
        dp.rightMargin = ui.dp(8);
        row.addView(dot, dp);
        row.addView(ui.chevron(), new LinearLayout.LayoutParams(ui.dp(16), ui.dp(16)));
        ui.tap(row, () -> host.showDeviceSheet(peer.id));
        row.setContentDescription(peer.name + "，" + (wired ? "有线" : "Wi-Fi") + "，" + peer.ip);
        return row;
    }

    private View offlineRow(PeerStore.Known known) {
        String name = known.name == null || known.name.isEmpty() ? known.id : known.name;
        LinearLayout row = ui.row();
        row.setMinimumHeight(ui.dp(76));
        row.setPadding(ui.dp(16), ui.dp(12), ui.dp(16), ui.dp(12));
        row.setAlpha(0.55f);
        boolean isPhone = "Android".equals(known.os);
        row.addView(ui.avatar(isPhone ? R.drawable.ic_device_phone : R.drawable.ic_device_desktop,
            ui.ghost, ui.muted, 40), new LinearLayout.LayoutParams(ui.dp(40), ui.dp(40)));

        LinearLayout info = ui.col();
        LinearLayout.LayoutParams ip = new LinearLayout.LayoutParams(0, -2, 1);
        ip.leftMargin = ui.dp(12);
        row.addView(info, ip);
        TextView nameView = ui.t(name, 15, ui.fg2, true);
        nameView.setSingleLine(true);
        nameView.setEllipsize(android.text.TextUtils.TruncateAt.END);
        info.addView(nameView);
        long seen = known.lastSeen;
        TextView meta = ui.t("离线 · 最近在线 " + MainActivity.when(seen), 12, ui.muted, false);
        LinearLayout.LayoutParams mp = new LinearLayout.LayoutParams(-1, -2);
        mp.topMargin = ui.dp(4);
        info.addView(meta, mp);
        row.addView(ui.chevron(), new LinearLayout.LayoutParams(ui.dp(16), ui.dp(16)));
        ui.tap(row, () -> host.openChat(known.id));
        return row;
    }

    private View emptyBlock(boolean issue) {
        LinearLayout box = ui.col();
        box.setPadding(0, ui.dp(24), 0, ui.dp(16));
        LinearLayout center = new LinearLayout(host);
        center.setOrientation(LinearLayout.VERTICAL);
        center.setGravity(Gravity.CENTER);
        ImageView radar = ui.icon(R.drawable.ic_device_scan, 48, ui.muted);
        radar.setAlpha(0.45f);
        center.addView(radar, new LinearLayout.LayoutParams(ui.dp(48), ui.dp(48)));
        TextView title = ui.t(issue ? "无法发现设备" : "没有发现设备", 17, ui.fg, true);
        title.setGravity(Gravity.CENTER);
        LinearLayout.LayoutParams tp = new LinearLayout.LayoutParams(-1, -2);
        tp.topMargin = ui.dp(16);
        center.addView(title, tp);
        TextView hint = ui.t("· 两台设备连接同一个 Wi-Fi\n· 或用网线直连（有线更快）\n· 确认对方已打开以太传输",
            13, ui.fg2, false);
        hint.setGravity(Gravity.CENTER);
        hint.setLineSpacing(ui.dp(6), 1f);
        LinearLayout.LayoutParams hp = new LinearLayout.LayoutParams(-1, -2);
        hp.topMargin = ui.dp(10);
        center.addView(hint, hp);
        TextView retry = ui.primary("重新扫描", host::rescan);
        LinearLayout.LayoutParams rp = new LinearLayout.LayoutParams(-2, ui.dp(48));
        rp.topMargin = ui.dp(20);
        rp.gravity = Gravity.CENTER_HORIZONTAL;
        center.addView(retry, rp);
        box.addView(center);
        return box;
    }

    void setScanning(boolean scanning) {
        if (scanning) {
            RotateAnimation spin = new RotateAnimation(0, 360,
                android.view.animation.Animation.RELATIVE_TO_SELF, 0.5f,
                android.view.animation.Animation.RELATIVE_TO_SELF, 0.5f);
            spin.setDuration(1000);
            spin.setInterpolator(new LinearInterpolator());
            spin.setRepeatCount(android.view.animation.Animation.INFINITE);
            refreshIcon.startAnimation(spin);
        } else {
            refreshIcon.clearAnimation();
        }
    }
}
