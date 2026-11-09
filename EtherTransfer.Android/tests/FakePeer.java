import java.io.*;
import java.net.*;
import java.nio.charset.StandardCharsets;
import java.util.UUID;

/**
 * 无界面测试对端：实现 EtherTransfer 的「发现 + 文本/文件接收」最小协议，
 * 用于没有桌面端 GUI 时验证手机侧的发现、发送、失败与补发。
 *
 * <pre>
 *   javac -d out FakePeer.java
 *   java -cp out FakePeer                 # 正常：UDP 发现 + TCP 接收
 *   java -cp out FakePeer --no-tcp        # 只广播、不监听：模拟「能发现但连不上」
 *   java -cp out FakePeer --offline       # 完全不回应（模拟设备下线）
 * </pre>
 */
public class FakePeer {
    private static final String ID = "fake-peer-1";
    private static final String NAME = "测试对端";
    private static final String SESSION = UUID.randomUUID().toString();
    private static final int DISCOVERY_PORT = 50000;

    public static void main(String[] args) throws Exception {
        boolean noTcp = false, offline = false, push = false;
        for (String a : args) {
            if ("--no-tcp".equals(a)) noTcp = true;
            if ("--offline".equals(a)) offline = true;
            if ("--push".equals(a)) push = true;
        }
        boolean pushMode = push;

        int tcpPort = 0;
        if (!noTcp) {
            ServerSocket server = new ServerSocket(0);
            tcpPort = server.getLocalPort();
            final int port = tcpPort;
            Thread acceptor = new Thread(() -> {
                while (true) {
                    try {
                        Socket socket = server.accept();
                        new Thread(() -> handle(socket)).start();
                    } catch (Exception e) {
                        System.out.println("[TCP] accept 结束: " + e);
                        return;
                    }
                }
            });
            acceptor.setDaemon(true);
            acceptor.start();
            System.out.println("[TCP] 监听 " + tcpPort);
        } else {
            // 仍上报一个端口，让手机把它登记为在线设备；但该端口没人监听 → 连接被拒
            tcpPort = 45123;
            System.out.println("[TCP] 未监听（模拟能发现但连不上，上报端口 " + tcpPort + "）");
        }

        if (offline) {
            System.out.println("[UDP] 离线模式：不回应任何发现请求");
            Thread.sleep(Long.MAX_VALUE);
        }

        DatagramSocket udp = new DatagramSocket(null);
        udp.setReuseAddress(true);
        udp.bind(new InetSocketAddress(DISCOVERY_PORT));
        udp.setSoTimeout(1000);
        System.out.println("[UDP] 监听 " + DISCOVERY_PORT + "，等待手机 HELLO…");

        byte[] buffer = new byte[65507];
        long last = 0;
        while (true) {
            try {
                DatagramPacket packet = new DatagramPacket(buffer, buffer.length);
                udp.receive(packet);
                String text = new String(packet.getData(), 0, packet.getLength(), StandardCharsets.UTF_8);
                if (!text.contains("EtherTransferApp-V1")) continue;
                if (!text.contains("\"HELLO\"")) continue;
                String reply = "{\"Id\":\"EtherTransferApp-V1\",\"Type\":\"HELLO_ACK\""
                    + ",\"SessionId\":\"" + SESSION + "\",\"DeviceId\":\"" + ID + "\""
                    + ",\"ComputerName\":\"" + NAME + "\",\"TcpPort\":" + tcpPort
                    + ",\"OS\":\"PC\",\"SequenceNumber\":1}";
                byte[] bytes = reply.getBytes(StandardCharsets.UTF_8);
                // 必须回到对方的发现端口（50000）：手机发 HELLO 用的是临时端口，早已关闭。
                udp.send(new DatagramPacket(bytes, bytes.length, packet.getAddress(), DISCOVERY_PORT));
                long now = System.currentTimeMillis();
                if (now - last > 5000) {
                    System.out.println("[UDP] 回应 HELLO → " + packet.getAddress().getHostAddress()
                        + ":" + packet.getPort() + "（TcpPort=" + tcpPort + "）");
                    last = now;
                }
                if (pushMode) {
                    // 反向测试：电脑主动给手机发一个文件（验证 P10 接收确认与落盘）
                    pushMode = false;
                    final String phoneIp = packet.getAddress().getHostAddress();
                    final int phonePort = advertisedPort(text);
                    System.out.println("[PUSH] 准备向 " + phoneIp + ":" + phonePort + " 发送测试文件");
                    new Thread(() -> pushFile(phoneIp, phonePort)).start();
                }
            } catch (SocketTimeoutException ignored) {
            } catch (Exception e) {
                System.out.println("[UDP] 异常: " + e);
            }
        }
    }

