using System.Text.Json;

namespace RenameLedger;

internal static class Program
{
    private const string Help = """
        RenameLedger — preview, apply, and undo batch file renames

        renameledger plan CONFIG.json --out PLAN.json [--json]
        renameledger apply PLAN.json --journal JOURNAL.jsonl
        renameledger undo JOURNAL.jsonl

        Planning never renames files. Apply refuses conflicts and changed files.
        Use a fresh plan output and a fresh journal path for every transaction.
        """;

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "--help" or "-h") { Console.WriteLine(Help); return 0; }
            if (args[0] == "--version") { Console.WriteLine("renameledger 1.0.0"); return 0; }
            if (args.Length < 2) throw new RenameLedgerException("A command and input path are required. Use --help.");
            var flags = ParseFlags(args.Skip(2).ToArray());
            switch (args[0])
            {
                case "plan":
                    EnsureFlags(flags, "--out", "--json");
                    var output = Required(flags, "--out");
                    var plan = PlanService.Create(args[1]);
                    if (Path.GetFullPath(output).Equals(Path.GetFullPath(args[1]), StringComparison.OrdinalIgnoreCase))
                        throw new RenameLedgerException("Plan output cannot replace the configuration.");
                    JsonSupport.Write(output, plan);
                    if (flags.ContainsKey("--json")) Console.WriteLine(JsonSerializer.Serialize(plan, JsonSupport.Options));
                    else
                    {
                        foreach (var action in plan.Actions)
                            Console.WriteLine($"{action.Status,-10} {action.Source} -> {action.Destination}{(action.Reason is null ? "" : $" ({action.Reason})")}");
                        Console.WriteLine($"Plan: {Path.GetFullPath(output)}; {plan.Actions.Count(item => item.Status == "planned")} renames, {plan.Actions.Count(item => item.Status == "conflict")} conflicts.");
                    }
                    return plan.Actions.Any(item => item.Status == "conflict") ? 3 : 0;
                case "apply":
                    EnsureFlags(flags, "--journal");
                    var journal = TransactionService.Apply(JsonSupport.Read<PlanDocument>(args[1]), Required(flags, "--journal"));
                    Console.WriteLine($"Applied. Journal: {journal}");
                    return 0;
                case "undo":
                    EnsureFlags(flags);
                    TransactionService.Undo(args[1]);
                    Console.WriteLine("Undo completed.");
                    return 0;
                default: throw new RenameLedgerException($"Unknown command: {args[0]}");
            }
        }
        catch (Exception error) when (error is RenameLedgerException or IOException or UnauthorizedAccessException or ArgumentException or System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            Console.Error.WriteLine($"renameledger: {error.Message}");
            return 2;
        }
    }

    private static Dictionary<string, string?> ParseFlags(string[] args)
    {
        var flags = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++)
        {
            var flag = args[index];
            if (!flag.StartsWith("--", StringComparison.Ordinal) || flags.ContainsKey(flag))
                throw new RenameLedgerException($"Invalid or repeated option: {flag}");
            string? value = null;
            if (flag != "--json")
            {
                if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
                    throw new RenameLedgerException($"Missing value for {flag}");
                value = args[index];
            }
            flags.Add(flag, value);
        }
        return flags;
    }

    private static string Required(Dictionary<string, string?> flags, string key) =>
        flags.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : throw new RenameLedgerException($"{key} is required.");

    private static void EnsureFlags(Dictionary<string, string?> flags, params string[] allowed)
    {
        foreach (var key in flags.Keys)
            if (!allowed.Contains(key)) throw new RenameLedgerException($"Unsupported option: {key}");
    }
}
