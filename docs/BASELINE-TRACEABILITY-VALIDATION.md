# Task 29 — delivered implementation and validation

For the subsequent completeness audit, two analysis corrections, dense-comparison optimization and updated regression evidence, see [post-limit review](BASELINE-TRACEABILITY-POST-LIMIT-REVIEW.md). Original acceptance counts below are retained as historical results.

The Baseline Set Traceability Verification engine is integrated with the existing net48 solution. WPF has a new **Baseline Traceability** tab; the first nine tabs retain their ordering. The feature performs only reads against DOORS. It exposes no create/update/delete, baseline/set close, lock/save/discard or link-repair operation. Local protected audit and requested export files are the only writes performed by this feature.

## Integration architecture

- **BaselineLinkSnapshotService** composes an `IBaselineSetVerificationSource` and `IBaselineLinkResolver`. It captures metadata around non-atomic data reads, detects drift, distinguishes set membership from selected verification scope and imports actual stored Task 28 references without inventing missing historical evidence.
- **NativeBaselineSetVerificationSource** reuses the existing Native Baseline Set adapter's inspection path. Its metadata provider must supply authoritative visibility, historical definition scope, typed member baseline identities and access facts. Default native metadata is explicitly unvalidated, not silently inferred from readable-only lists.
- **ServiceBaselineLinkResolver** reuses RequirementService and LinkService with explicit selected module baselines and attribute DXL disabled. A separate read-only native version-evidence reader obtains actual outgoing source/target ModuleVersions and incoming source/anchor-target evidence. Ambiguous joins stay unresolved. A missing native version cannot be replaced with current-module data.
- **BaselineTraceabilityAnalyzer** verifies internal/external/version/echo/access classes, expected relationships, policy-required coverage, orphan objects and broken chains. It calls the existing graph projection/iterative component/impact algorithms. Existing graph node/edge/observation models retain actual incoming/outgoing observations. Same object in different baselines gets separate graph identity; module baseline numeric versions need not equal the set version or each other.
- **BaselineTraceabilityComparisonService** compares only supplied evidence. It reports added/removed/retargeted/version-changed/unresolved/physical-duplicate observations and explicitly validated expected rebinding. No reference means no reconstructed historical absence. Inaccessible evidence is not proof of loss. Shareable risk plus suitable comparable evidence can produce **SuspectedLinkLoss**, not a causal claim or automatic repair.
- **BaselineSetVerificationService** orchestrates read-only capture, analysis, comparison, sanitized logging and local protected audit. Verification Job ID, source/environment, policy fingerprint, snapshot fingerprint and UTC execution timestamps are retained. Native wait failure stays Unknown; there is no blind retry. Failed local audit persistence is reported and prevents Passed without falling back to plaintext.
- **BaselineVerificationReportService** uses the existing disk-spooled, bounded, atomic-publication export pipeline. JSON, CSV, XLSX and escaped HTML include versions, membership, requirement identities/rich representation, link results, coverage, findings, limitations and overall status. HTML is additive; existing writers remain registered once each through DI.
- **WPF MVVM** uses the workspace's background execution, progress and cancellation path. Node/edge selection, type/search filtering and bounded incoming/outgoing multi-hop work on captured data. UI draws at most 300 nodes/1,000 edges and virtualizes bounded result lists; export retains the full captured scope. Real/Mock and read-only intent are explicit.

Read-only verification does not invoke JobRunner's mutation path. It reuses the same shared STA executor/operation gate and journal protection/sanitation primitives. Existing Independent/Native execution and journal formats are preserved. BaselineComparisonService's stable module + Absolute Number identity convention and rich-text representation are followed; its comparison implementation is not duplicated or changed.

## Acceptance and stage results

The final acceptance directories are:

- `artifacts/BaselineVerification/task29-accepted-x86`
- `artifacts/BaselineVerification/task29-accepted-x64`

Each contains `acceptance.json`, `acceptance.csv`, `verification.trx`, build/restore/test logs, and `mock-evidence` with a WPF PNG and four report examples. `summary.json` and `sha256.json` provide final suite counts and source/binary evidence.