    static int advertisedPort(String json) {
        int at = json.indexOf("\"TcpPort\"");
        if (at < 0) return 0;
        int colon = json.indexOf(':', at);
        int end = colon + 1;
        while (end < json.length() && Character.isDigit(json.charAt(end))) end++;
        try { return Integer.parseInt(json.substring(colon + 1, end)); } catch (Exception e) { return 0; }
    }

    /** 主动推送一个文件到手机，等待对方的接收确认。 */
    static void pushFile(String host, int port) {
        if (port <= 0) { System.out.println("[PUSH] 手机没有上报端口，放弃"); return; }
        long started = System.currentTimeMillis();
        try (Socket socket = new Socket()) {
            socket.connect(new InetSocketAddress(host, port), 5000);
            socket.setSoTimeout(60000);
            InputStream in = socket.getInputStream();
            OutputStream out = socket.getOutputStream();
            String body = "hello from desktop\n";
            byte[] bytes = body.getBytes(StandardCharsets.UTF_8);
            send(out, "{\"Type\":\"TRANSFER_REQUEST\",\"SenderName\":\"测试对端\""
                + ",\"TotalFiles\":1,\"TotalSize\":" + bytes.length
                + ",\"ContainsFolders\":false,\"PayloadFolderCount\":0,\"PayloadFileCount\":1"
                + ",\"RootElementNames\":[\"push-test.txt\"]}");
            System.out.println("[PUSH] 已发出 TRANSFER_REQUEST，等待对方确认…");
            String response = message(in);
            long waited = System.currentTimeMillis() - started;
            System.out.println("[PUSH] 对方响应（" + waited + " ms）：" + response);
            if (!response.contains("\"Accepted\":true")) {
                System.out.println("[PUSH] 被拒绝或超时，结束");
                return;
            }
            send(out, "{\"Type\":\"FILE_BEGIN\"}");
            send(out, "{\"RelativePath\":\"push-test.txt\",\"RootName\":\"push-test.txt\""
                + ",\"Size\":" + bytes.length + "}");
            out.write(bytes);
            out.flush();
            send(out, "{\"Type\":\"TRANSFER_END\"}");
            System.out.println("[PUSH] 文件已发送完毕");
        } catch (Exception e) {
            System.out.println("[PUSH] 失败: " + e);
        }
    }

    // ---------------- 最小协议实现 ----------------

    static String message(InputStream in) throws IOException {
        long length = number(in, 4);
        if (length < 1 || length > 10 * 1024 * 1024) throw new IOException("长度无效: " + length);
        byte[] bytes = new byte[(int) length];
        int offset = 0;
        while (offset < length) {
            int n = in.read(bytes, offset, (int) length - offset);
            if (n < 0) throw new EOFException();
            offset += n;
        }
        return new String(bytes, StandardCharsets.UTF_8);
    }

    static long number(InputStream in, int bytes) throws IOException {
        long value = 0;
        for (int i = 0; i < bytes; i++) {
            int b = in.read();
            if (b < 0) throw new EOFException();
            value |= (long) b << (8 * i);
        }
        return value;
    }

