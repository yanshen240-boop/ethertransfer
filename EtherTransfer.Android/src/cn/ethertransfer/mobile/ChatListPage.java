package cn.ethertransfer.mobile;

import android.graphics.Color;
import android.view.Gravity;
import android.view.View;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;

import java.util.ArrayList;
import java.util.List;
import java.util.Map;

/**
 * P01 会话列表（Tab 1）。
 * <p>按设备聚合会话：头像 40 + 名称 15 + 链路胶囊 + 预览 13 + 时间 11 + 未读点 8。
 * 本页只做「跟谁聊过」，找设备在 P06。</p>
 */
final class ChatListPage extends LinearLayout {
    private final MainActivity host;
    private final Ui ui;
    private final TextView subtitle;
    private final LinearLayout listBox;
    private final FrameLayout listHost;
    private final LinearLayout emptyBox;
    private final LinearLayout anomalyBox;
    private final LinearLayout anomalyInner;
    private String signature = "";

    ChatListPage(MainActivity host) {
        super(host);
        this.host = host;
        this.ui = host.ui();
        setOrientation(VERTICAL);
        setBackgroundColor(ui.surface);

        // —— 页头 56：标题 / 在线数 / 搜索 / 更多 ——
        LinearLayout header = ui.row();
        header.setPadding(ui.dp(16), ui.dp(4), ui.dp(4), ui.dp(4));
        header.setMinimumHeight(ui.dp(56));
        LinearLayout titles = ui.col();
        TextView title = ui.titleH1("以太传输");
        titles.addView(title);
        subtitle = ui.t("正在启动", 12, ui.muted, false);
        LinearLayout.LayoutParams sp = new LinearLayout.LayoutParams(-1, -2);
        sp.topMargin = ui.dp(3);
        titles.addView(subtitle, sp);
        header.addView(titles, new LinearLayout.LayoutParams(0, -2, 1));
        header.addView(ui.iconButton(R.drawable.ic_common_search, ui.fg2, host::openSearchPicker),
            new LinearLayout.LayoutParams(ui.dp(48), ui.dp(48)));
        header.addView(ui.iconButton(R.drawable.ic_nav_more, ui.fg2, host::showGlobalMenu),
            new LinearLayout.LayoutParams(ui.dp(48), ui.dp(48)));
        addView(header, new LinearLayout.LayoutParams(-1, -2));
        addView(ui.divider(ui.border), new LinearLayout.LayoutParams(-1, Math.max(1, ui.dp(1))));

        // —— P19 异常条（最多一条） ——
        anomalyBox = ui.col();
        anomalyBox.setVisibility(GONE);
        anomalyInner = ui.col();
        anomalyInner.setPadding(ui.dp(16), ui.dp(12), ui.dp(16), ui.dp(12));
        anomalyBox.addView(anomalyInner);
        addView(anomalyBox, new LinearLayout.LayoutParams(-1, -2));

        // —— 列表 / 空态 ——
        listHost = new FrameLayout(host);
        ScrollView scroll = new ScrollView(host);
        scroll.setFillViewport(true);
        scroll.setClipToPadding(false);
        listBox = ui.col();
        listBox.setPadding(0, ui.dp(4), 0, ui.dp(24));
        scroll.addView(listBox, new FrameLayout.LayoutParams(-1, -2));
        listHost.addView(scroll, new FrameLayout.LayoutParams(-1, -1));

        emptyBox = new LinearLayout(host);
        emptyBox.setOrientation(VERTICAL);
        emptyBox.setGravity(Gravity.CENTER);
        listHost.addView(emptyBox, new FrameLayout.LayoutParams(-1, -1));
        addView(listHost, new LinearLayout.LayoutParams(-1, 0, 1));
    }

    void refresh() {
        int online = host.onlineCount();
        subtitle.setText(online > 0 ? online + " 台在线" : "没有发现设备");
        subtitle.setTextColor(online > 0 ? ui.muted : ui.muted);

        String issue = host.lanIssue();
        renderAnomaly(issue);

        List<ChatStore.Conv> convs = host.conversations();
        StringBuilder sig = new StringBuilder(issue == null ? "" : issue).append('|');
        for (ChatStore.Conv conv : convs) {
            sig.append(conv.deviceId).append(':').append(conv.count).append(':')
                .append(conv.time).append(':').append(conv.last == null ? "" : conv.last.state)
                .append(':').append(host.unread(conv.deviceId)).append(':')
                .append(host.peerTitle(conv.deviceId)).append(';');
        }
        if (sig.toString().equals(signature)) return;
        signature = sig.toString();

        listBox.removeAllViews();
        emptyBox.removeAllViews();
        if (convs.isEmpty()) {
            listHost.setBackgroundColor(ui.surface);
            emptyBox.setVisibility(VISIBLE);
            emptyBox.addView(ui.emptyState(R.drawable.ic_tab_chat, "还没有会话",
                "在设备页找一台设备，就能开始发送文本和文件。", "选择设备", () -> host.openTab(MainActivity.TAB_DEVICES)),
                new FrameLayout.LayoutParams(-1, -2, Gravity.CENTER));
            return;
        }
        emptyBox.setVisibility(GONE);
        listBox.setBackgroundColor(ui.surface);
        for (int i = 0; i < convs.size(); i++) {
            listBox.addView(row(convs.get(i)));
            if (i < convs.size() - 1) {
                LinearLayout wrap = ui.col();
                wrap.setPadding(ui.dp(72), 0, 0, 0);
                wrap.addView(ui.divider(ui.border));
                listBox.addView(wrap);
            }
        }
    }

