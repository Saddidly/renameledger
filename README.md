# RenameLedger

A .NET 8 batch-renaming CLI with composable rules, a reviewable JSON plan,
content preconditions, staged name swaps, and durable undo journals.

Use it to normalize downloaded documents, number an image collection, replace
inconsistent filename fragments, or rename an archive without losing its history.
File contents are never transformed. Renames stay within each source directory.

## Build and test

Install the .NET 8 SDK or a compatible newer SDK.

```sh
dotnet build src/RenameLedger/RenameLedger.csproj -c Release
dotnet run --project tests/RenameLedger.Tests -c Release
dotnet run --project src/RenameLedger -- --help
```

The test runner is a dependency-free executable that returns a nonzero status on
failure. It uses real temporary files to check apply/undo, collisions, edited
content, swaps, case-only changes, and journal/path protection. It is not an
empty `dotnet test` invocation.

## Quick start

Copy `examples/rules.json`, change `directory` to a folder you control, and run:

```sh
dotnet run --project src/RenameLedger -- plan examples/rules.json --out work/plan.json
# Read the plan before applying it.
dotnet run --project src/RenameLedger -- apply work/plan.json --journal work/rename-001.jsonl
dotnet run --project src/RenameLedger -- undo work/rename-001.jsonl
```

Plan outputs and apply journals must be fresh paths. Existing files are never
replaced by these artifacts. The root is resolved relative to the config file,
not the current working directory. `--json` prints the full plan in addition to
saving it. Plan status 3 means collisions were found. Invalid or failed operations
return 2; successful operations return 0.

## Configuration

```json
{
  "version": 1,
  "directory": "../inbox",
  "recursive": false,
  "pattern": "*.jpg",
  "transforms": [
    { "type": "replace", "find": "IMG_", "replacement": "", "ignoreCase": true },
    { "type": "prefix", "value": "trip-" },
    { "type": "numbering", "start": 1, "width": 3, "separator": "-", "placement": "prefix" }
  ]
}
```

Transforms apply in order to the filename **stem**; the extension is retained.
Supported transforms are `prefix`, `suffix`, literal `replace`, .NET `regex`, and
`numbering`. Numbering follows a deterministic sort of relative source paths;
it does not depend on directory enumeration order. Regex replacements support
.NET capture references such as `$1`; evaluation has a one-second timeout.
The basename `pattern` supports `*` and `?`, with case-insensitive matching.

Recursive scanning skips symbolic links and reparse points. Collision checks
are deliberately case-insensitive on every platform, making a plan conservative
when moving between Windows and Linux. A plan can represent cycles and case-only
renames because all sources are staged before any final name is published.

## Safety and recovery

Plans record length, modification time, and SHA-256. Apply validates the entire
plan before creating a fresh journal or moving a file, then rechecks content
before staging. `File.Move` never replaces an existing destination. On a normal
I/O failure, the transaction attempts rollback and records whether it completed.

Undo checks applied content and original-name occupancy before beginning. It
stages applied names first, so reversing a swap is safe. A completed or interrupted
undo cannot be blindly repeated. Journals are one JSON object per line, flushed
to disk after each event. A crash can occur between a filesystem change and its
journal event; recovery then requires manual inspection of the journal and the
unique `.renameledger-*.stage` or `.undo` files. Automatic crash recovery is not
implemented. Filesystem durability and atomicity depend on the underlying volume.

Work in quiescent folders you control. Checks do not form a sandbox against a
hostile process replacing parent directories. Keep backups for important data.
Do not use a journal from an untrusted source. See [architecture](ARCHITECTURE.md),
[contributing](CONTRIBUTING.md), and [security](SECURITY.md).

Licensed under [MIT](LICENSE).
