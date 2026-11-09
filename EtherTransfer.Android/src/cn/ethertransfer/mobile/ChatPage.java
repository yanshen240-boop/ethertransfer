package cn.ethertransfer.mobile;

import android.content.ClipData;
import android.net.Uri;
import android.text.Editable;
import android.text.TextUtils;
import android.text.TextWatcher;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.widget.EditText;
import android.widget.FrameLayout;
import android.widget.HorizontalScrollView;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;

import java.text.SimpleDateFormat;
import java.util.ArrayList;
import java.util.Date;
import java.util.HashMap;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;

/**
 * P02 会话详情（L1 整页）：消息流 + 融合输入 + 多选。
 * <p>顶栏 56 dp（返回 / 头像 / 名称 / 副行 / 搜索 / 更多）；消息气泡最大 78% 屏宽；
 * 输入区 chips 32 + 输入 44–120 + 工具行 48。</p>
 */
final class ChatPage extends LinearLayout {
    private final MainActivity host;
    private final Ui ui;
    final String deviceId;

    private LinearLayout topBar, selectionTop;
    private LinearLayout bottomFrame;
    private LinearLayout composer, selectionBar;
    private LinearLayout chipsRow, threadList;
    private ScrollView threadScroll;
    private FrameLayout threadHost;
    private LinearLayout emptyThread;
    private EditText input;
    private TextView peerName, peerMeta, attachSummary, clearButton;
    private TextView sendLabel;
    private LinearLayout sendButton;
    private TextView selCount, selAll;
    private ImageView avatarIcon;

    private final List<Uri> files = new ArrayList<>();
    private final Map<Uri, Long> sizes = new HashMap<>();
    private final Set<String> selectedIds = new LinkedHashSet<>();
    private final Map<String, TextView> liveMeta = new HashMap<>();
    private boolean selection;
    private String signature = "\n";
    private final int bubbleMax;

