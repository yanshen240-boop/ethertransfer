using System;

namespace EtherTransfer.Core.Models;

public enum ChatDirection
{
    Out,
    In
}

public enum ChatMessageKind
{
    Text,
    File
}

public enum ChatMessageState
{
    /// <summary>发出的文本 / 文件已送达。</summary>
    Sent,
    /// <summary>收到的消息。</summary>
    Received,
    /// <summary>发送中（进度由传输对话框展示）。</summary>
    Sending,
    /// <summary>发送失败，可重试。</summary>
    Failed
}

/// <summary>
/// 会话里的一条消息。按设计稿约定，<b>只存文本和文件名（大小 / 状态 / 时间），
/// 不保留文件本体，也不复制收到的文件</b>，所以记录只占几十 KB。
/// </summary>
public class ChatMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ChatDirection Direction { get; set; }
    public ChatMessageKind Kind { get; set; }

    /// <summary>文本内容；文件消息时可空（文件说明）。</summary>
    public string Text { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    /// <summary>格式化后的大小，例如 "12.4 MB"。</summary>
    public string SizeText { get; set; } = string.Empty;

    public ChatMessageState State { get; set; } = ChatMessageState.Sent;

    public long TimeUnixMs { get; set; }

    public static ChatMessage CreateText(ChatDirection direction, string text, long? timeUnixMs = null) => new()
    {
        Direction = direction,
        Kind = ChatMessageKind.Text,
        Text = text,
        State = direction == ChatDirection.In ? ChatMessageState.Received : ChatMessageState.Sent,
        TimeUnixMs = timeUnixMs ?? DateTimeOffset.Now.ToUnixTimeMilliseconds()
    };

    public static ChatMessage CreateFile(ChatDirection direction, string fileName, string sizeText,
        ChatMessageState state, string? text = null, long? timeUnixMs = null) => new()
    {
        Direction = direction,
        Kind = ChatMessageKind.File,
        FileName = fileName,
        SizeText = sizeText,
        Text = text ?? string.Empty,
        State = state,
        TimeUnixMs = timeUnixMs ?? DateTimeOffset.Now.ToUnixTimeMilliseconds()
    };
}
