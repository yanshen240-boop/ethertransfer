package cn.ethertransfer.mobile;

import android.graphics.Color;
import android.text.Editable;
import android.text.SpannableString;
import android.text.Spanned;
import android.text.TextUtils;
import android.text.TextWatcher;
import android.text.style.BackgroundColorSpan;
import android.text.style.ForegroundColorSpan;
import android.view.Gravity;
import android.view.View;
import android.widget.EditText;
import android.widget.FrameLayout;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;

import java.util.ArrayList;
import java.util.List;

/**
 * P03 会话内搜索（也可从会话列表做全局搜索）。
 * <p>命中按消息粒度展示，关键词高亮主色 + 12% 主色底；点击跳到 P02 并定位。</p>
 */
final class SearchPage extends LinearLayout {
    private final MainActivity host;
    private final Ui ui;
    private final String scopedDeviceId;
    private final LinearLayout results;
    private final FrameLayout resultHost;
    private final LinearLayout emptyBox;
    private final TextView scopeLabel;
    private final EditText input;

    SearchPage(MainActivity host, String scopedDeviceId) {
        super(host);
        this.host = host;
        this.ui = host.ui();
        this.scopedDeviceId = scopedDeviceId;
        setOrientation(VERTICAL);
        setBackgroundColor(ui.surface);

        LinearLayout bar = ui.row();
        bar.setPadding(ui.dp(4), ui.dp(6), ui.dp(12), ui.dp(6));
        bar.setMinimumHeight(ui.dp(56));
        bar.addView(ui.iconButton(R.drawable.ic_nav_back, ui.fg2, host::goBack),
            new LinearLayout.LayoutParams(ui.dp(48), ui.dp(48)));

        FrameLayout field = new FrameLayout(host);
        field.setBackground(ui.box(ui.card, 12, 1, ui.cardBorder));
        LinearLayout.LayoutParams fp = new LinearLayout.LayoutParams(0, ui.dp(44), 1);
        fp.rightMargin = ui.dp(4);
        bar.addView(field, fp);
        input = new EditText(host);
        input.setSingleLine(true);
        input.setTextSize(15);
        input.setTextColor(ui.fg);
        input.setHintTextColor(ui.muted);
        input.setHint("搜索会话记录");
        input.setBackgroundColor(Color.TRANSPARENT);
        input.setPadding(ui.dp(12), 0, ui.dp(36), 0);
        field.addView(input, new FrameLayout.LayoutParams(-1, -1));
        ImageView close = ui.icon(R.drawable.ic_nav_close, 18, ui.muted);
        FrameLayout.LayoutParams cp = new FrameLayout.LayoutParams(ui.dp(18), ui.dp(18));
        cp.gravity = Gravity.END | Gravity.CENTER_VERTICAL;
        cp.rightMargin = ui.dp(12);
        field.addView(close, cp);
        ui.tap(close, () -> input.setText(""));
        addView(bar, new LinearLayout.LayoutParams(-1, -2));

        scopeLabel = ui.t(scopedDeviceId == null ? "在全部会话中搜索"
            : "在「" + host.peerTitle(scopedDeviceId) + "」的会话中搜索", 12, ui.muted, false);
        scopeLabel.setPadding(ui.dp(16), 0, ui.dp(16), ui.dp(10));
        addView(scopeLabel);
        addView(ui.divider(ui.border), new LinearLayout.LayoutParams(-1, Math.max(1, ui.dp(1))));

        resultHost = new FrameLayout(host);
        ScrollView scroll = new ScrollView(host);
        results = ui.col();
        results.setPadding(0, ui.dp(4), 0, ui.dp(24));
        scroll.addView(results, new FrameLayout.LayoutParams(-1, -2));
        resultHost.addView(scroll, new FrameLayout.LayoutParams(-1, -1));
        emptyBox = new LinearLayout(host);
        emptyBox.setOrientation(VERTICAL);
        emptyBox.setGravity(Gravity.CENTER);
        resultHost.addView(emptyBox, new FrameLayout.LayoutParams(-1, -1));
        addView(resultHost, new LinearLayout.LayoutParams(-1, 0, 1));

        searchTask = () -> render(input.getText().toString().trim());
        input.addTextChangedListener(new TextWatcher() {
            public void beforeTextChanged(CharSequence s, int a, int b, int c) { }
            public void onTextChanged(CharSequence s, int a, int b, int c) { }
            public void afterTextChanged(Editable s) {
                input.removeCallbacks(searchTask);
                input.postDelayed(searchTask, 200);
            }
        });
        input.requestFocus();
        render("");
    }

