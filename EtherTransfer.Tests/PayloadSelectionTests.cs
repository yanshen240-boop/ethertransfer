using EtherTransfer.Core.Models;
using EtherTransfer.Transfer;

namespace EtherTransfer.Tests;

public class PayloadSelectionTests
{
    [Test]
    public async Task MixedLocalPaths_ScanFilesAndFoldersWithoutChangingSources()
    {
        var root = Path.Combine(Path.GetTempPath(), "EtherTransfer_Drop_" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "中文 文件.txt");
            var folder = Path.Combine(root, "资料夹");
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(file, "abc");
            var child = Path.Combine(folder, "内容.txt");
            await File.WriteAllTextAsync(child, "12345");
            var sender = new TransferSender();
            var payloads = await Task.WhenAll(new[] { file, folder }.Select(p => sender.ScanItemAsync(p)));

            Assert.Multiple(() =>
            {
                Assert.That(payloads.Select(p => p.Type), Is.EqualTo(new[] { PayloadItemType.File, PayloadItemType.Folder }));
                Assert.That(payloads.Select(p => p.FileCount), Is.EqualTo(new[] { 1, 1 }));
                Assert.That(payloads.Sum(p => p.TotalSize), Is.EqualTo(8));
                Assert.That(File.ReadAllText(file), Is.EqualTo("abc"));
                Assert.That(File.ReadAllText(child), Is.EqualTo("12345"));
            });
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
