using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace RenameLedger;

public static class PlanService
{
    private static readonly TimeSpan GlobTimeout = TimeSpan.FromMilliseconds(300);

    public static PlanDocument Create(string configPath)
    {
        var loaded = ConfigurationService.Load(configPath);
        var files = EnumerateFiles(loaded.Root, loaded.Config.Recursive)
            .Where(path => MatchesGlob(loaded.Config.Pattern, Path.GetFileName(path)))
            .OrderBy(path => FileSafety.Relative(loaded.Root, path), StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => FileSafety.Relative(loaded.Root, path), StringComparer.Ordinal)
            .ToList();

        var actions = new List<PlanAction>(files.Count);
        for (var index = 0; index < files.Count; index++)
        {
            var source = files[index];
            var info = new FileInfo(source);
            var relativeSource = FileSafety.Relative(loaded.Root, source);
            var extension = Path.GetExtension(source);
            var stem = Path.GetFileNameWithoutExtension(source);
            var transformed = ConfigurationService.TransformStem(stem, loaded.Config.Transforms, index);
            var destinationName = transformed + extension;
            var directory = Path.GetDirectoryName(relativeSource);
            var relativeDestination = string.IsNullOrEmpty(directory)
                ? destinationName
                : directory.Replace('\\', '/') + "/" + destinationName;
            actions.Add(new PlanAction
            {
                Source = relativeSource,
                Destination = relativeDestination,
                Length = info.Length,
                LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks,
                Sha256 = FileSafety.Hash(source),
                Status = FileSafety.SameRelativePath(relativeSource, relativeDestination) ? "unchanged" : "planned"
            });
        }

        MarkConflicts(loaded.Root, actions);
        return new PlanDocument
        {
            Root = loaded.Root,
            Recursive = loaded.Config.Recursive,
            Pattern = loaded.Config.Pattern,
            Actions = actions
        };
    }

    private static bool MatchesGlob(string pattern, string name)
    {
        var regex = "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return Regex.IsMatch(name, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, GlobTimeout);
    }

    private static List<string> EnumerateFiles(string root, bool recursive)
    {
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in System.IO.Directory.EnumerateFileSystemEntries(directory))
            {
                if (FileSafety.IsReparsePoint(entry))
                    continue;
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (recursive)
                        pending.Push(entry);
                }
                else
                {
                    files.Add(entry);
                }
            }
        }
        return files;
    }

    private static void MarkConflicts(string root, List<PlanAction> actions)
    {
        var planned = actions.Where(item => item.Status == "planned").ToList();
        var groups = planned.GroupBy(item => item.Destination, StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups.Where(group => group.Count() > 1))
        {
            foreach (var action in group)
                SetConflict(action, "multiple files would have the same destination (case-insensitive)");
        }

        var movingSources = planned.Select(item => item.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var action in planned.Where(item => item.Status == "planned"))
        {
            var destination = FileSafety.Absolute(root, action.Destination);
            var occupant = FileSafety.FindCaseInsensitiveOccupant(destination);
            if (occupant is null)
                continue;
            var occupantRelative = FileSafety.Relative(root, occupant);
            if (!movingSources.Contains(occupantRelative))
                SetConflict(action, $"destination already exists: {occupantRelative}");
        }
    }

    private static void SetConflict(PlanAction action, string reason)
    {
        action.Status = "conflict";
        action.Reason = reason;
    }

    public static string ValidateForApply(PlanDocument plan)
    {
        if (plan.Version != 1)
            throw new RenameLedgerException($"Unsupported plan version {plan.Version}.");
        if (string.IsNullOrWhiteSpace(plan.Root) || !System.IO.Directory.Exists(plan.Root))
            throw new RenameLedgerException("Plan root does not exist.");
        var root = Path.GetFullPath(plan.Root);
        if (FileSafety.IsReparsePoint(root))
            throw new RenameLedgerException("Plan root cannot be a symbolic link or reparse point.");
        if (plan.Actions is null)
            throw new RenameLedgerException("Plan actions are missing.");

        var sourceKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var action in plan.Actions)
        {
            FileSafety.ValidateRelative(action.Source);
            FileSafety.ValidateRelative(action.Destination);
            if (!StringComparer.Ordinal.Equals(Path.GetDirectoryName(action.Source.Replace('/', Path.DirectorySeparatorChar)),
                    Path.GetDirectoryName(action.Destination.Replace('/', Path.DirectorySeparatorChar))) &&
                !StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(action.Source.Replace('/', Path.DirectorySeparatorChar)),
                    Path.GetDirectoryName(action.Destination.Replace('/', Path.DirectorySeparatorChar))))
                throw new RenameLedgerException($"A rename plan cannot move a file to another directory: {action.Source} -> {action.Destination}");
            if (!sourceKeys.Add(action.Source))
                throw new RenameLedgerException($"Duplicate source in plan: {action.Source}");
            if (action.Status is not ("planned" or "unchanged" or "conflict"))
                throw new RenameLedgerException($"Unknown action status: {action.Status}");
            if (action.Status == "planned" && (action.Length < 0 || action.LastWriteUtcTicks < 0 ||
                action.Sha256.Length != 64 || !action.Sha256.All(Uri.IsHexDigit)))
                throw new RenameLedgerException($"Plan preconditions are incomplete for {action.Source}");
        }

        var conflicts = plan.Actions.Where(item => item.Status == "conflict").ToList();
        if (conflicts.Count > 0)
            throw new RenameLedgerException($"Plan contains {conflicts.Count} conflict(s); edit the rule or resolve them before applying.");

        var moving = plan.Actions.Where(item => item.Status == "planned").ToList();
        var movingSources = moving.Select(item => item.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var action in moving)
        {
            if (!destinations.Add(action.Destination))
                throw new RenameLedgerException($"Duplicate destination in plan: {action.Destination}");
            var source = FileSafety.Absolute(root, action.Source);
            if (!File.Exists(source) || FileSafety.IsReparsePoint(source))
                throw new RenameLedgerException($"Source is missing or is a reparse point: {action.Source}");
            var info = new FileInfo(source);
            if (info.Length != action.Length || info.LastWriteTimeUtc.Ticks != action.LastWriteUtcTicks ||
                !StringComparer.Ordinal.Equals(FileSafety.Hash(source), action.Sha256))
                throw new RenameLedgerException($"Source changed after planning: {action.Source}");
            var destination = FileSafety.Absolute(root, action.Destination);
            var occupant = FileSafety.FindCaseInsensitiveOccupant(destination);
            if (occupant is not null && !movingSources.Contains(FileSafety.Relative(root, occupant)))
                throw new RenameLedgerException($"Destination became occupied after planning: {action.Destination}");
        }
        return root;
    }
}
