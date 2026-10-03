# Verification

Local evidence recorded on 2026-10-03.

## Passed

Release build passed with warnings treated as errors. The executable test runner passed 8/8 behavioral checks, including apply/undo, stale inputs, name swaps, case-only changes, collisions, and journal/path protection.

## Environment and limits

Windows .NET SDK 8.0.425. No hosted CI run or published binary is claimed. Recovery after process termination is manual.

The checked-in CI workflow is ready to run when published. It is configuration,
not evidence of a hosted pass. Re-run README commands after changing dependencies
or moving to another platform. Screenshots, where included, use synthetic data.
