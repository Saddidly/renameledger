using System.Text.Json;

namespace RenameLedger;

internal static class JsonSupport
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static T Read<T>(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<T>(json, Options)
                ?? throw new RenameLedgerException($"JSON document is empty: {path}");
        }
        catch (JsonException ex)
        {
            throw new RenameLedgerException($"Invalid JSON in {path}: {ex.Message}", ex);
        }
        catch (IOException ex)
        {
            throw new RenameLedgerException($"Cannot read {path}: {ex.Message}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new RenameLedgerException($"Cannot read {path}: {ex.Message}", ex);
        }
    }

    public static void Write(string path, object value)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".renameledger-plan-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, Options) + Environment.NewLine);
            File.Move(temporary, fullPath, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