    static void send(OutputStream out, String json) throws IOException {
        byte[] bytes = json.getBytes(StandardCharsets.UTF_8);
        number(out, bytes.length, 4);
        out.write(bytes);
        out.flush();
    }

    static void number(OutputStream out, long value, int bytes) throws IOException {
        for (int i = 0; i < bytes; i++) out.write((int) (value >>> (8 * i)) & 255);
    }

    static void handle(Socket socket) {
        String remote = socket.getInetAddress().getHostAddress();
        try (Socket s = socket) {
            s.setSoTimeout(30000);
            InputStream in = s.getInputStream();
            OutputStream out = s.getOutputStream();
            String first = message(in);
            System.out.println("[TCP] " + remote + " → " + abbreviate(first));

            if (first.contains("TEXT_MESSAGE")) {
                send(out, "{\"Type\":\"TEXT_ACK\",\"Accepted\":true,\"Reason\":\"\"}");
                System.out.println("[TCP] 已 ACK 文本消息");
                return;
            }
            if (!first.contains("TRANSFER_REQUEST")) {
                send(out, "{\"Type\":\"TRANSFER_RESPONSE\",\"Accepted\":false,\"Reason\":\"测试对端不支持该请求\"}");
                return;
            }
            send(out, "{\"Type\":\"TRANSFER_RESPONSE\",\"Accepted\":true,\"Reason\":\"\"}");
            int files = 0;
            long bytes = 0;
            while (true) {
                String marker = message(in);
                if (marker.contains("TRANSFER_END")) break;
                if (marker.contains("FILE_BEGIN")) {
                    String meta = message(in);
                    long size = size(meta);
                    long read = skip(in, size);
                    bytes += read;
                    files++;
                    System.out.println("[TCP] 收到文件 " + name(meta) + " (" + size + " 字节)");
                } else if (marker.contains("FOLDER_BEGIN")) {
                    message(in);
                    while (true) {
                        long pathLength = number(in, 4);
                        if (pathLength == 0) break;
                        byte[] path = new byte[(int) pathLength];
                        readFully(in, path);
                        long size = number(in, 8);
                        number(in, 8);
                        number(in, 8);
                        bytes += skip(in, size);
                        files++;
                    }
                } else {
                    System.out.println("[TCP] 未知标记: " + marker);
                    break;
                }
            }
            System.out.println("[TCP] 传输完成：" + files + " 个文件 / " + bytes + " 字节");
        } catch (Exception e) {
            System.out.println("[TCP] 连接异常: " + e);
        }
    }

    static long skip(InputStream in, long size) throws IOException {
        byte[] buffer = new byte[64 * 1024];
        long done = 0;
        while (done < size) {
            int n = in.read(buffer, 0, (int) Math.min(buffer.length, size - done));
            if (n < 0) throw new EOFException("文件未传完");
            done += n;
        }
        return done;
    }

    static void readFully(InputStream in, byte[] target) throws IOException {
        int offset = 0;
        while (offset < target.length) {
            int n = in.read(target, offset, target.length - offset);
            if (n < 0) throw new EOFException();
            offset += n;
        }
    }

    static long size(String json) {
        int at = json.indexOf("\"Size\"");
        if (at < 0) return 0;
        int colon = json.indexOf(':', at);
        int end = colon + 1;
        while (end < json.length() && (Character.isDigit(json.charAt(end)) || json.charAt(end) == ' ')) end++;
        try { return Long.parseLong(json.substring(colon + 1, end).trim()); }
        catch (Exception e) { return 0; }
    }

    static String name(String json) {
        int at = json.indexOf("\"RootName\"");
        if (at < 0) return "?";
        int start = json.indexOf('"', json.indexOf(':', at)) + 1;
        int end = json.indexOf('"', start);
        return start > 0 && end > start ? json.substring(start, end) : "?";
    }

    static String abbreviate(String text) {
        return text.length() <= 90 ? text : text.substring(0, 90) + "…";
    }
}