    ChatPage(MainActivity host, String deviceId) {
        super(host);
        this.host = host;
        this.ui = host.ui();
        this.deviceId = deviceId;
        this.bubbleMax = Math.round(host.getResources().getDisplayMetrics().widthPixels * 0.78f);
        setOrientation(VERTICAL);
        setBackgroundColor(ui.surface);

        // ---------------- 顶栏 ----------------
        topBar = ui.row();
        topBar.setPadding(ui.dp(4), ui.dp(4), ui.dp(4), ui.dp(4));
        topBar.setMinimumHeight(ui.dp(56));
        topBar.addView(ui.iconButton(R.drawable.ic_nav_back, ui.fg2, host::goBack),
            new LinearLayout.LayoutParams(ui.dp(48), ui.dp(48)));
        FrameLayout avatar = ui.avatar(R.drawable.ic_device_desktop, ui.wifiBg, ui.wifiFg, 32);
        avatarIcon = (ImageView) avatar.getChildAt(0);
        LinearLayout.LayoutParams ap = new LinearLayout.LayoutParams(ui.dp(32), ui.dp(32));
        ap.leftMargin = ui.dp(2);
        topBar.addView(avatar, ap);

        LinearLayout titles = ui.col();
        LinearLayout.LayoutParams tp = new LinearLayout.LayoutParams(0, -2, 1);
        tp.leftMargin = ui.dp(10);
        topBar.addView(titles, tp);
        peerName = ui.t("设备", 17, ui.fg, true);
        peerName.setSingleLine(true);
        peerName.setEllipsize(TextUtils.TruncateAt.END);
        titles.addView(peerName);
        peerMeta = ui.t("", 12, ui.muted, false);
        peerMeta.setSingleLine(true);
        LinearLayout.LayoutParams mp = new LinearLayout.LayoutParams(-1, -2);
        mp.topMargin = ui.dp(2);
        titles.addView(peerMeta, mp);
        View.OnClickListener open = v -> host.showDeviceSheet(deviceId);
        titles.setOnClickListener(open);
        avatar.setOnClickListener(open);

        topBar.addView(ui.iconButton(R.drawable.ic_common_search, ui.fg2, () -> host.openSearch(deviceId)),
            new LinearLayout.LayoutParams(ui.dp(48), ui.dp(48)));
        topBar.addView(ui.iconButton(R.drawable.ic_nav_more, ui.fg2, () -> host.showThreadMenu(deviceId)),
            new LinearLayout.LayoutParams(ui.dp(48), ui.dp(48)));
        addView(topBar, new LinearLayout.LayoutParams(-1, -2));
        addView(ui.divider(ui.border), new LinearLayout.LayoutParams(-1, Math.max(1, ui.dp(1))));

        // ---------------- 多选态顶栏 ----------------
        selectionTop = ui.row();
        selectionTop.setPadding(ui.dp(4), ui.dp(4), ui.dp(4), ui.dp(4));
        selectionTop.setMinimumHeight(ui.dp(56));
        selectionTop.setBackgroundColor(ui.surface);
        selectionTop.addView(ui.iconButton(R.drawable.ic_nav_close, ui.fg2, this::exitSelection),
            new LinearLayout.LayoutParams(ui.dp(48), ui.dp(48)));
        selCount = ui.t("已选 0 条", 17, ui.fg, true);
        LinearLayout.LayoutParams scp = new LinearLayout.LayoutParams(0, -2, 1);
        scp.leftMargin = ui.dp(8);
        selectionTop.addView(selCount, scp);
        selAll = ui.textButton("全选", ui.accent, this::toggleSelectAll);
        selectionTop.addView(selAll);
        selectionTop.setVisibility(GONE);
        addView(selectionTop, new LinearLayout.LayoutParams(-1, -2));
        addView(ui.divider(ui.border), new LinearLayout.LayoutParams(-1, Math.max(1, ui.dp(1))));

        // ---------------- 消息流 ----------------
        threadHost = new FrameLayout(host);
        threadHost.setBackgroundColor(ui.bg);
        threadScroll = new ScrollView(host);
        threadScroll.setFillViewport(true);
        threadScroll.setClipToPadding(false);
        threadList = ui.col();
        threadList.setPadding(ui.dp(12), ui.dp(6), ui.dp(12), ui.dp(12));
        threadScroll.setFocusable(false);
        threadScroll.setDescendantFocusability(ViewGroup.FOCUS_BLOCK_DESCENDANTS);
        threadScroll.addView(threadList, new FrameLayout.LayoutParams(-1, -2));
        threadHost.addView(threadScroll, new FrameLayout.LayoutParams(-1, -1));
        emptyThread = ui.col();
        emptyThread.setGravity(Gravity.CENTER);
        threadHost.addView(emptyThread, new FrameLayout.LayoutParams(-1, -2, Gravity.CENTER));
        addView(threadHost, new LinearLayout.LayoutParams(-1, 0, 1));

        // ---------------- 底部：输入区 / 多选条 ----------------
        bottomFrame = ui.col();
        bottomFrame.setBackgroundColor(ui.surface);
        bottomFrame.addView(ui.divider(ui.border), new LinearLayout.LayoutParams(-1, Math.max(1, ui.dp(1))));
        composer = buildComposer();
        bottomFrame.addView(composer);
        selectionBar = buildSelectionBar();
        selectionBar.setVisibility(GONE);
        bottomFrame.addView(selectionBar);
        addView(bottomFrame, new LinearLayout.LayoutParams(-1, -2));
    }

