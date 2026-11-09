package cn.ethertransfer.mobile;

import android.net.Network;

import org.json.JSONObject;

import java.io.File;
import java.io.IOException;
import java.io.RandomAccessFile;
import java.net.InetAddress;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.util.HashMap;
import java.util.List;
import java.util.Map;

/**
 * AP 隔离模式的广播接收端（对应方案里的 recv）。
 * <p>单任务：一次只收一个文件或一条文本；先写 .part 临时文件，全部收齐后校验 SHA-256，
 * 一致才交给宿主导入到「下载」目录并回 DONE。</p>
 */
final class UdpReceiver implements Runnable {

    interface Host {
        /** 是否接受本次传输（自动接收时直接 true，否则弹确认浮层）。request 带 SenderName/TotalFiles/TotalSize。 */
        boolean accept(JSONObject request, String ip) throws Exception;

        void progress(String name, long done, long size);

        /** 校验通过后由宿主把临时文件导入接收位置（媒体库或 SAF），返回后本类删除临时文件。 */
        void completed(JSONObject meta, File part, String ip) throws Exception;

        void textReceived(JSONObject meta, String content, String ip) throws Exception;

        void log(String message, boolean warning);

        /** 本机 Wi-Fi 地址：用于排掉内核回环回来的自己发出的广播。 */
        java.util.List<InetAddress> localAddresses();
    }

    private static final int POLL_TIMEOUT = 1000;

    /** 会话空闲上限：发送端消失（关窗、断网、被杀）后必须能自动释放，否则接收端会永久占线。 */
    private static final long IDLE_TIMEOUT = 20000;

    private final int port;
    private final Network network;
    private final List<InetAddress> targets;
    private final File workDir;
    private final Host host;

    private volatile boolean running = true;
    private UdpChannel channel;
    private Thread thread;

    // 当前会话
    private byte[] transferId;
    private JSONObject meta;
    private int total;
    private long size;
    private boolean[] received;
    private int count;
    private RandomAccessFile part;
    private File partFile;
    private final Map<Integer, Integer> newSinceAck = new HashMap<>();
    private long lastPercent = -1;
    private String name = "";
    private String path = "";
    private boolean isText;

    // 已完成/已授权
    private byte[] doneId;
    /** 已给出结论的传输（tid -> 是否接受）：发送端会重发 START，不能每次都弹确认框。 */
    private final Map<String, Boolean> decisions = new HashMap<>();
    private String approvedBatch = "";
    private String approvedIp = "";

    private volatile long lastActivity;
    private long localCacheAt;
    private java.util.List<InetAddress> localCache = java.util.Collections.emptyList();

    UdpReceiver(int port, Network network, List<InetAddress> targets, File workDir, Host host) {
        this.port = port;
        this.network = network;
        this.targets = targets;
        this.workDir = workDir;
        this.host = host;
    }

    void start() throws IOException {
        if (targets.isEmpty()) throw new IOException("未获取到 Wi-Fi 广播地址");
        channel = UdpChannel.listener(network, port, targets);
        thread = new Thread(this, "ethertransfer-udp");
        thread.setDaemon(true);
        thread.start();
    }

    void stop() {
        running = false;
        if (channel != null) channel.close();
        release();
    }

    @Override public void run() {
        while (running) {
            try {
                UdpChannel.Incoming incoming = channel.receive(POLL_TIMEOUT);
                if (incoming == null) {
                    expireIfIdle();
                    continue;
                }

                // 定向广播会被内核回环给本机的监听端：不排掉自己发的报文，
                // 手机自己发的文件会被自己再收一份，还会占住接收状态。
                if (isSelf(incoming.source)) continue;

                UdpWire.Packet packet = UdpWire.parse(incoming.data, incoming.length);
                if (packet == null) continue;

                // 只让「本会话」的报文刷新空闲计时：别的设备重试 START 不能把会话一直吊着。
                if (transferId != null && UdpWire.sameId(transferId, packet.transferId))
                    lastActivity = System.currentTimeMillis();

                if (packet.kind == UdpWire.START) handleStart(packet, incoming.source);
                else if (packet.kind == UdpWire.DATA) handleData(packet, incoming.source);
                else if (packet.kind == UdpWire.FIN) handleFin(packet, incoming.source);
            } catch (Exception e) {
                if (!running) return;
                host.log("广播接收异常：" + e.getMessage(), true);
                failCurrent(null, null);
            }
        }
    }

    /** 发送端彻底消失时释放会话，避免「手机端正在接收另一个文件」把后续传输全部挡掉。 */
    private void expireIfIdle() {
        if (transferId == null) return;
        if (System.currentTimeMillis() - lastActivity < IDLE_TIMEOUT) return;
        host.log("广播接收超时（" + (IDLE_TIMEOUT / 1000) + " 秒无数据），已释放会话：" + path, true);
        failCurrent(null, null);
    }

