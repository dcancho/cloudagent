using System.Globalization;
using CloudAgent.Core.Memory;
using CloudAgent.Core.People;
using CloudAgent.Core.Providers;
using CloudAgent.Host.Configuration;

namespace CloudAgent.Host.Admin;

/// <summary>
/// Operating the host from its own machine: who is let in, what it has spent, how it is doing.
/// Whoever can run these already has access to the machine, which is the only authority they need;
/// nothing here is reachable from a conversation.
/// </summary>
public static class AdminCommands
{
    private const string DefaultEnvFile = "/etc/cloudagent/cloudagent.env";
    private const string RestartHint = "The running host reads the people file at startup: sudo systemctl restart cloudagent";

    public const string Usage = """
        Usage: CloudAgent.Host admin [--env-file <path>] <command>

          people list                      Everyone in the people file and their accounts
          people add <id>                  Add a person with no accounts
          people link <id> <account>       Link an account, written surface:senderId
          people unlink <account>          Remove an account from whoever holds it
          people remove <id>               Take a person out; their memory and conversations stay
          people who <account>             Who holds this account
          people refused [--days <n>]      Accounts turned away in the last n days (default 7)
          status                           Provider, surfaces, people, budget, ledger, spending today
          usage [--since 24h|7d|all]       Model calls by purpose, model and agent, with cost
        """;

    /// <summary>Run one command and return the process exit code.</summary>
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            var (rest, envFile) = TakeOption(args, "--env-file");
            LoadEnvironmentFile(envFile ?? (File.Exists(DefaultEnvFile) ? DefaultEnvFile : null));
            var config = HostConfig.FromEnvironment();

