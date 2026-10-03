using System.Security.Cryptography;

namespace RenameLedger;

internal static class FileSafety
{
    public static string Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    public static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');

    public static string Absolute(string root, string relative)
    {
        ValidateRelative(relative);
        var parts = relative.Split('/');
        var full = Path.GetFullPath(Path.Combine([root, .. parts]));
        var rootFull = Path.GetFullPath(root);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = rootFull.EndsWith(Path.DirectorySeparatorChar) ? rootFull : rootFull + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, comparison))
            throw new RenameLedgerException($"Path escapes the plan root: {relative}");

        var parent = Path.GetDirectoryName(full)!;
        var cursor = rootFull;
        var parentRelative = Path.GetRelativePath(rootFull, parent);
        if (parentRelative != ".")
        {
            foreach (var part in parentRelative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
            {
                cursor = Path.Combine(cursor, part);
                if (Directory.Exists(cursor) && IsReparsePoint(cursor))
                    throw new RenameLedgerException($"Path crosses a symbolic link or reparse point: {relative}");
            }
        }
        return full;
    }

    public static void ValidateRelative(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains('\\') || relative.Contains(':') ||
            relative.StartsWith('/') || Path.IsPathRooted(relative))
            throw new RenameLedgerException($"Unsafe relative path: {relative}");
        var parts = relative.Split('/');
        if (parts.Any(part => part.Length == 0 || part is "." or ".."))
            throw new RenameLedgerException($"Unsafe relative path: {relative}");
    }

    public static string? FindCaseInsensitiveOccupant(string absolutePath)
    {
        var parent = Path.GetDirectoryName(absolutePath)!;
        if (!Directory.Exists(parent))
            return null;
        var targetName = Path.GetFileName(absolutePath);
        foreach (var entry in Directory.EnumerateFileSystemEntries(parent))
        {
            if (StringComparer.OrdinalIgnoreCase.Equals(Path.GetFileName(entry), targetName))
                return entry;
        }
        return null;
    }

    public static bool SameRelativePath(string left, string right) =>
        StringComparer.Ordinal.Equals(left.Replace('\\', '/'), right.Replace('\\', '/'));
}
