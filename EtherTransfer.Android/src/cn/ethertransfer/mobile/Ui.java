package cn.ethertransfer.mobile;

import android.app.Activity;
import android.content.res.ColorStateList;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.graphics.drawable.RippleDrawable;
import android.text.TextUtils;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.widget.FrameLayout;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.TextView;

/**
 * 设计 token 与视图工厂。
 * <p>取值全部来自 {@code UI设计/设计规范.md} 与《安卓端-UI设计交付文档 v1.0》第 6 章：
 * 圆角 8 / 12 / 16 / 20 / 24 / 999，描边统一 1 dp，触控目标 ≥ 48 dp。</p>
 */
final class Ui {
    /** 唯一主色。 */
    static final int ACCENT = 0xFF0284C7;

    final Activity ctx;
    final boolean night;
    final float density;

    final int bg, surface, card, cardBorder, border, fg, fg2, muted, accent, onAccent,
        wireBg, wireFg, wifiBg, wifiFg, ghost, track, danger, dangerBg, success, successBg,
        warning, warningBg, outBg, outBorder, inBg, inBorder, scrim;

    Ui(Activity ctx) {
        this.ctx = ctx;
        this.density = ctx.getResources().getDisplayMetrics().density;
        night = (ctx.getResources().getConfiguration().uiMode
            & android.content.res.Configuration.UI_MODE_NIGHT_MASK)
            == android.content.res.Configuration.UI_MODE_NIGHT_YES;
        accent = ACCENT;
        onAccent = 0xFFFFFFFF;
        if (night) {
            bg = 0xFF000000; surface = 0xFF000000; card = 0xFF0C0C0E; cardBorder = 0xFF222228;
            border = 0xFF1E1E24; fg = 0xFFF4F4F5; fg2 = 0xFFA1A1AA; muted = 0xFF71717A;
            wireBg = 0xFF0D221A; wireFg = 0xFF10B981; wifiBg = 0xFF0B2436; wifiFg = 0xFF38BDF8;
            ghost = 0xFF16161A; track = 0xFF1E1E24; danger = 0xFFEF4444; dangerBg = 0xFF241215;
            success = 0xFF10B981; successBg = 0xFF0D221A; warning = 0xFFF59E0B; warningBg = 0xFF261E10;
            outBg = 0xFF06192A; outBorder = 0xFF0B3A55; inBg = 0xFF0C0C0E; inBorder = 0xFF222228;
            scrim = 0x99000000;
        } else {
            bg = 0xFFF3F4F6; surface = 0xFFFFFFFF; card = 0xFFFFFFFF; cardBorder = 0xFFD1D5DB;
            border = 0xFFD1D5DB; fg = 0xFF09090B; fg2 = 0xFF374151; muted = 0xFF9CA3AF;
            wireBg = 0xFFD1FAE5; wireFg = 0xFF047857; wifiBg = 0xFFE0F2FE; wifiFg = 0xFF0369A1;
            ghost = 0xFFF3F4F6; track = 0xFFE5E7EB; danger = 0xFFDC2626; dangerBg = 0xFFFEE2E2;
            success = 0xFF059669; successBg = 0xFFD1FAE5; warning = 0xFFB45309; warningBg = 0xFFFEF3C7;
            outBg = 0xFFF0F9FF; outBorder = 0xFFBAE6FD; inBg = 0xFFFFFFFF; inBorder = 0xFFD1D5DB;
            scrim = 0x66000000;
        }
    }

    int dp(float value) { return Math.round(value * density); }

    // ---------------- 形状 ----------------

    GradientDrawable box(int color, float radiusDp, float strokeDp, int strokeColor) {
        GradientDrawable d = new GradientDrawable();
        d.setColor(color);
        d.setCornerRadius(dp(radiusDp));
        if (strokeDp > 0) d.setStroke(Math.max(1, dp(strokeDp)), strokeColor);
        return d;
    }

    /** 只有上两角圆角（BottomSheet 20 / Dialog 24 用）。 */
    GradientDrawable topRounded(int color, float radiusDp, float strokeDp, int strokeColor) {
        GradientDrawable d = new GradientDrawable();
        d.setColor(color);
        float r = dp(radiusDp);
        d.setCornerRadii(new float[]{r, r, r, r, 0, 0, 0, 0});
        if (strokeDp > 0) d.setStroke(Math.max(1, dp(strokeDp)), strokeColor);
        return d;
    }

