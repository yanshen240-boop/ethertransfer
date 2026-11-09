using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using EtherTransfer.Core.Models;

namespace EtherTransfer.Transfer;

public class TransferProgressEventArgs : EventArgs
{
    public string CurrentFile { get; set; } = string.Empty;
    public long BytesSent { get; set; }
    public long TotalBytes { get; set; }
    public double SpeedMbPerSec { get; set; }
    public int CurrentElementIndex { get; set; }
    public int TotalElements { get; set; }
}

public class TextSendResult
{
    public bool Success { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public PeerEndpoint? Endpoint { get; set; }
    public long ElapsedMs { get; set; }
}

/// <summary>链路互通探测结果（不承载任何用户数据，对端自动应答且不留会话记录）。</summary>
public class ProbeOutcome
{
    public bool Ok { get; set; }
    public string Reason { get; set; } = string.Empty;
    public long ElapsedMs { get; set; }
}

public class TransferSender
{
    public event EventHandler<TransferProgressEventArgs>? ProgressUpdated;
    public event EventHandler<StructuredLogMessage>? DebugLog;

    private void Log(string msg, LogLevel level = LogLevel.Info, string eventId = "sender.log") =>
        DebugLog?.Invoke(this, new StructuredLogMessage(eventId, msg, level));

    /// <summary>
    /// 发送一条纯文本消息。走与文件传输完全相同的链路选择（有线优先、失败自动换链路），
    /// 但只发送一条请求并等待一条确认，适合很小、很快的交互。
    /// </summary>
    public async Task<TextSendResult> SendTextAsync(
        IReadOnlyList<PeerEndpoint> endpoints,
        string senderName,
        string text,
        CancellationToken ct,
        bool isProbe = false)
    {
        var result = new TextSendResult();
        var candidates = endpoints.Where(e => !string.IsNullOrWhiteSpace(e.Address)).ToList();

        if (candidates.Count == 0)
        {
            result.ErrorMessage = "没有可用的链路端点。";
            return result;
        }

        if (string.IsNullOrEmpty(text))
        {
            result.ErrorMessage = "文本内容为空。";
            return result;
        }

        Log($"准备发送文本，候选链路：{string.Join("、", candidates.Select(c => c.ToString()))}");

        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var connect = await EtherTransfer.Network.Routing.LinkRouter.ConnectAsync(candidates, ct, m => Log(m)).ConfigureAwait(false);
            using var client = connect.Client;
            result.Endpoint = connect.Endpoint;

            var stream = client.GetStream();
            client.NoDelay = true;

            var message = new TextMessageRequest
            {
                SenderName = senderName,
                Text = text,
                SentAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                MessageId = Guid.NewGuid().ToString("N"),
                IsProbe = isProbe
            };

            await ProtocolHelper.SendMessageAsync(stream, message, ct, 5000).ConfigureAwait(false);

            var ack = await ProtocolHelper.ReceiveMessageAsync<TextMessageAck>(stream, ct, 15000).ConfigureAwait(false);
            watch.Stop();

            if (ack == null)
            {
                result.ErrorMessage = $"对端在确认前关闭了连接（链路 {connect.Endpoint.LinkLabel} {connect.Endpoint.Address}）。";
                return result;
            }

            if (!ack.Accepted)
            {
                result.ErrorMessage = string.IsNullOrWhiteSpace(ack.Reason) ? "对端未接收这条文本。" : ack.Reason;
                return result;
            }

            result.Success = true;
            result.ElapsedMs = watch.ElapsedMilliseconds;
            Log($"文本已送达（{text.Length} 字），链路 {connect.Endpoint.LinkLabel}，往返 {watch.ElapsedMilliseconds} ms。");
            return result;
        }
        catch (Exception ex)
        {
            watch.Stop();
            result.Success = false;
            result.ErrorMessage = ex is OperationCanceledException ? "已取消。" : ex.Message;
            Log($"发送文本失败：{result.ErrorMessage}");
            return result;
        }
    }