    // ==================================================================
    // 输入区（P08）
    // ==================================================================
    private LinearLayout buildComposer() {
        LinearLayout wrap = ui.col();
        wrap.setPadding(ui.dp(12), ui.dp(8), ui.dp(12), ui.dp(10));

        // 附件 chips：32 dp 高，横向滚动
        HorizontalScrollView chipScroll = new HorizontalScrollView(host);
        chipScroll.setHorizontalScrollBarEnabled(false);
        chipScroll.setClipToPadding(false);
        chipsRow = ui.row();
        chipScroll.addView(chipsRow, new HorizontalScrollView.LayoutParams(-2, -2));
        chipScroll.setVisibility(GONE);
        chips = chipScroll;
        wrap.addView(chipScroll, new LinearLayout.LayoutParams(-1, ui.dp(32)));

        // 输入框 44–120
        input = new EditText(host);
        input.setHint("输入要发送的文本，或先添加文件…");
        input.setTextSize(15);
        input.setTextColor(ui.fg);
        input.setHintTextColor(ui.muted);
        input.setBackground(ui.box(ui.card, 12, 1, ui.cardBorder));
        input.setPadding(ui.dp(12), ui.dp(10), ui.dp(12), ui.dp(10));
        input.setFocusableInTouchMode(true);
        input.setGravity(Gravity.TOP | Gravity.START);
        input.setMaxLines(6);
        input.setInputType(android.text.InputType.TYPE_CLASS_TEXT
            | android.text.InputType.TYPE_TEXT_FLAG_MULTI_LINE
            | android.text.InputType.TYPE_TEXT_FLAG_CAP_SENTENCES);
        input.setImeOptions(android.view.inputmethod.EditorInfo.IME_ACTION_NONE);
        input.addTextChangedListener(new TextWatcher() {
            public void beforeTextChanged(CharSequence s, int a, int b, int c) { }
            public void onTextChanged(CharSequence s, int a, int b, int c) { }
            public void afterTextChanged(Editable s) { updateSendState(); resizeInput(); }
        });
        input.setOnFocusChangeListener((v, focused) -> {
            input.setBackground(ui.box(ui.card, 12, focused ? 1.5f : 1f,
                focused ? ui.accent : ui.cardBorder));
            if (focused) threadScroll.post(() -> threadScroll.fullScroll(View.FOCUS_DOWN));
        });
        LinearLayout.LayoutParams ip = new LinearLayout.LayoutParams(-1, ui.dp(44));
        ip.topMargin = ui.dp(8);
        wrap.addView(input, ip);

        // 工具行：＋文件 / 合计 / 清空 / 发送
        LinearLayout tools = ui.row();
        LinearLayout.LayoutParams tlp = new LinearLayout.LayoutParams(-1, ui.dp(48));
        tlp.topMargin = ui.dp(8);
        wrap.addView(tools, tlp);

        LinearLayout attach = ui.row();
        attach.setMinimumHeight(ui.dp(48));
        attach.setPadding(ui.dp(4), 0, ui.dp(4), 0);
        attach.addView(ui.icon(R.drawable.ic_chat_attach_file, 20, ui.fg2));
        TextView attachLabel = ui.t("文件", 13, ui.fg2, false);
        LinearLayout.LayoutParams alp = new LinearLayout.LayoutParams(-2, -2);
        alp.leftMargin = ui.dp(6);
        attach.addView(attachLabel, alp);
        ui.tap(attach, host::pickFiles);
        tools.addView(attach, new LinearLayout.LayoutParams(-2, ui.dp(48)));

        attachSummary = ui.t("", 11, ui.muted, false);
        attachSummary.setSingleLine(true);
        LinearLayout.LayoutParams sumParams = new LinearLayout.LayoutParams(0, -2, 1);
        sumParams.leftMargin = ui.dp(8);
        tools.addView(attachSummary, sumParams);

        clearButton = ui.t("清空", 12, ui.danger, false);
        clearButton.setGravity(Gravity.CENTER);
        clearButton.setMinWidth(ui.dp(48));
        clearButton.setMinHeight(ui.dp(48));
        ui.tap(clearButton, () -> {
            if (host.transferring()) return;
            files.clear(); sizes.clear(); renderChips();
        });
        tools.addView(clearButton, new LinearLayout.LayoutParams(-2, ui.dp(48)));

        // 发送 48 × 76，主色实心；禁用时轨道底 + 次要字
        sendButton = ui.row();
        sendButton.setGravity(Gravity.CENTER);
        sendButton.setPadding(ui.dp(12), 0, ui.dp(12), 0);
        sendButton.setMinimumWidth(ui.dp(76));
        ImageView sendImage = ui.icon(R.drawable.ic_chat_send, 18, ui.onAccent);
        sendButton.addView(sendImage, new LinearLayout.LayoutParams(ui.dp(18), ui.dp(18)));
        sendLabel = ui.t("发送", 15, ui.onAccent, true);
        LinearLayout.LayoutParams slp = new LinearLayout.LayoutParams(-2, -2);
        slp.leftMargin = ui.dp(5);
        sendButton.addView(sendLabel, slp);
        ui.tap(sendButton, this::send);
        LinearLayout.LayoutParams sbp = new LinearLayout.LayoutParams(-2, ui.dp(48));
        sbp.leftMargin = ui.dp(8);
        tools.addView(sendButton, sbp);
        renderChips();
        return wrap;
    }

    private HorizontalScrollView chips;

    private LinearLayout buildSelectionBar() {
        LinearLayout bar = ui.row();
        bar.setMinimumHeight(ui.dp(56));
        bar.setPadding(ui.dp(16), 0, ui.dp(8), 0);
        TextView hint = ui.t("删除的只是会话记录，已收发的文件不受影响", 12, ui.muted, false);
        bar.addView(hint, new LinearLayout.LayoutParams(0, -2, 1));
        bar.addView(ui.textButton("删除", ui.danger, this::deleteSelected));
        return bar;
    }

