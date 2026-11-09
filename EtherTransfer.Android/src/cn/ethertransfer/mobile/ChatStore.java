package cn.ethertransfer.mobile;

import android.content.Context;
import java.io.*;
import java.nio.charset.StandardCharsets;
import java.util.*;

/**
 * 会话消息与持久化（与桌面端 v3 的 MessageStore 对齐）。
 * <p>只存文本和文件名/大小/状态/时间，不保留文件本体，也不复制收到的文件。
 * 每台对端设备一个 JSON 文件：filesDir/messages-&lt;deviceId&gt;.json。
 * 所有读写都在类锁内完成（load-modify-save 原子），UI 线程与传输线程可安全并发调用。
 * JSON 采用手写序列化（不依赖 org.json），保证桌面 JVM 协议测试也能编译运行。</p>
 */
public final class ChatStore {
    /** 消息状态：发送中 / 已发送 / 发送失败 / 接收中 / 已接收。 */
    public static final String SENDING = "sending", SENT = "sent", FAILED = "failed",
        RECEIVING = "receiving", RECEIVED = "received";

    private static final Object LOCK = new Object();

    /** 字节数 → 人类可读（B/KB/MB/GB）。 */
    public static String sizeText(long bytes) { return Msg.sizeText(bytes); }

    public static class Msg {
        public String msgId;
        public boolean out;      // true=我发出的
        public boolean file;     // true=文件消息，false=文本消息（system 用 sys 标记）
        public boolean sys;      // true=系统提示（居中灰字）
        public String text = ""; // 文本内容；sys 时为提示文案
        public String fileName = "";
        /** 发送源 URI（content://...），仅发出的文件消息有；用于失败后自动补发。 */
        public String src = "";
        public long size;
        public String state = SENT;
        public long time;

        public Msg() { }

        public static Msg text(boolean out, String content, String state) {
            Msg m = new Msg(); m.msgId = newId(); m.out = out; m.file = false;
            m.text = content == null ? "" : content; m.state = state;
            m.time = System.currentTimeMillis(); return m;
        }
        public static Msg file(boolean out, String name, long size, String state) {
            Msg m = new Msg(); m.msgId = newId(); m.out = out; m.file = true;
            m.fileName = name == null ? "" : name; m.size = Math.max(0, size); m.state = state;
            m.time = System.currentTimeMillis(); return m;
        }
        public static Msg system(String content) {
            Msg m = new Msg(); m.msgId = newId(); m.sys = true;
            m.text = content == null ? "" : content; m.state = SENT;
            m.time = System.currentTimeMillis(); return m;
        }
        private static String newId() {
            return Long.toHexString(System.currentTimeMillis()) + Long.toHexString(Double.doubleToLongBits(Math.random()));
        }

        /** 展示用状态文案（与桌面端 StateText 一致）。 */
        public String stateText() {
            if (sys) return "";
            if (file) {
                if (SENDING.equals(state)) return "发送中…";
                if (RECEIVING.equals(state)) return "接收中…";
                if (FAILED.equals(state)) return out ? "发送失败 · 点此重试" : "接收失败";
                String sizeText = sizeText();
                if (RECEIVED.equals(state)) return sizeText.isEmpty() ? "已接收" : sizeText + " · 已接收";
                return sizeText.isEmpty() ? "已发送" : sizeText + " · 已发送";
            }
            return "";
        }
        public String sizeText() {
            return sizeText(size);
        }
        /** 字节数 → 人类可读（B/KB/MB/GB）。 */
        public static String sizeText(long bytes) {
            if (bytes <= 0) return "";
            double v = bytes; String unit = "B";
            if (v >= 1024) { v /= 1024; unit = "KB"; }
            if (v >= 1024) { v /= 1024; unit = "MB"; }
            if (v >= 1024) { v /= 1024; unit = "GB"; }
            return v >= 100 ? String.format(Locale.US, "%.0f %s", v, unit)
                : String.format(Locale.US, "%.1f %s", v, unit);
        }
    }