    private void renderAnomaly(String issue) {
        anomalyInner.removeAllViews();
        if (issue == null) {
            anomalyBox.setVisibility(GONE);
            return;
        }
        anomalyBox.setVisibility(VISIBLE);
        anomalyInner.setBackground(ui.box(ui.warningBg, 0, 0, Color.TRANSPARENT));
        LinearLayout line = ui.row();
        line.addView(ui.icon(R.drawable.ic_system_offline, 20, ui.warning),
            new LinearLayout.LayoutParams(ui.dp(20), ui.dp(20)));
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(0, -2, 1);
        lp.leftMargin = ui.dp(10);
        TextView msg = ui.t(issue, 13, ui.fg, false);
        line.addView(msg, lp);
        line.addView(ui.textButton("去设置", ui.accent, () -> host.openTab(MainActivity.TAB_SETTINGS)));
        anomalyInner.addView(line);
    }

    private View row(ChatStore.Conv conv) {
        MainActivity.Peer peer = host.findPeer(conv.deviceId);
        boolean online = peer != null;
        String name = host.peerTitle(conv.deviceId);
        String ip = peer != null ? peer.ip : host.peerIp(conv.deviceId);
        boolean wired = ip != null && ip.startsWith("169.254.");

        LinearLayout row = ui.row();
        row.setMinimumHeight(ui.dp(72));
        row.setPadding(ui.dp(16), ui.dp(12), ui.dp(16), ui.dp(12));
        row.setBackgroundColor(ui.surface);

        int iconRes = peer != null && "Android".equals(peer.os)
            ? R.drawable.ic_device_phone : R.drawable.ic_device_desktop;
        if (peer == null) {
            PeerStore.Known known = host.knownPeer(conv.deviceId);
            if (known != null && "Android".equals(known.os)) iconRes = R.drawable.ic_device_phone;
        }
        FrameLayout avatar = ui.avatar(iconRes, ui.wifiBg, ui.wifiFg, 40);
        if (!online) avatar.setAlpha(0.55f);
        row.addView(avatar, new LinearLayout.LayoutParams(ui.dp(40), ui.dp(40)));

        LinearLayout info = ui.col();
        LinearLayout.LayoutParams infoParams = new LinearLayout.LayoutParams(0, -2, 1);
        infoParams.leftMargin = ui.dp(12);
        infoParams.rightMargin = ui.dp(8);
        row.addView(info, infoParams);

        LinearLayout nameLine = ui.row();
        TextView nameView = ui.t(name, 15, online ? ui.fg : ui.fg2, true);
        nameView.setSingleLine(true);
        nameView.setEllipsize(android.text.TextUtils.TruncateAt.END);
        nameView.setMaxWidth(ui.dp(190));
        nameLine.addView(nameView, new LinearLayout.LayoutParams(-2, -2));
        if (ip != null && !ip.isEmpty()) {
            LinearLayout.LayoutParams bp = new LinearLayout.LayoutParams(-2, -2);
            bp.leftMargin = ui.dp(6);
            nameLine.addView(ui.badge(wired ? "有线" : "Wi-Fi",
                wired ? ui.wireFg : ui.wifiFg, wired ? ui.wireBg : ui.wifiBg), bp);
        }
        nameLine.addView(new View(host), new LinearLayout.LayoutParams(0, 1, 1));
        info.addView(nameLine);

        LinearLayout.LayoutParams pp = new LinearLayout.LayoutParams(-1, -2);
        pp.topMargin = ui.dp(4);
        TextView preview = ui.t(preview(conv.last), 13, ui.muted, false);
        preview.setSingleLine(true);
        preview.setEllipsize(android.text.TextUtils.TruncateAt.END);
        info.addView(preview, pp);

        LinearLayout right = ui.col();
        right.setGravity(Gravity.END);
        TextView when = ui.t(MainActivity.when(conv.time), 11, ui.muted, false);
        right.addView(when, new LinearLayout.LayoutParams(-2, -2));
        int unread = host.unread(conv.deviceId);
        if (unread > 0) {
            View dot = new View(host);
            dot.setBackground(ui.pill(ui.accent));
            LinearLayout.LayoutParams dp = new LinearLayout.LayoutParams(ui.dp(8), ui.dp(8));
            dp.topMargin = ui.dp(6);
            right.addView(dot, dp);
        }
        row.addView(right, new LinearLayout.LayoutParams(-2, -2));

        ui.tap(row, () -> host.openChat(conv.deviceId));
        row.setOnLongClickListener(v -> { host.showThreadMenu(conv.deviceId); return true; });
        row.setContentDescription(name + "，" + preview(conv.last) + "，" + MainActivity.when(conv.time));
        return row;
    }

    private String preview(ChatStore.Msg last) {
        if (last == null) return "";
        if (last.sys) return last.text;
        if (last.file) return (last.out ? "你：" : "") + "[文件] " + last.fileName;
        String text = last.text == null ? "" : last.text.replace('\n', ' ');
        return (last.out ? "你：" : "") + text;
    }

    /** 会话列表按设备聚合时给外部用的稳定排序。 */
    static List<ChatStore.Conv> sorted(List<ChatStore.Conv> input) {
        List<ChatStore.Conv> copy = new ArrayList<>(input);
        copy.sort((a, b) -> Long.compare(b.time, a.time));
        return copy;
    }

    @SuppressWarnings("unused")
    private static String name(Map<String, PeerStore.Known> map, String id) {
        PeerStore.Known k = map.get(id);
        return k == null || k.name.isEmpty() ? id : k.name;
    }
}
