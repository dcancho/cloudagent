using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CloudAgent.Core.People;

/// <summary>A known person and the surface accounts that are theirs.</summary>
/// <param name="Id">Stable person id, the same on every surface.</param>
/// <param name="Accounts">Linked accounts, each <c>surface:senderId</c>.</param>
public sealed record Person(string Id, IReadOnlyList<string> Accounts);

/// <summary>
/// The people the agent talks to, all on equal footing, and the allowlist for remote surfaces:
/// a sender is let in only if one of these people holds their account. Accounts are linked here
/// explicitly, by whoever operates the host, never inferred from names.
/// </summary>
public sealed class PeopleRegistry
{
    /// <summary>The sender of a surface that cannot tell who is on the other end.</summary>
    public const string AnonymousSender = "local";

    private readonly Dictionary<string, Person> _byId;
    private readonly Dictionary<string, Person> _byAccount;

    public PeopleRegistry(IEnumerable<Person> people)
    {
        People = people.ToArray();
        _byId = new Dictionary<string, Person>(StringComparer.Ordinal);
        _byAccount = new Dictionary<string, Person>(StringComparer.Ordinal);
        foreach (var person in People)
        {
            if (!IsValidId(person.Id))
                throw new ArgumentException(
                    $"'{person.Id}' is not a valid person id: use lowercase letters, digits, '-' and '_', up to 32.", nameof(people));
            if (!_byId.TryAdd(person.Id, person))
                throw new ArgumentException($"Person '{person.Id}' appears more than once.", nameof(people));
            foreach (var account in person.Accounts)
            {
                if (account.EndsWith($":{AnonymousSender}", StringComparison.Ordinal))
                    throw new ArgumentException($"Account '{account}' is the unidentified sender, not a person's.", nameof(people));
                if (!_byAccount.TryAdd(account, person))
                    throw new ArgumentException($"Account '{account}' is linked to more than one person.", nameof(people));
            }
        }
    }

    public IReadOnlyList<Person> People { get; }

    public static PeopleRegistry Empty { get; } = new([]);

    /// <summary>
    /// Read a registry from JSON: an object keyed by person id, each with its <c>accounts</c>.
    /// A missing file gives <see cref="Empty"/>.
    /// </summary>
    public static PeopleRegistry Load(string path)
    {
        if (!File.Exists(path))
            return Empty;

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var people = document.RootElement.EnumerateObject().Select(entry => new Person(
            entry.Name,
            entry.Value.TryGetProperty("accounts", out var accounts)
                ? accounts.EnumerateArray().Select(a => a.GetString() ?? string.Empty).Where(a => a.Length > 0).ToArray()
                : []));
        return new PeopleRegistry(people);
    }

    /// <summary>
    /// Whether <paramref name="id"/> can name a person. Ids name files and conversations as they are,
    /// so they are kept to characters that mean the same on every file system.
    /// </summary>
    public static bool IsValidId(string id) =>
        id.Length is >= 1 and <= 32
        && id != AnonymousSender
        && (char.IsAsciiLetterLower(id[0]) || char.IsAsciiDigit(id[0]))
        && id.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '_');

    /// <summary>The person with this id, or null when there is none.</summary>
    public Person? Find(string id) => _byId.GetValueOrDefault(id);

    /// <summary>The person who holds this account, or null when nobody does.</summary>
    public Person? Resolve(string surface, string senderId) =>
        senderId == AnonymousSender ? null : _byAccount.GetValueOrDefault($"{surface}:{senderId}");
}
