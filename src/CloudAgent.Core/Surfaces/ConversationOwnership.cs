using System;
using System.Security.Cryptography;
using System.Text;

namespace CloudAgent.Core.Surfaces;

/// <summary>
/// Conversation ids that carry their owner: every id starts with a key derived from the sender who
/// opened it, so a surface where clients pick conversations by id can refuse anyone else's.
/// </summary>
public static class ConversationOwnership
{
    private const int KeyLength = 10;
    private const int RandomLength = 12;

    /// <summary>A new conversation id owned by <paramref name="senderId"/>.</summary>
    public static string NewId(string senderId) =>
        KeyOf(senderId) + Guid.NewGuid().ToString("N")[..RandomLength];

    /// <summary>Whether <paramref name="conversationId"/> was opened by <paramref name="senderId"/>.</summary>
    public static bool Owns(string senderId, string conversationId) =>
        conversationId.Length == KeyLength + RandomLength
        && conversationId.StartsWith(KeyOf(senderId), StringComparison.Ordinal);

    private static string KeyOf(string senderId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(senderId)))[..KeyLength].ToLowerInvariant();
}
