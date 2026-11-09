using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using EtherTransfer.Core.Models;

namespace EtherTransfer.UI;

/// <summary>会话流里的一个条目：日期分隔或消息。按设计稿，分隔线在跨天时插入。</summary>
public abstract class ChatEntry
{
}

public class DaySeparatorEntry : ChatEntry
{
    public string Text { get; }

    public DaySeparatorEntry(string text) => Text = text;
}

/// <summary>
/// ChatMessage 的界面包装：把状态翻译成文案（"已发送 / 已接收 / 发送失败 · 点此重试"），
/// 并承载多选勾选。持久化永远走 Model 本体。
/// </summary>
public class ChatMessageEntry : ChatEntry, INotifyPropertyChanged
{
    public ChatMessage Model { get; }

    private bool _isSelected;

    public ChatMessageEntry(ChatMessage model) => Model = model;

    public string Id => Model.Id;
    public bool IsOut => Model.Direction == ChatDirection.Out;
    public bool IsText => Model.Kind == ChatMessageKind.Text;
    public bool IsFile => Model.Kind == ChatMessageKind.File;
    public string Text => Model.Text;
    public string FileName => Model.FileName;
    public string SizeText => Model.SizeText;

    public bool IsFailed => Model.State == ChatMessageState.Failed;
    public bool IsSending => Model.State == ChatMessageState.Sending;

    public string StateText
    {
        get
        {
            if (Model.State == ChatMessageState.Failed) return "发送失败 · 点此重试";
            if (Model.State == ChatMessageState.Sending) return "发送中…";
            if (Model.State == ChatMessageState.Received) return string.IsNullOrEmpty(Model.SizeText) ? "已接收" : $"{Model.SizeText} · 已接收";
            return string.IsNullOrEmpty(Model.SizeText) ? "已发送" : $"{Model.SizeText} · 已发送";
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); } }
    }

    public void RefreshFromModel()
    {
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsSending));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
