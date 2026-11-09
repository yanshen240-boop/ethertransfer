package cn.ethertransfer.mobile;

import java.text.SimpleDateFormat;
import java.util.ArrayList;
import java.util.Date;
import java.util.LinkedList;
import java.util.List;
import java.util.Locale;

/**
 * 环形日志缓冲（P17 开发者日志）：最多保留 2000 行，超出丢弃最早的。
 * <p>只存内存，不落盘——目的是现场排障，不是长期审计。</p>
 */
final class Logs {
    static final int DEFAULT = 0, ERROR = 1, WARNING = 2, SUCCESS = 3, INFO = 4, SPECIAL = 5;
    private static final int MAX = 2000;
    private static final LinkedList<Line> LINES = new LinkedList<>();
    private static final SimpleDateFormat TIME = new SimpleDateFormat("HH:mm:ss", Locale.US);

    static final class Line {
        final long time;
        final int level;
        final String text;
        Line(long time, int level, String text) {
            this.time = time;
            this.level = level;
            this.text = text;
        }
    }

    private Logs() { }

    static synchronized void add(int level, String text) {
        if (text == null || text.isEmpty()) return;
        Line line = new Line(System.currentTimeMillis(), level, TIME.format(new Date()) + "  " + text);
        LINES.addLast(line);
        while (LINES.size() > MAX) LINES.removeFirst();
        // 同时镜像到 logcat，便于用 adb 现场排障（尤其 AP 隔离模式下没有可点的界面）。
        android.util.Log.println(priorityOf(level), "EtherTransfer", text);
    }

    private static int priorityOf(int level) {
        if (level == ERROR) return android.util.Log.ERROR;
        if (level == WARNING) return android.util.Log.WARN;
        return android.util.Log.INFO;
    }

    static synchronized List<Line> lines() { return new ArrayList<>(LINES); }

    static synchronized int size() { return LINES.size(); }

    static synchronized String dump() {
        StringBuilder builder = new StringBuilder(LINES.size() * 48);
        for (Line line : LINES) builder.append(line.text).append('\n');
        return builder.toString();
    }
}