    private Runnable searchTask;

    private void render(String query) {
        results.removeAllViews();
        emptyBox.removeAllViews();
        if (query.isEmpty()) {
            emptyBox.setVisibility(VISIBLE);
            emptyBox.addView(ui.emptyState(R.drawable.ic_common_search, "搜索会话记录",
                "输入关键词，回车即可在" + (scopedDeviceId == null ? "全部会话" : "当前会话") + "里查找文本与文件名。",
                null, null));
            return;
        }
        List<Hit> hits = new ArrayList<>();
        String lower = query.toLowerCase();
        for (ChatStore.Conv conv : host.conversations()) {
            if (scopedDeviceId != null && !scopedDeviceId.equals(conv.deviceId)) continue;
            for (ChatStore.Msg m : ChatStore.load(this.getContext(), conv.deviceId)) {
                if (m.sys) continue;
                String body = m.file ? m.fileName : m.text;
                if (body == null || !body.toLowerCase().contains(lower)) continue;
                Hit hit = new Hit();
                hit.deviceId = conv.deviceId;
                hit.msg = m;
                hit.body = body;
                hits.add(hit);
            }
        }
        hits.sort((a, b) -> Long.compare(b.msg.time, a.msg.time));
        if (hits.isEmpty()) {
            emptyBox.setVisibility(VISIBLE);
            emptyBox.addView(ui.emptyState(R.drawable.ic_common_search, "没有找到相关记录",
                "换个关键词，或检查是否选错了会话。", null, null));
            return;
        }
        emptyBox.setVisibility(GONE);
        for (Hit hit : hits) results.addView(hitRow(hit, query));
    }

    private static final class Hit {
        String deviceId;
        ChatStore.Msg msg;
        String body;
    }

    private View hitRow(Hit hit, String query) {
        LinearLayout row = ui.row();
        row.setMinimumHeight(ui.dp(64));
        row.setPadding(ui.dp(16), ui.dp(10), ui.dp(16), ui.dp(10));
        row.addView(ui.icon(hit.msg.file ? R.drawable.ic_file_document : R.drawable.ic_chat_send,
            20, ui.muted), new LinearLayout.LayoutParams(ui.dp(20), ui.dp(20)));

        LinearLayout info = ui.col();
        LinearLayout.LayoutParams ip = new LinearLayout.LayoutParams(0, -2, 1);
        ip.leftMargin = ui.dp(12);
        row.addView(info, ip);

        LinearLayout top = ui.row();
        TextView who = ui.t(host.peerTitle(hit.deviceId), 12, ui.muted, false);
        top.addView(who, new LinearLayout.LayoutParams(0, -2, 1));
        top.addView(ui.t(MainActivity.when(hit.msg.time), 11, ui.muted, false));
        info.addView(top);

        TextView snippet = ui.t(highlight(hit.body, query), 14, ui.fg, false);
        snippet.setSingleLine(true);
        snippet.setEllipsize(TextUtils.TruncateAt.END);
        LinearLayout.LayoutParams sp = new LinearLayout.LayoutParams(-1, -2);
        sp.topMargin = ui.dp(4);
        info.addView(snippet, sp);

        ui.tap(row, () -> host.openChat(hit.deviceId));
        return row;
    }

    private CharSequence highlight(String body, String query) {
        String lower = body.toLowerCase();
        String needle = query.toLowerCase();
        int at = lower.indexOf(needle);
        if (at < 0) return body;
        SpannableString span = new SpannableString(body);
        span.setSpan(new ForegroundColorSpan(ui.accent), at, at + query.length(),
            Spanned.SPAN_EXCLUSIVE_EXCLUSIVE);
        span.setSpan(new BackgroundColorSpan(0x1F0284C7), at, at + query.length(),
            Spanned.SPAN_EXCLUSIVE_EXCLUSIVE);
        return span;
    }
}
