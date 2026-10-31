using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CloudAgent.Core.People;

/// <summary>A known person and the surface accounts that are theirs.</summary>
/// <param name="Id">Stable person id, the same on every surface.</param>
/// <param name="Owner">Whether this is the person the agent works for.</param>
/// <param name="Accounts">Linked accounts, each <c>surface:senderId</c>.</param>
public sealed record Person(string Id, bool Owner, IReadOnlyList<string> Accounts);

/// <summary>
/// The people the agent talks to, and the allowlist: a sender is let in only if one of these
/// people holds their account. Accounts are linked here explicitly, never inferred from names.
/// </summary>
public sealed class PeopleRegistry
{
    /// <summary>The account the console and an unproxied local web chat speak from.</summary>
    public const string LocalSender = "local";

    private readonly Dictionary<string, Person> _byAccount;

    public PeopleRegistry(IEnumerable<Person> people)
    {
        People = people.ToArray();
        if (People.Count(p => p.Owner) > 1)
            throw new ArgumentException("Only one person can be the owner.", nameof(people));

        _byAccount = new Dictionary<string, Person>(StringComparer.Ordinal);
        foreach (var person in People)
        foreach (var account in person.Accounts)
        {
            if (!_byAccount.TryAdd(account, person))
                throw new ArgumentException($"Account '{account}' is linked to more than one person.", nameof(people));
        }
    }

    public IReadOnlyList<Person> People { get; }

    /// <summary>
    /// The registry used when no file exists: an owner reachable only from this machine, so local
    /// development keeps working and no remote surface lets anyone in.
    /// </summary>
    public static PeopleRegistry LocalOnly() =>
        new([new Person("owner", Owner: true, [$"cli:{LocalSender}", $"webchat:{LocalSender}"])]);

    /// <summary>
    /// Read a registry from JSON: an object keyed by person id, each with <c>accounts</c> and an
    /// optional <c>owner</c> flag. A missing file gives <see cref="LocalOnly"/>.
    /// </summary>
    public static PeopleRegistry Load(string path)
    {
        if (!File.Exists(path))
            return LocalOnly();

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var people = document.RootElement.EnumerateObject().Select(entry => new Person(
            entry.Name,
            entry.Value.TryGetProperty("owner", out var owner) && owner.ValueKind == JsonValueKind.True,
            entry.Value.TryGetProperty("accounts", out var accounts)
                ? accounts.EnumerateArray().Select(a => a.GetString() ?? string.Empty).Where(a => a.Length > 0).ToArray()
                : []));
        return new PeopleRegistry(people);
    }

    /// <summary>The person who holds this account, or null when nobody does.</summary>
    public Person? Resolve(string surface, string senderId) =>
        _byAccount.GetValueOrDefault($"{surface}:{senderId}");
}
