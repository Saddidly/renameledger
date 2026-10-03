using System.Text.Json;
using RenameLedger;

var failures = 0;
var tests = new (string Name, Action<string> Run)[]
{
    ("preview/apply/undo with numbering", root =>
    {
        File.WriteAllText(Path.Combine(root, "photo.txt"), "photo");
        var plan = Plan(root, """[{"type":"prefix","value":"archive-"},{"type":"numbering","width":3}]""");
        Check(File.Exists(Path.Combine(root, "photo.txt")), "Preview mutated source");
        Check(plan.Actions[0].Destination == "001-archive-photo.txt", "Incorrect composed name");
        var journal = TransactionService.Apply(plan, Path.Combine(root, "journal.jsonl"));
        Check(File.ReadAllText(Path.Combine(root, "001-archive-photo.txt")) == "photo", "Apply lost content");
        foreach (var line in File.ReadLines(journal)) using (JsonDocument.Parse(line)) { }
        TransactionService.Undo(journal);
        Check(File.ReadAllText(Path.Combine(root, "photo.txt")) == "photo", "Undo lost content");
        Reject(() => TransactionService.Undo(journal));
    }),
    ("changed source refused before mutation", root =>
    {
        var file = Path.Combine(root, "one.txt"); File.WriteAllText(file, "before");
        var plan = Plan(root, """[{"type":"prefix","value":"x-"}]""");
        File.WriteAllText(file, "after");
        Reject(() => TransactionService.Apply(plan, Path.Combine(root, "journal.jsonl")));
        Check(File.ReadAllText(file) == "after", "Rejected apply changed source");
    }),
    ("destination collisions are visible", root =>
    {
        File.WriteAllText(Path.Combine(root, "a.txt"), "A"); File.WriteAllText(Path.Combine(root, "b.txt"), "B");
        var plan = Plan(root, """[{"type":"regex","pattern":".*","replacement":"same"}]""");
        Check(plan.Actions.All(item => item.Status == "conflict"), "Collision was not reported");
        Reject(() => TransactionService.Apply(plan, Path.Combine(root, "journal.jsonl")));
    }),
    ("staging supports name swaps", root =>
    {
        File.WriteAllText(Path.Combine(root, "a.txt"), "A"); File.WriteAllText(Path.Combine(root, "b.txt"), "B");
        var plan = Plan(root, "[]");
        foreach (var action in plan.Actions) { action.Status = "planned"; action.Destination = action.Source == "a.txt" ? "b.txt" : "a.txt"; }
        var journal = TransactionService.Apply(plan, Path.Combine(root, "journal.jsonl"));
        Check(File.ReadAllText(Path.Combine(root, "a.txt")) == "B", "Swap did not commit");
        TransactionService.Undo(journal);
        Check(File.ReadAllText(Path.Combine(root, "a.txt")) == "A", "Swap undo did not restore");
    }),
    ("case-only rename works", root =>
    {
        File.WriteAllText(Path.Combine(root, "photo.txt"), "content");
        var plan = Plan(root, """[{"type":"replace","find":"photo","replacement":"PHOTO"}]""");
        var journal = TransactionService.Apply(plan, Path.Combine(root, "journal.jsonl"));
        Check(Directory.GetFiles(root).Any(path => Path.GetFileName(path) == "PHOTO.txt"), "Case-only rename lost case");
        TransactionService.Undo(journal);
    }),
    ("modified destination blocks undo", root =>
    {
        File.WriteAllText(Path.Combine(root, "a.txt"), "A");
        var plan = Plan(root, """[{"type":"prefix","value":"x-"}]""");
        var journal = TransactionService.Apply(plan, Path.Combine(root, "journal.jsonl"));
        File.WriteAllText(Path.Combine(root, "x-a.txt"), "edited");
        Reject(() => TransactionService.Undo(journal));
        Check(File.ReadAllText(Path.Combine(root, "x-a.txt")) == "edited", "Undo overwrote changed file");
    }),
    ("existing journal is never appended by apply", root =>
    {
        File.WriteAllText(Path.Combine(root, "a.txt"), "A");
        var plan = Plan(root, """[{"type":"prefix","value":"x-"}]""");
        var journal = Path.Combine(root, "journal.jsonl"); File.WriteAllText(journal, "keep");
        Reject(() => TransactionService.Apply(plan, journal));
        Check(File.ReadAllText(journal) == "keep", "Existing journal was changed");
        Check(File.Exists(Path.Combine(root, "a.txt")), "Rejected transaction moved source");
    }),
    ("path escape in edited plan is refused", root =>
    {
        File.WriteAllText(Path.Combine(root, "a.txt"), "A");
        var plan = Plan(root, """[{"type":"prefix","value":"x-"}]""");
        plan.Actions[0].Destination = "../escape.txt";
        Reject(() => TransactionService.Apply(plan, Path.Combine(root, "journal.jsonl")));
    }),
};
foreach (var (name, test) in tests)
{
    var root = Path.Combine(Path.GetTempPath(), "renameledger-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try { test(root); Console.WriteLine($"PASS {name}"); }
    catch (Exception error) { failures++; Console.Error.WriteLine($"FAIL {name}: {error}"); }
    finally { Directory.Delete(root, recursive: true); }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} behavioral checks passed.");
return failures == 0 ? 0 : 1;

static PlanDocument Plan(string root, string transforms)
{
    var config = Path.Combine(root, "rules.json");
    File.WriteAllText(config, $$"""{"directory":{{JsonSerializer.Serialize(root)}},"pattern":"*.txt","transforms":{{transforms}}} """);
    return PlanService.Create(config);
}
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Reject(Action action)
{
    try { action(); } catch (Exception error) when (error is RenameLedgerException or IOException) { return; }
    throw new Exception("Expected an operation to be rejected");
}