| Check | x86 | x64 |
|---|---|---|
| Release net48 build | PASS, 0 warnings/errors | PASS, 0 warnings/errors |
| Full regression | 1,559 PASS; 0 FAIL; 21 NOT_RUN | 1,559 PASS; 0 FAIL; 21 NOT_RUN |
| Task 29 Mock/application tests | 36 PASS | 36 PASS |
| Task 29 native 9.5 read-only integration | NOT_RUN | NOT_RUN |
| Task 29 native 9.7 read-only integration | NOT_RUN | NOT_RUN |
| Real DOORS mutations attempted | No | No |
| Native DXL/shareable/echo/DPAPI validation | NOT_RUN | NOT_RUN |

Stages: initial build and Task 28 Mock 28/28 PASS; Core Mock 22/22 PASS; protocol/report/MVVM 33 PASS with two native NOT_RUN; related existing WPF/Native regression 90 PASS with two native NOT_RUN. Additional module-fact, no-mutation and incomplete-enumeration boundaries bring the Task 29 Mock total to 36. The dense fixture is 12,000 requirements / 90,000 directed links. The first x86 run took about 29 seconds; immutable version/object identity caches reduced subsequent runs to about 6–7 seconds. Actual final timing is in the acceptance JSON/TRX, marked Mock only.

An intermediate four-format test run lacked its temporary export parent directory; the setup and WPF export-parent creation were corrected. The first complete regression also exposed an old assertion that exactly three writers were registered; it now checks all registered format kinds and the single HTML registration. Those earlier failed TRX/logs are retained; the accepted directories are the final evidence.

## Coverage and status interpretation

Coverage considers policy-required selected objects, not every object in every module. Known unresolved objects remain in the denominator. Unenumerated modules/unknown eligibility are explicit, make the overall denominator uncertain, and suppress the overall percentage. Known-scope bounds remain separate. Incomplete link enumeration prevents overall Passed even when observed links satisfy a minimum. Counts for inaccessible objects whose IDs/counts cannot be read are not fabricated.

Confirmed policy errors yield Failed; uncertain capture/access/version evidence remains visible and cannot become Passed. Open complete sets are warnings; open partial sets are incomplete. Confirmed absent baselines in closed sets are failures. Individual observations and physical link multiplicity are distinct: incoming/outgoing/echo observations alone never prove duplicate links. Echoed external links can be valid, while contradictory internal echo context needs review. Cycle severity follows the chosen project rule.

The 36 Mock cases exercise all 15 requested scenario families, boundary behavior, all four reports, default native non-dispatch, protected audit, WPF selection/impact/reference/export and the rendered screen. An adapter spy verifies zero ExecuteAsync calls. Mock setup actions used by reused Task 28 fixtures are in-memory simulated operations, not DOORS database writes.

## Native verification gaps / remaining risks

1. No real DOORS 9.5 or 9.7 environment was used. Native tests are NOT_RUN; no native baseline/link/module/requirement was created, updated, closed or deleted.
2. Source/target/echo signatures are documented in the IBM 9.7.2 reference and corroborated by the IBM-authored 9.6.1 manual. That manual is not a 9.5-specific reference. Exact-release compilation/runtime acceptance remains required for both families.
3. Default Real metadata/cache inspection and version-evidence compatibility are disabled. Real UI returns Unknown/NOT_RUN until validated providers are supplied. This is an explicit remaining native integration point, not an assertion that unverified perms work.
4. Native membership iterators can hide inaccessible entries; `baselineExists(ModuleVersion)` false also includes unreadable data. An authoritative provider must distinguish absent from denied/unknown and verify the definition scope appropriate to the selected set, especially after a closed definition was subsequently edited.
5. Configured edit-shareable state is not inferred from current edit mode. No release-specific configuration perm was guessed. Unsupported configuration is Unknown; actual detection and runtime link behavior remain unvalidated. The IBM warning is linked in the technical/acceptance documents.
6. Native incoming LinkRef has no assumed targetVersion overload. The observed target anchor supplies that side's version; LinkService verifies object/counterpart resolution. Actual echo, access, ambiguous join and cleanup behavior need a controlled prepared read-only dataset.
7. Task 28 legacy references have link-only evidence. Missing typed historical versions, object inventory or full scope are preserved as limitations. They cannot by themselves prove that a baseline operation lost links. Explicitly validated rebinding is required to classify expected version transitions.
8. Graph reads are not database transactions. Metadata drift and declared limits make results incomplete. Very large or heavily denied native data can exceed configured limits or require longer read times. Active COM cannot be safely assumed abortable; original completion owns cleanup.
9. Production audit protection uses Windows DPAPI CurrentUser. Tests inject the existing ephemeral test protector. Native DPAPI success has not been established here; failure is disclosed without plaintext fallback.
10. Analysis and exported reports are evidence under the selected policy, not DOORS native history or a certification/compliance decision. No automatic repair is offered.

