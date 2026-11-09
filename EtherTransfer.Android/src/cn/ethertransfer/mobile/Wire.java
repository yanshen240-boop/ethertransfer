package cn.ethertransfer.mobile;

import java.io.*;
import java.nio.charset.StandardCharsets;

/** EtherTransfer V1: little-endian lengths, UTF-8 JSON, then exact-size raw file bytes. */
public final class Wire {
    public static long number(InputStream in, int bytes) throws IOException {
        long value = 0;
        for (int i = 0; i < bytes; i++) {
            int b = in.read();
            if (b < 0) throw new EOFException("连接提前结束");
            value |= (long)b << (8 * i);
        }
        return value;
    }
    public static void number(OutputStream out, long value, int bytes) throws IOException {
        for (int i = 0; i < bytes; i++) out.write((int)(value >>> (8 * i)) & 255);
    }
    public static String text(InputStream in, int length) throws IOException {
        if (length < 1 || length > 10 * 1024 * 1024) throw new IOException("元数据长度无效");
        byte[] bytes = new byte[length];
        int offset = 0;
        while (offset < length) {
            int count = in.read(bytes, offset, length - offset);
            if (count < 0) throw new EOFException("元数据不完整");
            offset += count;
        }
        return new String(bytes, StandardCharsets.UTF_8);
    }
    public static String message(InputStream in) throws IOException {
        return text(in, (int)number(in, 4));
    }
    public static void message(OutputStream out, String json) throws IOException {
        byte[] bytes = json.getBytes(StandardCharsets.UTF_8);
        number(out, bytes.length, 4);
        out.write(bytes);
        out.flush();
    }
    public static String[] safePath(String path) throws IOException {
        if (path == null || path.isEmpty() || path.startsWith("/") || path.contains("\\"))
            throw new IOException("不安全的文件路径");
        String[] parts = path.split("/", -1);
        for (String part : parts)
            if (part.isEmpty() || part.equals(".") || part.equals("..") || part.contains(":") ||
                part.indexOf(0) >= 0 || part.length() > 255)
                throw new IOException("不安全的文件路径");
        return parts;
    }
    public static void folderEntry(OutputStream out,String path,long size) throws IOException {
        safePath(path); byte[] bytes=path.getBytes(StandardCharsets.UTF_8);
        if(bytes.length>4096 || size<0)throw new IOException("文件夹条目无效");
        number(out,bytes.length,4);out.write(bytes);number(out,size,8);
        number(out,-1,8);number(out,-1,8);
    }
    public interface FileConsumer { void receive(String path, long size) throws IOException; }
    public static int folder(InputStream in, FileConsumer consumer) throws IOException {
        int count = 0;
        while (true) {
            int length = (int)number(in, 4);
            if (length == 0) return count;
            if (length < 0 || length > 4096) throw new IOException("文件路径长度无效");
            String path = text(in, length); safePath(path);
            long size = number(in, 8);
            if (size < 0) throw new IOException("文件大小无效");
            number(in, 8); number(in, 8);
            if (++count > 1000000) throw new IOException("文件数量过多");
            consumer.receive(path, size);
        }
    }
    public static void copy(InputStream in, OutputStream out, long size) throws IOException {
        if (size < 0) throw new IOException("文件大小无效");
        byte[] buffer = new byte[128 * 1024];
        while (size > 0) {
            int count = in.read(buffer, 0, (int)Math.min(buffer.length, size));
            if (count < 0) throw new EOFException("文件不完整");
            out.write(buffer, 0, count);
            size -= count;
        }
    }
}
