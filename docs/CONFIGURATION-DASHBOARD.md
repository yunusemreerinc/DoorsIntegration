# Configuration Management Dashboard — Task 31

The active WPF client has a **Configuration Dashboard** tab. It aggregates existing read services and locally indexed analysis results. Dashboard commands never create, modify, save or delete a DOORS requirement, attribute, link, module, definition or baseline. There is no dashboard write approval/execution flow. The independent/native baseline screens retain their existing approval gates.

## Use

1. Choose Mock/Real and 9.5/9.7 with the existing connection selectors. The banner always identifies the source. Mock uses the existing `/Traceability` Task 29/30 fixture; the older Workspace `/Demo` fixture is a different simulated repository.
2. Open Dashboard. **Load Cached** reads only the matching local snapshot. No implicit COM discovery or analysis is performed when the tab opens.
3. Under **Scope / filters**, choose entire project, multiple folders (individual recursion flags), selected stable formal module identities/current versions, an explicit historical module snapshot, or an existing native Baseline Set. In Real mode, **Observe Real Environment** supplies the actual database/environment binding; **Add observed Workspace module** takes the QUID from a successfully read module. Save/load named local scope profiles to reuse selection, filters and policies.
4. **Refresh Existing Results** refreshes observations and rereads indexed results; it does not silently run graph, quality, impact or baseline verification analysis. Select a single section for partial refresh. Pure analysis-section refresh reads local evidence without repeating the live inventory scan.
5. **Run New Analysis (read-only)** explicitly runs the selected existing analysis services. Baseline-set scope uses `BaselineSetVerificationService`; other scopes use `TraceabilityGraphService`. Quality uses `RequirementsQualityRuleEngine` and loaded/built-in JSON profiles. Impact uses `ChangeImpactAnalysisService` and the selected version/latest existing baseline context. Choose detailed comparisons in the existing Impact/Compare screens when another comparison direction is needed.
6. Baseline, Findings and Activity support drill-down. Exact environment, repository, module QUID, object Absolute Number, typed baseline, analysis/snapshot/finding/job IDs travel in the navigation intent. Replaced, denied, ambiguous or missing sources are rejected. A read-only context panel remains visible in the destination; source modules can be opened from Workspace's **Dashboard scope modules** chooser. Navigating never starts a baseline/write job. New analyses in destination screens remain explicit.
7. History opens immutable aggregate snapshots. Export offers six report kinds in JSON, CSV, XLSX and HTML. Existing export writers retain Unicode, UTC date formatting, Excel cell chunking, CSV formula protection and HTML escaping.

All filters invalidate the complete displayed snapshot together. Page projection retains at most 100 module/finding rows in each grid; grids recycle/virtualize rows and columns. KPI cards, charts, finding tables and exports use the same request key. The shared Workspace progress/cancel/error pipeline runs collection and aggregation off the dispatcher. Original COM work is drained before another operation; cancelling a wait does not stop native execution.

## Meaning of the numbers

- Stable object key = repository + DOORS module QUID + Absolute Number; the typed module baseline/current identity is a separate version key. Object Number/display identifier is never the key.
- Counts over unreadable/hidden/limited inventories are observations/lower bounds, with completeness, source, as-of time, warning and stale state. Unknown/unavailable is not converted to zero.
- `ChangedSinceBaseline` and `UpToDate` require a recent, conclusive latest-baseline → Current comparison that actually compared that module. A modification date is never change proof. Deleted baseline numbering and module/set version differences stay in the existing services.
- Expected-link coverage counts only policy-applicable objects. Missing, unresolved/unknown, access denied and excluded are separate. Unproven eligibility/access/denominator makes the percentage unavailable. Per-project/module/type/baseline charts retain the same uncertainty. A generic module-role graph cannot prove coverage for mixed object roles: these remain Unknown; typed baseline-set verification supports the existing per-object mapping.
- Generic graph confirmation is not native baseline endpoint verification. Verified baseline link counts require the Task 29 typed verification result. Chain counts without chain-analysis evidence are unavailable rather than zero.
- Impact findings, unique impacted persistent requirements and path count are separate. Overlapping/different comparison contexts are not summed. Trends have a separate series key for each baseline/policy/profile/schema scope. Local impact decisions are reread through `ImpactReviewService`; changed snapshot/evidence invalidates their applicability.
- Quality findings and unique affected requirements differ. Error→High, Warning→Medium, Information→Informational is an explicit mapping from the existing rule engine. Draft/unapproved or differently versioned profiles never imply approved quality. Cross-project approval is not assumed; an approved profile must bind the selected canonical project identity. Automated findings and reviewer dispositions stay distinct. No certification decision is made.
- Optional score weights default to 25/30/20/15/10 for baseline/traceability/quality/change review/verification. Every component and formula is shown. Unknown, stale, incomplete or empty unproven categories yield `Incomplete`/`ScoreUnavailable`, not an invented zero/100. These are project defaults, not a DO standard scoring system.
- Audit shows available local histories and indexed analyses only. It does not replace DOORS native history. Failed/cancelled/unknown jobs remain filterable; journal records are read-only.

## Storage, refresh and bounds

Composition is in `DashboardSession`. Local data lives below `<configured root or LocalAppData/DoorsIntegration>/Dashboard/<mode>/<release>`:

- `cache/<request hash>`: protected immutable aggregate files, atomic publication, invalidation generation marker and history.
- `cache/profiles`: protected scope/filter/policy profiles, simple names only.
- `analyses`: append-only protected normalized analysis observations. Object text/rich text and raw native handles are not copied into the dashboard cache or ViewModel. Background normalized inventories/links remain outside the UI.