    private static File fileFor(Context c, String deviceId) {
        String safe = deviceId == null ? "unknown" : deviceId.replaceAll("[^A-Za-z0-9_-]", "_");
        return new File(c.getFilesDir(), "messages-" + safe + ".json");
    }

    /** 读取某设备的全部消息（文件损坏时返回空列表，绝不抛出）。 */
    public static List<Msg> load(Context c, String deviceId) {
        synchronized (LOCK) { return loadLocked(fileFor(c, deviceId)); }
    }

    /** 追加一条消息并落盘。 */
    public static void append(Context c, String deviceId, Msg msg) {
        synchronized (LOCK) {
            File f = fileFor(c, deviceId);
            List<Msg> msgs = loadLocked(f);
            msgs.add(msg); saveLocked(f, msgs);
        }
    }

    public static void appendAll(Context c, String deviceId, List<Msg> msgs) {
        synchronized (LOCK) {
            if (msgs.isEmpty()) return;
            File f = fileFor(c, deviceId);
            List<Msg> all = loadLocked(f);
            all.addAll(msgs); saveLocked(f, all);
        }
    }

    /** 更新消息状态并落盘。 */
    public static void updateState(Context c, String deviceId, String msgId, String state) {
        synchronized (LOCK) {
            File f = fileFor(c, deviceId);
            List<Msg> msgs = loadLocked(f);
            boolean changed = false;
            for (Msg m : msgs) if (msgId.equals(m.msgId)) { m.state = state; changed = true; }
            if (changed) saveLocked(f, msgs);
        }
    }

    /** 更新消息文件大小（接收完成时回填实际字节数）并落盘。 */
    public static void updateSize(Context c, String deviceId, String msgId, long size) {
        synchronized (LOCK) {
            File f = fileFor(c, deviceId);
            List<Msg> msgs = loadLocked(f);
            boolean changed = false;
            for (Msg m : msgs) if (msgId.equals(m.msgId)) { m.size = size; changed = true; }
            if (changed) saveLocked(f, msgs);
        }
    }

    /** 全部设备的消息总条数（设备页“清理缓存”展示用）。 */
    public static int totalCount(Context c) {
        synchronized (LOCK) {
            int total = 0;
            File[] files = c.getFilesDir().listFiles((dir, name) -> name.startsWith("messages-") && name.endsWith(".json"));
            if (files != null) for (File f : files) total += loadLocked(f).size();
            return total;
        }
    }

    /** 删除指定消息，返回删除条数。 */
    public static int deleteMessages(Context c, String deviceId, Collection<String> ids) {
        if (ids == null || ids.isEmpty()) return 0;
        synchronized (LOCK) {
            File f = fileFor(c, deviceId);
            List<Msg> msgs = loadLocked(f);
            int before = msgs.size();
            Iterator<Msg> it = msgs.iterator();
            while (it.hasNext()) if (ids.contains(it.next().msgId)) it.remove();
            int removed = before - msgs.size();
            if (removed > 0) saveLocked(f, msgs);
            return removed;
        }
    }

    /** 清空某设备会话；keepRecent>0 时每设备保留最近 N 条。返回删除条数。 */
    public static int clear(Context c, String deviceId, int keepRecent) {
        synchronized (LOCK) {
            File f = fileFor(c, deviceId);
            List<Msg> msgs = loadLocked(f);
            return clearLocked(f, msgs, keepRecent);
        }
    }

    /** 清理全部设备的会话记录（设备页“清理缓存”），keepRecent<0 表示全部删除。 */
    public static int clearAll(Context c, int keepRecent) {
        synchronized (LOCK) {
            int removed = 0;
            File[] files = c.getFilesDir().listFiles((dir, name) -> name.startsWith("messages-") && name.endsWith(".json"));
            if (files == null) return 0;
            for (File f : files) removed += clearLocked(f, loadLocked(f), keepRecent);
            return removed;
        }
    }

    /** 会话列表用的聚合行：一台对端设备一条。 */
    public static final class Conv {
        public String deviceId;
        public Msg last;
        public int count;
        public long time;
    }

