# Task 31: Configuration Management Dashboard

Implementation plan, based on the existing solution:

1. Core adds serializable, bounded scope/filter/evidence/metric contracts. Stable module QUID + Absolute Number identify objects; typed baseline version identifies an object version. No display number becomes a key.
2. Core aggregation projects one validated scope into overview, baseline, traceability, impact, quality and audit sections. Inaccessible, unknown, stale and incomplete observations remain explicit. Scores are optional project weights, never certification decisions.
3. Snapshot/cache services persist aggregate DTOs locally, key by environment/database/version/scope/policies/filters, retain section timestamps/history and publish atomically. UI never holds the raw requirement/link inventory. Cache-only load never calls DOORS.
4. A new Application project composes existing ProjectService, ModuleService, RequirementService, IBaselineMetadataService, INativeBaselineSetService, graph/verification/impact and RequirementsQualityRuleEngine ports. Existing results are recorded independently of dashboard snapshots. Refresh does not implicitly run an analysis. Baseline health changes require an actual conclusive diff.
5. Refresh coordination serializes dashboard requests, supports section refresh/cancellation/progress, and preserves previous snapshots on collection/cache failures. Existing COM/DXL queue/gate remain authoritative; no new DXL or write dispatch is introduced.
6. WPF adds an appended Dashboard tab with cache/as-of status, scope/filter/profile controls, KPI/charts, virtualized/paged details, history, export and stable-identity drill-down through a validated navigation intent. Existing tab positions and MVVM flows are preserved.
7. Reporting reuses ExportService's JSON/CSV/XLSX/HTML writers. Summary and charts retain as-of, source analysis IDs, policy/profile versions and limitations.
8. Build and focused tests follow each major layer; final x86/x64 regressions and deterministic Mock performance tests remain separate from native 9.5/9.7 NOT_RUN acceptance.

Existing service equivalences: Quality is `RequirementsQualityRuleEngine`; NativeBaselineSetManager is `INativeBaselineSetService`; bulk history is `IJobHistoryService`; typed endpoint and baseline coverage results come from `BaselineSetVerificationService` / `BaselineTraceabilityAnalyzer`. Graph policy evidence comes from `TraceabilityGraphService`. Existing `ImpactReviewService` remains the review authority. The archived WinForms project is not reintroduced.

The dashboard dispatches no baseline/create/link/requirement mutation command; reused service interfaces retain their existing write APIs for other application screens. Read-only analyses may write local snapshot, report and analysis/audit files only. All numbers are observations within the stated scope; hidden DOORS inventory is not inferred.