## Changed files

New Core files:

- `src/DoorsIntegration.Core/BaselineVerificationModels.cs`
- `src/DoorsIntegration.Core/BaselineVerificationCopy.cs`
- `src/DoorsIntegration.Core/BaselineVerificationPolicyProfile.cs`
- `src/DoorsIntegration.Core/BaselineLinkSnapshotService.cs`
- `src/DoorsIntegration.Core/BaselineGraphProjection.cs`
- `src/DoorsIntegration.Core/BaselineTraceabilityAnalyzer.cs`
- `src/DoorsIntegration.Core/BaselineTraceabilityComparisonService.cs` (also the orchestration service)
- `src/DoorsIntegration.Core/MockBaselineSetVerificationAdapter.cs` (Mock and read-source provider implementations)
- `src/DoorsIntegration.Core/FileBaselineVerificationAuditSink.cs`

Additive Core changes: `TraceabilityGraphModels.cs`, `TraceabilityGraphService.cs`, `ExportModels.cs`.

Native read evidence:

- `src/DoorsIntegration.Dxl/DxlBaselineNativeLinkEvidenceReader.cs`
- `src/DoorsIntegration.Dxl/BaselineTraceabilityAcceptanceEnvironment.cs`
- `scripts/dxl/baseline-link-versions.dxl`

Reporting:

- New `src/DoorsIntegration.Export/BaselineVerificationReportService.cs`
- New `src/DoorsIntegration.Export/HtmlExportWriter.cs`
- Additive schemas in `src/DoorsIntegration.Export/ExportSchemas.cs`

WPF:

- New `Services/BaselineVerificationSession.cs`
- New `ViewModels/BaselineTraceabilityViewModel.cs`
- New `Controls/BaselineTraceabilityGraph.cs`
- New `Controls/BaselineTraceabilityView.xaml` / `.xaml.cs`
- Composition/read-adapter additions in `Services/ClientSession.cs`, `Services/NativeBaselineSetSession.cs`, `Services/MockDoorsAdapter.cs`
- Tab/lifecycle wiring in `MainWindow.xaml` and `ViewModels/WorkspaceViewModel.cs`

Console/Desktop `ServiceCollectionExtensions.cs` receive replaceable verification ports and the HTML writer.

Tests and tooling:

- New `BaselineVerificationTests.cs`, `BaselineVerificationBoundaryTests.cs`, `BaselineVerificationWpfTests.cs`, `BaselineVerificationIntegrationTests.cs`
- Existing `ExportServiceTests.cs`, `WpfClientTests.cs`, `BulkBaselinePreflightWpfTests.cs`: intentional format/tab count expectations updated
- `scripts/Test-BaselineTraceabilityAcceptance.ps1`
- `docs/BASELINE-TRACEABILITY-ARCHITECTURE.md`
- `docs/BASELINE-TRACEABILITY-ACCEPTANCE.md`
- `docs/BASELINE-TRACEABILITY-VALIDATION.md`
- `README.md`

Settings, watchdog, application icon and previous release packages are preserved. No NuGet/project/solution dependency was added. See [architecture/API evidence](BASELINE-TRACEABILITY-ARCHITECTURE.md) and [usage, repeatable acceptance runner and separate native checklists](BASELINE-TRACEABILITY-ACCEPTANCE.md).
