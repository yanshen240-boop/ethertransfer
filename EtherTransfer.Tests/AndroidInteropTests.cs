using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using EtherTransfer.Core.Models;
using EtherTransfer.Transfer;

namespace EtherTransfer.Tests;

public class AndroidInteropTests
{
    private static Process Peer(params string[] args)
    {
        var classes = Environment.GetEnvironmentVariable("ETHERTRANSFER_JAVA_TEST_CLASSES");
        if (string.IsNullOrEmpty(classes)) Assert.Ignore("Run EtherTransfer.Android/test.ps1 to enable Java/C# interoperability checks.");
        var info = new ProcessStartInfo("java") { RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in new[] { "-cp", classes!, "cn.ethertransfer.mobile.InteropPeer" }.Concat(args)) info.ArgumentList.Add(arg);
        return Process.Start(info)!;
    }
    private static async Task Verify(Process peer)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await peer.WaitForExitAsync(timeout.Token); }
        finally { if (!peer.HasExited) peer.Kill(true); }
        Assert.That(peer.ExitCode, Is.Zero, await peer.StandardError.ReadToEndAsync());
        Assert.That(await peer.StandardOutput.ReadToEndAsync(), Does.Contain("PASS"));
    }
    [TestCase(false)]
    [TestCase(true)]
    public async Task DesktopToAndroidTransfersFilesAndFolderStreams(bool folder)
    {
        var temp = Path.Combine(Path.GetTempPath(), "ether-interop-" + Guid.NewGuid()); Directory.CreateDirectory(temp);
        try
        {
            var bytes = Enumerable.Range(0, 65539).Select(i => (byte)(i % 251)).ToArray();
            var first = Path.Combine(temp, "中文.bin"); var empty = Path.Combine(temp, "empty.txt");
            await File.WriteAllBytesAsync(first, bytes); await File.WriteAllBytesAsync(empty, []);
            using var peer = Peer("receive");
            var port = int.Parse((await peer.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)))!);
            var session = new TransferSession { ContainsFolders = folder, PayloadFolderCount = folder ? 1 : 0, PayloadFileCount = folder ? 0 : 2 };
            var root = new DirectoryInfo(temp).Name;
            session.AddFiles(new[] { first, empty }.Select(f => new FileSelectionItem {
                AbsolutePath = f, RelativePath = (folder ? root + "/" : "") + Path.GetFileName(f),
                RootName = folder ? root : Path.GetFileName(f), Size = new FileInfo(f).Length }));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var result = await new TransferSender().TransmitSessionAsync("127.0.0.1", port, "电脑", session, timeout.Token);
            Assert.That(result.Success, Is.True, result.ErrorMessage);
            await Verify(peer);
        }
        finally { Directory.Delete(temp, true); }
    }
    [TestCase("send")]
    [TestCase("send-folder")]
    [TestCase("send-empty-folder")]
    public async Task AndroidToDesktopPreservesFilesAndFolders(string mode)
    {
        var temp = Path.Combine(Path.GetTempPath(), "ether-interop-" + Guid.NewGuid()); Directory.CreateDirectory(temp);
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var peer = Peer(mode, port.ToString());
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            var receiver = new TransferReceiver { OnIncomingTransfer = (_, _) => Task.FromResult((true, temp, CancellationToken.None)) };
            var result = await receiver.HandleClientAsync(client, timeout.Token);
            Assert.That(result.Success, Is.True, result.ErrorMessage);
            var root = mode == "send" ? temp : Path.Combine(temp, "中文文件夹");
            Assert.That(Directory.Exists(root), Is.True);
            if (mode != "send-empty-folder")
            {
                var file = mode == "send" ? Path.Combine(root, "中文.bin") : Path.Combine(root, "子目录", "中文.bin");
                Assert.That(await File.ReadAllBytesAsync(file), Is.EqualTo(Enumerable.Range(0, 65539).Select(i => (byte)(i % 251)).ToArray()));
                Assert.That(new FileInfo(Path.Combine(root, "empty.txt")).Length, Is.Zero);
            }
            else Assert.That(Directory.EnumerateFileSystemEntries(root), Is.Empty);
            await Verify(peer);
        }
        finally { Directory.Delete(temp, true); }
    }
}