    /**
     * 按设备聚合会话（最后一条消息、条数、时间），按时间倒序。
     * 设备 id 取自文件名：{@link #fileFor} 的清洗对 UUID / ip- 前缀是幂等的，可直接反解。
     */
    public static List<Conv> conversations(Context c) {
        synchronized (LOCK) {
            List<Conv> out = new ArrayList<>();
            File[] files = c.getFilesDir().listFiles((dir, name) -> name.startsWith("messages-") && name.endsWith(".json"));
            if (files == null) return out;
            for (File f : files) {
                String name = f.getName();
                String deviceId = name.substring("messages-".length(), name.length() - ".json".length());
                List<Msg> msgs = loadLocked(f);
                if (msgs.isEmpty()) continue;
                Conv conv = new Conv();
                conv.deviceId = deviceId;
                conv.count = msgs.size();
                conv.last = msgs.get(msgs.size() - 1);
                conv.time = conv.last.time;
                out.add(conv);
            }
            out.sort((a, b) -> Long.compare(b.time, a.time));
            return out;
        }
    }

    /** 某设备会话的条数。 */
    public static int count(Context c, String deviceId) {
        synchronized (LOCK) { return loadLocked(fileFor(c, deviceId)).size(); }
    }

    /** 会话记录占用总字节数（仅 JSON 记录本身）。 */
    public static long totalBytes(Context c) {
        synchronized (LOCK) {
            long total = 0;
            File[] files = c.getFilesDir().listFiles((dir, name) -> name.startsWith("messages-") && name.endsWith(".json"));
            if (files != null) for (File f : files) total += f.length();
            return total;
        }
    }

    private static int clearLocked(File f, List<Msg> msgs, int keepRecent) {
        if (msgs.isEmpty()) return 0;
        int removed;
        if (keepRecent > 0 && msgs.size() > keepRecent) {
            removed = msgs.size() - keepRecent;
            saveLocked(f, new ArrayList<>(msgs.subList(removed, msgs.size())));
        } else if (keepRecent <= 0) {
            removed = msgs.size();
            saveLocked(f, new ArrayList<>());
        } else {
            return 0;
        }
        return removed;
    }

    // ---------- JSON 序列化（手写，仅覆盖本结构需要的类型） ----------

    private static List<Msg> loadLocked(File f) {
        List<Msg> msgs = new ArrayList<>();
        if (!f.isFile()) return msgs;
        try (DataInputStream in = new DataInputStream(new BufferedInputStream(new FileInputStream(f)))) {
            byte[] bytes = new byte[(int) f.length()];
            in.readFully(bytes);
            parseArray(new String(bytes, StandardCharsets.UTF_8), msgs);
        } catch (Exception ignored) { }
        return msgs;
    }

    private static void saveLocked(File f, List<Msg> msgs) {
        StringBuilder json = new StringBuilder(256 * (msgs.size() + 1)).append('[');
        for (int i = 0; i < msgs.size(); i++) {
            if (i > 0) json.append(',');
            Msg m = msgs.get(i);
            json.append('{')
                .append("\"id\":\"").append(esc(m.msgId)).append("\",")
                .append("\"out\":").append(m.out).append(',')
                .append("\"file\":").append(m.file).append(',')
                .append("\"sys\":").append(m.sys).append(',')
                .append("\"text\":\"").append(esc(m.text)).append("\",")
                .append("\"name\":\"").append(esc(m.fileName)).append("\",")
                .append("\"src\":\"").append(esc(m.src)).append("\",")
                .append("\"size\":").append(m.size).append(',')
                .append("\"state\":\"").append(esc(m.state)).append("\",")
                .append("\"time\":").append(m.time)
                .append('}');
        }
        json.append(']');
        File temp = new File(f.getParentFile(), f.getName() + ".tmp");
        try (OutputStream out = new FileOutputStream(temp)) {
            out.write(json.toString().getBytes(StandardCharsets.UTF_8));
            out.flush();
        } catch (Exception ignored) { return; }
        if (!temp.renameTo(f)) { f.delete(); temp.renameTo(f); }
    }

