using System;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using EtherTransfer.Core.Models;

namespace EtherTransfer.Core;

/// <summary>
/// 轻量落盘日志：把调试窗口里的结构化日志同时写入
/// %APPDATA%\EtherTransfer\app.log，便于事后排查（之前日志只在内存里，关窗即失）。
/// 写入走后台单线程 + 有界队列，不占用界面线程，队列满时丢弃新日志而不是阻塞。
/// </summary>
public static class AppLog
{
    private const long MaxBytes = 2 * 1024 * 1024;

    private static readonly object PathLock = new();
    private static string? _filePath;

    private static readonly Channel<string> Queue = Channel.CreateBounded<string>(
        new BoundedChannelOptions(4000)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite
        });

    private static int _started;

    public static string FilePath
    {
        get
        {
            lock (PathLock)
            {
                return _filePath ??= Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "EtherTransfer",
                    "app.log");
            }
        }
    }

    public static void Write(LogLevel level, string eventId, string message)
    {
        try
        {
            var flat = (message ?? string.Empty).Replace("\r", " ").Replace("\n", " ");
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level,-7}] {eventId} {flat}";
            Queue.Writer.TryWrite(line);

            if (Interlocked.Exchange(ref _started, 1) == 0)
                _ = Task.Run(DrainAsync);
        }
        catch
        {
        }
    }

    private static async Task DrainAsync()
    {
        StreamWriter? writer = null;
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            RotateIfTooLarge();

            var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            writer = new StreamWriter(stream) { AutoFlush = true };

            await foreach (var line in Queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                await writer.WriteLineAsync(line).ConfigureAwait(false);
            }
        }
        catch
        {
        }
        finally
        {
            try { writer?.Dispose(); } catch { }
        }
    }

    private static void RotateIfTooLarge()
    {
        try
        {
            var info = new FileInfo(FilePath);
            if (!info.Exists || info.Length < MaxBytes) return;

            var backup = Path.Combine(info.DirectoryName ?? string.Empty, "app.old.log");
            if (File.Exists(backup)) File.Delete(backup);
            File.Move(FilePath, backup);
        }
        catch
        {
        }
    }
}
