# Verification

Local evidence recorded on 2026-10-03.

## Passed

Release build passed with warnings treated as errors. The executable test runner passed 8/8 behavioral checks, including apply/undo, stale inputs, name swaps, case-only changes, collisions, and journal/path protection.

## Environment and limits

Windows .NET SDK 8.0.425. Hosted Ubuntu/Windows checks passed; no packaged release binary is published. Recovery after process termination is manual.

## Hosted evidence

[GitHub Actions run](https://github.com/Saddidly/renameledger/actions/runs/37111977186) passed on 2026-10-03 for code revision `4add1c277dde7ba593bea679fb820f54c61d53fd`.

Ubuntu and Windows, .NET 8; release build and behavioral test runner.

These checks cover the named environments and cases, not every possible input or platform. Re-run README commands after changing dependencies or moving to another platform. Screenshots and acceptance data are synthetic.

