using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using EtherTransfer.Core.Models;

namespace EtherTransfer.Core;

/// <summary>
/// 会话消息的落盘存储：按设备分会话，序列化到 <c>%APPDATA%\EtherTransfer\messages.json</c>
/// （便携模式下跟随 settings.json 所在目录）。
/// 只存文本和文件名 / 大小 / 状态 / 时间，不写文件路径副本、不拷文件本体。
/// </summary>
public class MessageStore
{
    private static readonly object _lock = new();

    private const int MaxMessagesPerSession = 2000;

    public List<ChatSession> Sessions { get; set; } = new();

    public class ChatSession
    {
        public string Key { get; set; } = string.Empty;
        public string DeviceName { get; set; } = string.Empty;
        public List<ChatMessage> Messages { get; set; } = new();
    }

    private sealed class PersistModel
    {
        public int Version { get; set; } = 1;
        public List<ChatSession> Sessions { get; set; } = new();
    }

    public static string StoreFile => Path.Combine(SettingsManager.SettingsFolder, "messages.json");

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    public static MessageStore Load()
    {
        lock (_lock)
        {
            var filePath = StoreFile;
            if (!File.Exists(filePath)) return new MessageStore();

            try
            {
                var json = File.ReadAllText(filePath);
                var model = JsonSerializer.Deserialize<PersistModel>(json, _jsonOptions);
                var store = new MessageStore();
                if (model?.Sessions != null)
                {
                    store.Sessions = model.Sessions;
                }
                return store;
            }
            catch
            {
                return new MessageStore();
            }
        }
    }

    public void Save()
    {
        lock (_lock)
        {
            try
            {
                var folder = SettingsManager.SettingsFolder;
                if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);

                var model = new PersistModel { Sessions = Sessions };
                File.WriteAllText(StoreFile, JsonSerializer.Serialize(model, _jsonOptions));
            }
            catch
            {
                // 落盘失败不致命：会话消息丢了也比影响传输强。
            }
        }
    }

    /// <summary>会话键：优先用稳定的 DeviceId，没有就用设备名兜底（老设备 / 离线来源）。</summary>
    public static string KeyFor(string deviceId, string deviceName) =>
        string.IsNullOrWhiteSpace(deviceId) ? $"name:{deviceName}" : $"id:{deviceId}";

    public ChatSession GetOrCreateSession(string key, string deviceName)
    {
        lock (_lock)
        {
            var session = Sessions.FirstOrDefault(s => s.Key == key);
            if (session == null)
            {
                session = new ChatSession { Key = key, DeviceName = deviceName };
                Sessions.Add(session);
            }

            if (!string.IsNullOrWhiteSpace(deviceName)) session.DeviceName = deviceName;
            return session;
        }
    }

    public ChatSession? FindSession(string key)
    {
        lock (_lock)
        {
            return Sessions.FirstOrDefault(s => s.Key == key);
        }
    }

    public void Append(string key, string deviceName, ChatMessage message)
    {
        lock (_lock)
        {
            var session = GetOrCreateSession(key, deviceName);
            session.Messages.Add(message);
            Trim(session);
        }
    }

    /// <summary>追加一组消息（一次传输落多条文件卡片时用），一次落盘。</summary>
    public void AppendRange(string key, string deviceName, IEnumerable<ChatMessage> messages)
    {
        lock (_lock)
        {
            var session = GetOrCreateSession(key, deviceName);
            session.Messages.AddRange(messages);
            Trim(session);
        }
    }

    public void DeleteMessages(string key, IEnumerable<string> messageIds)
    {
        lock (_lock)
        {
            var session = FindSession(key);
            if (session == null) return;
            var ids = new HashSet<string>(messageIds);
            session.Messages.RemoveAll(m => ids.Contains(m.Id));
        }
    }

    public void ClearSession(string key)
    {
        lock (_lock)
        {
            var session = FindSession(key);
            if (session != null) session.Messages.Clear();
        }
    }

    /// <summary>清理缓存：删除全部会话记录（不动已接收的文件本体）。</summary>
    public void ClearAll()
    {
        lock (_lock)
        {
            Sessions.Clear();
            Save();
        }
    }

    private static void Trim(ChatSession session)
    {
        if (session.Messages.Count > MaxMessagesPerSession)
        {
            session.Messages.RemoveRange(0, session.Messages.Count - MaxMessagesPerSession);
        }
    }
}
