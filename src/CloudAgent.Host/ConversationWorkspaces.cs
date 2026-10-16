using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace CloudAgent.Host;

/// <summary>
/// Which workspace each conversation works in, by agent id, kept in one JSON file so a
/// conversation returns to the same repository after a restart. Conversations not listed work in
/// the agent's own workspace.
/// </summary>
public sealed class ConversationWorkspaces
{
    private readonly string _path;
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _bindings;

    public ConversationWorkspaces(string path)
    {
        _path = path;
        _bindings = File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject json
            ? json.Where(p => p.Value is not null).ToDictionary(p => p.Key, p => p.Value!.GetValue<string>())
            : [];
    }

    public string? Of(string agentId)
    {
        lock (_gate)
            return _bindings.GetValueOrDefault(agentId);
    }

    public void Bind(string agentId, string workspace)
    {
        lock (_gate)
        {
            _bindings[agentId] = workspace;
            Save();
        }
    }

    public void Forget(string agentId)
    {
        lock (_gate)
        {
            if (_bindings.Remove(agentId))
                Save();
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var json = new JsonObject(_bindings.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)p.Value)));
        var temp = _path + ".tmp";
        File.WriteAllText(temp, json.ToJsonString());
        File.Move(temp, _path, overwrite: true);
    }
}
