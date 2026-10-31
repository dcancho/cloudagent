using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CloudAgent.Core.People;

/// <summary>
/// Edits the people file on behalf of whoever operates the host. Every change is checked by
/// building the registry the host would load from it, so a file this class writes always loads.
/// The previous file is kept next to it as <c>.bak</c>.
/// </summary>
public sealed partial class PeopleFile(string path)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string Path { get; } = path;

    /// <summary>Whether <paramref name="account"/> reads as <c>surface:senderId</c>.</summary>
    public static bool IsValidAccount(string account) => AccountPattern().IsMatch(account);

    /// <summary>Everyone in the file, in file order.</summary>
    public IReadOnlyList<Person> List() => PeopleRegistry.Load(Path).People;

    /// <summary>Add a person with no accounts.</summary>
    public Person Add(string id)
    {
        var people = List().ToList();
        if (people.Any(p => p.Id == id))
            throw new InvalidOperationException($"'{id}' is already in the file.");

        var person = new Person(id, []);
        people.Add(person);
        Save(people);
        return person;
    }

    /// <summary>Link <paramref name="account"/> to the person called <paramref name="id"/>.</summary>
    public Person Link(string id, string account)
    {
        if (!IsValidAccount(account))
            throw new ArgumentException($"'{account}' is not an account: write it as surface:senderId, for example discord:123456789.");

        var people = List().ToList();
        var index = IndexOf(people, id);
        if (people.FirstOrDefault(p => p.Accounts.Contains(account)) is { } holder)
            throw new InvalidOperationException(
                holder.Id == id ? $"'{id}' already has {account}." : $"{account} already belongs to '{holder.Id}'.");

        people[index] = people[index] with { Accounts = [.. people[index].Accounts, account] };
        Save(people);
        return people[index];
    }

    /// <summary>Remove <paramref name="account"/> from whoever holds it and return that person as they are now.</summary>
    public Person Unlink(string account)
    {
        var people = List().ToList();
        var index = people.FindIndex(p => p.Accounts.Contains(account));
        if (index < 0)
            throw new InvalidOperationException($"Nobody holds {account}.");

        people[index] = people[index] with { Accounts = people[index].Accounts.Where(a => a != account).ToArray() };
        Save(people);
        return people[index];
    }

    /// <summary>Take the person out of the file. Their memory and conversations stay where they are.</summary>
    public void Remove(string id)
    {
        var people = List().ToList();
        people.RemoveAt(IndexOf(people, id));
        Save(people);
    }

    private static int IndexOf(List<Person> people, string id)
    {
        var index = people.FindIndex(p => p.Id == id);
        return index >= 0 ? index : throw new InvalidOperationException($"Nobody called '{id}' is in the file.");
    }

    private void Save(IEnumerable<Person> people)
    {
        var all = people.ToArray();
        _ = new PeopleRegistry(all);

        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!;
        Directory.CreateDirectory(directory);
        if (File.Exists(Path))
        {
            var backup = Path + ".bak";
            if (File.Exists(backup))
                File.Delete(backup);
            File.Copy(Path, backup);
        }

        var document = all.ToDictionary(p => p.Id, p => new { accounts = p.Accounts });
        var temporary = Path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(document, JsonOptions) + Environment.NewLine);
        File.Move(temporary, Path, overwrite: true);
    }

    [GeneratedRegex(@"^[a-z][a-z0-9_-]*:\S+$")]
    private static partial Regex AccountPattern();
}