    // ==================================================================
    // 状态刷新
    // ==================================================================
    /** 底部输入区（输入框 + 工具行）的实际高度，悬浮传输条据此避让。 */
    int bottomBlockHeight() {
        return bottomFrame == null ? 0 : bottomFrame.getHeight();
    }

    void refresh() {
        MainActivity.Peer peer = host.findPeer(deviceId);
        String title = host.peerTitle(deviceId);
        peerName.setText(title);
        String ip = peer != null ? peer.ip : host.peerIp(deviceId);
        boolean wired = ip != null && ip.startsWith("169.254.");
        peerMeta.setText(peer == null
            ? "离线" + (ip == null || ip.isEmpty() ? "" : " · " + ip)
            : (wired ? "有线" : "Wi-Fi") + " · " + ip);
        avatarIcon.setImageResource(peer != null && "Android".equals(peer.os)
            ? R.drawable.ic_device_phone : R.drawable.ic_device_desktop);

        boolean online = peer != null;
        input.setEnabled(online && !host.transferring());
        input.setHint(online ? "输入要发送的文本，或先添加文件…"
            : "设备离线，重新上线后可继续");

        List<ChatStore.Msg> msgs = ChatStore.load(host, deviceId);
        StringBuilder sig = new StringBuilder();
        sig.append(host.transferring()).append(selection).append(online).append('|');
        for (ChatStore.Msg m : msgs) {
            sig.append(m.msgId).append(':').append(m.state).append(':').append(m.size)
                .append(':').append(selectedIds.contains(m.msgId)).append(';');
        }
        if (!sig.toString().equals(signature)) {
            signature = sig.toString();
            renderThread(msgs);
        }
        updateSendState();
        updateLiveMeta();
    }

    private void renderThread(List<ChatStore.Msg> msgs) {
        liveMeta.clear();
        progressByMsg.clear();
        threadList.removeAllViews();
        SimpleDateFormat dayKey = new SimpleDateFormat("yyyyMMdd", Locale.US);
        String lastDay = "";
        for (ChatStore.Msg m : msgs) {
            String day = dayKey.format(new Date(m.time));
            if (!day.equals(lastDay)) {
                lastDay = day;
                threadList.addView(daySeparator(m.time));
            }
            threadList.addView(m.sys ? systemLine(m) : messageRow(m));
        }
        emptyThread.removeAllViews();
        if (msgs.isEmpty()) {
            emptyThread.setVisibility(VISIBLE);
            emptyThread.addView(ui.emptyState(R.drawable.ic_tab_chat, "会话还是空的",
                "发一条文本或添加文件，两台设备之间直连传输。", null, null));
        } else {
            emptyThread.setVisibility(GONE);
        }
        threadScroll.post(() -> threadScroll.fullScroll(View.FOCUS_DOWN));
    }

    private TextView daySeparator(long time) {
        TextView v = ui.t(MainActivity.dayLabel(time) + " " + MainActivity.timeOnly(time),
            11, ui.muted, false);
        v.setGravity(Gravity.CENTER);
        v.setPadding(0, ui.dp(12), 0, ui.dp(8));
        v.setLayoutParams(new LinearLayout.LayoutParams(-1, -2));
        return v;
    }

    private TextView systemLine(ChatStore.Msg m) {
        TextView v = ui.t(m.text, 11, ui.muted, false);
        v.setGravity(Gravity.CENTER);
        v.setPadding(ui.dp(20), ui.dp(3), ui.dp(20), ui.dp(8));
        v.setLayoutParams(new LinearLayout.LayoutParams(-1, -2));
        return v;
    }

