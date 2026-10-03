# Architecture

Configuration loading and stem transforms are separate from inventory/planning.
`PlanService` inventories paths, records preconditions, and classifies collisions.
`TransactionService` validates, stages, commits, and reverses operations; it owns
the journal. `FileSafety` owns relative-path containment and filesystem checks.
`Program` only parses commands, formats a preview, and chooses exit status.

Public plan and transaction services can be invoked without the CLI. DTOs have a
versioned JSON format; they are intentionally validated again at the mutation
boundary because users may edit a saved plan. A journal is distinct from a plan:
the plan states intent, while journal events record completed phases and failures.

Staging is necessary for swaps, cycles, and case-only renames. Rollback first puts
committed destinations back into staging, then restores original names. Undo uses
the same two-phase ordering. This is filesystem coordination, not a database-style
atomic transaction; partial failure evidence remains available to the operator.

There are no NuGet runtime dependencies. The test executable exercises public
services with temporary directories. Tests do not establish crash consistency,
network-volume behavior, or resistance to adversarial filesystem races.
