# Task 30 — implementation, verification and delivery record

## Scope

Advanced Change Impact Analysis is additive to Tasks 1–29. The engine and WPF workflow perform read-only DOORS analysis; only local audit/review/report files are written. No save/discard, requirement/link/attribute/module/baseline mutation, repair, rollback, native write authorization bypass or automatic retry was introduced. Existing Independent/Native Baseline Set features, settings, watchdog configuration and icon are retained.

Architecture and extension contracts: [CHANGE-IMPACT-ARCHITECTURE.md](CHANGE-IMPACT-ARCHITECTURE.md). Usage, builds, project JSON policy and separate release checklists: [CHANGE-IMPACT-ACCEPTANCE.md](CHANGE-IMPACT-ACCEPTANCE.md).

## Classes and contracts

- IChangeImpactAnalysisService / ChangeImpactAnalysisService.
- IRequirementChangeDetector / RequirementChangeDetector, sharing the established BaselineComparisonResult attribute implementation through BaselineAttributeComparison.
- IImpactPropagationEngine / ImpactPropagationEngine, reusing BaselineTraceabilityAnalyzer and TraceabilityGraphService's version-aware projection.
- IImpactPolicyProvider / JsonImpactPolicyProvider, ImpactClassificationService.
- IImpactSnapshotProvider / ServiceImpactSnapshotProvider / MockChangeImpactSnapshotProvider; WPF MockDoorsAdapter implements the new read port.
- ImpactReviewService / IImpactReviewRepository / FileImpactReviewRepository; existing IBaselineJournalProtector and protected audit sink reused.
- ImpactAnalysisReportService; existing ExportService, JSON/CSV/XLSX/HTML writers reused.
- ChangeImpactSession, ChangeImpactViewModel, ChangeImpactView, ImpactChangeRow and ImpactBaselineChoice; existing bounded BaselineTraceabilityGraph drawing control reused.
- IChangeImpactAcceptanceEnvironment, providing separately configured real 9.5/9.7 read-only scenarios.

Models include all requested ChangeImpactRequest, ChangeImpactResult, RequirementChange, AttributeChange, LinkChange, ImpactedRequirement, ImpactPropagationPath, ImpactAnalysisPolicy, ImpactAnalysisFinding, ImpactReviewDecision and ImpactAnalysisSnapshot types. Supporting ImpactRequirementVersion, ImpactAttributeValue, ImpactSnapshotSelection and ImpactPathStep retain identity, value status, hierarchy and versioned evidence. Severity, criticality, review priority, uncertainty, test recommendation and disposition remain separate.

## Changed files

| Area | Added | Modified |
| --- | --- | --- |
| Core | ChangeImpactModels.cs; ImpactAnalysisPolicy.cs; RequirementChangeDetector.cs; ImpactSnapshotProvider.cs; MockChangeImpactSnapshotProvider.cs; ImpactPropagationEngine.cs; ChangeImpactAnalysisService.cs; ImpactReviewService.cs | BaselineComparisonService.cs; BaselineTraceabilityAnalyzer.cs; BaselineLinkSnapshotService.cs; ExportModels.cs |
| Dxl | ChangeImpactAcceptanceEnvironment.cs | None; existing scripts unchanged |
| Export | ImpactAnalysisReportService.cs | ExportSchemas.cs |
| WPF services | ChangeImpactSession.cs | ClientSession.cs; MockDoorsAdapter.cs |
| WPF MVVM/views | ViewModels/ChangeImpactViewModel.cs; Controls/ChangeImpactView.xaml; Controls/ChangeImpactView.xaml.cs | ViewModels/WorkspaceViewModel.cs; MainWindow.xaml |
| Tests | ChangeImpactDetectionTests.cs; ChangeImpactEngineTests.cs; ChangeImpactBoundaryTests.cs; ChangeImpactProviderTests.cs; ChangeImpactReportTests.cs; ChangeImpactWpfTests.cs; ChangeImpactIntegrationTests.cs | WpfClientTests.cs; BulkBaselinePreflightWpfTests.cs (expected eleventh tab; all previous tab order preserved) |
| Documentation / runner | CHANGE-IMPACT-ARCHITECTURE.md; CHANGE-IMPACT-ACCEPTANCE.md; CHANGE-IMPACT-VALIDATION.md; impact-policy.example.json; scripts/Test-ChangeImpactAcceptance.ps1 | README.md |

Core changes are additive except for sharing the old attribute-comparison body without behavior changes. The shared resolver defaults to baseline-only as before; Task 30 explicitly opts in to Current snapshots, validates null-baseline echoes, and retains an actually observed qualified link-module identity even for a known empty scope. Unknown/missing identity is never synthesized from a path.

## Verification evidence

Focused phase evidence is retained under `artifacts/TestResults/task30-*.trx` with build logs under `artifacts/task30-*.log`. Milestones include 51 passing detection/existing baseline tests, 23 passing engine/detection tests, 29 passing engine/report/WPF cases, then 38 passing extended Mock cases plus 16 separately NOT_RUN native cases. The final provider/Task 29 boundary regression phase passed 21 checks. Later additions include four read-port checks and a long-Windows-path journal regression.