    private View messageRow(ChatStore.Msg m) {
        boolean checked = selectedIds.contains(m.msgId);
        LinearLayout row = ui.row();
        row.setGravity(Gravity.TOP);
        row.setPadding(ui.dp(2), ui.dp(3), ui.dp(2), ui.dp(3));

        if (selection) {
            FrameLayout circle = new FrameLayout(host);
            circle.setBackground(checked
                ? ui.box(ui.accent, 12, 0, 0)
                : ui.box(0x00000000, 12, 1.5f, ui.cardBorder));
            if (checked) {
                ImageView check = ui.icon(R.drawable.ic_common_check, 14, ui.onAccent);
                FrameLayout.LayoutParams cp = new FrameLayout.LayoutParams(ui.dp(14), ui.dp(14));
                cp.gravity = Gravity.CENTER;
                circle.addView(check, cp);
            }
            LinearLayout.LayoutParams cp = new LinearLayout.LayoutParams(ui.dp(24), ui.dp(24));
            cp.rightMargin = ui.dp(8);
            cp.topMargin = ui.dp(6);
            row.addView(circle, cp);
        }

        LinearLayout stack = ui.row();
        stack.setGravity(selection || !m.out ? Gravity.START : Gravity.END);
        LinearLayout.LayoutParams stackParams = new LinearLayout.LayoutParams(0, -2, 1);
        LinearLayout content = ui.col();
        content.setGravity(selection || !m.out ? Gravity.START : Gravity.END);
        content.addView(m.file ? fileCard(m) : textBubble(m),
            new LinearLayout.LayoutParams(-2, -2));
        if (!m.file && m.out && ChatStore.FAILED.equals(m.state)) {
            TextView fail = ui.t("发送失败 · 点此重新编辑", 12, ui.danger, false);
            fail.setPadding(ui.dp(4), ui.dp(4), ui.dp(4), 0);
            ui.tap(fail, () -> {
                input.setText(m.text);
                input.setSelection(input.getText().length());
                input.requestFocus();
                host.snack("原文已放回输入框，可直接重新发送");
            });
            content.addView(fail, new LinearLayout.LayoutParams(-2, -2));
        }
        stack.addView(content, new LinearLayout.LayoutParams(-2, -2));
        row.addView(stack, stackParams);

        if (selection) {
            if (checked) row.setBackground(ui.box(ui.outBg, 10, 0, 0));
            ui.tap(row, () -> toggleOne(m.msgId));
        } else {
            row.setOnLongClickListener(v -> { host.showMessageSheet(deviceId, m); return true; });
        }
        return row;
    }

    private TextView textBubble(ChatStore.Msg m) {
        TextView bubble = ui.t(m.text, 15, ui.fg, false);
        bubble.setBackground(ui.box(m.out ? ui.outBg : ui.inBg, 12, 1, m.out ? ui.outBorder : ui.inBorder));
        bubble.setPadding(ui.dp(10), ui.dp(8), ui.dp(10), ui.dp(8));
        bubble.setMaxWidth(bubbleMax);
        return bubble;
    }

    private LinearLayout fileCard(ChatStore.Msg m) {
        boolean failed = ChatStore.FAILED.equals(m.state);
        boolean live = ChatStore.SENDING.equals(m.state) || ChatStore.RECEIVING.equals(m.state);

        LinearLayout card = ui.col();
        card.setBackground(ui.box(m.out ? ui.outBg : ui.inBg, 12, 1,
            failed ? ui.danger : (m.out ? ui.outBorder : ui.inBorder)));
        card.setPadding(ui.dp(10), ui.dp(10), ui.dp(10), ui.dp(10));
        card.setMinimumWidth(Math.min(ui.dp(260), bubbleMax));

        LinearLayout top = ui.row();
        FrameLayout tile = new FrameLayout(host);
        tile.setBackground(ui.box(ui.ghost, 10, 0, 0));
        tile.addView(ui.icon(fileIcon(m.fileName), 20, ui.fg2),
            new FrameLayout.LayoutParams(ui.dp(20), ui.dp(20), Gravity.CENTER));
        top.addView(tile, new LinearLayout.LayoutParams(ui.dp(40), ui.dp(40)));

        LinearLayout info = ui.col();
        LinearLayout.LayoutParams ip = new LinearLayout.LayoutParams(0, -2, 1);
        ip.leftMargin = ui.dp(10);
        top.addView(info, ip);
        TextView name = ui.t(m.fileName, 14, failed ? ui.danger : ui.fg, true);
        name.setSingleLine(true);
        name.setEllipsize(TextUtils.TruncateAt.MIDDLE);
        info.addView(name);
        String meta = stateText(m);
        TextView metaView = ui.t(meta, 12, failed ? ui.danger : ui.muted, false);
        LinearLayout.LayoutParams mp = new LinearLayout.LayoutParams(-1, -2);
        mp.topMargin = ui.dp(3);
        info.addView(metaView, mp);
        card.addView(top);
        liveMeta.put(m.msgId, metaView);

        if (live) {
            Ui.ProgressLine progress = new Ui.ProgressLine(host, ui, 4, ui.accent, ui.track);
            LinearLayout.LayoutParams pp = new LinearLayout.LayoutParams(-1, ui.dp(4));
            pp.topMargin = ui.dp(8);
            card.addView(progress, pp);
            progressByMsg.put(m.msgId, progress);
            progress.setPercent(host.transferPercentFor(m.msgId) / 100f);
        }

        if (failed && m.out) {
            TextView retry = ui.t("重新发送", 12, ui.accent, true);
            retry.setPadding(0, ui.dp(8), 0, 0);
            card.addView(retry);
            ui.tap(card, () -> host.retryMessage(m));
        }
        return card;
    }

