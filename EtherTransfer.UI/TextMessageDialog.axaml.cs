using System;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace EtherTransfer.UI;

public partial class TextMessageDialog : Window
{
    private string _content = "";

    public TextMessageDialog()
    {
        InitializeComponent();
        WindowsTitleBarTheme.EnableDarkMode(this);
    }

    public TextMessageDialog(string sender, string content, long sentAtUnixMs) : this()
    {
        _content = content;

        this.FindControl<TextBlock>("SenderText")!.Text = $"{sender} 发来一条文本消息";

        var timeText = this.FindControl<TextBlock>("TimeText")!;
        if (sentAtUnixMs > 0)
        {
            var local = DateTimeOffset.FromUnixTimeMilliseconds(sentAtUnixMs).ToLocalTime();
            timeText.Text = local.ToString("yyyy-MM-dd HH:mm:ss");
        }
        else
        {
            timeText.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        }

        this.FindControl<TextBox>("ContentBox")!.Text = content;
    }

    private async void Copy_Click(object? sender, RoutedEventArgs e)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null)
        {
            await clipboard.SetTextAsync(_content);
        }
    }

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