            return rest switch
            {
                ["people", "list"] => PeopleList(config, output),
                ["people", "add", var id] => Edit(config, output, f => $"Added {f.Add(id).Id}."),
                ["people", "link", var id, var account] => Edit(config, output, f => $"{id}: {string.Join(", ", f.Link(id, account).Accounts)}"),
                ["people", "unlink", var account] => Edit(config, output, f => $"{f.Unlink(account).Id} no longer has {account}."),
                ["people", "remove", var id] => Edit(config, output, f => { f.Remove(id); return $"Removed {id}."; }),
                ["people", "who", var account] => PeopleWho(config, account, output),
                ["people", "refused", ..var options] => PeopleRefused(config, options, output),
                ["status"] => Status(config, output),
                ["usage", ..var options] => UsageCommand(config, options, output),
                _ => Fail(error, Usage, 2),
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException or FormatException)
        {
            return Fail(error, ex.Message, 1);
        }
    }

    private static int Fail(TextWriter error, string message, int code)
    {
        error.WriteLine(message);
        return code;
    }

    private static int PeopleList(HostConfig config, TextWriter output)
    {
        var people = new PeopleFile(config.PeoplePath).List();
        if (people.Count == 0)
            output.WriteLine($"Nobody is in {config.PeoplePath}.");
        foreach (var person in people)
            output.WriteLine($"{person.Id,-16}{(person.Accounts.Count == 0 ? "(no accounts)" : string.Join("  ", person.Accounts))}");
        return 0;
    }

    private static int Edit(HostConfig config, TextWriter output, Func<PeopleFile, string> change)
    {
        output.WriteLine(change(new PeopleFile(config.PeoplePath)));
        output.WriteLine(RestartHint);
        return 0;
    }

    private static int PeopleWho(HostConfig config, string account, TextWriter output)
    {
        var separator = account.IndexOf(':');
        var holder = separator > 0
            ? PeopleRegistry.Load(config.PeoplePath).Resolve(account[..separator], account[(separator + 1)..])
            : null;
        output.WriteLine(holder is null ? $"Nobody holds {account}: the host turns it away." : $"{account} is {holder.Id}.");
        return 0;
    }

    private static int PeopleRefused(HostConfig config, string[] options, TextWriter output)
    {
        var (_, days) = TakeOption(options, "--days");
        var window = int.TryParse(days, out var n) && n > 0 ? n : 7;
        var today = DateOnly.FromDateTime(DateTime.Now);
        var logs = Path.Combine(config.WorkspaceRoot, ".agent", "logs");

        var lines = new List<(DateOnly, string)>();
        foreach (var file in Directory.Exists(logs) ? Directory.GetFiles(logs, "host-*.log") : [])
        {
            var name = Path.GetFileNameWithoutExtension(file)["host-".Length..];
            if (DateOnly.TryParseExact(name, "yyyy-MM-dd", out var day) && day >= today.AddDays(1 - window))
                lines.AddRange(File.ReadLines(file).Select(l => (day, l)));
        }

        var registry = PeopleRegistry.Load(config.PeoplePath);
        var refusals = RefusalLog.Parse(lines);
        if (refusals.Count == 0)
            output.WriteLine($"Nobody was turned away in the last {window} day(s).");
        foreach (var refusal in refusals)
        {
            var separator = refusal.Account.IndexOf(':');
            var holder = registry.Resolve(refusal.Account[..separator], refusal.Account[(separator + 1)..]);
            output.WriteLine(
                $"{refusal.Account,-32}{refusal.Count,4}x  last {refusal.LastDay:yyyy-MM-dd}  " +
                $"{refusal.Name ?? "",-20}{(holder is null ? "" : $"now linked to {holder.Id}")}");
        }
        return 0;
    }

    private static int Status(HostConfig config, TextWriter output)
    {
        var agent = Path.Combine(config.WorkspaceRoot, ".agent");
        var surfaces = new List<string>();
        if (config.WebchatEnabled) surfaces.Add("webchat");
        if (config.TelegramBotToken is not null) surfaces.Add("telegram");
        if (config.DiscordBotToken is not null) surfaces.Add("discord");

        output.WriteLine($"Provider:   {config.Llm.Provider}, main '{config.Llm.Model}', background '{config.Llm.SmallModel}'");
        output.WriteLine($"Routing:    {config.ContextRouting}");
        output.WriteLine($"Surfaces:   {(surfaces.Count == 0 ? "none" : string.Join(", ", surfaces))}");
        output.WriteLine($"People:     {string.Join(", ", new PeopleFile(config.PeoplePath).List().Select(p => p.Id))}");

        if (config.TokenBudget is { } limit)
        {
            var budget = new TokenBudget(limit, Path.Combine(agent, "usage.json"));
            output.WriteLine($"Budget:     {budget.Used:N0} of {limit:N0} tokens ({100.0 * budget.Used / limit:0.##}%)");
        }
        else
        {
            output.WriteLine("Budget:     no limit");
        }

        var ledgerPath = Path.Combine(agent, "memory", "consolidation.jsonl");
        var chain = File.Exists(ledgerPath) ? (new ConsolidationLedger(ledgerPath).Verify() ? "OK" : "BROKEN") : "empty";
        var pending = new PendingConsolidations(Path.Combine(agent, "memory", "pending")).List().Count;
        output.WriteLine($"Memory:     ledger chain {chain}; {pending} pending proposal(s)");

        var since = DateTimeOffset.UtcNow.AddHours(-24);
        var recent = ReadUsage(config, since);
        var report = Core.Providers.UsageReport.Build(recent, ModelPrices.Parse(config.ModelPrices));
        output.WriteLine($"Last 24h:   {Describe(report.Total)}");
        return 0;
    }

    private static int UsageCommand(HostConfig config, string[] options, TextWriter output)
    {
        var (_, since) = TakeOption(options, "--since");
        var from = ParseSince(since);
        var prices = ModelPrices.Parse(config.ModelPrices);
        var report = Core.Providers.UsageReport.Build(ReadUsage(config, from), prices);

        if (report.Total.Calls == 0)
        {
            output.WriteLine("No model calls recorded in that period.");
            return 0;
        }

        output.WriteLine($"Total       {Describe(report.Total)}");
        Table(output, "By purpose", report.ByPurpose);
        Table(output, "By model", report.ByModel);
        Table(output, "By agent", report.ByAgent);

        var unpriced = report.ByModel.Where(m => m.Totals.Unpriced).Select(m => m.Key).ToList();
        if (unpriced.Count > 0)
            output.WriteLine($"\nNo price for {string.Join(", ", unpriced.Select(m => $"'{m}'"))}: costs are a floor. " +
                             "Set MODEL_PRICES, for example MODEL_PRICES='model=0.10/0.25' (dollars per million tokens, input/output).");
        return 0;
    }

    private static void Table(TextWriter output, string title, IReadOnlyList<UsageGroup> groups)
    {
        output.WriteLine($"\n{title}");
        foreach (var group in groups)
        {
            var t = group.Totals;
            output.WriteLine(
                $"  {Clip(group.Key, 34),-35}{t.Calls,6} calls{t.InputTokens,12:N0} in{t.OutputTokens,10:N0} out" +
                $"{Money(t),12}{(t.Calls == 0 ? "" : $"{t.Tokens / t.Calls,9:N0} tokens/call")}" +
                $"{(t.Failed > 0 ? $"  {t.Failed} failed" : "")}");
        }
    }

    private static string Describe(UsageTotals t) =>
        $"{t.Calls:N0} calls, {t.InputTokens:N0} in, {t.OutputTokens:N0} out, {Money(t).Trim()}" +
        (t.Failed > 0 ? $", {t.Failed} failed" : "");

    private static string Money(UsageTotals t) =>
        t.Cost == 0m && t.Unpriced ? "unpriced" : $"${t.Cost.ToString("0.0000", CultureInfo.InvariantCulture)}{(t.Unpriced ? "+" : "")}";

    private static string Clip(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "~";

    private static IReadOnlyList<UsageRecord> ReadUsage(HostConfig config, DateTimeOffset? since)
    {
        var records = new UsageLedger(Path.Combine(config.WorkspaceRoot, ".agent", "usage.jsonl")).Read();
        return since is null ? records : records.Where(r => r.At >= since).ToList();
    }

    private static DateTimeOffset? ParseSince(string? value)
    {
        if (value is null or "all")
            return null;

        if (value.Length >= 2 && int.TryParse(value[..^1], out var amount) && amount > 0)
        {
            switch (value[^1])
            {
                case 'h': return DateTimeOffset.UtcNow.AddHours(-amount);
                case 'd': return DateTimeOffset.UtcNow.AddDays(-amount);
            }
        }
        throw new FormatException($"--since: '{value}' is not a period. Use 24h, 7d or all.");
    }

    private static (string[] Remaining, string? Value) TakeOption(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        if (index < 0)
            return (args, null);
        if (index + 1 >= args.Length)
            throw new FormatException($"{name} needs a value.");
        return (args.Where((_, i) => i != index && i != index + 1).ToArray(), args[index + 1]);
    }

    private static void LoadEnvironmentFile(string? path)
    {
        if (path is null)
            return;

        foreach (var line in File.ReadLines(path))
        {
            var text = line.Trim();
            var equals = text.IndexOf('=');
            if (text.Length == 0 || text[0] == '#' || equals <= 0)
                continue;

            var key = text[..equals].Trim();
            if (Environment.GetEnvironmentVariable(key) is null)
                Environment.SetEnvironmentVariable(key, text[(equals + 1)..].Trim());
        }
    }
}