    private final Map<String, Ui.ProgressLine> progressByMsg = new HashMap<>();

    private String stateText(ChatStore.Msg m) {
        if (ChatStore.SENDING.equals(m.state) || ChatStore.RECEIVING.equals(m.state)) {
            int pct = host.transferPercentFor(m.msgId);
            String verb = ChatStore.RECEIVING.equals(m.state) ? "接收中" : "发送中";
            return pct > 0 ? verb + " · " + pct + "%" : verb + "…";
        }
        return m.stateText();
    }

    private int fileIcon(String name) {
        String n = name == null ? "" : name.toLowerCase(Locale.US);
        if (n.matches(".*\\.(png|jpg|jpeg|gif|webp|bmp|heic)$")) return R.drawable.ic_file_image;
        if (!n.contains(".")) return R.drawable.ic_file_folder;
        return R.drawable.ic_file_document;
    }

    private void updateLiveMeta() {
        for (Map.Entry<String, TextView> e : liveMeta.entrySet()) {
            Ui.ProgressLine bar = progressByMsg.get(e.getKey());
            int pct = host.transferPercentFor(e.getKey());
            if (bar != null) bar.setPercent(pct / 100f);
            String text = e.getValue().getText().toString();
            if (text.startsWith("发送中") || text.startsWith("接收中"))
                e.getValue().setText(text.substring(0, 3) + " · " + pct + "%");
        }
    }

    // ==================================================================
    // 附件 / 发送
    // ==================================================================
    void addFiles(Uri... uris) {
        if (uris == null) return;
        for (Uri uri : uris) {
            if (uri == null || files.contains(uri)) continue;
            files.add(uri);
            long size = host.querySize(uri);
            sizes.put(uri, size);
        }
        renderChips();
    }

    private void renderChips() {
        chipsRow.removeAllViews();
        long total = 0;
        boolean known = true;
        for (Uri uri : files) {
            Long size = sizes.get(uri);
            if (size == null || size < 0) known = false;
            else total += size;
            chipsRow.addView(chip(uri, size == null ? -1 : size));
        }
        chips.setVisibility(files.isEmpty() ? GONE : VISIBLE);
        if (files.isEmpty()) {
            attachSummary.setText("");
        } else {
            String count = files.size() + " 个文件";
            attachSummary.setText(known ? count + " · " + ChatStore.sizeText(total) : count);
        }
        clearButton.setVisibility(files.isEmpty() ? GONE : VISIBLE);
        updateSendState();
    }

    private View chip(Uri uri, long size) {
        String name = host.displayName(uri);
        LinearLayout chip = ui.row();
        chip.setBackground(ui.box(ui.outBg, 8, 1, ui.outBorder));
        chip.setPadding(ui.dp(8), 0, ui.dp(4), 0);
        chip.addView(ui.icon(fileIcon(name), 16, ui.fg2),
            new LinearLayout.LayoutParams(ui.dp(16), ui.dp(16)));
        TextView label = ui.t(name, 12, ui.fg, false);
        label.setSingleLine(true);
        label.setEllipsize(TextUtils.TruncateAt.MIDDLE);
        label.setMaxWidth(ui.dp(120));
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(-2, -2);
        lp.leftMargin = ui.dp(6);
        chip.addView(label, lp);
        if (size > 0) {
            TextView sizeView = ui.t(ChatStore.sizeText(size), 11, ui.muted, false);
            LinearLayout.LayoutParams sp = new LinearLayout.LayoutParams(-2, -2);
            sp.leftMargin = ui.dp(6);
            chip.addView(sizeView, sp);
        }
        FrameLayout close = ui.iconButton(R.drawable.ic_nav_close, ui.muted, () -> {
            if (host.transferring()) return;
            files.remove(uri); sizes.remove(uri); renderChips();
        });
        LinearLayout.LayoutParams cp = new LinearLayout.LayoutParams(ui.dp(24), ui.dp(32));
        chip.addView(close, cp);
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(-2, ui.dp(32));
        params.rightMargin = ui.dp(8);
        chip.setLayoutParams(params);
        return chip;
    }

