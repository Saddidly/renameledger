using System.Text;
using System.Text.Json;

namespace RenameLedger;

public static class TransactionService
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    public static string Apply(PlanDocument plan, string journalPath)
    {
        var root = PlanService.ValidateForApply(plan);
        var actions = plan.Actions.Where(item => item.Status == "planned").ToList();
        if (actions.Count == 0)
            throw new RenameLedgerException("Plan contains no file renames.");

        var id = Guid.NewGuid().ToString("N");
        var operations = actions.Select((action, index) =>
        {
            var sourcePath = FileSafety.Absolute(root, action.Source);
            var stagePath = Path.Combine(Path.GetDirectoryName(sourcePath)!, $".renameledger-{id}-{index:D5}.stage");
            if (FileSafety.FindCaseInsensitiveOccupant(stagePath) is not null)
                throw new RenameLedgerException($"Temporary staging name is already occupied: {stagePath}");
            return new TransactionAction
            {
                Source = action.Source,
                Destination = action.Destination,
                Length = action.Length,
                LastWriteUtcTicks = action.LastWriteUtcTicks,
                Sha256 = action.Sha256,
                Status = action.Status,
                Reason = action.Reason,
                Stage = FileSafety.Relative(root, stagePath)
            };
        }).ToList();

        using var journal = new JournalWriter(journalPath, createNew: true);
        journal.Append(new JournalRecord { Event = "transaction_started", Id = id, Root = root, Actions = operations });
        var staged = new HashSet<string>(PathComparer);
        var committed = new HashSet<string>(PathComparer);
        try
        {
            foreach (var operation in operations)
            {
                VerifyCurrentFile(root, operation.Source, operation);
                File.Move(FileSafety.Absolute(root, operation.Source), FileSafety.Absolute(root, operation.Stage));
                staged.Add(operation.Source);
                journal.Append(new JournalRecord { Event = "source_staged", Source = operation.Source, Stage = operation.Stage });
            }

            foreach (var operation in operations)
            {
                var destination = FileSafety.Absolute(root, operation.Destination);
                if (FileSafety.FindCaseInsensitiveOccupant(destination) is not null)
                    throw new RenameLedgerException($"Destination became occupied during apply: {operation.Destination}");
                File.Move(FileSafety.Absolute(root, operation.Stage), destination);
                committed.Add(operation.Source);
                journal.Append(new JournalRecord { Event = "destination_written", Source = operation.Source, Destination = operation.Destination });
            }

            journal.Append(new JournalRecord { Event = "transaction_applied" });
            return Path.GetFullPath(journalPath);
        }
        catch (Exception ex)
        {
            journal.Append(new JournalRecord { Event = "apply_failed", Error = ex.Message });
            var rollbackComplete = RollbackApply(root, operations, staged, committed, journal);
            journal.Append(new JournalRecord { Event = rollbackComplete ? "rollback_complete" : "rollback_incomplete", Error = ex.Message });
            if (!rollbackComplete)
                throw new RenameLedgerException($"Apply failed and rollback was incomplete; inspect {Path.GetFullPath(journalPath)}: {ex.Message}", ex);
            throw new RenameLedgerException($"Apply failed; original files were restored: {ex.Message}", ex);
        }
    }

    public static void Undo(string journalPath)
    {
        var path = Path.GetFullPath(journalPath);
        var rows = JournalWriter.Read(path);
        var started = rows.FirstOrDefault(row => row.Event == "transaction_started")
            ?? throw new RenameLedgerException("Journal does not contain a transaction_started event.");
        if (!rows.Any(row => row.Event == "transaction_applied"))
            throw new RenameLedgerException("Journal does not describe a completed apply.");
        if (rows.Any(row => row.Event == "undo_complete"))
            throw new RenameLedgerException("This transaction has already been undone.");
        if (rows.Any(row => row.Event == "undo_started"))
            throw new RenameLedgerException("A previous undo attempt started; inspect the journal and filesystem before retrying.");
        var root = started.Root is null ? "" : Path.GetFullPath(started.Root);
        if (!Directory.Exists(root) || started.Actions is null || started.Actions.Count == 0)
            throw new RenameLedgerException("Journal root or transaction actions are unavailable.");
        if (FileSafety.IsReparsePoint(root)) throw new RenameLedgerException("Journal root is a reparse point.");

        var operations = started.Actions;
        if (operations.Select(item => item.Source).Distinct(PathComparer).Count() != operations.Count ||
            operations.Select(item => item.Destination).Distinct(PathComparer).Count() != operations.Count)
            throw new RenameLedgerException("Journal contains duplicate source or destination paths.");
        var destinationKeys = operations.Select(item => item.Destination).ToHashSet(PathComparer);
        foreach (var operation in operations)
        {
            FileSafety.ValidateRelative(operation.Source);
            FileSafety.ValidateRelative(operation.Destination);
            var current = FileSafety.Absolute(root, operation.Destination);
            VerifyCurrentFile(root, operation.Destination, operation);
            var original = FileSafety.Absolute(root, operation.Source);
            var occupant = FileSafety.FindCaseInsensitiveOccupant(original);
            if (occupant is not null && !destinationKeys.Contains(FileSafety.Relative(root, occupant)))
                throw new RenameLedgerException($"Original name is occupied by an unrelated file: {operation.Source}");
        }

        var undoId = Guid.NewGuid().ToString("N");
        var undoStages = new Dictionary<string, string>(PathComparer);
        for (var index = 0; index < operations.Count; index++)
        {
            var operation = operations[index];
            var current = FileSafety.Absolute(root, operation.Destination);
            var temporary = Path.Combine(Path.GetDirectoryName(current)!, $".renameledger-{undoId}-{index:D5}.undo");
            if (FileSafety.FindCaseInsensitiveOccupant(temporary) is not null)
                throw new RenameLedgerException($"Temporary undo name is already occupied: {temporary}");
            undoStages.Add(operation.Source, FileSafety.Relative(root, temporary));
        }

        using var journal = new JournalWriter(path);
        journal.Append(new JournalRecord { Event = "undo_started", Id = undoId });
        var staged = new HashSet<string>(PathComparer);
        var restored = new HashSet<string>(PathComparer);
        try
        {
            foreach (var operation in operations)
            {
                var stage = undoStages[operation.Source];
                File.Move(FileSafety.Absolute(root, operation.Destination), FileSafety.Absolute(root, stage));
                staged.Add(operation.Source);
                journal.Append(new JournalRecord { Event = "undo_file_staged", Destination = operation.Destination, Stage = stage });
            }
            foreach (var operation in operations)
            {
                var original = FileSafety.Absolute(root, operation.Source);
                if (FileSafety.FindCaseInsensitiveOccupant(original) is not null)
                    throw new RenameLedgerException($"Original name became occupied during undo: {operation.Source}");
                File.Move(FileSafety.Absolute(root, undoStages[operation.Source]), original);
                restored.Add(operation.Source);
                journal.Append(new JournalRecord { Event = "original_restored", Source = operation.Source });
            }
            journal.Append(new JournalRecord { Event = "undo_complete" });
        }
        catch (Exception ex)
        {
            var rollbackComplete = RollbackUndo(root, operations, undoStages, staged, restored, journal);
            journal.Append(new JournalRecord { Event = rollbackComplete ? "undo_rollback_complete" : "undo_rollback_incomplete", Error = ex.Message });
            if (!rollbackComplete)
                throw new RenameLedgerException($"Undo failed and rollback was incomplete; inspect {path}: {ex.Message}", ex);
            throw new RenameLedgerException($"Undo failed; applied names were restored: {ex.Message}", ex);
        }
    }

    private static void VerifyCurrentFile(string root, string relative, PlanAction expected)
    {
        var path = FileSafety.Absolute(root, relative);
        if (!File.Exists(path) || FileSafety.IsReparsePoint(path))
            throw new RenameLedgerException($"File is missing or is a reparse point: {relative}");
        var info = new FileInfo(path);
        if (info.Length != expected.Length ||
            !StringComparer.Ordinal.Equals(FileSafety.Hash(path), expected.Sha256))
            throw new RenameLedgerException($"File content changed since planning: {relative}");
    }

    private static bool RollbackApply(
        string root,
        List<TransactionAction> operations,
        HashSet<string> staged,
        HashSet<string> committed,
        JournalWriter journal)
    {
        var complete = true;
        foreach (var operation in operations.AsEnumerable().Reverse())
        {
            if (!committed.Contains(operation.Source))
                continue;
            try
            {
                var destination = FileSafety.Absolute(root, operation.Destination);
                var stage = FileSafety.Absolute(root, operation.Stage);
                if (File.Exists(destination) && !File.Exists(stage) &&
                    StringComparer.Ordinal.Equals(FileSafety.Hash(destination), operation.Sha256))
                {
                    File.Move(destination, stage);
                    journal.Append(new JournalRecord { Event = "rollback_destination_staged", Source = operation.Source, Destination = operation.Destination });
                }
                else
                {
                    throw new IOException($"Cannot safely recover destination {operation.Destination}.");
                }
            }
            catch (Exception rollbackError)
            {
                complete = false;
                journal.Append(new JournalRecord { Event = "rollback_error", Source = operation.Source, Error = rollbackError.Message });
            }
        }

        foreach (var operation in operations.AsEnumerable().Reverse())
        {
            if (!staged.Contains(operation.Source))
                continue;
            try
            {
                var source = FileSafety.Absolute(root, operation.Source);
                var stage = FileSafety.Absolute(root, operation.Stage);
                if (File.Exists(source))
                    throw new IOException($"Original path is occupied: {operation.Source}.");
                File.Move(stage, source);
                journal.Append(new JournalRecord { Event = "rollback_source_restored", Source = operation.Source });
            }
            catch (Exception rollbackError)
            {
                complete = false;
                journal.Append(new JournalRecord { Event = "rollback_error", Source = operation.Source, Error = rollbackError.Message });
            }
        }
        return complete;
    }

    private static bool RollbackUndo(
        string root,
        List<TransactionAction> operations,
        Dictionary<string, string> undoStages,
        HashSet<string> staged,
        HashSet<string> restored,
        JournalWriter journal)
    {
        var complete = true;
        foreach (var operation in operations.AsEnumerable().Reverse())
        {
            if (!restored.Contains(operation.Source))
                continue;
            try
            {
                var original = FileSafety.Absolute(root, operation.Source);
                var stage = FileSafety.Absolute(root, undoStages[operation.Source]);
                if (!File.Exists(original) || !StringComparer.Ordinal.Equals(FileSafety.Hash(original), operation.Sha256) || File.Exists(stage))
                    throw new IOException($"Cannot safely restage original {operation.Source}.");
                File.Move(original, stage);
                journal.Append(new JournalRecord { Event = "undo_rollback_source_staged", Source = operation.Source });
            }
            catch (Exception rollbackError)
            {
                complete = false;
                journal.Append(new JournalRecord { Event = "undo_rollback_error", Source = operation.Source, Error = rollbackError.Message });
            }
        }
        foreach (var operation in operations.AsEnumerable().Reverse())
        {
            if (!staged.Contains(operation.Source))
                continue;
            try
            {
                var destination = FileSafety.Absolute(root, operation.Destination);
                var stage = FileSafety.Absolute(root, undoStages[operation.Source]);
                if (File.Exists(destination) || !File.Exists(stage))
                    throw new IOException($"Cannot safely restore applied name {operation.Destination}.");
                File.Move(stage, destination);
                journal.Append(new JournalRecord { Event = "undo_rollback_destination_restored", Destination = operation.Destination });
            }
            catch (Exception rollbackError)
            {
                complete = false;
                journal.Append(new JournalRecord { Event = "undo_rollback_error", Destination = operation.Destination, Error = rollbackError.Message });
            }
        }
        return complete;
    }

    private sealed class JournalWriter : IDisposable
    {
        private readonly FileStream _stream;
        private readonly StreamWriter _writer;

        private static readonly JsonSerializerOptions JournalOptions = new(JsonSupport.Options) { WriteIndented = false };

        public JournalWriter(string path, bool createNew = false)
        {
            var fullPath = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            _stream = new FileStream(fullPath, createNew ? FileMode.CreateNew : FileMode.Append, FileAccess.Write, FileShare.Read);
            _writer = new StreamWriter(_stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        public void Append(JournalRecord record)
        {
            _writer.WriteLine(JsonSerializer.Serialize(record, JournalOptions));
            _writer.Flush();
            _stream.Flush(flushToDisk: true);
        }

        public static List<JournalRecord> Read(string path)
        {
            try
            {
                var records = new List<JournalRecord>();
                foreach (var line in File.ReadLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;
                    records.Add(JsonSerializer.Deserialize<JournalRecord>(line, JsonSupport.Options)
                        ?? throw new RenameLedgerException("Journal contains an empty record."));
                }
                return records;
            }
            catch (IOException ex)
            {
                throw new RenameLedgerException($"Cannot read journal {path}: {ex.Message}", ex);
            }
            catch (JsonException ex)
            {
                throw new RenameLedgerException($"Journal is invalid JSON: {ex.Message}", ex);
            }
        }

        public void Dispose()
        {
            _writer.Dispose();
            _stream.Dispose();
        }
    }
}