    private static String esc(String s) {
        if (s == null) return "";
        StringBuilder b = new StringBuilder(s.length() + 8);
        for (int i = 0; i < s.length(); i++) {
            char ch = s.charAt(i);
            switch (ch) {
                case '"': b.append("\\\""); break;
                case '\\': b.append("\\\\"); break;
                case '\n': b.append("\\n"); break;
                case '\r': b.append("\\r"); break;
                case '\t': b.append("\\t"); break;
                default:
                    if (ch < 0x20) b.append(String.format(Locale.US, "\\u%04x", (int) ch));
                    else b.append(ch);
            }
        }
        return b.toString();
    }

    private static void parseArray(String json, List<Msg> out) {
        Parser p = new Parser(json);
        p.ws();
        if (p.peek() != '[') return;
        p.next();
        p.ws();
        if (p.peek() == ']') return;
        while (p.hasMore()) {
            p.ws();
            if (p.peek() != '{') return;
            Msg m = new Msg();
            parseObject(p, m);
            if (m.msgId != null) out.add(m);
            p.ws();
            if (p.peek() == ',') { p.next(); continue; }
            break;
        }
    }

    private static void parseObject(Parser p, Msg m) {
        p.next(); // '{'
        while (p.hasMore()) {
            p.ws();
            if (p.peek() == '}') { p.next(); return; }
            String key = p.string();
            p.ws();
            if (p.peek() != ':') return;
            p.next(); p.ws();
            char ch = p.peek();
            if (ch == '"') {
                String value = p.string();
                if ("id".equals(key)) m.msgId = value;
                else if ("text".equals(key)) m.text = value;
                else if ("name".equals(key)) m.fileName = value;
                else if ("src".equals(key)) m.src = value;
                else if ("state".equals(key)) m.state = value;
            } else if (ch == 't' || ch == 'f') {
                boolean value = p.bool();
                if ("out".equals(key)) m.out = value;
                else if ("file".equals(key)) m.file = value;
                else if ("sys".equals(key)) m.sys = value;
            } else {
                long value = p.number();
                if ("size".equals(key)) m.size = value;
                else if ("time".equals(key)) m.time = value;
            }
            p.ws();
            if (p.peek() == ',') p.next();
        }
    }

    /** 极小 JSON 词法器：只支持本结构用到的字符串/数字/布尔/结构与空白。 */
    private static final class Parser {
        private final String s; private int i;
        Parser(String s) { this.s = s; }
        boolean hasMore() { return i < s.length(); }
        char peek() { return i < s.length() ? s.charAt(i) : '\0'; }
        void next() { i++; }
        void ws() { while (i < s.length() && Character.isWhitespace(s.charAt(i))) i++; }
        boolean bool() {
            if (s.startsWith("true", i)) { i += 4; return true; }
            i += 5; return false;
        }
        long number() {
            int start = i;
            while (i < s.length() && "+-0123456789.eE".indexOf(s.charAt(i)) >= 0) i++;
            try { return (long) Double.parseDouble(s.substring(start, i)); }
            catch (Exception e) { return 0; }
        }
        String string() {
            StringBuilder b = new StringBuilder();
            if (s.charAt(i) != '"') return "";
            i++;
            while (i < s.length()) {
                char ch = s.charAt(i++);
                if (ch == '"') break;
                if (ch == '\\' && i < s.length()) {
                    char e = s.charAt(i++);
                    switch (e) {
                        case '"': case '\\': case '/': b.append(e); break;
                        case 'n': b.append('\n'); break;
                        case 'r': b.append('\r'); break;
                        case 't': b.append('\t'); break;
                        case 'b': case 'f': break;
                        default:
                            if (e == 'u' && i + 4 <= s.length()) {
                                try { b.append((char) Integer.parseInt(s.substring(i, i + 4), 16)); } catch (Exception ignored) { }
                                i += 4;
                            }
                    }
                } else b.append(ch);
            }
            return b.toString();
        }
    }

    private ChatStore() { }
}