    private boolean hasAttachments() { return !files.isEmpty(); }

    private void updateSendState() {
        if (sendButton == null) return;
        boolean online = host.findPeer(deviceId) != null;
        boolean hasText = input != null && !input.getText().toString().trim().isEmpty();
        boolean enabled = online && !host.transferring() && (hasAttachments() || hasText);
        sendButton.setBackground(ui.box(enabled ? ui.accent : ui.track, 12, 0, 0));
        int fgColor = enabled ? ui.onAccent : ui.fg2;
        sendLabel.setTextColor(fgColor);
        sendLabel.setText(host.transferring() ? "发送中" : "发送");
        for (int i = 0; i < sendButton.getChildCount(); i++) {
            View child = sendButton.getChildAt(i);
            if (child instanceof ImageView) {
                ((ImageView) child).setImageTintList(android.content.res.ColorStateList.valueOf(fgColor));
            }
        }
        sendButton.setEnabled(enabled);
        sendButton.setAlpha(enabled ? 1f : 0.6f);
    }

    private void resizeInput() {
        input.post(() -> {
            int lineHeight = input.getLineHeight();
            int height = Math.min(ui.dp(120), Math.max(ui.dp(44),
                input.getLineCount() * lineHeight + ui.dp(20)));
            ViewGroup.LayoutParams lp = input.getLayoutParams();
            if (lp.height != height) {
                lp.height = height;
                input.setLayoutParams(lp);
            }
        });
    }

    private void send() {
        String text = input.getText().toString().trim();
        if (text.length() > 20000) { host.snack("文本过长（上限 20000 字）"); return; }
        if (files.isEmpty() && text.isEmpty()) return;
        if (!host.transferring()) {
            List<Uri> items = new ArrayList<>(files);
            host.send(deviceId, items, text, () -> {
                files.clear();
                sizes.clear();
                input.setText("");
                renderChips();
                signature = "\n";
                refresh();
            });
        }
    }

    // ==================================================================
    // 多选
    // ==================================================================
    boolean inSelection() { return selection; }

    void enterSelection(String firstId) {
        selection = true;
        selectedIds.clear();
        if (firstId != null) selectedIds.add(firstId);
        applySelectionUi();
    }

    void exitSelection() {
        selection = false;
        selectedIds.clear();
        applySelectionUi();
    }

    private void toggleOne(String id) {
        if (selectedIds.contains(id)) selectedIds.remove(id);
        else selectedIds.add(id);
        applySelectionUi();
    }

    private void toggleSelectAll() {
        List<ChatStore.Msg> msgs = ChatStore.load(host, deviceId);
        boolean all = !msgs.isEmpty() && selectedIds.size() >= msgs.size();
        selectedIds.clear();
        if (!all) for (ChatStore.Msg m : msgs) selectedIds.add(m.msgId);
        applySelectionUi();
    }

    private void applySelectionUi() {
        selectionTop.setVisibility(selection ? VISIBLE : GONE);
        topBar.setVisibility(selection ? GONE : VISIBLE);
        composer.setVisibility(selection ? GONE : VISIBLE);
        selectionBar.setVisibility(selection ? VISIBLE : GONE);
        selCount.setText("已选 " + selectedIds.size() + " 条");
        List<ChatStore.Msg> msgs = ChatStore.load(host, deviceId);
        selAll.setText(!msgs.isEmpty() && selectedIds.size() >= msgs.size() ? "全不选" : "全选");
        signature = "\n";
        refresh();
    }

    private void deleteSelected() {
        if (selectedIds.isEmpty()) return;
        int count = selectedIds.size();
        host.confirm("删除会话记录", "将删除选中的 " + count + " 条记录，不影响已收发的文件。", "删除",
            () -> {
                ChatStore.deleteMessages(host, deviceId, new ArrayList<>(selectedIds));
                for (String id : selectedIds) host.forgetResend(id);
                selectedIds.clear();
                selection = false;
                applySelectionUi();
                signature = "\n";
                refresh();
                host.refreshAll();
                host.snack("已删除 " + count + " 条记录");
            });
    }
}
