# Task 28 implementation and verification

The Native Baseline Set application workflow is implemented alongside the unchanged Independent request/journal mode. Production native writes are disabled. This is **not** a declaration of DOORS 9.5/9.7 runtime compatibility.

## Delivered architecture

- `INativeBaselineSetService`: definition and membership queries/mutations; Major/Minor set creation, open-set discovery, population, independently approved closure, verification and read-only reconciliation. Each mutation gets a durable intent, explicit review and fresh preflight; a Real mutation additionally requires second confirmation.
- `IBaselineSetAdapter`: persisted Mock implementation and external-script DXL adapter through the existing shared STA executor. Set versions have their own type; module baseline versions are read separately. Formal modules retain repository/qualified identity and canonical path. Multi-project/folder selection is supported within one database scope.
- `BaselineOperationCoordinator`: reused by the Independent runner and native workflow. Actual COM calls retain the existing global STA queue/gate. Cancellation stops dispatch before it starts; an active native call is not assumed abortable. Timeout/protocol uncertainty remains Unknown until read-only reconciliation.
- `IBaselineSetJobRepository`: protected append-only revision frames, immutable request/plan fingerprint, atomic durable writes, full history and process restart recovery. Reuses journal protection/options and sanitized logging. Independent journal files and format are preserved.
- `IBaselineSetLinkReader`: bridge to existing LinkService with explicit complete scope and observed identities/version bindings; Mock fault injection. Risky/unknown shareable configuration blocks baseline actions. Reference links are recorded before set creation, checked before closure and after closure; lost/mismatched/external-version findings remain visible.
- WPF MVVM: shared virtualized folder/module selection plus a separate Native panel for definitions, set/member versions, missing modules, per-action dry-run/review/execute, open-state, verification, job history, reconciliation and export. Search/filter preserves selection. Mode, read-only and selection changes invalidate approval. Opening a job restores its folder scope.
- `NativeBaselineSetReportService`: reuses existing JSON/CSV/OpenXML writers; exports the captured append-only job revision prefix, approved intent, before/after definition/set/module state and link findings. Existing CSV neutralization, Unicode, UTC dates, progress, cancellation and bounded export infrastructure remain in use. No additional package dependency.

There is no automatic close after population, delete, rollback or blind retry. Native population requires all definition modules together; an already partially populated native set is blocked pending controlled recovery. Synthetic Mock partial failure does not claim native addBaselines atomicity behavior.

## Verification results

Final machine-readable evidence and full TRX are stored under `artifacts/NativeBaselineSet/task28-release-x86` and `task28-release-x64`. See `summary.json` in each directory for the final counts and source classification. Builds target Release/net48 and use SDK 5.0.416 through the repository's SmokeBuildHost.

| Check | x86 | x64 |
|---|---|---|
| Build | PASS, 0 warnings/errors | PASS, 0 warnings/errors |
| Full regression | PASS: 1,523; FAIL: 0; NOT_RUN: 19 | PASS: 1,523; FAIL: 0; NOT_RUN: 19 |
| Native Mock application suite | 28 PASS | 28 PASS |
| Native 9.5 read/write integration | NOT_RUN | NOT_RUN |
| Native 9.7 read/write integration | NOT_RUN | NOT_RUN |
| Live DOORS writes attempted | No | No |
| Native DXL compilation/link behavior/DPAPI validated | No | No |

Stage 1 before changes: build PASS and existing Task 27 acceptance 24/24 PASS. Stage 2: Core/Mock plus Independent runner regression 44/44 PASS. Stage 3: native/old WPF Manager 45/45 PASS. Stage 4: protocol/export/MVVM 27/27 PASS, subsequently expanded with the rendered Native UI case. Final full regression includes the rendered case and latest repository-scope guard.

An intermediate acceptance build overlapped an x86 test process and encountered a locked test DLL. That infrastructure attempt is retained under `task28-verified-x86` as FAIL. Subsequent builds were run after that process finished; clean final build logs and TRX are the acceptance evidence. Prior attempts and Tasks 1–27 release artifacts were not replaced.

## Acceptance boundaries

Mock covers definition CRUD, multi-folder deduplication, separate Major/Minor set/member versions, one-open-set enforcement, membership immutability while open, population, missing modules/partial outcomes, close, link mismatch, permission/lock/dirty/shareable gates, protected append-only journal, restart/reconciliation without duplicate creates, WPF approval/context invalidation and all three report formats. Existing Independent behavior is checked by full regression.

