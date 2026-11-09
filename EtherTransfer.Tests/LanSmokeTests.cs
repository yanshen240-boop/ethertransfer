using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EtherTransfer.Core.Models;
using EtherTransfer.Transfer;
using NUnit.Framework;

namespace EtherTransfer.Tests;

/// <summary>
/// 真机联调用例：默认不参与常规测试（Explicit），需要显式指定对端地址才会运行。
/// 用法：
///   ETHERTRANSFER_PEER_IP=10.160.3.19 ETHERTRANSFER_PEER_PORT=46463 \
///   dotnet test --filter FullyQualifiedName~LanSmokeTests -- NUnit.ExplicitMode=true
/// </summary>
[TestFixture]
public class LanSmokeTests
{
    private static string PeerIp => Environment.GetEnvironmentVariable("ETHERTRANSFER_PEER_IP") ?? "";
    private static int PeerPort => int.TryParse(Environment.GetEnvironmentVariable("ETHERTRANSFER_PEER_PORT"), out var p) ? p : 0;

    [Test]
    public async Task SendTextToRealPeer()
    {
        Assume.That(PeerIp, Is.Not.Empty, "未设置 ETHERTRANSFER_PEER_IP");
        Assume.That(PeerPort, Is.GreaterThan(0), "未设置 ETHERTRANSFER_PEER_PORT");

        var sender = new TransferSender();
        sender.DebugLog += (_, m) => TestContext.Out.WriteLine($"[{m.Level}] {m.Message}");

        var result = await sender.SendTextAsync(
            new List<PeerEndpoint> { new() { Address = PeerIp, Port = PeerPort, IsWired = false } },
            "联调主机",
            "联调测试：这条文本来自电脑端。",
            CancellationToken.None);

        TestContext.Out.WriteLine($"成功={result.Success} 链路={result.Endpoint?.LinkLabel} 用时={result.ElapsedMs}ms 错误={result.ErrorMessage}");
        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public async Task SendFileToRealPeer()
    {
        Assume.That(PeerIp, Is.Not.Empty, "未设置 ETHERTRANSFER_PEER_IP");
        Assume.That(PeerPort, Is.GreaterThan(0), "未设置 ETHERTRANSFER_PEER_PORT");

        var name = "smoke-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".txt";
        var temp = Path.Combine(Path.GetTempPath(), name);
        await File.WriteAllTextAsync(temp, "EtherTransfer 联调测试文件\n" + new string('A', 4000));

        var session = new TransferSession { PayloadFileCount = 1 };
        session.AddFiles(new[]
        {
            new FileSelectionItem
            {
                AbsolutePath = temp,
                RelativePath = name,
                RootName = name,
                Size = new FileInfo(temp).Length
            }
        });

        var sender = new TransferSender();
        sender.DebugLog += (_, m) => TestContext.Out.WriteLine($"[{m.Level}] {m.Message}");

        var result = await sender.TransmitSessionAsync(
            new List<PeerEndpoint> { new() { Address = PeerIp, Port = PeerPort, IsWired = false } },
            "联调主机",
            session,
            CancellationToken.None);

        TestContext.Out.WriteLine($"文件名={name} 成功={result.Success} 错误={result.ErrorMessage}");
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        TestContext.Out.WriteLine("PHONE_FILE=" + name);
    }

    [Test]
    public async Task SendFolderToRealPeer()
    {
        Assume.That(PeerIp, Is.Not.Empty, "未设置 ETHERTRANSFER_PEER_IP");
        Assume.That(PeerPort, Is.GreaterThan(0), "未设置 ETHERTRANSFER_PEER_PORT");

        var root = "smoke-dir-" + Guid.NewGuid().ToString("N").Substring(0, 6);
        var src = Path.Combine(Path.GetTempPath(), root);
        Directory.CreateDirectory(Path.Combine(src, "sub"));
        await File.WriteAllTextAsync(Path.Combine(src, "a.txt"), "根目录文件");
        await File.WriteAllTextAsync(Path.Combine(src, "sub", "b.txt"), "子目录文件");

        var session = new TransferSession
        {
            ContainsFolders = true,
            PayloadFolderCount = 1,
            PayloadFileCount = 0
        };

        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(Path.GetTempPath(), file).Replace('\\', '/');
            session.AddFiles(new[]
            {
                new FileSelectionItem
                {
                    AbsolutePath = file,
                    RelativePath = rel,
                    RootName = root,
                    Size = new FileInfo(file).Length
                }
            });
        }

        var sender = new TransferSender();
        sender.DebugLog += (_, m) => TestContext.Out.WriteLine($"[{m.Level}] {m.Message}");

        var result = await sender.TransmitSessionAsync(
            new List<PeerEndpoint> { new() { Address = PeerIp, Port = PeerPort, IsWired = false } },
            "联调主机",
            session,
            CancellationToken.None);

        TestContext.Out.WriteLine($"目录={root} 成功={result.Success} 错误={result.ErrorMessage}");
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        TestContext.Out.WriteLine("PHONE_DIR=" + root);
    }
}
