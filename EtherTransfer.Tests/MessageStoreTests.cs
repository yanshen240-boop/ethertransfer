using System;
using System.IO;
using EtherTransfer.Core;
using EtherTransfer.Core.Models;
using NUnit.Framework;

namespace EtherTransfer.Tests;

public class MessageStoreTests
{
    private string _tempDir = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ethertransfer-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        SettingsManager.SetCustomSettingsDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        SettingsManager.SetCustomSettingsDirectory(null);
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Test]
    public void KeyFor_UsesDeviceId_WhenPresent()
    {
        Assert.That(MessageStore.KeyFor("abc", "M332BF"), Is.EqualTo("id:abc"));
    }

    [Test]
    public void KeyFor_FallsBackToName_WhenDeviceIdMissing()
    {
        Assert.That(MessageStore.KeyFor("", "笔记本"), Is.EqualTo("name:笔记本"));
    }

    [Test]
    public void AppendThenLoad_PersistsMessages()
    {
        var store = new MessageStore();
        store.Append("id:abc", "M332BF", ChatMessage.CreateText(ChatDirection.In, "你好"));
        store.Append("id:abc", "M332BF", ChatMessage.CreateFile(ChatDirection.Out, "报告.pdf", "12.4 MB", ChatMessageState.Sent));
        store.Save();

        var loaded = MessageStore.Load();
        var session = loaded.FindSession("id:abc");

        Assert.That(session, Is.Not.Null);
        Assert.That(session!.Messages, Has.Count.EqualTo(2));
        Assert.That(session.Messages[0].Kind, Is.EqualTo(ChatMessageKind.Text));
        Assert.That(session.Messages[0].Text, Is.EqualTo("你好"));
        Assert.That(session.Messages[1].FileName, Is.EqualTo("报告.pdf"));
        Assert.That(session.Messages[1].SizeText, Is.EqualTo("12.4 MB"));
        Assert.That(session.Messages[1].State, Is.EqualTo(ChatMessageState.Sent));
    }

    [Test]
    public void DeleteMessages_RemovesOnlySelected()
    {
        var store = new MessageStore();
        var keep = ChatMessage.CreateText(ChatDirection.Out, "保留");
        var drop = ChatMessage.CreateText(ChatDirection.Out, "删除");
        store.AppendRange("id:abc", "M332BF", new[] { keep, drop });

        store.DeleteMessages("id:abc", new[] { drop.Id });

        var session = store.FindSession("id:abc");
        Assert.That(session!.Messages, Has.Count.EqualTo(1));
        Assert.That(session.Messages[0].Text, Is.EqualTo("保留"));
    }

    [Test]
    public void ClearAll_RemovesEverything()
    {
        var store = new MessageStore();
        store.Append("id:abc", "M332BF", ChatMessage.CreateText(ChatDirection.In, "你好"));
        store.ClearAll();

        var loaded = MessageStore.Load();
        Assert.That(loaded.Sessions, Is.Empty);
    }

    [Test]
    public void CorruptFile_LoadsEmptyStore()
    {
        File.WriteAllText(Path.Combine(_tempDir, "messages.json"), "{ not valid json !!!");
        var loaded = MessageStore.Load();
        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded.Sessions, Is.Empty);
    }
}