    /// <summary>
    /// 单端点互通探测：一次建连 + 一条 IsProbe 文本 + 等一个回执。
    /// 与正式发送的区别是「不做多轮重试、不接管广播通道」，所以又便宜又不会打扰对端；
    /// 对端若回“忙”，说明连接已经建立、链路是通的，同样算通。
    /// </summary>
    public async Task<ProbeOutcome> ProbeAsync(
        PeerEndpoint endpoint,
        string senderName,
        int connectTimeoutMs,
        int ackTimeoutMs,
        bool wakePeer,
        CancellationToken ct)
    {
        var outcome = new ProbeOutcome();
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var connect = await EtherTransfer.Network.Routing.LinkRouter
            .TryConnectOnceAsync(endpoint, connectTimeoutMs, wakePeer, m => Log(m))
            .ConfigureAwait(false);

        if (connect.Client == null)
        {
            watch.Stop();
            outcome.Ok = false;
            outcome.Reason = connect.Error;
            outcome.ElapsedMs = watch.ElapsedMilliseconds;
            return outcome;
        }

        try
        {
            using var client = connect.Client;
            var stream = client.GetStream();
            client.NoDelay = true;

            var message = new TextMessageRequest
            {
                SenderName = senderName,
                Text = "__LINK_PROBE__",
                SentAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                MessageId = Guid.NewGuid().ToString("N"),
                IsProbe = true
            };

            await ProtocolHelper.SendMessageAsync(stream, message, ct, ackTimeoutMs).ConfigureAwait(false);
            var ack = await ProtocolHelper.ReceiveMessageAsync<TextMessageAck>(stream, ct, ackTimeoutMs).ConfigureAwait(false);
            watch.Stop();

            if (ack == null)
            {
                outcome.Ok = false;
                outcome.Reason = "对端建连后未回执（可能刚开始处理另一项传输）。";
            }
            else
            {
                // 只要收到回执就算“能传”：回执本身证明报文已经往返成功。
                // 对端回“正在处理另一项传输”这类拒绝，是忙而不是断，链路照样是通的。
                outcome.Ok = true;
                outcome.Reason = ack.Accepted
                    ? "互通正常"
                    : $"对端忙，但链路可应答{(string.IsNullOrWhiteSpace(ack.Reason) ? "" : "：" + ack.Reason)}";
            }

            outcome.ElapsedMs = watch.ElapsedMilliseconds;
            Log(outcome.Ok
                ? $"探测通过：{endpoint.LinkLabel} {endpoint.Address}:{endpoint.Port}，往返 {outcome.ElapsedMs} ms"
                : $"探测未通过：{endpoint.LinkLabel} {endpoint.Address}:{endpoint.Port} → {outcome.Reason}");
            return outcome;
        }
        catch (Exception ex)
        {
            watch.Stop();
            outcome.Ok = false;
            outcome.Reason = ex is OperationCanceledException ? "探测超时" : ex.Message;
            outcome.ElapsedMs = watch.ElapsedMilliseconds;
            Log($"探测异常：{endpoint.LinkLabel} {endpoint.Address}:{endpoint.Port} → {outcome.Reason}", LogLevel.Warning);
            return outcome;
        }
    }

