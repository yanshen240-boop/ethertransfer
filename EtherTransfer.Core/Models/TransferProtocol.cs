namespace EtherTransfer.Core.Models;

public static class ProtocolMessageTypes
{
    public const string TransferRequest = "TRANSFER_REQUEST";
    public const string TransferResponse = "TRANSFER_RESPONSE";
    public const string FileBegin = "FILE_BEGIN";
    public const string FolderBegin = "FOLDER_BEGIN";
    public const string TransferEnd = "TRANSFER_END";
    public const string FileSkip = "FILE_SKIP";

    /// <summary>纯文本消息：作为连接上的第一条报文发送，对端回 TextAck 后连接结束。</summary>
    public const string TextMessage = "TEXT_MESSAGE";
    public const string TextAck = "TEXT_ACK";
}

/// <summary>
/// 文本消息。与文件传输共用同一条 TCP 通道和同一套链路选择逻辑，
/// 但只有一次“请求—确认”，不进入文件流，因此即使没有选择任何文件也能发送。
/// </summary>
public class TextMessageRequest : BaseProtocolMessage
{
    public TextMessageRequest()
    {
        Type = ProtocolMessageTypes.TextMessage;
    }

    public string SenderName { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public long SentAtUnixMs { get; set; }
    public string MessageId { get; set; } = string.Empty;

    /// <summary>
    /// 链路连通性探测：只用于验证端到端可达，接收方应回应答但不在会话中显示。
    /// 旧版本对端不认识该字段会当作普通文本显示（向后兼容）。
    /// </summary>
    public bool IsProbe { get; set; }
}

public class TextMessageAck : BaseProtocolMessage
{
    public TextMessageAck()
    {
        Type = ProtocolMessageTypes.TextAck;
    }

    public bool Accepted { get; set; }
    public string Reason { get; set; } = string.Empty;
    public long ReceivedAtUnixMs { get; set; }
}

public class BaseProtocolMessage
{
    public string Type { get; set; } = string.Empty;
}

public class TransferRequestMessage : BaseProtocolMessage
{
    public TransferRequestMessage()
    {
        Type = ProtocolMessageTypes.TransferRequest;
    }

    public string SenderName { get; set; } = string.Empty;
    public int TotalFiles { get; set; }
    public long TotalSize { get; set; }
    public bool ContainsFolders { get; set; }
    public int PayloadFolderCount { get; set; }
    public int PayloadFileCount { get; set; }
    public System.Collections.Generic.List<string> RootElementNames { get; set; } = new();
}

public class TransferResponseMessage : BaseProtocolMessage
{
    public TransferResponseMessage()
    {
        Type = ProtocolMessageTypes.TransferResponse;
    }

    public bool Accepted { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public class FileItemMetadata
{
    public string RelativePath { get; set; } = string.Empty;
    public string RootName { get; set; } = string.Empty;
    public long Size { get; set; }
    public long? CreationTimeUnixMs { get; set; }
    public long? LastWriteTimeUnixMs { get; set; }
}

public class FolderTarMetadata
{
    public string RootName { get; set; } = string.Empty;
    public int TotalFiles { get; set; }
    public long TotalSize { get; set; }
    public long? CreationTimeUnixMs { get; set; }
    public long? LastWriteTimeUnixMs { get; set; }
}

public class FileChecksumMessage : BaseProtocolMessage
{
    public FileChecksumMessage() { Type = "FILE_CHECKSUM"; }
    public string Sha256 { get; set; } = string.Empty;
}

public class FileSkipMessage : BaseProtocolMessage
{
    public FileSkipMessage() { Type = ProtocolMessageTypes.FileSkip; }
    public string RelativePath { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

