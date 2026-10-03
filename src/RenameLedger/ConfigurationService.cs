using System.Text.RegularExpressions;

namespace RenameLedger;

internal sealed record LoadedConfig(RenameConfig Config, string Root);

internal static class ConfigurationService
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    public static LoadedConfig Load(string path)
    {
        var configPath = Path.GetFullPath(path);
        var config = JsonSupport.Read<RenameConfig>(configPath);
        if (config.Version != 1)
            throw new RenameLedgerException($"Unsupported config version {config.Version}; expected 1.");
        if (string.IsNullOrWhiteSpace(config.Directory))
            throw new RenameLedgerException("Config directory must be a non-empty path.");
        if (string.IsNullOrWhiteSpace(config.Pattern))
            throw new RenameLedgerException("Config pattern must be a non-empty glob.");
        if (config.Transforms is null)
            throw new RenameLedgerException("Config transforms must be an array.");

        var configDirectory = Path.GetDirectoryName(configPath)!;
        var root = Path.GetFullPath(Path.IsPathRooted(config.Directory)
            ? config.Directory
            : Path.Combine(configDirectory, config.Directory));
        if (!System.IO.Directory.Exists(root))
            throw new RenameLedgerException($"Source directory does not exist: {root}");
        if (FileSafety.IsReparsePoint(root))
            throw new RenameLedgerException("The configured source directory cannot be a symbolic link or reparse point.");

        foreach (var transform in config.Transforms)
            ValidateTransform(transform);
        return new LoadedConfig(config, root);
    }

    private static void ValidateTransform(TransformSpec transform)
    {
        if (transform is null || string.IsNullOrWhiteSpace(transform.Type))
            throw new RenameLedgerException("Every transform needs a non-empty type.");
        var type = transform.Type.Trim().ToLowerInvariant();
        switch (type)
        {
            case "prefix":
            case "suffix":
                if (transform.Value is null)
                    throw new RenameLedgerException($"{type} transform requires value.");
                break;
            case "replace":
                if (string.IsNullOrEmpty(transform.Find))
                    throw new RenameLedgerException("replace transform requires a non-empty find value.");
                break;
            case "regex":
                if (string.IsNullOrEmpty(transform.Pattern))
                    throw new RenameLedgerException("regex transform requires pattern.");
                try
                {
                    _ = new Regex(transform.Pattern, transform.IgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None, RegexTimeout);
                }
                catch (ArgumentException ex)
                {
                    throw new RenameLedgerException($"Invalid regex transform: {ex.Message}", ex);
                }
                break;
            case "numbering":
                if (transform.Start < 0 || transform.Width is < 0 or > 20)
                    throw new RenameLedgerException("numbering start must be non-negative and width must be between 0 and 20.");
                if (transform.Placement is not ("prefix" or "suffix"))
                    throw new RenameLedgerException("numbering placement must be prefix or suffix.");
                break;
            default:
                throw new RenameLedgerException($"Unknown transform type: {transform.Type}");
        }
    }

    public static string TransformStem(string stem, IReadOnlyList<TransformSpec> transforms, int fileIndex)
    {
        var result = stem;
        foreach (var transform in transforms)
        {
            switch (transform.Type.Trim().ToLowerInvariant())
            {
                case "prefix":
                    result = transform.Value + result;
                    break;
                case "suffix":
                    result += transform.Value;
                    break;
                case "replace":
                    result = result.Replace(
                        transform.Find!,
                        transform.Replacement ?? "",
                        transform.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
                    break;
                case "regex":
                    var options = transform.IgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None;
                    result = Regex.Replace(result, transform.Pattern!, transform.Replacement ?? "", options, RegexTimeout);
                    break;
                case "numbering":
                    var digits = ((long)transform.Start + fileIndex).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    if (transform.Width > 0)
                        digits = digits.PadLeft(transform.Width, '0');
                    result = transform.Placement == "suffix"
                        ? result + transform.Separator + digits
                        : digits + transform.Separator + result;
                    break;
            }
        }
        if (result is "." or ".." || result.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new RenameLedgerException($"Transforms produced an invalid filename stem: {result}");
        if (string.IsNullOrWhiteSpace(result))
            throw new RenameLedgerException("Transforms produced an empty filename.");
        return result;
    }
}
