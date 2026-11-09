package cn.ethertransfer.mobile;

import org.json.JSONObject;

import java.io.File;
import java.io.IOException;
import java.io.RandomAccessFile;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.util.HashMap;
import java.util.Map;

/**
 * AP 隔离模式的广播发送端（对应方案里的 send）。
 * <p>START → READY → 32 分片窗口 + 累积位图 ACK 重传 → FIN → DONE。
 * 每个文件一个独立的传输 ID；一批文件共用 batch 标识，接收端只需确认一次目录。</p>
 */
final class UdpSender {

    interface Progress {
        void update(long confirmed) throws IOException;
    }

    interface Check {
        void check() throws IOException;
    }

    private interface ChunkSource {
        int read(long offset, byte[] buffer, int length) throws IOException;
        void close();
    }

    private UdpSender() { }

    static void sendFile(UdpChannel channel, File file, String name, String path, String root,
                         String senderName, String batch, int batchIndex, int batchCount,
                         Progress progress, Check check) throws Exception {
        long size = file.length();
        if (size > UdpWire.MAX_FILE) throw new IOException("文件超过 20 GiB 上限：" + name);

        String digest = digest(file);
        JSONObject meta = metadata(name, path, root, size, digest, senderName, batch, batchIndex, batchCount, "file");
        meta.put("mtime", file.lastModified());

        RandomAccessFile handle = new RandomAccessFile(file, "r");
        try {
            transmit(channel, new RandomAccessFileSource(handle), meta, size, progress, check);
        } finally {
            try { handle.close(); } catch (IOException ignored) { }
        }
    }

    static void sendText(UdpChannel channel, String text, String senderName, Check check) throws Exception {
        byte[] body = text.getBytes(StandardCharsets.UTF_8);
        String digest = UdpWire.hex(MessageDigest.getInstance("SHA-256").digest(body));
        String name = "消息-" + System.currentTimeMillis() + ".txt";

        JSONObject meta = metadata(name, "", "", body.length, digest, senderName,
            "", 0, 1, "text");
        meta.put("mtime", System.currentTimeMillis());

        transmit(channel, new MemorySource(body), meta, body.length, new Progress() {
            @Override public void update(long confirmed) { }
        }, check);
    }

    private static JSONObject metadata(String name, String path, String root, long size, String digest,
                                       String senderName, String batch, int index, int count, String kind)
        throws Exception {
        return new JSONObject()
            .put("name", name)
            .put("path", path == null ? "" : path)
            .put("root", root == null ? "" : root)
            .put("size", size)
            .put("chunk_size", UdpWire.CHUNK)
            .put("total", UdpWire.totalChunks(size))
            .put("sha256", digest)
            .put("sender", senderName == null ? "" : senderName)
            .put("kind", kind)
            .put("batch", batch == null ? "" : batch)
            .put("batch_index", index)
            .put("batch_count", count);
    }

    private static void transmit(UdpChannel channel, ChunkSource source, JSONObject meta, long size,
                                 Progress progress, Check check) throws Exception {
        int total = UdpWire.totalChunks(size);
        byte[] transferId = UdpWire.transferId();
        byte[] startPacket = UdpWire.start(transferId, meta);

        awaitReady(channel, transferId, startPacket, meta.optString("name", ""), check);

        for (int base = 0; base < total; base += UdpWire.WINDOW) {
            check.check();
            int count = Math.min(UdpWire.WINDOW, total - base);
            sendWindow(channel, transferId, source, size, total, base, count, check);
            progress.update(Math.min(size, (long) (base + count) * UdpWire.CHUNK));
        }

        awaitDone(channel, transferId, source, size, total, check);
        source.close();
    }

    private static void awaitReady(UdpChannel channel, byte[] transferId, byte[] startPacket,
                                   String label, Check check) throws Exception {
        long deadline = System.currentTimeMillis() + UdpWire.START_SECONDS * 1000L;
        int attempt = 0;

        while (System.currentTimeMillis() < deadline) {
            check.check();
            attempt++;
            channel.send(startPacket);

            long windowEnd = System.currentTimeMillis() + UdpWire.START_INTERVAL;
            while (System.currentTimeMillis() < windowEnd) {
                UdpChannel.Incoming incoming = channel.receive(
                    (int) Math.max(30, windowEnd - System.currentTimeMillis()));
                if (incoming == null) break;

                UdpWire.Packet packet = UdpWire.parse(incoming.data, incoming.length);
                if (packet == null || !UdpWire.sameId(packet.transferId, transferId)) continue;

                if (packet.kind == UdpWire.READY) {
                    Logs.add(Logs.INFO, "广播接收端已就绪（" + incoming.source.getHostAddress() + "），开始发送 " + label);
                    return;
                }
                if (packet.kind == UdpWire.ERROR) throw new IOException(UdpWire.errorText(packet.body));
            }

            if (attempt % 5 == 0) Logs.add(Logs.WARNING, "等待接收端 READY（第 " + attempt + " 次未收到）");
        }

        throw new IOException("没有收到接收端的 READY：请确认对端已启动，且 AP 允许客户端之间转发 UDP 广播");
    }