    GradientDrawable pill(int color) { return box(color, 999, 0, Color.TRANSPARENT); }

    // ---------------- 文本 ----------------

    TextView t(CharSequence value, float sp, int color, boolean bold) {
        return t(value, sp, color, bold ? Typeface.BOLD : Typeface.NORMAL);
    }

    TextView t(CharSequence value, float sp, int color, int style) {
        TextView v = new TextView(ctx);
        v.setText(value);
        v.setTextSize(sp);
        v.setTextColor(color);
        v.setTypeface(Typeface.create("sans-serif", style));
        v.setIncludeFontPadding(false);
        v.setLineSpacing(dp(3), 1f);
        return v;
    }

    /** 分组标题：11 sp、字距 0.08em、最弱色。 */
    TextView sectionLabel(String value) {
        TextView v = t(value, 11, muted, Typeface.NORMAL);
        v.setLetterSpacing(0.08f);
        v.setPadding(dp(4), dp(18), dp(4), dp(8));
        return v;
    }

    TextView titleH1(String value) { return t(value, 22, fg, Typeface.BOLD); }

    TextView caption(String value, int color) { return t(value, 12, color, Typeface.NORMAL); }

    // ---------------- 图标 ----------------

    ImageView icon(int res, float sizeDp, int tint) {
        ImageView v = new ImageView(ctx);
        v.setImageResource(res);
        v.setScaleType(ImageView.ScaleType.FIT_CENTER);
        if (tint != 0) v.setImageTintList(ColorStateList.valueOf(tint));
        v.setLayoutParams(new LinearLayout.LayoutParams(dp(sizeDp), dp(sizeDp)));
        v.setImportantForAccessibility(View.IMPORTANT_FOR_ACCESSIBILITY_NO);
        return v;
    }

    /** 48 × 48 图标按钮（热区达标），按下 12% 主色。 */
    FrameLayout iconButton(int res, int tint, Runnable action) {
        FrameLayout holder = new FrameLayout(ctx);
        ImageView image = icon(res, 24, tint);
        FrameLayout.LayoutParams ip = new FrameLayout.LayoutParams(dp(24), dp(24));
        ip.gravity = Gravity.CENTER;
        holder.addView(image, ip);
        holder.setLayoutParams(new LinearLayout.LayoutParams(dp(48), dp(48)));
        holder.setContentDescription("");
        if (action != null) tap(holder, action);
        return holder;
    }

    // ---------------- 交互 ----------------

    void ripple(View v, int color) {
        v.setBackground(new RippleDrawable(ColorStateList.valueOf(color), v.getBackground(), null));
    }

    void tap(View v, Runnable action) {
        v.setClickable(true);
        v.setFocusable(true);
        v.setForeground(new RippleDrawable(ColorStateList.valueOf(0x1F0284C7), null, null));
        v.setOnClickListener(x -> action.run());
    }

    // ---------------- 组件 ----------------

    TextView badge(String label, int fgColor, int bgColor) {
        TextView v = t(label, 11, fgColor, Typeface.BOLD);
        v.setPadding(dp(7), dp(2), dp(7), dp(2));
        v.setBackground(pill(bgColor));
        v.setSingleLine(true);
        return v;
    }

    FrameLayout avatar(int iconRes, int bgColor, int fgColor, float sizeDp) {
        FrameLayout holder = new FrameLayout(ctx);
        holder.setBackground(box(bgColor, 12, 0, Color.TRANSPARENT));
        ImageView image = icon(iconRes, sizeDp * 0.5f, fgColor);
        FrameLayout.LayoutParams ip = new FrameLayout.LayoutParams(dp(sizeDp * 0.5f), dp(sizeDp * 0.5f));
        ip.gravity = Gravity.CENTER;
        holder.addView(image, ip);
        holder.setLayoutParams(new LinearLayout.LayoutParams(dp(sizeDp), dp(sizeDp)));
        return holder;
    }

    /** 主按钮：48 高、圆角 12、主色实心、白字 15。 */
    TextView primary(String label, Runnable action) {
        TextView v = t(label, 15, onAccent, Typeface.BOLD);
        v.setGravity(Gravity.CENTER);
        v.setMinHeight(dp(48));
        v.setBackground(box(accent, 12, 0, Color.TRANSPARENT));
        v.setPadding(dp(20), 0, dp(20), 0);
        if (action != null) tap(v, action);
        return v;
    }