Real API acceptance remains blocked by release-specific runtime validation. The 9.7 signatures have documentary corroboration; the accessible IBM-authored corroborating manual is **9.6.1**, not 9.5. A 9.5-specific manual and exact-build runtime evidence are still required. Shipped native state/cache/shareable and complete link-scope providers deliberately report NOT_RUN; they do not guess unsupported perms. Consequently the shipped Real UI cannot create a native set or dispatch even metadata scripts requiring unvalidated cache inspection. A reviewed deployment must supply validated providers and pinned script/build/database/user evidence through DI. Approval alone cannot bypass these gates.

Native definition identifiers are explicitly logical folder-identity/name locators. Rename changes that locator; uncertain rename requires inspection rather than silently assuming a native GUID. Module member baseline locators bind the observed set and stable module identity; they do not invent a DOORS native baseline GUID. The Mock selector's last-version fixture and Native member-version fixture are independent simulations; the Native member grid/report is the authority for the simulated set's members.

Windows production journal protection uses DPAPI CurrentUser. Tests use an ephemeral test protector. Native DPAPI success has not been established in this execution environment; there is no plaintext fallback.

## Changed files

New Core files:

- `src/DoorsIntegration.Core/NativeBaselineSetModels.cs`
- `src/DoorsIntegration.Core/NativeBaselineSetService.cs`
- `src/DoorsIntegration.Core/MockNativeBaselineSetAdapter.cs`
- `src/DoorsIntegration.Core/FileBaselineSetJobRepository.cs`
- `src/DoorsIntegration.Core/ServiceBaselineSetLinkReader.cs`
- `src/DoorsIntegration.Core/BaselineOperationCoordinator.cs`

Core changes: `BulkBaselineJobRunner.cs` (common coordination primitive only) and `ExportModels.cs` (additive table kinds).

New DXL/adapter files:

- `scripts/dxl/native-baseline-set-read.dxl`
- `scripts/dxl/native-baseline-set-write.dxl`
- `src/DoorsIntegration.Dxl/DxlNativeBaselineSetAdapter.cs`
- `src/DoorsIntegration.Dxl/NativeBaselineSetAcceptanceEnvironment.cs`

Reporting: new `src/DoorsIntegration.Export/NativeBaselineSetReportService.cs`; additive schemas in `ExportSchemas.cs`.

New WPF files:

- `src/DoorsIntegration.Wpf/Services/NativeBaselineSetSession.cs`
- `src/DoorsIntegration.Wpf/ViewModels/NativeBaselineSetViewModel.cs`
- `src/DoorsIntegration.Wpf/Controls/NativeBaselineSetView.xaml`
- `src/DoorsIntegration.Wpf/Controls/NativeBaselineSetView.xaml.cs`

WPF composition/selection changes: `Services/ClientSession.cs`, `Services/MockDoorsAdapter.cs`, `ViewModels/BulkBaselineManagerViewModel.cs`, `Controls/BulkBaselineManagerView.xaml`. Console and Desktop `ServiceCollectionExtensions.cs` receive replaceable, default-disabled native DI registrations.

Tests/tooling/documentation:

- `tests/DoorsIntegration.Tests/NativeBaselineSetTests.cs`
- `tests/DoorsIntegration.Tests/NativeBaselineSetBoundaryTests.cs`
- `tests/DoorsIntegration.Tests/NativeBaselineSetIntegrationTests.cs`
- `scripts/Test-NativeBaselineSetAcceptance.ps1`
- `docs/NATIVE-BASELINE-SET-ARCHITECTURE.md`
- `docs/NATIVE-BASELINE-SET-ACCEPTANCE.md`
- `docs/NATIVE-BASELINE-SET-VALIDATION.md`
- `README.md`

Settings, watchdog configuration, existing application icon and previous release ZIPs are preserved. Source/binary SHA256 evidence is recorded with the final acceptance summary.

See [architecture and primary IBM references](NATIVE-BASELINE-SET-ARCHITECTURE.md) and [usage, build instructions, real acceptance checklists and integration fixture](NATIVE-BASELINE-SET-ACCEPTANCE.md).
