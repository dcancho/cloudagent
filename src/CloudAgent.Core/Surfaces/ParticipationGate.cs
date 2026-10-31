using System;
using System.Collections.Generic;
using System.Linq;

namespace CloudAgent.Core.Surfaces;

/// <summary>Knobs for deciding when the agent speaks in a group.</summary>
public sealed class ParticipationOptions
{
    /// <summary>
    /// How long a person who addressed the agent, or was just answered, keeps talking to it without
    /// addressing it again. Zero means every message must address the agent.
    /// </summary>
    public TimeSpan Engagement { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>The most unaddressed messages kept per group to give the agent context.</summary>
    public int MaxOverheard { get; init; } = 20;

    /// <summary>How long an unaddressed message stays worth showing the agent.</summary>
    public TimeSpan OverheardLifetime { get; init; } = TimeSpan.FromMinutes(30);
}

/// <summary>
/// Decides, per message in a group, whether the agent is being spoken to. Messages that address it
/// (a mention, a reply to it) or come from someone it is already talking with start a turn.
/// The rest are kept briefly and handed to the agent as context with the next message that does,
/// so it can follow a conversation without answering every line. Direct conversations always pass.
/// </summary>
public sealed class ParticipationGate(ParticipationOptions? options = null, TimeProvider? time = null)
{
    private readonly ParticipationOptions _options = options ?? new ParticipationOptions();
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<string, Room> _rooms = new();

    /// <summary>
    /// The message to deliver to the agent, carrying what was overheard before it, or null when the
    /// agent should stay quiet and the message was only kept as context.
    /// </summary>
    public InboundMessage? Admit(InboundMessage message)
    {
        if (message.Kind != ConversationKind.Group)
            return message;

        var now = _time.GetUtcNow();
        lock (_gate)
        {
            var room = RoomOf(message.Surface, message.ConversationId);
            var engaged = room.EngagedUntil.TryGetValue(message.SenderId, out var until) && until > now;

            if (!message.Addressed && !engaged)
            {
                room.Overheard.Add(message with { Overheard = null });
                Trim(room, now);
                return null;
            }

            Trim(room, now);
            room.EngagedUntil[message.SenderId] = now + _options.Engagement;
            var heard = room.Overheard.ToList();
            room.Overheard.Clear();
            return heard.Count == 0 ? message : message with { Overheard = heard };
        }
    }

    /// <summary>The agent answered in a conversation: the people it is talking with stay engaged.</summary>
    public void Answered(string surface, string conversationId)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (!_rooms.TryGetValue(Key(surface, conversationId), out var room))
                return;

            foreach (var sender in room.EngagedUntil.Where(e => e.Value > now).Select(e => e.Key).ToList())
                room.EngagedUntil[sender] = now + _options.Engagement;
        }
    }

    private void Trim(Room room, DateTimeOffset now)
    {
        room.Overheard.RemoveAll(m => now - m.ReceivedAt > _options.OverheardLifetime);
        if (room.Overheard.Count > _options.MaxOverheard)
            room.Overheard.RemoveRange(0, room.Overheard.Count - _options.MaxOverheard);
    }

    private Room RoomOf(string surface, string conversationId)
    {
        var key = Key(surface, conversationId);
        if (!_rooms.TryGetValue(key, out var room))
            _rooms[key] = room = new Room();
        return room;
    }

    private static string Key(string surface, string conversationId) => $"{surface}:{conversationId}";

    private sealed class Room
    {
        public List<InboundMessage> Overheard { get; } = [];
        public Dictionary<string, DateTimeOffset> EngagedUntil { get; } = new();
    }
}