    TextView secondary(String label, Runnable action) {
        TextView v = t(label, 15, fg, Typeface.BOLD);
        v.setGravity(Gravity.CENTER);
        v.setMinHeight(dp(48));
        v.setBackground(box(card, 12, 1, cardBorder));
        v.setPadding(dp(20), 0, dp(20), 0);
        if (action != null) tap(v, action);
        return v;
    }

    /** 文字按钮：无底无框，主色或危险色。 */
    TextView textButton(String label, int color, Runnable action) {
        TextView v = t(label, 15, color, Typeface.BOLD);
        v.setGravity(Gravity.CENTER);
        v.setMinHeight(dp(48));
        v.setPadding(dp(12), 0, dp(12), 0);
        if (action != null) tap(v, action);
        return v;
    }

    View divider(int color) {
        View v = new View(ctx);
        v.setBackgroundColor(color);
        v.setLayoutParams(new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, Math.max(1, dp(1))));
        return v;
    }

    void space(LinearLayout parent, float h) {
        View v = new View(ctx);
        parent.addView(v, new LinearLayout.LayoutParams(1, dp(h)));
    }

    LinearLayout col() {
        LinearLayout l = new LinearLayout(ctx);
        l.setOrientation(LinearLayout.VERTICAL);
        return l;
    }

    LinearLayout row() {
        LinearLayout l = new LinearLayout(ctx);
        l.setOrientation(LinearLayout.HORIZONTAL);
        l.setGravity(Gravity.CENTER_VERTICAL);
        return l;
    }

    /** 空态：图标 48（45% 次要色）+ 标题 17 + 说明 13，垂直居中。 */
    LinearLayout emptyState(int iconRes, String title, String detail, String actionLabel, Runnable action) {
        LinearLayout box = col();
        box.setGravity(Gravity.CENTER_HORIZONTAL);
        box.setPadding(dp(32), dp(24), dp(32), dp(24));
        ImageView mark = icon(iconRes, 48, muted);
        mark.setAlpha(0.45f);
        LinearLayout.LayoutParams mp = new LinearLayout.LayoutParams(dp(48), dp(48));
        mp.gravity = Gravity.CENTER_HORIZONTAL;
        mp.bottomMargin = dp(16);
        box.addView(mark, mp);
        TextView head = t(title, 17, fg, Typeface.BOLD);
        head.setGravity(Gravity.CENTER);
        box.addView(head, new LinearLayout.LayoutParams(-1, -2));
        if (detail != null && !detail.isEmpty()) {
            space(box, 8);
            TextView sub = t(detail, 13, fg2, Typeface.NORMAL);
            sub.setGravity(Gravity.CENTER);
            box.addView(sub, new LinearLayout.LayoutParams(-1, -2));
        }
        if (actionLabel != null) {
            space(box, 20);
            TextView button = primary(actionLabel, action);
            LinearLayout.LayoutParams bp = new LinearLayout.LayoutParams(-2, dp(48));
            bp.gravity = Gravity.CENTER_HORIZONTAL;
            box.addView(button, bp);
        }
        return box;
    }

    ImageView chevron() { return icon(R.drawable.ic_nav_chevron_right, 16, muted); }

    // ---------------- 进度 ----------------

    /** 4/6 dp 高进度条：轨道 Track + 主色填充，圆角 999。 */
    static final class ProgressLine extends FrameLayout {
        private final View fill;
        private final int trackColor;
        private final int fillColor;
        private final float heightDp;
        private float percent;

        ProgressLine(Activity ctx, Ui ui, float heightDp, int fillColor, int trackColor) {
            super(ctx);
            this.heightDp = heightDp;
            this.fillColor = fillColor;
            this.trackColor = trackColor;
            setBackground(ui.pill(trackColor));
            fill = new View(ctx);
            fill.setBackground(ui.pill(fillColor));
            addView(fill, new FrameLayout.LayoutParams(0, ViewGroup.LayoutParams.MATCH_PARENT));
        }

        void setPercent(float value) {
            float p = Math.max(0f, Math.min(1f, value));
            if (Math.abs(p - percent) < 0.004f && p != 1f) return;
            percent = p;
            apply();
        }

        private void apply() {
            ViewGroup.LayoutParams lp = fill.getLayoutParams();
            lp.width = Math.round(getWidth() * percent);
            fill.setLayoutParams(lp);
        }

        @Override protected void onSizeChanged(int w, int h, int oldw, int oldh) {
            super.onSizeChanged(w, h, oldw, oldh);
            apply();
        }
    }
}
