package cn.ethertransfer.mobile;

import android.net.Network;

import java.io.Closeable;
import java.io.IOException;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.SocketTimeoutException;
import java.util.ArrayList;
import java.util.List;

/**
 * AP 隔离模式的广播收发通道。
 * <p>与 TCP 不同，这里不建立任何连接：报文固定发往子网定向广播地址（外加受限广播 255.255.255.255），
 * 套接字必须先绑定到 Wi-Fi {@link Network}，否则 Android 可能从蜂窝网络发出。</p>
 */
final class UdpChannel implements Closeable {
    private static final int BUFFER_SIZE = 65535;

    /** 收到的一份数据报；data 是复用的缓冲区，调用方必须在下次 receive 前用完。 */
    static final class Incoming {
        final byte[] data;
        final int length;
        final InetAddress source;
        Incoming(byte[] data, int length, InetAddress source) {
            this.data = data;
            this.length = length;
            this.source = source;
        }
    }

    private final DatagramSocket socket;
    private final List<InetAddress> targets;
    private final int port;
    private final byte[] buffer = new byte[BUFFER_SIZE];

    private UdpChannel(DatagramSocket socket, List<InetAddress> targets, int port) {
        this.socket = socket;
        this.targets = targets;
        this.port = port;
    }

    /** 接收端：绑定 0.0.0.0:port，接收任意网卡进来的广播。 */
    static UdpChannel listener(Network network, int port, List<InetAddress> targets) throws IOException {
        DatagramSocket socket = new DatagramSocket(null);
        socket.setReuseAddress(true);
        socket.setBroadcast(true);
        try { socket.setReceiveBufferSize(4 * 1024 * 1024); } catch (Exception ignored) { }
        network.bindSocket(socket);
        socket.bind(new InetSocketAddress(port));
        return new UdpChannel(socket, new ArrayList<>(targets), port);
    }

    /** 发送端：绑定到 Wi-Fi 网络后向广播地址发送。 */
    static UdpChannel sender(Network network, int port, List<InetAddress> targets) throws IOException {
        DatagramSocket socket = new DatagramSocket(null);
        socket.setReuseAddress(true);
        socket.setBroadcast(true);
        try { socket.setSendBufferSize(1024 * 1024); } catch (Exception ignored) { }
        network.bindSocket(socket);
        socket.bind(new InetSocketAddress(0));
        return new UdpChannel(socket, new ArrayList<>(targets), port);
    }

    boolean hasTargets() {
        return !targets.isEmpty();
    }

    String targetText() {
        if (targets.isEmpty()) return "未找到广播地址";
        StringBuilder builder = new StringBuilder();
        for (InetAddress target : targets) {
            if (builder.length() > 0) builder.append('、');
            builder.append(target.getHostAddress());
        }
        return builder.toString();
    }

    /** 广播一份报文：定向广播逐个发，再补一发受限广播。 */
    void send(byte[] payload) throws IOException {
        IOException failure = null;
        boolean sent = false;
        for (InetAddress target : targets) {
            try {
                socket.send(new DatagramPacket(payload, payload.length, target, port));
                sent = true;
            } catch (IOException e) {
                failure = e;
            }
        }
        try {
            socket.send(new DatagramPacket(payload, payload.length,
                InetAddress.getByName("255.255.255.255"), port));
            sent = true;
        } catch (IOException e) {
            failure = e;
        }
        if (!sent && failure != null) throw failure;
    }

    /** 带超时的接收；超时返回 null。 */
    Incoming receive(int timeoutMs) throws IOException {
        socket.setSoTimeout(Math.max(1, timeoutMs));
        DatagramPacket packet = new DatagramPacket(buffer, buffer.length);
        try {
            socket.receive(packet);
        } catch (SocketTimeoutException timeout) {
            return null;
        }
        return new Incoming(packet.getData(), packet.getLength(), packet.getAddress());
    }

    @Override public void close() {
        try { socket.close(); } catch (Exception ignored) { }
    }
}
