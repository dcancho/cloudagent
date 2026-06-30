using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CloudAgent.Core.Todos;

/// <summary>
/// The agent's taskboard: a small ordered list of todos, persisted to a JSON file in the
/// workspace so it survives across sessions. Single-agent, single-threaded by assumption.
/// </summary>
public sealed class TodoStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly List<TodoItem> _items;

    public TodoStore(string path)
    {
        _path = path;
        _items = Load(path);
    }

    public IReadOnlyList<TodoItem> Items => _items;

    public int Count => _items.Count;

    /// <summary>Add a task and return it. Ids are assigned 1, 2, 3, … (max + 1).</summary>
    public TodoItem Add(string title)
    {
        var id = _items.Count == 0 ? 1 : _items.Max(i => i.Id) + 1;
        var item = new TodoItem { Id = id, Title = title.Trim() };
        _items.Add(item);
        Save();
        return item;
    }

    /// <summary>Mark a task done. Returns the item, or null if no such id.</summary>
    public TodoItem? Complete(int id)
    {
        var item = _items.FirstOrDefault(i => i.Id == id);
        if (item is null)
            return null;

        item.Status = TodoStatus.Done;
        item.CompletedAt = DateTimeOffset.UtcNow;
        Save();
        return item;
    }

    /// <summary>Remove a task. Returns the removed item, or null if no such id.</summary>
    public TodoItem? Remove(int id)
    {
        var item = _items.FirstOrDefault(i => i.Id == id);
        if (item is null)
            return null;

        _items.Remove(item);
        Save();
        return item;
    }

    /// <summary>Empty the taskboard. Returns how many were removed.</summary>
    public int Clear()
    {
        var count = _items.Count;
        _items.Clear();
        Save();
        return count;
    }

    private static List<TodoItem> Load(string path)
    {
        if (!File.Exists(path))
            return [];

        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<List<TodoItem>>(stream, JsonOptions) ?? [];
    }

    private void Save()
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        using var stream = File.Create(_path);
        JsonSerializer.Serialize(stream, _items, JsonOptions);
    }
}