    private boolean isSelf(InetAddress source) {
        long now = android.os.SystemClock.elapsedRealtime();
        if (now - localCacheAt > 5000) {
            try {
                localCache = host.localAddresses();
            } catch (Exception e) {
                localCache = java.util.Collections.emptyList();
            }
            localCacheAt = now;
        }
        for (InetAddress local : localCache) {
            if (local.equals(source)) return true;
        }
        return false;
    }

    private void handleStart(UdpWire.Packet packet, InetAddress source) throws Exception {
        String ip = source.getHostAddress();

        // DONE 丢包导致发送端重发 START：直接再回一次 READY 收尾。
        if (UdpWire.sameId(doneId, packet.transferId)) {
            send(UdpWire.packet(UdpWire.READY, packet.transferId, 0, 0, null, 0));
            return;
        }

        if (transferId != null) {
            if (!UdpWire.sameId(transferId, packet.transferId)) {
                host.log("拒绝新的广播传输：正在处理 " + UdpWire.hex(transferId).substring(0, 8)
                    + "（" + path + "），新请求 " + UdpWire.hex(packet.transferId).substring(0, 8), true);
                send(UdpWire.error(packet.transferId, "手机端正在接收另一个文件，请稍后重试"));
            } else {
                send(UdpWire.packet(UdpWire.READY, packet.transferId, total, 0, null, 0));
            }
            return;
        }

        JSONObject incoming;
        int expected;
        try {
            incoming = new JSONObject(new String(packet.body, StandardCharsets.UTF_8));
            expected = UdpWire.validate(incoming);

            String key = UdpWire.hex(packet.transferId);
            Boolean prior = decisions.get(key);
            if (prior != null && !prior)
                throw new IOException("同一个传输此前已拒绝或已超时，请重新发起");

            String batch = incoming.optString("batch", "");
            if (!isText(incoming)) {
                boolean approved = !batch.isEmpty() && batch.equals(approvedBatch) && ip.equals(approvedIp);
                if (!approved) {
                    JSONObject request = new JSONObject()
                        .put("SenderName", incoming.optString("sender", "对方"))
                        .put("TotalFiles", Math.max(1, incoming.optInt("batch_count", 1)))
                        .put("TotalSize", incoming.optLong("size", 0));
                    boolean ok = host.accept(request, ip);
                    remember(key, ok);
                    if (!ok) throw new IOException("用户已拒绝");
                    approvedBatch = batch;
                    approvedIp = ip;
                }
            }
        } catch (Exception invalid) {
            // 参数错误或用户拒绝都要立刻告诉发送端，否则它只能干等 60 秒握手超时。
            send(UdpWire.error(packet.transferId,
                invalid.getMessage() == null ? "START 参数错误" : invalid.getMessage()));
            return;
        }

        meta = incoming;
        transferId = packet.transferId;
        total = expected;
        size = incoming.optLong("size", 0);
        received = new boolean[total];
        count = 0;
        newSinceAck.clear();
        lastPercent = -1;
        isText = isText(incoming);
        name = incoming.optString("name", "接收文件");
        path = incoming.optString("path", "");
        if (path.isEmpty()) path = name;

        if (isText) {
            partFile = new File(workDir, "udp-" + UdpWire.hex(packet.transferId).substring(0, 8) + ".txt");
        } else {
            partFile = new File(workDir, "udp-" + UdpWire.hex(packet.transferId).substring(0, 8) + ".part");
        }
        lastActivity = System.currentTimeMillis();
        part = new RandomAccessFile(partFile, "rw");
        part.setLength(0);
        part.setLength(size);

        host.log("开始接收广播" + (isText ? "文本" : "文件") + "：" + path + "（" + size + " 字节，"
            + total + " 个分片，来自 " + ip + "）", false);
        send(UdpWire.packet(UdpWire.READY, packet.transferId, total, 0, null, 0));
    }

