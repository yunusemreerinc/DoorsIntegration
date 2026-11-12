# Task 29 — post-limit completeness review

## What was checked

Before edits, both architecture manifests matched all 42 recorded source/documentation files, eight built product binaries and four protected settings/watchdog/icon files (54 checks per architecture). The saved full TRX results matched the recorded 1,559 PASS / 0 FAIL / 21 NOT_RUN totals. No file truncation or missing recorded source artifact was found. Existing WPF/DI/export registrations were present; the Real metadata provider remains deliberately unvalidated, as detailed below. The whole solution, not only the new feature, is included in regression.

## Corrections found during review

1. **Explicit access denial:** a requirement snapshot can report AccessDenied without a readable qualified module ID. The resolver previously checked that missing ID first and downgraded the explicit denial to Unknown. Request path/baseline/target echo is still checked first; for a matching request, explicit denial is now retained, completeness is false and objects/links are not read. An incorrectly echoed request remains Unknown even if its status says denied.
2. **Insufficient target evidence:** `All(...)` on an empty target metadata list was true, allowing an external target outside the observed metadata scope to qualify as verified for SuspectedLinkLoss. The comparison now requires actual target facts, readable state and matching or explicitly validated rebound baseline identity. A denied/absent/different target version cannot produce that stronger suspicion. A confirmed absence in a complete source scope remains an observation; it does not prove loss or its cause.
3. **Dense comparison:** unresolved observations were rescanned for each removed link. A stable source-object identity index now replaces that quadratic scan. Target facts are indexed too. A regression fixture compares 90,000 links against 45,000 remaining links, checking all 45,000 removals within an explicit performance bound.
4. **Existing timeout-test cleanup race:** the first x86 full run had 42/42 passing Task 29 Mock cases, but failed the older `LinkTests.TimeoutKeepsComAndGateAliveThenDeletesLateResult` during fixture cleanup. That test waited for the result file to disappear, then recursively deleted its parent while the late-cleanup continuation was still deleting the workspace. It now waits for the workspace to disappear as well. Production COM timeout/cleanup behavior was not changed; the failed run is preserved in `task29-review-x86`.

These are read-only analysis corrections. No DOORS baseline, link, object, requirement, module or lock was changed. No native compatibility gate was enabled.

## Test evidence

Seven focused checks passed, covering the six added review cases plus the existing positive suspected-loss/uncertainty test. The added cases are two denial/scope cases, three target-evidence cases and one dense comparison case.

Final Release/net48 build and full regression results are in:

- `artifacts/BaselineVerification/task29-review-final-x86`
- `artifacts/BaselineVerification/task29-review-final-x64`

Each directory contains full TRX, logs, PASS/FAIL/NOT_RUN acceptance JSON/CSV, report examples and a WPF screenshot. `summary.json` and `sha256.json` record final counts, source/binary hashes and preserved protected files. Task 29 now has 42 Mock/application cases and two separately gated native cases. Original acceptance directories are retained as historical evidence.

| Final Release/net48 evidence | x86 | x64 |
| --- | --- | --- |
| Build | PASS; 0 warnings, 0 errors | PASS; 0 warnings, 0 errors |
| Full suite PASS | 1,565 | 1,565 |
| Full suite FAIL | 0 | 0 |
| Full suite NOT_RUN | 21 | 21 |
| Full suite total | 1,586 | 1,586 |
| Task 29 Mock/application PASS | 42 | 42 |
| Task 29 native 9.5 / 9.7 | NOT_RUN / NOT_RUN | NOT_RUN / NOT_RUN |

The 21 full-suite NOT_RUN cases include gated native integration cases from earlier tasks. They are not counted as passing. The earlier failed x86 run and the initial passing x64 run remain in `task29-review-x86` and `task29-review-x64`; final directories contain the rerun after the timeout-test cleanup correction.

Only these implementation files changed in this review:

- `src/DoorsIntegration.Core/BaselineLinkSnapshotService.cs`
- `src/DoorsIntegration.Core/BaselineTraceabilityComparisonService.cs`
- new `tests/DoorsIntegration.Tests/BaselineVerificationReviewTests.cs`
- `tests/DoorsIntegration.Tests/LinkTests.cs` (wait for late workspace cleanup before fixture disposal)

This document and the validation document's link to it were added for traceability.

## What remains incomplete for real DOORS

The Mock/WPF/reporting application flow is implemented. **The shipped Real mode is not production-ready.** This is more than merely a missing test result:

- Production composition still registers `UnvalidatedBaselineSetVerificationMetadataProvider`; a validated native implementation must supply authoritative membership/access, historical definition scope, typed member-version bindings and configured-shareable state. The new resolver preserves conservative Unknown/NOT_RUN behavior until such evidence exists.
- The Task 28 native read/cache safety gate and Task 29 native sourceVersion/targetVersion/echo compatibility gate remain disabled. Exact DOORS 9.5 and 9.7 manual/runtime checks are needed before deployment. Native script presence is not compilation or integration proof.
- Configured-shareable detection must use a verified release API; current edit mode alone is insufficient. No unsupported perm was guessed.
- Real COM timeout/cleanup/echo behavior and Windows-account DPAPI must be verified in the target environment. Current Mock tests do not validate these.

No native integration test was enabled by this review. Real 9.5/9.7 cases remain NOT_RUN, and no automatic repair/write path is introduced. See [usage and separate native checklists](BASELINE-TRACEABILITY-ACCEPTANCE.md) and [original architecture/validation](BASELINE-TRACEABILITY-VALIDATION.md).