The first full x64 run (`artifacts/ChangeImpact/task30-final-x64`) found the review temporary basename exceeding legacy net48/MAX_PATH at a deep export evidence root, plus an old WPF expected-tab header list. The journal now uses a temporary basename no longer than its final basename; a regression test covers it. The old WPF assertion now includes the appended tab. The subsequent x86 intermediate run (`task30-accepted-x86`) had all 43 Task 30 Mock/application cases passing; its one full-suite failure was the still-old compiled header assertion. These failed/intermediate runs are retained, not relabeled as passing.

Intermediate full regressions in `artifacts/ChangeImpact/task30-release-x86` and `task30-release-x64` both passed 1,608 cases, with zero failures and 37 NOT_RUN (43 Task 30 Mock cases). A subsequent identity/count review added a regression that separates unverified references from unique stable requirements, retains unresolved graph nodes and prevents mixing source paths into the target graph's depth projection. The focused boundary/report/WPF phase passed 17 checks before the final graph-scope refinement.

Final evidence directories are `artifacts/ChangeImpact/task30-delivery-x86` and `task30-delivery-x64`. Their `acceptance.json`/CSV and TRX are authoritative for PASS/FAIL/NOT_RUN counts; `summary.json` and `sha256.json` retain final source/product/test binary/evidence hashes. Full-suite counts include all prior tasks. Real cases skipped by an explicit environment gate are NOT_RUN, never PASS.

| Final Release/net48 evidence | x86 | x64 |
| --- | --- | --- |
| Build | PASS; 0 warnings / 0 errors | PASS; 0 warnings / 0 errors |
| Full suite | 1,609 PASS / 0 FAIL / 37 NOT_RUN | 1,609 PASS / 0 FAIL / 37 NOT_RUN |
| Full suite total | 1,646 | 1,646 |
| Task 30 Mock/application | 44 PASS / 0 FAIL | 44 PASS / 0 FAIL |
| Task 30 native | 16 NOT_RUN (8 per release) | 16 NOT_RUN (8 per release) |
| Dense Mock wall time | 31,753 ms | 28,710 ms |
| Managed heap before/after delta | 301,216,288 bytes (287.3 MiB) | 383,121,176 bytes (365.4 MiB) |
| Dense fixture input | 12,000 requirements / 90,000 links | 12,000 requirements / 90,000 links |
| Recorded bounded paths | 114 | 114 |

The dense fixture changes one SYS requirement and uses both graphs, MaximumPaths=2,000, MaximumPathsPerRequirement=2, MaximumDepth=8 and TimeoutSeconds=120. Alternate-path limits deliberately make the dense analysis Truncated/Incomplete; the measurement is not a claim of exhaustive path enumeration. The original fixture construction is outside the measured wall-clock interval; snapshot capture, diff, graph indexing and propagation are included. The memory number is a managed-heap sample delta, not peak working set or COM memory. Native performance is NOT_RUN. The 37 full-suite NOT_RUN cases comprise the 16 new release/scenario cases and 21 gated cases from earlier tasks.

The final identity/count correction passed focused boundary/report/WPF tests before the final full regression. It keeps unverified references out of stable requirement counts and shows unknown graph nodes without borrowing paths from another baseline snapshot. Settings, DesktopSettingsStore, DxlWatchdogSettings and the application icon matched their prior protected SHA-256 values.

## Native limitations / remaining risks

- Real DOORS 9.5 and 9.7 were not accessed for this task. Eight native scenarios per release are prepared; default execution is NOT_RUN. Mock and mocked native-port DTO tests validate application behavior only.
- Task 29's authoritative native set membership/history/access/shareable metadata provider and native sourceVersion/targetVersion/echo compatibility gates remain unvalidated. Real metadata discovery and source/target version bindings must be supplied/verified before production use; the engine does not bypass those gates.
- The shipped DxlBaselineNativeLinkEvidenceReader remains historical-only and explicitly refuses Current. Task 30 implements Current at the read/resolver/analysis ports and tests it with deterministic DTOs; a verified Current native evidence reader is still required. Exact environment/client/operator binding and qualified link-module IDs must be observed, not inferred from entered paths or labels. Single-module default context is caller-bound.
- Native deleted-content, rich text/OLE, real lock/access behavior, non-atomic current capture, STA timeout/drain/reconnect and target-account DPAPI require actual release-specific acceptance. The application may retain partial evidence but cannot label unknown scope complete or infer lost data.
- Rich representation differences do not establish semantic/visual equivalence. Custom/ambiguous type/date representations remain Unknown until mapped. Depth/path/node/time/render limits are explicit; exhaustive impact completeness is not claimed outside the selected observed scope.
- Managed memory samples are not peak process/COM memory. The dense performance measurement is Mock-only and hardware/load dependent. There is no hidden stale cache or production incremental cache in this version.
- Local reviewer decisions are engineering dispositions; they do not change DOORS or establish certification compliance. Journals are API-managed append-only protected local files, not independent WORM storage or native DOORS history.

## Task 31 handoff

Use IImpactSnapshotProvider and IBaselineNativeLinkEvidenceReader for authoritative native/current/archived evidence; IImpactPolicyProvider for YAML/project approval/versioning; IRequirementChangeDetector/IImpactPropagationEngine for alternate rule strategies; IImpactReviewRepository for a governed review store; and ChangeImpactResult/fingerprints/versioned paths plus the additive export tables for downstream workflows. The engine remains independent of WPF progress screens. Candidate findings must not automatically become a DOORS write or mandatory test-execution action.