    private void handleData(UdpWire.Packet packet, InetAddress source) throws IOException {
        if (transferId == null || !UdpWire.sameId(transferId, packet.transferId)) return;

        int sequence = packet.arg1;
        if (sequence < 0 || sequence >= total) return;

        int expected = UdpWire.chunkLength(size, sequence);
        if (packet.bodyLength != expected) {
            send(UdpWire.error(packet.transferId, "分片 " + sequence + " 长度不正确"));
            return;
        }

        int windowBase = sequence / UdpWire.WINDOW * UdpWire.WINDOW;
        boolean duplicate = received[sequence];

        if (!duplicate) {
            long offset = (long) sequence * UdpWire.CHUNK;
            part.seek(offset);
            part.write(packet.body, 0, packet.bodyLength);
            received[sequence] = true;
            count++;

            Integer seen = newSinceAck.get(windowBase);
            newSinceAck.put(windowBase, seen == null ? 1 : seen + 1);
        }

        // 累积位图 ACK；重复分片也回一次，丢失的 ACK 能立刻恢复。
        Integer seen = newSinceAck.get(windowBase);
        if (duplicate || (seen != null && seen >= UdpWire.ACK_EVERY) || sequence == total - 1) {
            sendAck(windowBase, packet.transferId);
            newSinceAck.put(windowBase, 0);
        }

        if (total > 0) {
            long percent = (long) count * 100 / total;
            if (percent >= lastPercent + 5 || count == total) {
                lastPercent = percent;
                host.progress(name, Math.min(size, (long) count * UdpWire.CHUNK), size);
            }
        }
    }

    private void handleFin(UdpWire.Packet packet, InetAddress source) throws Exception {
        if (UdpWire.sameId(doneId, packet.transferId)) {
            send(UdpWire.packet(UdpWire.DONE, packet.transferId, 0, 0, null, 0));
            return;
        }

        if (transferId == null || !UdpWire.sameId(transferId, packet.transferId)) return;

        if (count != total) {
            // 告诉发送端每个窗口到底缺哪些分片。
            for (int base = 0; base < total; base += UdpWire.WINDOW) sendAck(base, packet.transferId);
            return;
        }

        part.getFD().sync();
        String actual = digest(partFile);

        if (!actual.equalsIgnoreCase(meta.optString("sha256", ""))) {
            failCurrent(packet.transferId, "SHA-256 校验失败；文件未保存");
            return;
        }

        if (isText) {
            byte[] body = new byte[(int) size];
            part.seek(0);
            part.readFully(body);
            String content = new String(body, StandardCharsets.UTF_8);
            if (content.isEmpty()) throw new IOException("文本消息内容为空");
            if (content.length() > 20000) throw new IOException("文本过长，已拒绝");

            String senderIp = source.getHostAddress();
            host.textReceived(meta, content, senderIp);
            host.log("收到 " + meta.optString("sender", "对方") + " 的广播文本（" + content.length() + " 字）", false);
        } else {
            String senderIp = source.getHostAddress();
            host.completed(meta, partFile, senderIp);
            host.log("广播接收完成：" + path, false);
        }

        byte[] finished = packet.transferId;
        release();
        doneId = finished;
        send(UdpWire.packet(UdpWire.DONE, finished, 0, 0, null, 0));
    }

    private void sendAck(int windowBase, byte[] id) throws IOException {
        int width = Math.min(UdpWire.WINDOW, total - windowBase);
        if (width <= 0) return;

        int mask = 0;
        for (int bit = 0; bit < width; bit++) {
            if (received[windowBase + bit]) mask |= 1 << bit;
        }
        send(UdpWire.packet(UdpWire.ACK, id, windowBase, mask, null, 0));
    }

    private void send(byte[] payload) {
        try {
            if (channel != null) channel.send(payload);
        } catch (IOException e) {
            host.log("广播应答失败：" + e.getMessage(), true);
        }
    }

    private void failCurrent(byte[] id, String reason) {
        if (reason != null && id != null) send(UdpWire.error(id, reason));
        release();
    }

    private void release() {
        if (part != null) {
            try { part.close(); } catch (IOException ignored) { }
            part = null;
        }
        if (partFile != null && partFile.exists()) {
            try { partFile.delete(); } catch (Exception ignored) { }
        }
        partFile = null;
        transferId = null;
        meta = null;
        received = null;
        count = 0;
        total = 0;
        size = 0;
        newSinceAck.clear();
    }

    /** 记住本次传输的结论，避免发送端重发的 START 反复弹确认框（每个超时 120 秒会彻底占住接收端）。 */
    private void remember(String key, boolean accepted) {
        if (decisions.size() > 64) decisions.clear();
        decisions.put(key, accepted);
    }

    private static boolean isText(JSONObject meta) {
        return "text".equalsIgnoreCase(meta.optString("kind", "file"));
    }

    private static String digest(File file) throws Exception {
        MessageDigest sha = MessageDigest.getInstance("SHA-256");
        try (java.io.InputStream in = new java.io.BufferedInputStream(
            new java.io.FileInputStream(file), 1024 * 1024)) {
            byte[] buffer = new byte[1024 * 1024];
            int read;
            while ((read = in.read(buffer)) != -1) sha.update(buffer, 0, read);
        }
        return UdpWire.hex(sha.digest());
    }
}
