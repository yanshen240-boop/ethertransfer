using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using EtherTransfer.Core.Models;
using EtherTransfer.Transfer;
using NUnit.Framework;

namespace EtherTransfer.Tests;

/// <summary>
/// 文本消息与文件传输共用同一条 TCP 通道，靠第一条报文的 Type 分流。
/// 这里验证分流正确、回执正确，避免以后改动把“发文本”误判成“传输请求”。
/// </summary>
[TestFixture]
public class TextMessageTests
{
    private static (TcpListener listener, int port) StartTestListener()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return (listener, port);
    }

    [Test]
    public async Task TextMessage_IsDeliveredAndAcknowledged()
    {
        var (listener, port) = StartTestListener();
        string? received = null;

        var receiver = new TransferReceiver
        {
            OnIncomingText = (msg, ct) =>
            {
                received = msg.Text;
                return Task.FromResult((true, string.Empty));
            }
        };

        var receiverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            return await receiver.HandleClientAsync(client, CancellationToken.None);
        });

        var sender = new TransferSender();
        try
        {
            var result = await sender.SendTextAsync(
                new List<PeerEndpoint> { new() { Address = "127.0.0.1", Port = port } },
                "测试主机",
                "这是一条测试文本",
                CancellationToken.None);

            Assert.That(result.Success, Is.True, result.ErrorMessage);
            Assert.That(received, Is.EqualTo("这是一条测试文本"));

            var receiverResult = await receiverTask;
            Assert.That(receiverResult.Success, Is.True);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Test]
    public async Task TextMessage_RejectedByReceiver_ReturnsReason()
    {
        var (listener, port) = StartTestListener();

        var receiver = new TransferReceiver
        {
            OnIncomingText = (msg, ct) => Task.FromResult((false, "接收方忙，稍后再发。"))
        };

        var receiverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            return await receiver.HandleClientAsync(client, CancellationToken.None);
        });

        var sender = new TransferSender();
        try
        {
            var result = await sender.SendTextAsync(
                new List<PeerEndpoint> { new() { Address = "127.0.0.1", Port = port } },
                "测试主机",
                "被拒绝的文本",
                CancellationToken.None);

            Assert.That(result.Success, Is.False);
            Assert.That(result.ErrorMessage, Does.Contain("接收方忙"));

            await receiverTask;
        }
        finally
        {
            listener.Stop();
        }
    }

    [Test]
    public async Task FileTransferRequest_IsStillRoutedToTransferHandler()
    {
        var (listener, port) = StartTestListener();
        bool transferSeen = false;
        bool textSeen = false;

        var receiver = new TransferReceiver
        {
            OnIncomingText = (msg, ct) =>
            {
                textSeen = true;
                return Task.FromResult((true, string.Empty));
            },
            OnIncomingTransfer = (req, ct) =>
            {
                transferSeen = true;
                return Task.FromResult((false, string.Empty, CancellationToken.None));
            }
        };

        var receiverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            return await receiver.HandleClientAsync(client, CancellationToken.None);
        });

        using var sender = new TcpClient();
        try
        {
            await sender.ConnectAsync(IPAddress.Loopback, port);
            var stream = sender.GetStream();
            await ProtocolHelper.SendMessageAsync(stream, new TransferRequestMessage
            {
                SenderName = "测试主机",
                TotalFiles = 1,
                TotalSize = 1024,
                PayloadFileCount = 1,
                RootElementNames = new List<string> { "a.txt" }
            }, CancellationToken.None);

            var response = await ProtocolHelper.ReceiveMessageAsync<TransferResponseMessage>(stream, CancellationToken.None);

            Assert.That(response, Is.Not.Null);
            Assert.That(response!.Accepted, Is.False);
            Assert.That(transferSeen, Is.True);
            Assert.That(textSeen, Is.False);

            await receiverTask;
        }
        finally
        {
            listener.Stop();
        }
    }
}