    private static void sendWindow(UdpChannel channel, byte[] transferId, ChunkSource source,
                                   long size, int total, int base, int count, Check check) throws Exception {
        int fullMask = UdpWire.fullMask(count);
        int ackMask = 0;
        byte[] body = new byte[UdpWire.CHUNK];

        for (int attempt = 1; attempt <= UdpWire.MAX_RETRIES; attempt++) {
            check.check();
            int missing = fullMask & ~ackMask;

            for (int bit = 0; bit < count; bit++) {
                if ((missing & (1 << bit)) == 0) continue;
                int sequence = base + bit;
                int length = source.read((long) sequence * UdpWire.CHUNK, body,
                    UdpWire.chunkLength(size, sequence));
                channel.send(UdpWire.packet(UdpWire.DATA, transferId, sequence, total, body, length));
                pauseBetweenChunks();
            }

            long windowEnd = System.currentTimeMillis() + UdpWire.WINDOW_TIMEOUT;
            while (ackMask != fullMask && System.currentTimeMillis() < windowEnd) {
                UdpChannel.Incoming incoming = channel.receive(
                    (int) Math.max(20, windowEnd - System.currentTimeMillis()));
                if (incoming == null) break;

                UdpWire.Packet packet = UdpWire.parse(incoming.data, incoming.length);
                if (packet == null || !UdpWire.sameId(packet.transferId, transferId)) continue;

                if (packet.kind == UdpWire.ACK && packet.arg1 == base) {
                    ackMask |= packet.arg2 & fullMask;
                } else if (packet.kind == UdpWire.ERROR) {
                    throw new IOException(UdpWire.errorText(packet.body));
                }
            }

            if (ackMask == fullMask) return;

            Logs.add(Logs.WARNING, "分片组 " + base + "-" + (base + count - 1) + " 第 " + attempt + "/"
                + UdpWire.MAX_RETRIES + " 次重传，已确认 " + Integer.bitCount(ackMask) + "/" + count);
        }

        throw new IOException("分片组 " + base + "-" + (base + count - 1) + " 多次重传仍未全部确认");
    }

    private static void awaitDone(UdpChannel channel, byte[] transferId, ChunkSource source,
                                  long size, int total, Check check) throws Exception {
        for (int attempt = 1; attempt <= UdpWire.FIN_ATTEMPTS; attempt++) {
            check.check();
            channel.send(UdpWire.packet(UdpWire.FIN, transferId, total, 0, null, 0));

            Map<Integer, Integer> repair = new HashMap<>();
            boolean finished = false;
            long windowEnd = System.currentTimeMillis() + UdpWire.FIN_INTERVAL;

            while (System.currentTimeMillis() < windowEnd) {
                UdpChannel.Incoming incoming = channel.receive(
                    (int) Math.max(20, windowEnd - System.currentTimeMillis()));
                if (incoming == null) break;

                UdpWire.Packet packet = UdpWire.parse(incoming.data, incoming.length);
                if (packet == null || !UdpWire.sameId(packet.transferId, transferId)) continue;

                if (packet.kind == UdpWire.DONE) {
                    finished = true;
                    Logs.add(Logs.SUCCESS, "接收端 " + incoming.source.getHostAddress() + " 已完成接收与 SHA-256 校验");
                    break;
                }
                if (packet.kind == UdpWire.ACK) {
                    repair.put(packet.arg1, packet.arg2);
                } else if (packet.kind == UdpWire.ERROR) {
                    throw new IOException(UdpWire.errorText(packet.body));
                }
            }

            if (finished) return;

            for (Map.Entry<Integer, Integer> entry : repair.entrySet()) {
                int base = entry.getKey();
                int count = Math.min(UdpWire.WINDOW, total - base);
                int fullMask = UdpWire.fullMask(count);
                if (count <= 0 || (entry.getValue() & fullMask) == fullMask) continue;

                Logs.add(Logs.WARNING, "接收端报告分片组 " + base + " 有缺失，补发中");
                sendWindow(channel, transferId, source, size, total, base, count, check);
            }

            if (attempt % 5 == 0) Logs.add(Logs.WARNING, "等待完成确认（第 " + attempt + "/" + UdpWire.FIN_ATTEMPTS + " 次）");
        }

        throw new IOException("数据已发送完毕，但没有收到最终 DONE：接收端可能已保存文件，请检查对端");
    }

    /** 0.4 ms 的分片间隔（对应方案的 SEND_GAP），避免把 AP 的广播队列打爆。 */
    private static void pauseBetweenChunks() {
        try {
            Thread.sleep(0, 400000);
        } catch (InterruptedException interrupted) {
            Thread.currentThread().interrupt();
        }
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

    private static final class RandomAccessFileSource implements ChunkSource {
        private final RandomAccessFile handle;
        RandomAccessFileSource(RandomAccessFile handle) { this.handle = handle; }

        @Override public int read(long offset, byte[] buffer, int length) throws IOException {
            if (length <= 0) return 0;
            handle.seek(offset);
            handle.readFully(buffer, 0, length);
            return length;
        }

        @Override public void close() { }
    }

    private static final class MemorySource implements ChunkSource {
        private final byte[] data;
        MemorySource(byte[] data) { this.data = data; }

        @Override public int read(long offset, byte[] buffer, int length) {
            int start = (int) offset;
            int count = Math.min(length, Math.max(0, data.length - start));
            if (count > 0) System.arraycopy(data, start, buffer, 0, count);
            return count;
        }

        @Override public void close() { }
    }
}