    public Task<PayloadItem> ScanItemAsync(string path, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var payload = new PayloadItem
            {
                Path = path
            };

            try
            {
                if (File.Exists(path))
                {
                    var fi = new FileInfo(path);
                    payload.Name = fi.Name;
                    payload.Type = PayloadItemType.File;
                    payload.DeepScannedFiles.Add(new FileSelectionItem
                    {
                        AbsolutePath = path,
                        RelativePath = fi.Name,
                        RootName = fi.Name,
                        Size = fi.Length
                    });
                }
                else if (Directory.Exists(path))
                {
                    var baseDir = new DirectoryInfo(path);
                    payload.Name = baseDir.Name;
                    payload.Type = PayloadItemType.Folder;

                    var parentDir = baseDir.Parent?.FullName ?? baseDir.FullName;
                    var options = new EnumerationOptions
                    {
                        IgnoreInaccessible = true,
                        RecurseSubdirectories = true,
                        ReturnSpecialDirectories = false
                    };

                    int count = 0;
                    foreach (var file in Directory.EnumerateFiles(path, "*", options))
                    {
                        ct.ThrowIfCancellationRequested();
                        var fi = new FileInfo(file);
                        payload.DeepScannedFiles.Add(new FileSelectionItem
                        {
                            AbsolutePath = file,
                            RelativePath = Path.GetRelativePath(parentDir, file).Replace('\\', '/'),
                            RootName = baseDir.Name,
                            Size = fi.Length
                        });
                        count++;
                        if (count % 100 == 0) progress?.Report(count);
                    }
                    progress?.Report(count);
                }
            }
            catch (OperationCanceledException)
            {
                payload.DeepScannedFiles.Clear();
                throw;
            }
            catch (Exception ex)
            {
                Log($"扫描 {path} 时出错：{ex.Message}");
            }

            Log($"已扫描 {payload.Name} -> {payload.DeepScannedFiles.Count} 个文件，{payload.TotalSize / 1024 / 1024} MB");
            return payload;
        }, ct);
    }

    /// <summary>兼容旧调用：只给一个目标地址，不带链路信息。</summary>
    public Task<TransferResult> TransmitSessionAsync(
        string targetIp,
        int targetPort,
        string senderName,
        TransferSession session,
        CancellationToken ct)
    {
        return TransmitSessionCoreAsync(
            new[] { new PeerEndpoint { Address = targetIp, Port = targetPort } },
            senderName, session, ct);
    }

    /// <summary>按链路端点发送：首选链路在前，失败会自动换另一条链路。</summary>
    public Task<TransferResult> TransmitSessionAsync(
        IReadOnlyList<PeerEndpoint> endpoints,
        string senderName,
        TransferSession session,
        CancellationToken ct)
    {
        return TransmitSessionCoreAsync(endpoints, senderName, session, ct);
    }

    private async Task<TransferResult> TransmitSessionCoreAsync(
        IReadOnlyList<PeerEndpoint> endpoints,
        string senderName,
        TransferSession session,
        CancellationToken ct)
    {
        var rootElements = session.Files.Select(f => f.RootName).Distinct().ToList();
        var result = new TransferResult
        {
            TotalElements = session.PayloadFolderCount + session.PayloadFileCount,
            AllElementNames = rootElements
        };

        if (session.Files.Count == 0)
        {
            result.Success = true;
            return result;
        }

        var candidates = endpoints.Where(e => !string.IsNullOrWhiteSpace(e.Address)).ToList();
        if (candidates.Count == 0)
        {
            result.Success = false;
            result.ErrorMessage = "没有可用的链路端点。";
            return result;
        }

        Log($"准备连接，候选链路：{string.Join("、", candidates.Select(c => c.ToString()))}");

        var connect = await EtherTransfer.Network.Routing.LinkRouter.ConnectAsync(candidates, ct, m => Log(m)).ConfigureAwait(false);

        using var client = connect.Client;
        var usedEndpoint = connect.Endpoint;
        // 建连成功 = 这条链路是通的；失败时上层据此判断该不该改走 UDP 广播通道。
        result.Connected = true;
        result.Channel = TransferChannels.Tcp;
        Log($"已选中链路：{usedEndpoint}，建连耗时 {connect.ElapsedMs} ms");

        var networkStream = client.GetStream();
        client.NoDelay = true;
        client.SendBufferSize = 2 * 1024 * 1024;
        client.ReceiveBufferSize = 2 * 1024 * 1024;
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

        var request = new TransferRequestMessage
        {
            SenderName = senderName,
            TotalFiles = session.TotalFiles,
            TotalSize = session.TotalSize,
            ContainsFolders = session.ContainsFolders,
            PayloadFolderCount = session.PayloadFolderCount,
            PayloadFileCount = session.PayloadFileCount,
            RootElementNames = rootElements
        };

        await ProtocolHelper.SendMessageAsync(networkStream, request, ct, 3000).ConfigureAwait(false);

        Log("正在等待接收方接受...");
        var responseSw = System.Diagnostics.Stopwatch.StartNew();
        var response = await ProtocolHelper.ReceiveMessageAsync<TransferResponseMessage>(networkStream, ct).ConfigureAwait(false);
        responseSw.Stop();
        Log($"接收方响应耗时 {responseSw.ElapsedMilliseconds} ms");

        if (response == null)
            throw new Exception($"接收方在响应前关闭了连接（链路 {usedEndpoint.LinkLabel} {usedEndpoint.Address}）。" +
                                "常见原因：对端未授权接收目录、对端在弹出确认框之前就报错，或对端防火墙拦住了入站。");

        if (!response.Accepted)
        {
            var reason = string.IsNullOrWhiteSpace(response.Reason) ? "接收方拒绝了本次传输。" : response.Reason;
            Log($"接收方未接受本次传输：{reason}");
            throw new Exception($"接收方拒绝了本次传输：{reason}");
        }

        Log($"接收方已接受！正在传输 {session.TotalFiles} 个文件（{session.TotalSize / 1024 / 1024} MB）...");

        long totalBytesSent = 0;
        int filesSent = 0;
        int filesSkipped = 0;
        int totalElements = session.PayloadFolderCount + session.PayloadFileCount;
        int currentElementIndex = 0;
        string? currentRootName = null;

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var speedTracker = new SpeedTracker();
        speedTracker.Start();

        var lastProgressReport = watch.ElapsedMilliseconds;

        void ReportProgress(bool force = false)
        {
            var now = watch.ElapsedMilliseconds;
            if (force || now - lastProgressReport >= 50 || totalBytesSent >= session.TotalSize)
            {
                lastProgressReport = now;
                var currentSpeed = speedTracker.CalculateSpeed(totalBytesSent);

                ProgressUpdated?.Invoke(this, new TransferProgressEventArgs
                {
                    CurrentFile = currentRootName ?? string.Empty,
                    BytesSent = totalBytesSent,
                    TotalBytes = session.TotalSize,
                    SpeedMbPerSec = currentSpeed,
                    CurrentElementIndex = currentElementIndex,
                    TotalElements = totalElements
                });
            }
        }

        using var countingStream = new CountingStream(networkStream, onBytesWritten: written =>
        {
            totalBytesSent = written;
            ReportProgress(force: false);
        });

        var itemsByRoot = new List<(string RootName, List<FileSelectionItem> Files)>();
        var seenRoots = new HashSet<string>();
        foreach (var file in session.Files)
        {
            if (seenRoots.Add(file.RootName))
            {
                itemsByRoot.Add((file.RootName, session.Files.Where(f => f.RootName == file.RootName).ToList()));
            }
        }

        try
        {
            foreach (var (rootName, rootFiles) in itemsByRoot)
            {
                ct.ThrowIfCancellationRequested();

                currentRootName = rootName;
                currentElementIndex++;
                ReportProgress(force: true);

                bool isFolder = rootFiles.Count > 1 ||
                                (rootFiles.Count == 1 && rootFiles[0].RelativePath != rootFiles[0].RootName) ||
                                (rootFiles.Count == 1 && Directory.Exists(rootFiles[0].AbsolutePath));

                if (isFolder)
                {
                    var folderBegin = new BaseProtocolMessage { Type = ProtocolMessageTypes.FolderBegin };
                    await ProtocolHelper.SendMessageAsync(countingStream, folderBegin, ct, 3000).ConfigureAwait(false);

                    long? rootCreatedMs = null;
                    long? rootModifiedMs = null;
                    try
                    {
                        var firstFile = rootFiles[0].AbsolutePath;
                        var rootDirPath = Path.GetDirectoryName(firstFile);
                        while (rootDirPath != null && !Path.GetFileName(rootDirPath).Equals(rootName, StringComparison.OrdinalIgnoreCase))
                        {
                            var parent = Path.GetDirectoryName(rootDirPath);
                            if (parent == null) break;
                            rootDirPath = parent;
                        }
                        if (rootDirPath != null && Directory.Exists(rootDirPath))
                        {
                            var di = new DirectoryInfo(rootDirPath);
                            rootCreatedMs = new DateTimeOffset(di.CreationTimeUtc).ToUnixTimeMilliseconds();
                            rootModifiedMs = new DateTimeOffset(di.LastWriteTimeUtc).ToUnixTimeMilliseconds();
                        }
                    }
                    catch { }

                    var folderMeta = new FolderTarMetadata
                    {
                        RootName = rootName,
                        TotalFiles = rootFiles.Count,
                        TotalSize = rootFiles.Sum(f => f.Size),
                        CreationTimeUnixMs = rootCreatedMs,
                        LastWriteTimeUnixMs = rootModifiedMs
                    };
                    await ProtocolHelper.SendMessageAsync(countingStream, folderMeta, ct, 3000).ConfigureAwait(false);

                    var copyBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(128 * 1024);
                    try
                    {
                        foreach (var item in rootFiles)
                        {
                            ct.ThrowIfCancellationRequested();

                            FileStream? fs = null;
                            try
                            {
                                fs = new FileStream(
                                    item.AbsolutePath,
                                    FileMode.Open,
                                    FileAccess.Read,
                                    FileShare.ReadWrite,
                                    1,
                                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                            }
                            catch (FileNotFoundException)
                            {
                                Log($"跳过（文件不存在）：{item.RelativePath}");
                                filesSkipped++;
                                continue;
                            }
                            catch (Exception ex)
                            {
                                Log($"跳过（读取错误）：{item.RelativePath} - {ex.Message}");
                                filesSkipped++;
                                continue;
                            }

                            long fileCreatedMs = 0;
                            long fileModifiedMs = 0;
                            try
                            {
                                var fi = new FileInfo(item.AbsolutePath);
                                fileCreatedMs = new DateTimeOffset(fi.CreationTimeUtc).ToUnixTimeMilliseconds();
                                fileModifiedMs = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds();
                            }
                            catch { }

                            using (fs)
                            {
                                var actualSize = fs.Length;
                                var pathBytes = System.Text.Encoding.UTF8.GetBytes(item.RelativePath);
                                int headerLen = 4 + pathBytes.Length + 8 + 8 + 8;

                                if (headerLen + actualSize <= copyBuffer.Length)
                                {
                                    BitConverter.GetBytes(pathBytes.Length).CopyTo(copyBuffer, 0);
                                    pathBytes.CopyTo(copyBuffer, 4);
                                    BitConverter.GetBytes(actualSize).CopyTo(copyBuffer, 4 + pathBytes.Length);
                                    BitConverter.GetBytes(fileCreatedMs).CopyTo(copyBuffer, 4 + pathBytes.Length + 8);
                                    BitConverter.GetBytes(fileModifiedMs).CopyTo(copyBuffer, 4 + pathBytes.Length + 16);

                                    if (actualSize > 0)
                                    {
                                        int totalRead = 0;
                                        while (totalRead < actualSize)
                                        {
                                            int read = await fs.ReadAsync(copyBuffer.AsMemory(headerLen + totalRead, (int)actualSize - totalRead), ct).ConfigureAwait(false);
                                            if (read == 0) break;
                                            totalRead += read;
                                        }
                                    }

                                    await countingStream.WriteAsync(copyBuffer.AsMemory(0, headerLen + (int)actualSize), ct).ConfigureAwait(false);
                                }
                                else
                                {
                                    BitConverter.GetBytes(pathBytes.Length).CopyTo(copyBuffer, 0);
                                    pathBytes.CopyTo(copyBuffer, 4);
                                    BitConverter.GetBytes(actualSize).CopyTo(copyBuffer, 4 + pathBytes.Length);
                                    BitConverter.GetBytes(fileCreatedMs).CopyTo(copyBuffer, 4 + pathBytes.Length + 8);
                                    BitConverter.GetBytes(fileModifiedMs).CopyTo(copyBuffer, 4 + pathBytes.Length + 16);

                                    await countingStream.WriteAsync(copyBuffer.AsMemory(0, headerLen), ct).ConfigureAwait(false);

                                    long remaining = actualSize;
                                    while (remaining > 0)
                                    {
                                        ct.ThrowIfCancellationRequested();
                                        int toRead = (int)Math.Min(copyBuffer.Length, remaining);
                                        int read = await fs.ReadAsync(copyBuffer.AsMemory(0, toRead), ct).ConfigureAwait(false);
                                        if (read == 0) break;
                                        await countingStream.WriteAsync(copyBuffer.AsMemory(0, read), ct).ConfigureAwait(false);
                                        remaining -= read;
                                    }
                                }

                                filesSent++;
                            }
                        }

                        await countingStream.WriteAsync(BitConverter.GetBytes(0), ct).ConfigureAwait(false);
                        await countingStream.FlushAsync(ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        System.Buffers.ArrayPool<byte>.Shared.Return(copyBuffer);
                    }

                    if (!result.CompletedElementNames.Contains(rootName))
                    {
                        result.CompletedElementNames.Add(rootName);
                    }
                }
                else
                {

                    var item = rootFiles[0];

                    FileStream? fs = null;
                    try
                    {
                        fs = new FileStream(
                            item.AbsolutePath,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.ReadWrite,
                            1,
                            FileOptions.Asynchronous | FileOptions.SequentialScan);
                    }
                    catch (FileNotFoundException)
                    {
                        Log($"跳过（文件已删除）：{item.RelativePath}");
                        await SendFileSkip(countingStream, item.RelativePath, "文件在扫描后被删除。", ct).ConfigureAwait(false);
                        filesSkipped++;
                        continue;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        Log($"跳过（拒绝访问）：{item.RelativePath}");
                        await SendFileSkip(countingStream, item.RelativePath, "权限不足。", ct).ConfigureAwait(false);
                        filesSkipped++;
                        continue;
                    }
                    catch (IOException ex)
                    {
                        Log($"跳过（文件被占用）：{item.RelativePath}");
                        await SendFileSkip(countingStream, item.RelativePath, $"文件被占用：{ex.Message}", ct).ConfigureAwait(false);
                        filesSkipped++;
                        continue;
                    }

                    using (fs)
                    {
                        var actualSize = fs.Length;

                        var fileBegin = new BaseProtocolMessage { Type = ProtocolMessageTypes.FileBegin };
                        await ProtocolHelper.SendMessageAsync(countingStream, fileBegin, ct, 3000).ConfigureAwait(false);

                        long? fileCreatedMs = null;
                        long? fileModifiedMs = null;
                        try
                        {
                            var fi = new FileInfo(item.AbsolutePath);
                            fileCreatedMs = new DateTimeOffset(fi.CreationTimeUtc).ToUnixTimeMilliseconds();
                            fileModifiedMs = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds();
                        }
                        catch { }

                        var meta = new FileItemMetadata
                        {
                            RelativePath = item.RelativePath,
                            RootName = item.RootName,
                            Size = actualSize,
                            CreationTimeUnixMs = fileCreatedMs,
                            LastWriteTimeUnixMs = fileModifiedMs
                        };
                        await ProtocolHelper.SendMessageAsync(countingStream, meta, ct, 3000).ConfigureAwait(false);

                        await PipelinedTransferEngine.StreamFileToNetworkAsync(
                            fs,
                            countingStream,
                            actualSize,
                            onBytesSent: _ => ReportProgress(force: false),
                            ct).ConfigureAwait(false);

                        await countingStream.FlushAsync(ct).ConfigureAwait(false);

                        filesSent++;

                        if (!result.CompletedElementNames.Contains(item.RootName))
                        {
                            result.CompletedElementNames.Add(item.RootName);
                        }
                    }
                }
            }

            var endMsg = new BaseProtocolMessage { Type = ProtocolMessageTypes.TransferEnd };
            await ProtocolHelper.SendMessageAsync(countingStream, endMsg, ct, 3000).ConfigureAwait(false);
            await countingStream.FlushAsync(ct).ConfigureAwait(false);

            result.Success = true;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex is OperationCanceledException ? "传输已取消。" : ex.Message;
            try
            {
                client.LingerState = new LingerOption(true, 0);
                client.Close();
            }
            catch { }
        }

        watch.Stop();
        ReportProgress(force: true);

        var summary = result.Success
            ? $"传输完成！共发送 {totalBytesSent / 1024 / 1024} MB（{filesSent} 个文件），耗时 {watch.Elapsed.TotalSeconds:F1} 秒，链路 {usedEndpoint.LinkLabel}（握手 {connect.ElapsedMs} ms）。"
            : $"传输失败（已发送 {totalBytesSent / 1024 / 1024} MB，{filesSent} 个文件完成），耗时 {watch.Elapsed.TotalSeconds:F1} 秒，链路 {usedEndpoint.LinkLabel}，原因：{result.ErrorMessage}";
        if (filesSkipped > 0)
            summary += $"（跳过 {filesSkipped} 个）";
        Log(summary);

        result.FailedElementNames = result.AllElementNames
            .Where(name => !result.CompletedElementNames.Contains(name))
            .ToList();

        return result;
    }

    private static async Task SendFileSkip(Stream stream, string relativePath, string reason, CancellationToken ct)
    {
        var skipMsg = new FileSkipMessage
        {
            RelativePath = relativePath,
            Reason = reason
        };
        await ProtocolHelper.SendMessageAsync(stream, skipMsg, ct, 3000).ConfigureAwait(false);
    }
}

