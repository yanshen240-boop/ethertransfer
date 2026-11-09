package cn.ethertransfer.mobile;

import android.net.Network;

import java.io.IOException;
import java.net.InetAddress;
import java.nio.charset.StandardCharsets;
import java.util.List;

/**
 * 双向广播连通性测试（对应方案里的 probe-send / probe-listen，端口 39001）。
 * <p>收到 PING 立刻广播 PONG。只有这一步通过，才说明 AP 在两个方向上都放行客户端之间的 UDP 广播，
 * 文件与文本才有必要尝试走广播通道。</p>
 */
final class UdpProbe implements Runnable {
    private static final String PING = "PING:";
    private static final String PONG = "PONG:";

    private final int port;
    private final Network network;
    private final List<InetAddress> targets;

    private volatile boolean running = true;
    private UdpChannel listener;
    private UdpChannel reply;
    private Thread thread;

    UdpProbe(int port, Network network, List<InetAddress> targets) {
        this.port = port;
        this.network = network;
        this.targets = targets;
    }

    void start() {
        try {
            listener = UdpChannel.listener(network, port, targets);
        } catch (IOException e) {
            Logs.add(Logs.WARNING, "广播测试监听端启动失败（" + port + "）：" + e.getMessage());
            return;
        }
        thread = new Thread(this, "ethertransfer-udp-probe");
        thread.setDaemon(true);
        thread.start();
        Logs.add(Logs.INFO, "广播测试监听端已启动：UDP " + port);
    }

    void stop() {
        running = false;
        if (listener != null) listener.close();
        if (reply != null) reply.close();
    }

    @Override public void run() {
        while (running) {
            try {
                UdpChannel.Incoming incoming = listener.receive(1000);
                if (incoming == null) continue;

                String text = new String(incoming.data, 0, incoming.length, StandardCharsets.UTF_8);
                if (!text.startsWith(PING)) continue;

                if (reply == null) reply = UdpChannel.sender(network, port, targets);
                byte[] payload = (PONG + text.substring(PING.length())).getBytes(StandardCharsets.UTF_8);
                reply.send(payload);
                Logs.add(Logs.INFO, "收到来自 " + incoming.source.getHostAddress() + " 的 PING，已广播 PONG");
            } catch (Exception e) {
                if (!running) return;
            }
        }
    }
}
