using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EtherTransfer.Core.Models;

namespace EtherTransfer.Transfer;

public class TransferReceiver
{
    /// <summary>文本消息上限：足够放一段长文，又不至于被用来做变相的文件传输。</summary>
    public const int MaxTextLength = 20000;

    public Func<TransferRequestMessage, CancellationToken, Task<(bool accept, string savePath, CancellationToken cancelToken)>>? OnIncomingTransfer { get; set; }

    /// <summary>收到文本消息时的界面处理程序；返回 false 表示用户拒绝或无法展示。</summary>
    public Func<TextMessageRequest, CancellationToken, Task<(bool accepted, string reason)>>? OnIncomingText { get; set; }

    public event EventHandler<TransferProgressEventArgs>? ProgressUpdated;
    public event EventHandler<StructuredLogMessage>? DebugLog;

    private void Log(string msg, LogLevel level = LogLevel.Info, string eventId = "receiver.log") =>
        DebugLog?.Invoke(this, new StructuredLogMessage(eventId, msg, level));

    public async Task<TransferResult> HandleClientAsync(TcpClient client, CancellationToken appCt)
    {
        var result = new TransferResult();
        var remoteEp = client.Client.RemoteEndPoint?.ToString();
        var localIp = (client.Client.LocalEndPoint as System.Net.IPEndPoint)?.Address;
        Log($"正在处理来自 {remoteEp} 的入站连接（本端 {localIp}，链路={EtherTransfer.Network.NetworkInterfaces.NetworkHelper.LinkLabelOf(localIp)}）");

        try
        {
            using (client)
            {
                var networkStream = client.GetStream();
                client.NoDelay = true;
                client.SendBufferSize = 2 * 1024 * 1024;
                client.ReceiveBufferSize = 2 * 1024 * 1024;
                client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

                var firstJson = await ProtocolHelper.ReceiveRawJsonAsync(networkStream, appCt, 8000).ConfigureAwait(false);
                if (firstJson == null)
                    throw new Exception("未收到任何请求（连接在发送请求前关闭）。");

                string? firstType;
                using (var probe = JsonDocument.Parse(firstJson))
                {
                    firstType = probe.RootElement.TryGetProperty("Type", out var typeElement)
                        ? typeElement.GetString()
                        : null;
                }

                // 文本消息与文件传输共用同一条通道，靠第一条报文的 Type 分流。
                if (string.Equals(firstType, ProtocolMessageTypes.TextMessage, StringComparison.OrdinalIgnoreCase))
                {
                    return await HandleTextMessageAsync(networkStream, firstJson, result, appCt).ConfigureAwait(false);
                }

                if (!string.Equals(firstType, ProtocolMessageTypes.TransferRequest, StringComparison.OrdinalIgnoreCase))
                    throw new Exception($"不支持的请求类型：{firstType ?? "未知"}");

                var request = JsonSerializer.Deserialize<TransferRequestMessage>(firstJson);
                if (request == null)
                    throw new Exception("传输请求格式无效。");

                Log($"收到传输：{request.SenderName} - {request.TotalFiles} 个文件，{request.TotalSize / 1024 / 1024} MB");

                if (OnIncomingTransfer == null)
                    throw new Exception("未绑定入站传输的界面处理程序。");

                using var consentCts = CancellationTokenSource.CreateLinkedTokenSource(appCt);

                // 这里不再对 socket 做“探测读”：
                // 旧实现的 ReadAsync(1 字节) 在取消时会吞掉 1 个字节，并与后续正式读取竞争，
                // 造成协议错位；发送端提前断开由下面的发送看门狗与接收看门狗兜底。
                var (accepted, savePath, cancelToken) = await OnIncomingTransfer(request, consentCts.Token).ConfigureAwait(false);

                using var linkedCt = CancellationTokenSource.CreateLinkedTokenSource(appCt, cancelToken);
                var transferCt = linkedCt.Token;

                var response = new TransferResponseMessage
                {
                    Accepted = accepted,
                    Reason = accepted ? "" : "用户已拒绝。"
                };
                await ProtocolHelper.SendMessageAsync(networkStream, response, transferCt, 3000).ConfigureAwait(false);

                if (!accepted)
                {
                    Log("用户拒绝了本次传输。");
                    result.ErrorMessage = "接收方拒绝了本次传输。";
                    return result;
                }

                Directory.CreateDirectory(savePath);
                Log($"已接受传输，正在保存到：{savePath}");

                long totalBytesReceived = 0;
                int filesReceived = 0;
                int filesSkipped = 0;
                var filesByRootElement = new Dictionary<string, List<string>>();
                var createdDirectoriesThisSession = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                int totalElements = request.PayloadFolderCount + request.PayloadFileCount;
                result.TotalElements = totalElements;
                result.AllElementNames = request.RootElementNames ?? new List<string>();
                int currentElementIndex = 0;
                string? currentRootName = null;

                bool receivedTransferEnd = false;
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var speedTracker = new SpeedTracker();
                speedTracker.Start();

                var lastProgressReport = watch.ElapsedMilliseconds;

                void ReportProgress(bool force = false)
                {
                    var now = watch.ElapsedMilliseconds;
                    if (force || now - lastProgressReport >= 50 || totalBytesReceived >= request.TotalSize)
                    {
                        lastProgressReport = now;
                        var currentSpeed = speedTracker.CalculateSpeed(totalBytesReceived);

                        ProgressUpdated?.Invoke(this, new TransferProgressEventArgs
                        {
                            CurrentFile = currentRootName ?? string.Empty,
                            BytesSent = totalBytesReceived,
                            TotalBytes = request.TotalSize,
                            SpeedMbPerSec = currentSpeed,
                            CurrentElementIndex = currentElementIndex,
                            TotalElements = totalElements
                        });
                    }
                }

                using var countingStream = new CountingStream(networkStream, onBytesRead: read =>
                {
                    totalBytesReceived = read;
                    ReportProgress(force: false);
                });

                try
                {
                    while (true)
                    {
                        transferCt.ThrowIfCancellationRequested();

                        var markerJson = await ProtocolHelper.ReceiveRawJsonAsync(countingStream, transferCt, 5000).ConfigureAwait(false);
                        if (markerJson == null)
                        {
                            throw new IOException("传输完成前发送方意外关闭了连接。");
                        }

                        var baseMsg = JsonSerializer.Deserialize<BaseProtocolMessage>(markerJson);
                        if (baseMsg == null)
                        {
                            throw new IOException("收到无效的协议消息头。");
                        }

                        if (baseMsg.Type == ProtocolMessageTypes.TransferEnd)
                        {
                            receivedTransferEnd = true;
                            break;
                        }

                        if (baseMsg.Type == ProtocolMessageTypes.FileSkip)
                        {
                            var skipMsg = JsonSerializer.Deserialize<FileSkipMessage>(markerJson);
                            if (skipMsg != null)
                                Log($"发送方已跳过：{skipMsg.RelativePath} - {skipMsg.Reason}");
                            filesSkipped++;
                            continue;
                        }

                        if (baseMsg.Type == ProtocolMessageTypes.FolderBegin)
                        {
                            var folderMeta = await ProtocolHelper.ReceiveMessageAsync<FolderTarMetadata>(countingStream, transferCt, 3000).ConfigureAwait(false);
                            if (folderMeta == null)
                                throw new IOException("读取文件夹元数据时连接中断。");

                            var targetRootDirectory = Path.Combine(savePath, folderMeta.RootName);
                            var resolvedRootDirectory = PathSanitizer.ResolveDirectoryCollision(targetRootDirectory);
                            var resolvedRootName = Path.GetFileName(resolvedRootDirectory);

                            if (!createdDirectoriesThisSession.Contains(resolvedRootDirectory))
                            {
                                Directory.CreateDirectory(resolvedRootDirectory);
                                createdDirectoriesThisSession.Add(resolvedRootDirectory);
                            }

                            if (resolvedRootName != currentRootName)
                            {
                                if (currentRootName != null && !result.CompletedElementNames.Contains(currentRootName))
                                {
                                    result.CompletedElementNames.Add(currentRootName);
                                }
                                currentRootName = resolvedRootName;
                                currentElementIndex++;
                            }

                            if (!filesByRootElement.ContainsKey(resolvedRootName))
                            {
                                filesByRootElement[resolvedRootName] = new List<string>();
                            }

                            ReportProgress(force: true);

                            int workerCount = Math.Clamp(Environment.ProcessorCount, 4, 16);
                            var fileChannel = System.Threading.Channels.Channel.CreateBounded<FolderFileTask>(
                                new System.Threading.Channels.BoundedChannelOptions(256)
                                {
                                    SingleWriter = true,
                                    SingleReader = false,
                                    FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait
                                });

                            using var workerCts = CancellationTokenSource.CreateLinkedTokenSource(transferCt);
                            var workerTasks = new Task[workerCount];
                            for (int i = 0; i < workerCount; i++)
                            {
                                workerTasks[i] = Task.Run(async () =>
                                {
                                    var reader = fileChannel.Reader;
                                    while (await reader.WaitToReadAsync(workerCts.Token).ConfigureAwait(false))
                                    {
                                        while (reader.TryRead(out var task))
                                        {
                                            try
                                            {
                                                var dir = Path.GetDirectoryName(task.SafePath);
                                                if (dir != null && !Directory.Exists(dir))
                                                {
                                                    Directory.CreateDirectory(dir);
                                                }

                                                if (task.Buffer != null && task.Length > 0)
                                                {
                                                    await using (var fs = new FileStream(
                                                        task.SafePath,
                                                        FileMode.Create,
                                                        FileAccess.Write,
                                                        FileShare.None,
                                                        1,
                                                        FileOptions.Asynchronous | FileOptions.SequentialScan))
                                                    {
                                                        await fs.WriteAsync(task.Buffer.AsMemory(0, task.Length), workerCts.Token).ConfigureAwait(false);
                                                    }
                                                }
                                                else
                                                {
                                                    using (new FileStream(task.SafePath, FileMode.Create, FileAccess.Write, FileShare.None)) { }
                                                }

                                                ApplyFileTimestamps(task.SafePath, task.CreationTimeUnixMs, task.LastWriteTimeUnixMs);
                                            }
                                            catch (OperationCanceledException) { }
                                            catch (Exception ex)
                                            {
                                                Log($"写入任务出错：{task.SafePath} - {ex.Message}");
                                                throw;
                                            }
                                            finally
                                            {
                                                if (task.Buffer != null)
                                                {
                                                    System.Buffers.ArrayPool<byte>.Shared.Return(task.Buffer);
                                                }
                                            }
                                        }
                                    }
                                }, workerCts.Token);
                            }

                            var inFlightFiles = new List<string>();
                            try
                            {
                                var lenBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(4);
                                var sizeBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(8);
                                var timeBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(16);
                                var copyBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(128 * 1024);
                                try
                                {
                                    while (true)
                                    {
                                        transferCt.ThrowIfCancellationRequested();

                                        if (!await ProtocolHelper.ReadExactAsync(countingStream, lenBuffer, 4, transferCt, 5000).ConfigureAwait(false))
                                        {
                                            throw new IOException("读取文件夹内文件路径长度时连接中断。");
                                        }

                                        int pathLen = BitConverter.ToInt32(lenBuffer, 0);
                                        if (pathLen == 0)
                                        {
                                            break;
                                        }

                                        if (pathLen < 0 || pathLen > 4096)
                                        {
                                            throw new InvalidDataException($"文件夹数据流中的路径长度无效：{pathLen}");
                                        }

                                        var pathBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(pathLen);
                                        string relativePath;
                                        try
                                        {
                                            if (!await ProtocolHelper.ReadExactAsync(countingStream, pathBuffer, pathLen, transferCt, 5000).ConfigureAwait(false))
                                            {
                                                throw new IOException("读取文件夹内文件路径时连接中断。");
                                            }
                                            relativePath = System.Text.Encoding.UTF8.GetString(pathBuffer, 0, pathLen);
                                        }
                                        finally
                                        {
                                            System.Buffers.ArrayPool<byte>.Shared.Return(pathBuffer);
                                        }

                                        if (!await ProtocolHelper.ReadExactAsync(countingStream, sizeBuffer, 8, transferCt, 5000).ConfigureAwait(false))
                                        {
                                            throw new IOException("读取文件夹内文件大小时连接中断。");
                                        }

                                        long fileSize = BitConverter.ToInt64(sizeBuffer, 0);
                                        if (fileSize < 0)
                                        {
                                            throw new InvalidDataException($"文件夹数据流中的文件大小无效：{fileSize}");
                                        }

                                        if (!await ProtocolHelper.ReadExactAsync(countingStream, timeBuffer, 16, transferCt, 5000).ConfigureAwait(false))
                                        {
                                            throw new IOException("读取文件夹内文件时间戳时连接中断。");
                                        }

                                        long fileCreatedMs = BitConverter.ToInt64(timeBuffer, 0);
                                        long fileModifiedMs = BitConverter.ToInt64(timeBuffer, 8);

                                        string adjustedRelativePath = relativePath;
                                        if (resolvedRootName != folderMeta.RootName)
                                        {
                                            if (relativePath.StartsWith(folderMeta.RootName + "/", StringComparison.OrdinalIgnoreCase))
                                            {
                                                adjustedRelativePath = resolvedRootName + relativePath.Substring(folderMeta.RootName.Length);
                                            }
                                            else if (relativePath.StartsWith(folderMeta.RootName + "\\", StringComparison.OrdinalIgnoreCase))
                                            {
                                                adjustedRelativePath = resolvedRootName + relativePath.Substring(folderMeta.RootName.Length);
                                            }
                                            else if (relativePath.Equals(folderMeta.RootName, StringComparison.OrdinalIgnoreCase))
                                            {
                                                adjustedRelativePath = resolvedRootName;
                                            }
                                            else
                                            {
                                                adjustedRelativePath = Path.Combine(resolvedRootName, relativePath);
                                            }
                                        }

                                        var safePath = PathSanitizer.SanitizeRelativePath(savePath, adjustedRelativePath);
                                        if (safePath == null)
                                        {
                                            Log($"安全：已拦截文件夹数据流中的恶意路径：{relativePath}");
                                            await DrainBytesAsync(countingStream, fileSize, transferCt).ConfigureAwait(false);
                                            filesSkipped++;
                                            continue;
                                        }

                                        var dirPath = Path.GetDirectoryName(safePath);
                                        if (dirPath != null && !createdDirectoriesThisSession.Contains(dirPath))
                                        {
                                            Directory.CreateDirectory(dirPath);
                                            createdDirectoriesThisSession.Add(dirPath);
                                        }

                                        inFlightFiles.Add(safePath);
                                        filesByRootElement[resolvedRootName].Add(safePath);

                                        if (fileSize <= 64 * 1024)
                                        {
                                            if (fileSize > 0)
                                            {
                                                var fileBuf = System.Buffers.ArrayPool<byte>.Shared.Rent((int)fileSize);
                                                if (!await ProtocolHelper.ReadExactAsync(countingStream, fileBuf, (int)fileSize, transferCt, 5000).ConfigureAwait(false))
                                                {
                                                    System.Buffers.ArrayPool<byte>.Shared.Return(fileBuf);
                                                    throw new IOException("读取文件夹内文件数据时连接中断。");
                                                }
                                                await fileChannel.Writer.WriteAsync(new FolderFileTask(safePath, fileBuf, (int)fileSize, resolvedRootName, fileCreatedMs, fileModifiedMs), transferCt).ConfigureAwait(false);
                                            }
                                            else
                                            {
                                                await fileChannel.Writer.WriteAsync(new FolderFileTask(safePath, null, 0, resolvedRootName, fileCreatedMs, fileModifiedMs), transferCt).ConfigureAwait(false);
                                            }
                                        }
                                        else
                                        {
                                            FileStream? fs = new FileStream(
                                                safePath,
                                                FileMode.Create,
                                                FileAccess.Write,
                                                FileShare.None,
                                                1,
                                                FileOptions.Asynchronous | FileOptions.SequentialScan);

                                            try
                                            {
                                                if (fileSize >= 1024 * 1024)
                                                {
                                                    try { fs.SetLength(fileSize); } catch { }
                                                }

                                                long remaining = fileSize;
                                                while (remaining > 0)
                                                {
                                                    transferCt.ThrowIfCancellationRequested();
                                                    int toRead = (int)Math.Min(copyBuffer.Length, remaining);
                                                    int read = await countingStream.ReadAsync(copyBuffer.AsMemory(0, toRead), transferCt).ConfigureAwait(false);
                                                    if (read == 0)
                                                    {
                                                        throw new IOException("读取文件夹内文件数据时连接意外中断。");
                                                    }
                                                    await fs.WriteAsync(copyBuffer.AsMemory(0, read), transferCt).ConfigureAwait(false);
                                                    remaining -= read;
                                                }

                                                await fs.DisposeAsync().ConfigureAwait(false);
                                                fs = null;
                                                ApplyFileTimestamps(safePath, fileCreatedMs, fileModifiedMs);
                                            }
                                            finally
                                            {
                                                if (fs != null)
                                                {
                                                    try { await fs.DisposeAsync().ConfigureAwait(false); } catch { }
                                                }
                                            }
                                        }

                                        filesReceived++;
                                    }
                                }
                                finally
                                {
                                    System.Buffers.ArrayPool<byte>.Shared.Return(lenBuffer);
                                    System.Buffers.ArrayPool<byte>.Shared.Return(sizeBuffer);
                                    System.Buffers.ArrayPool<byte>.Shared.Return(timeBuffer);
                                    System.Buffers.ArrayPool<byte>.Shared.Return(copyBuffer);
                                }

                                fileChannel.Writer.Complete();
                                await Task.WhenAll(workerTasks).ConfigureAwait(false);
                                ApplyDirectoryTimestamps(resolvedRootDirectory, folderMeta.CreationTimeUnixMs, folderMeta.LastWriteTimeUnixMs);
                            }
                            catch (Exception folderEx)
                            {
                                Log($"接收文件夹出错：{folderEx.Message}", LogLevel.Warning);
                                workerCts.Cancel();
                                while (fileChannel.Reader.TryRead(out var task))
                                {
                                    if (task.Buffer != null)
                                    {
                                        System.Buffers.ArrayPool<byte>.Shared.Return(task.Buffer);
                                    }
                                }
                                foreach (var path in inFlightFiles)
                                {
                                    try
                                    {
                                        if (File.Exists(path)) File.Delete(path);
                                    }
                                    catch { }
                                }
                                throw;
                            }

                            if (!result.CompletedElementNames.Contains(resolvedRootName))
                            {
                                result.CompletedElementNames.Add(resolvedRootName);
                            }
                            continue;
                        }

                        if (baseMsg.Type == ProtocolMessageTypes.FileBegin)
                        {
                            var fileMeta = await ProtocolHelper.ReceiveMessageAsync<FileItemMetadata>(countingStream, transferCt, 3000).ConfigureAwait(false);
                            if (fileMeta == null)
                            {
                                throw new IOException("读取文件元数据时连接中断。");
                            }

                            if (fileMeta.RootName != currentRootName)
                            {
                                if (currentRootName != null && !result.CompletedElementNames.Contains(currentRootName))
                                {
                                    result.CompletedElementNames.Add(currentRootName);
                                }
                                currentRootName = fileMeta.RootName;
                                currentElementIndex++;
                            }

                            var rootKey = string.IsNullOrEmpty(fileMeta.RootName) ? fileMeta.RelativePath : fileMeta.RootName;
                            if (!filesByRootElement.ContainsKey(rootKey))
                            {
                                filesByRootElement[rootKey] = new List<string>();
                            }

                            var safePath = PathSanitizer.SanitizeRelativePath(savePath, fileMeta.RelativePath);
                            if (safePath == null)
                            {
                                Log($"安全：已拦截恶意路径：{fileMeta.RelativePath}");
                                await DrainBytesAsync(countingStream, fileMeta.Size, transferCt).ConfigureAwait(false);
                                filesSkipped++;
                                continue;
                            }

                            safePath = PathSanitizer.ResolveCollision(safePath);

                            var dirPath = Path.GetDirectoryName(safePath);
                            if (dirPath != null && !createdDirectoriesThisSession.Contains(dirPath))
                            {
                                Directory.CreateDirectory(dirPath);
                                createdDirectoriesThisSession.Add(dirPath);
                            }

                            ReportProgress(force: true);

                            FileStream? fs = null;
                            try
                            {
                                filesByRootElement[rootKey].Add(safePath);

                                fs = new FileStream(
                                    safePath,
                                    FileMode.Create,
                                    FileAccess.Write,
                                    FileShare.None,
                                    1,
                                    FileOptions.Asynchronous | FileOptions.SequentialScan);

                                await PipelinedTransferEngine.StreamNetworkToFileAsync(
                                    countingStream,
                                    fs,
                                    fileMeta.Size,
                                    onBytesReceived: _ => ReportProgress(force: false),
                                    transferCt).ConfigureAwait(false);

                                await fs.DisposeAsync().ConfigureAwait(false);
                                fs = null;
                                ApplyFileTimestamps(safePath, fileMeta.CreationTimeUnixMs, fileMeta.LastWriteTimeUnixMs);

                                filesReceived++;

                                if (totalElements > 1 && (fileMeta.RelativePath == fileMeta.RootName || string.IsNullOrEmpty(fileMeta.RootName)))
                                {
                                    if (!result.CompletedElementNames.Contains(rootKey))
                                    {
                                        result.CompletedElementNames.Add(rootKey);
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Log($"接收 {fileMeta.RelativePath} 时出错：{ex.Message}");

                                if (fs != null)
                                {
                                    try { await fs.DisposeAsync().ConfigureAwait(false); } catch { }
                                    fs = null;
                                }

                                try
                                {
                                    if (File.Exists(safePath))
                                    {
                                        File.Delete(safePath);
                                        Log($"已清理未完成的文件：{fileMeta.RelativePath}");
                                    }
                                }
                                catch (Exception delEx)
                                {
                                    Log($"删除未完成的文件失败：{delEx.Message}", LogLevel.Warning);
                                }

                                throw;
                            }
                            finally
                            {
                                if (fs != null)
                                {
                                    try { await fs.DisposeAsync().ConfigureAwait(false); } catch { }
                                }
                            }
                        }
                    }

                    if (!receivedTransferEnd)
                    {
                        throw new IOException("传输在收到 TRANSFER_END 标记前提前结束。");
                    }

                    if (currentRootName != null && !result.CompletedElementNames.Contains(currentRootName))
                    {
                        result.CompletedElementNames.Add(currentRootName);
                    }

                    result.Success = true;
                    watch.Stop();
                    ReportProgress(force: true);

                    var summary = $"传输完成！共接收 {totalBytesReceived / 1024 / 1024} MB（{filesReceived} 个文件），耗时 {watch.Elapsed.TotalSeconds:F1} 秒。";
                    Log(summary);
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.ErrorMessage = ex is OperationCanceledException
                        ? "传输已取消。"
                        : (ex is System.IO.IOException || ex is System.Net.Sockets.SocketException
                            ? "连接中断（发送方中止或网络断开）。"
                            : ex.Message);

                    if (totalElements <= 1)
                    {

                        foreach (var kvp in filesByRootElement)
                        {
                            foreach (var file in kvp.Value)
                            {
                                try
                                {
                                    if (File.Exists(file))
                                    {
                                        File.Delete(file);
                                        Log($"回滚：已删除会话文件 {file}");
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                    else
                    {

                        foreach (var kvp in filesByRootElement)
                        {
                            if (!result.CompletedElementNames.Contains(kvp.Key))
                            {
                                foreach (var file in kvp.Value)
                                {
                                    try
                                    {
                                        if (File.Exists(file))
                                        {
                                            File.Delete(file);
                                            Log($"回滚：已删除未完成的项目文件 {file}");
                                        }
                                    }
                                    catch { }
                                }
                            }
                        }
                    }

                    foreach (var dir in createdDirectoriesThisSession.OrderByDescending(d => d.Length))
                    {
                        try
                        {
                            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                            {
                                Directory.Delete(dir);
                                Log($"回滚：已删除空目录 {dir}");
                            }
                        }
                        catch { }
                    }

                    try { client.LingerState = new LingerOption(true, 0); client.Close(); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex is System.IO.IOException || ex is System.Net.Sockets.SocketException
                ? "连接中断（网线拔出或发送方中止）。"
                : ex.Message;
            try { client.LingerState = new LingerOption(true, 0); client.Close(); } catch { }
        }

        result.FailedElementNames = result.AllElementNames
            .Where(name => !result.CompletedElementNames.Contains(name))
            .ToList();

        return result;
    }

    private async Task<TransferResult> HandleTextMessageAsync(
        NetworkStream stream,
        string json,
        TransferResult result,
        CancellationToken ct)
    {
        var message = JsonSerializer.Deserialize<TextMessageRequest>(json)
                      ?? throw new Exception("文本消息格式无效。");

        if (string.IsNullOrWhiteSpace(message.Text))
            throw new Exception("文本消息内容为空。");

        if (message.Text.Length > MaxTextLength)
            throw new Exception($"文本过长（{message.Text.Length} 字，上限 {MaxTextLength} 字）。");

        Log($"收到文本消息：{message.SenderName}（{message.Text.Length} 字）");

        bool accepted = true;
        string reason = string.Empty;

        if (OnIncomingText != null)
        {
            try
            {
                (accepted, reason) = await OnIncomingText(message, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                accepted = false;
                reason = "接收方已关闭窗口。";
            }
            catch (Exception ex)
            {
                accepted = false;
                reason = ex.Message;
            }
        }

        await ProtocolHelper.SendMessageAsync(stream, new TextMessageAck
        {
            Accepted = accepted,
            Reason = reason,
            ReceivedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        }, ct, 5000).ConfigureAwait(false);

        result.Success = accepted;
        result.TotalElements = 1;
        result.AllElementNames = new List<string> { "文本消息" };
        if (accepted)
        {
            result.CompletedElementNames.Add("文本消息");
            Log("文本消息已确认送达。");
        }
        else
        {
            result.ErrorMessage = string.IsNullOrWhiteSpace(reason) ? "接收方未接收这条文本。" : reason;
        }

        return result;
    }

    private static async Task DrainBytesAsync(Stream stream, long count, CancellationToken ct)
    {
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            long drained = 0;
            while (drained < count)
            {
                int toRead = (int)Math.Min(buffer.Length, count - drained);
                int read = await stream.ReadAsync(buffer.AsMemory(0, toRead), ct).ConfigureAwait(false);
                if (read == 0)
                    throw new IOException("读取被跳过文件的数据时连接中断。");

                drained += read;
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void ApplyFileTimestamps(string path, long? creationTimeUnixMs, long? lastWriteTimeUnixMs)
    {
        try
        {
            if (lastWriteTimeUnixMs.HasValue && lastWriteTimeUnixMs.Value > 0)
            {
                File.SetLastWriteTimeUtc(path, DateTimeOffset.FromUnixTimeMilliseconds(lastWriteTimeUnixMs.Value).UtcDateTime);
            }
            if (creationTimeUnixMs.HasValue && creationTimeUnixMs.Value > 0)
            {
                File.SetCreationTimeUtc(path, DateTimeOffset.FromUnixTimeMilliseconds(creationTimeUnixMs.Value).UtcDateTime);
            }
        }
        catch { }
    }

    private static void ApplyDirectoryTimestamps(string path, long? creationTimeUnixMs, long? lastWriteTimeUnixMs)
    {
        try
        {
            if (lastWriteTimeUnixMs.HasValue && lastWriteTimeUnixMs.Value > 0 && Directory.Exists(path))
            {
                Directory.SetLastWriteTimeUtc(path, DateTimeOffset.FromUnixTimeMilliseconds(lastWriteTimeUnixMs.Value).UtcDateTime);
            }
            if (creationTimeUnixMs.HasValue && creationTimeUnixMs.Value > 0 && Directory.Exists(path))
            {
                Directory.SetCreationTimeUtc(path, DateTimeOffset.FromUnixTimeMilliseconds(creationTimeUnixMs.Value).UtcDateTime);
            }
        }
        catch { }
    }
}

internal readonly struct FolderFileTask
{
    public readonly string SafePath;
    public readonly byte[]? Buffer;
    public readonly int Length;
    public readonly string RootName;
    public readonly long? CreationTimeUnixMs;
    public readonly long? LastWriteTimeUnixMs;

    public FolderFileTask(string safePath, byte[]? buffer, int length, string rootName, long? creationTimeUnixMs, long? lastWriteTimeUnixMs)
    {
        SafePath = safePath;
        Buffer = buffer;
        Length = length;
        RootName = rootName;
        CreationTimeUnixMs = creationTimeUnixMs;
        LastWriteTimeUnixMs = lastWriteTimeUnixMs;
    }
}

