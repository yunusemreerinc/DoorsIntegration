# Code analysis: DoorsIntegration

Date: 2026-11-12. Reviewed at commit `cc614b4` (`main` after pull requests #1 and #2).

This is a static review. The full solution has **not** been built or tested on Windows as part of it. See [Verification status](#verification-status).

## Overview

DoorsIntegration is a .NET Framework 4.8 / C# 7.3 solution that automates IBM DOORS Classic 9.5 and 9.7. It connects through late-bound COM (`DOORS.Application`) and runs DXL scripts with `runStr` / `runFile`. It has a WPF desktop client and a console app for diagnostics.

Size: about 27k lines of C# in 9 projects, about 2.9k lines of DXL in 20 scripts under `scripts/dxl`, 700+ MSTest methods, and about 50 design documents in `docs/`.

## Architecture

| Project | Role |
| --- | --- |
| `Core` | Domain models, ports (interfaces), services, mock adapters, file-based journals |
| `Dxl` | Builds DXL requests, runs them, validates the response format; one gateway per feature (requirements, links, baselines, module creation) |
| `Com` | COM session, Windows registry checks, UI Automation for the DXL run-limit prompt |
| `Processing` | Quality-rule engine, batch imports |
| `Export` | XLSX (OpenXml), HTML and text export |
| `Application` | Composition shared by the front ends |
| `Wpf` / `Console` | Front ends |

Dependencies point the right way: `Dxl` and `Com` implement interfaces defined in `Core`, and every adapter has a mock. This lets the logic be tested without DOORS.

## Strengths

- **COM threading is handled carefully.** Each COM session is created, used and released on a single STA thread. A process-wide gate (`DoorsOperationGate`) allows one DOORS operation at a time. A timeout stops *waiting* but never pretends to cancel a COM call that is already running.
- **The DXL request/response format is safe.**
  - Parameters are passed as escaped string literals (`DxlStringLiteral.Encode`) under a reserved `di_` prefix, never pasted in as code.
  - Each request first writes a PENDING marker tagged with its own ID. A stale result, a `halt` or another client's output can't be mistaken for success.
- **Write scripts are defensive.** `requirement-write.dxl` reads values back after writing, reports `OutcomeUnknown` instead of guessing, leaves a module open for review rather than discarding a partial edit, and frees its buffers.
- **Saved files survive crashes.** The job journal writes to a temp file with write-through and `Flush(true)`, then swaps it in with `File.Replace`. Audit revisions are only ever created, never overwritten. Lock files stop two workers from using the same journal.
- **Test coverage is broad**, and extensive design documentation lives in `docs/`.

## Findings

| # | Finding | Severity | Status |
| --- | --- | --- | --- |
| 1 | A write DOORS confirmed could be reported as `OutcomeUnknown` after a cleanup error | High | Fixed in #2 |
| 2 | Generated files and chat logs committed to the repository | Medium | Fixed in #1 |
| 3 | Unused method blocks on async code | Low | Fixed |
| 4 | WPF error handling depends on convention; window can get stuck open | Medium | Fixed |
| 5 | Tab navigation uses hard-coded positions | Low | Fixed |
| 6 | Very long lines and hard-coded user-facing strings | Low | Open |
| 7 | File result could be disposed twice on a failure path | Info | Fixed in #2 |

### 1. Cleanup error could hide a successful write (fixed)

`DxlExecutor` used to turn any cleanup error after a successful run into the operation's error. Cleanup means releasing the COM connection, deleting the temp workspace, or releasing the gate. The workspace delete is non-recursive (`src/DoorsIntegration.Dxl/DxlWorkspace.cs:111`), so it fails whenever a script leaves an extra file behind.

The write gateways turn an executor error into `OutcomeUnknown` and never retry automatically. So a write that DOORS had finished, acknowledged and read back was marked "unknown", and its parsed result was thrown away.

**Fix (pull request #2):** once DOORS returns a validated completion response, cleanup errors are logged as `DxlCleanupFailed` with `ResultAcknowledged=True`, and the result is returned. Failure paths are unchanged. Four test cases were added to `tests/DoorsIntegration.Tests/DxlExecutorTests.cs`.

### 2. Repository contents (fixed)

`.packages/` (restored NuGet cache, 1,367 files, about 300 MB), `.vs/`, `.cli/` and four `conversation-*.md` chat logs were committed. Pull request #1 untracked them, added them to `.gitignore`, and replaced the placeholder `README.md`.

**Remaining:** the files are still in git history, so a clone is still about 45 MB. Shrinking it would require rewriting history and force-pushing.

### 3. Unused method blocks on async code (fixed)

`BaselineManagerSession.History` called `.GetAwaiter().GetResult()` on an async query. Nothing called it. If it were ever called from the UI thread it could deadlock.

**Fix:** the method was deleted. Callers use `JobHistory.QueryAsync` directly.

### 4. WPF error handling (fixed)

- `UiCommand.Execute` is `async void` (ICommand contract). Any exception a view model didn't catch would crash the app.
- In `MainWindow.OnClosing`, if `PrepareCloseAsync` threw, `closeRequested` stayed `true` and `allowClose` stayed `false`, so the window could never be closed.

**Fix:**
- `UiCommand.Execute` now catches and logs (`Trace.TraceError`) anything that escapes. `ExecuteAsync` still propagates, so tests and hosts that await it see the exception.
- `OnClosing` uses `try`/`catch`/`finally`, so the window always closes. The `Closed` handler moved to `OnClosed`. It logs the `InvalidOperationException` that `WorkspaceViewModel.Dispose` throws when work is still running, which can only happen after a failed prepare, instead of crashing on exit.

### 5. Hard-coded tab positions (fixed)

`MainWindow` mapped dashboard destinations to tab numbers (0, 1, 5, 8, 9, 10). Adding or reordering a tab would silently break navigation.

**Fix:** the target tabs now have `x:Name`s (`WorkspaceTab`, `BaselineDiffTab`, `OperationsTab`, `BulkBaselineManagerTab`, `BaselineTraceabilityTab`, `ChangeImpactTab`). `OnDashboardNavigation` and `OnFindSelection` select them by reference. The mapping is unchanged.

### 6. Readability (open)

- About 1,500 lines in `src/` are longer than 200 characters. The longest are 936 characters in `Core` and 1,297 in `Wpf`.
- Many lines contain several statements; for example `src/DoorsIntegration.Wpf/ViewModels/BulkBaselineManagerViewModel.cs:227`.
- User-facing strings are hard-coded in a mix of Turkish and English instead of resource files.

**Suggested fix:** run the formatter once using the existing `.editorconfig`. Move user-facing strings into resources when localization matters.

### 7. Double dispose on a failure path (fixed)

The executor could dispose a file-mode result twice on a failure path. This was harmless as long as `Dispose` stayed idempotent. The restructured cleanup in pull request #2 removed that path.

## Verification status

**Fixes for #3–#5 (2026-11-13):** `.\scripts\Build.ps1 -Platform x86` was run on Windows with 0 warnings and 0 errors. Tests: 1,677 passed, 0 failed, 69 skipped (1,746 total). The WPF close and navigation changes have not been exercised by hand against a real DOORS client.

Original review:

- **Windows build and tests:** not run. Run `.\scripts\Build.ps1 -Platform x86` on Windows to build and run the full suite.
- **`DoorsIntegration.Dxl`:** builds for net48 on Linux with 0 warnings (`TreatWarningsAsErrors`).
- **Executor tests:** the 4 new cases and the 6 existing tests on results, errors and cleanup were run in a temporary net8 harness built from the Core/Processing/Dxl sources. All 10 pass. Against the original executor, exactly the 4 new cases fail.
- **Full solution build on Linux:** started but not finished. Everything except the WPF project restores. The WPF project needs the WindowsDesktop SDK, which the Linux SDK package used here lacks.