The cache key includes environment, database, source mode/release, canonical scope/typed versions, filters, policy/profile/schema versions, analysis age, collection limits and score settings. Snapshot identity additionally includes collection time and a unique ID. Each section keeps its own as-of time on partial refresh. Cached old analyses and newer metadata are explicitly non-atomic observations. Default TTL is 300 seconds and analysis age is 86400 seconds.

Memory cache holds eight aggregate keys; disk history scan is bounded (256 files, 64 displayed history entries). Analysis reads use at most 32 recent distinct analyses / two million normalized rows from a bounded file scan. Missing/corrupt/limited local evidence produces warnings. A cache publish failure displays fresh data with a persistence warning; corrupt cache falls back to valid history. Cancellation/read failure retains the committed previous snapshot, never a partly published replacement. No automatic COM retry is introduced.

Analysis indexing is available for dashboard-created graph/quality/impact/verification results and bound existing Impact/Verification runs. Arbitrary legacy report folders or unbound older analyses are **not** guessed into a current repository/policy context. Run or explicitly bind a matching analysis if a section says unavailable.

## Architecture and changed files

The initial plan and reuse mapping are in [CONFIGURATION-DASHBOARD-ARCHITECTURE.md](CONFIGURATION-DASHBOARD-ARCHITECTURE.md).

| Layer | New files / classes |
|---|---|
| Core | `DashboardModels.cs`: request, scope/filter, summary, metric, data quality, finding, trend, module/baseline/traceability/impact/quality/audit health, snapshot, refresh/navigation DTOs; `IConfigurationDashboardService`, `IDashboardDataProvider`, `IDashboardClock` |
| Core | `DashboardEvidence.cs`: bounded normalized facts, analysis records, `IDashboardAnalysisRepository`, diagnostics and set observations |
| Core | `DashboardAggregationService.cs`: `DashboardAggregationService`, `DashboardMetricsCalculator` |
| Core | `DashboardSnapshotService.cs`: `DashboardSnapshotService`, `DashboardCacheService`, `DashboardRefreshCoordinator`, `ConfigurationDashboardService`, `DashboardNavigationService` |
| Application (new net48 project) | `ServiceDashboardDataProvider`, `DashboardReadPorts`, `DashboardAnalysisRepository`, `DashboardResultMapper`, `DashboardSnapshotLinkReader`, `DashboardComCallMeter`, `DashboardReportService` / `DashboardReportKind` |
| WPF | `Services/DashboardSession.cs`, `ViewModels/ConfigurationDashboardViewModel.cs`, `Controls/ConfigurationDashboardView.xaml` and code-behind |
| Tests | `DashboardContractTests`, `DashboardAggregationTests`, `DashboardCacheTests`, `DashboardApplicationTests`, `DashboardSafetyTests`, `DashboardEvidenceTests`, `DashboardPerformanceTests`, `DashboardWpfTests`, `DashboardIntegrationTests` |

Existing files extended: `DoorsIntegration.sln`, WPF project/`ClientSession` factory, `WorkspaceViewModel`, `MainWindow.xaml/.cs`, `ChangeImpactViewModel`, `BaselineTraceabilityViewModel`, `BulkBaselineManagerViewModel`, Core `ImpactSnapshotProvider` (exposes its existing captured reader), `ImpactReviewService` (exact evidence-bound decision lookup), `ExportModels`, Export `ExportSchemas`/`HtmlExportWriter`, Processing `RuleProfile` (parses JSON text through the existing parser), `Package-Wpf.ps1`, README. Two existing WPF tab-order tests now assert all 12 tabs, including the appended Dashboard. No DXL script, native COM API name, watchdog setting or archived WinForms implementation was changed for Task 31. No new third-party dependency was added.

## Build, tests and performance

From repository root (Windows .NET Framework 4.8 targeting pack and pinned SDK required):

```powershell
./scripts/Build.ps1 -Configuration Release -Platform x86
./scripts/Build.ps1 -Configuration Release -Platform x64
# Focused dashboard tests, after the corresponding build:
dotnet test tests/DoorsIntegration.Tests/DoorsIntegration.Tests.csproj -c Release -p:Platform=x86 --no-build --no-restore --filter TestCategory=DashboardMock -- RunConfiguration.TargetPlatform=x86
```

Final counts, performance measurements and artifact paths are recorded in [CONFIGURATION-DASHBOARD-ACCEPTANCE.md](CONFIGURATION-DASHBOARD-ACCEPTANCE.md). TRX keeps all outcomes including skipped real tests; native unconfigured tests use explicit `NOT_RUN` reasons. The performance fixture has 100 formal modules, 10,000 requirements, 100,000 normalized links, three set-count observations, 5,000 quality and 2,500 impact findings. It measures aggregation/index/cache/export plumbing, not native COM discovery, engine graph traversal, server throughput or an SLA. Peak working set is the entire test process; managed delta is not peak allocation. Full regressions run many other tests, so isolated and full-run memory/time differ.

## Java Code ↔ DOORS integration points

1. Add a separate read-only code-analysis provider/index producing versioned observations; retain repository/commit/symbol identity separately from DOORS QUID/object/version identity.
2. Add a policy-versioned mapping between source symbols, tests and DOORS requirements. Persist mapping provenance and review status; do not use method display names as stable IDs or infer historical mappings from current source.
3. Feed explicit typed link/snapshot evidence through `TraceabilityGraphService`, `BaselineSetVerificationService` and `ChangeImpactAnalysisService`; keep unknown code or requirement endpoints unresolved.
4. Extend normalized dashboard records/metrics with a new versioned section or provider decorator. Reuse request scope, cancellation, cache identity, data-quality rules and existing report schemas/writers. Include code commit/build/tool version in cache and comparable trend keys.
5. Extend navigation intents with a separately validated source-code URI/commit/symbol identity and read-only destination. Current dashboard navigation contains no command that can modify DOORS or source code.
