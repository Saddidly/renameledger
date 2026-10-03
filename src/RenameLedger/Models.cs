using System.Text.Json.Serialization;

namespace RenameLedger;

public sealed class RenameConfig
{
    public int Version { get; set; } = 1;
    public string Directory { get; set; } = ".";
    public bool Recursive { get; set; }
    public string Pattern { get; set; } = "*";
    public List<TransformSpec> Transforms { get; set; } = [];
}

public sealed class TransformSpec
{
    public string Type { get; set; } = "";
    public string? Value { get; set; }
    public string? Find { get; set; }
    public string? Replacement { get; set; }
    public string? Pattern { get; set; }
    public bool IgnoreCase { get; set; }
    public int Start { get; set; } = 1;
    public int Width { get; set; }
    public string Separator { get; set; } = "-";
    public string Placement { get; set; } = "prefix";
}

public sealed class PlanDocument
{
    public int Version { get; set; } = 1;
    public string Root { get; set; } = "";
    public bool Recursive { get; set; }
    public string Pattern { get; set; } = "*";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<PlanAction> Actions { get; set; } = [];
}

public class PlanAction
{
    public string Source { get; set; } = "";
    public string Destination { get; set; } = "";
    public long Length { get; set; }
    public long LastWriteUtcTicks { get; set; }
    public string Sha256 { get; set; } = "";
    public string Status { get; set; } = "planned";
    public string? Reason { get; set; }
}

public sealed class TransactionAction : PlanAction
{
    public string Stage { get; set; } = "";
}

public sealed class JournalRecord
{
    [JsonPropertyName("event")]
    public string Event { get; set; } = "";
    public string? Id { get; set; }
    public string? Root { get; set; }
    public string? Source { get; set; }
    public string? Destination { get; set; }
    public string? Stage { get; set; }
    public string? Path { get; set; }
    public string? Error { get; set; }
    public List<TransactionAction>? Actions { get; set; }
    public List<PlanAction>? Conflicts { get; set; }
}

public sealed class RenameLedgerException(string message, Exception? inner = null) : Exception(message, inner);
