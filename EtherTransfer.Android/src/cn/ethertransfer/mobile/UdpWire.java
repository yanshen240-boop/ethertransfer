package cn.ethertransfer.mobile;

import org.json.JSONObject;

import java.io.IOException;
import java.nio.ByteBuffer;
import java.nio.charset.StandardCharsets;
import java.util.UUID;

/**
 * AP 隔离模式：UFT1 报文编解码，与桌面端及方案脚本完全一致。
 * <p>4 字节魔数 "UFT1" + 1 字节类型 + 16 字节传输 ID + 两个大端 int + 正文（29 字节头）。
 * 文件按 900 字节分片，32 个分片一个发送窗口，ACK 是 32 位累积位图。</p>
 */
final class UdpWire {
    static final byte START = 1, READY = 2, DATA = 3, ACK = 4, FIN = 5, DONE = 6, ERROR = 7;

    static final int TRANSFER_PORT = 39000;
    static final int PROBE_PORT = 39001;

    /** 分片大小：远低于常见 MTU，避免广播数据报被分片后再丢。 */
    static final int CHUNK = 900;
    /** 发送窗口大小，等于 ACK 位图宽度。 */
    static final int WINDOW = 32;
    /** 每收到 8 个新分片回一次 ACK。 */
    static final int ACK_EVERY = 8;
    static final int MAX_RETRIES = 20;
    static final int WINDOW_TIMEOUT = 450;

    /** START 重发间隔与握手总时长（与界面 120 秒接收确认超时对齐）。 */
    static final int START_INTERVAL = 600;
    static final int START_SECONDS = 120;
    /** FIN 重发间隔与次数。 */
    static final int FIN_INTERVAL = 800;
    static final int FIN_ATTEMPTS = 20;

    static final int HEADER = 29;
    static final int MAX_ERROR_BYTES = 800;
    static final long MAX_FILE = 20L * 1024 * 1024 * 1024;

    private static final byte[] MAGIC = {'U', 'F', 'T', '1'};

    static final class Packet {
        byte kind;
        byte[] transferId;
        int arg1;
        int arg2;
        byte[] body;
        int bodyLength;
    }

    private UdpWire() { }

    static byte[] transferId() {
        UUID uuid = UUID.randomUUID();
        return ByteBuffer.allocate(16)
            .putLong(uuid.getMostSignificantBits())
            .putLong(uuid.getLeastSignificantBits())
            .array();
    }

    static boolean sameId(byte[] left, byte[] right) {
        if (left == null || right == null || left.length != right.length) return false;
        for (int i = 0; i < left.length; i++) if (left[i] != right[i]) return false;
        return true;
    }

    static byte[] packet(byte kind, byte[] transferId, int arg1, int arg2, byte[] body, int bodyLength) {
        byte[] out = new byte[HEADER + Math.max(0, bodyLength)];
        System.arraycopy(MAGIC, 0, out, 0, 4);
        out[4] = kind;
        System.arraycopy(transferId, 0, out, 5, 16);
        putInt(out, 21, arg1);
        putInt(out, 25, arg2);
        if (bodyLength > 0) System.arraycopy(body, 0, out, HEADER, bodyLength);
        return out;
    }

    static byte[] start(byte[] transferId, JSONObject metadata) {
        byte[] body = metadata.toString().getBytes(StandardCharsets.UTF_8);
        return packet(START, transferId, 0, 0, body, body.length);
    }

    static byte[] error(byte[] transferId, String message) {
        byte[] body = message == null ? new byte[0] : message.getBytes(StandardCharsets.UTF_8);
        int length = Math.min(body.length, MAX_ERROR_BYTES);
        return packet(ERROR, transferId, 0, 0, body, length);
    }

    /** 解析失败返回 null（可能收到的是同网段其它程序的广播）。 */
    static Packet parse(byte[] data, int length) {
        if (data == null || length < HEADER) return null;
        for (int i = 0; i < 4; i++) if (data[i] != MAGIC[i]) return null;

        Packet packet = new Packet();
        packet.kind = data[4];
        packet.transferId = new byte[16];
        System.arraycopy(data, 5, packet.transferId, 0, 16);
        packet.arg1 = getInt(data, 21);
        packet.arg2 = getInt(data, 25);
        packet.bodyLength = length - HEADER;
        packet.body = new byte[packet.bodyLength];
        System.arraycopy(data, HEADER, packet.body, 0, packet.bodyLength);
        return packet;
    }

    static String errorText(byte[] body) {
        return body == null || body.length == 0 ? "对端报告未知错误" : new String(body, StandardCharsets.UTF_8);
    }

    static int totalChunks(long size) {
        return size <= 0 ? 0 : (int) ((size + CHUNK - 1) / CHUNK);
    }

    static int chunkLength(long size, int sequence) {
        long offset = (long) sequence * CHUNK;
        long remaining = size - offset;
        if (remaining <= 0) return 0;
        return (int) Math.min(CHUNK, remaining);
    }

    static int fullMask(int count) {
        return count >= WINDOW ? -1 : (1 << count) - 1;
    }

    static void putInt(byte[] target, int offset, int value) {
        target[offset] = (byte) (value >>> 24);
        target[offset + 1] = (byte) (value >>> 16);
        target[offset + 2] = (byte) (value >>> 8);
        target[offset + 3] = (byte) value;
    }

    static int getInt(byte[] source, int offset) {
        return ((source[offset] & 255) << 24)
            | ((source[offset + 1] & 255) << 16)
            | ((source[offset + 2] & 255) << 8)
            | (source[offset + 3] & 255);
    }

    static String hex(byte[] value) {
        StringBuilder builder = new StringBuilder(value.length * 2);
        for (byte b : value) {
            int v = b & 255;
            if (v < 16) builder.append('0');
            builder.append(Integer.toHexString(v));
        }
        return builder.toString();
    }

    /** 校验 START 元数据，规则与方案一致。 */
    static int validate(JSONObject meta) throws IOException {
        long size = meta.optLong("size", -1);
        int chunk = meta.optInt("chunk_size", -1);
        int total = meta.optInt("total", -1);
        String sha = meta.optString("sha256", "");

        if (size < 0 || size > MAX_FILE) throw new IOException("START 参数错误：文件大小超出允许范围");
        if (chunk != CHUNK) throw new IOException("START 参数错误：分片参数不匹配");
        if (total != totalChunks(size)) throw new IOException("START 参数错误：分片总数不正确");
        if (sha.length() != 64) throw new IOException("START 参数错误：SHA-256 信息无效");

        boolean text = "text".equalsIgnoreCase(meta.optString("kind", "file"));
        if (text && size > 20000L * 4) throw new IOException("START 参数错误：文本消息过长");
        if (!text && meta.optString("name", "").isEmpty() && meta.optString("path", "").isEmpty())
            throw new IOException("START 参数错误：缺少文件名");
        return total;
    }
}
