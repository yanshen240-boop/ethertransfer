using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using EtherTransfer.Core.Models;

namespace EtherTransfer.Network;

public class TcpServer : IDisposable
{
    private readonly int _port;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public int Port { get; private set; }

    public Action<TcpClient>? OnClientConnected { get; set; }

    public event EventHandler<StructuredLogMessage>? DebugLog;

    private void Log(string msg, LogLevel level = LogLevel.Info, string eventId = "tcpserver.log")
    {
        DebugLog?.Invoke(this, new StructuredLogMessage(eventId, msg, level));
    }

    public TcpServer(int port)
    {
        _port = port;
        Port = port;
    }

    public void Start()
    {
        Stop();

        _cts = new CancellationTokenSource();

        try
        {

            _listener = new TcpListener(IPAddress.Any, _port);

            _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

            _listener.Start();

            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Log($"已开始监听 0.0.0.0:{Port}");

            _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        }
        catch (Exception ex)
        {
            Log($"TCP 服务在端口 {_port} 启动失败：{ex.Message}", LogLevel.Error);
            throw;
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (_listener == null) break;

                var client = await _listener.AcceptTcpClientAsync(ct);

                var remoteEp = client.Client.RemoteEndPoint?.ToString();
                Log($"已接受来自 {remoteEp} 的连接");

                if (OnClientConnected != null)
                {
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            OnClientConnected(client);
                        }
                        catch (Exception ex)
                        {
                            Log($"处理 {remoteEp} 连接时出错：{ex.Message}");
                            try { client.Dispose(); } catch { }
                        }
                    });
                }
                else
                {

                    Log($"未注册处理程序，已断开来自 {remoteEp} 的连接。");
                    client.Dispose();
                }
            }
        }
        catch (OperationCanceledException)
        {
            Log("接受连接循环已取消。");
        }
        catch (SocketException ex)
        {
            Log($"接受连接循环发生套接字错误：{ex.Message}");
        }
        catch (ObjectDisposedException)
        {
            Log("监听器已释放。");
        }
        finally
        {
            _listener?.Stop();
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listener?.Stop();
        Log("Stopped.");
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            Stop();
            _cts?.Dispose();

            (_listener as IDisposable)?.Dispose();
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}

