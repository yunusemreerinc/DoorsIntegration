# Task 31 — implementation and acceptance evidence

Feature/architecture/file/class inventory: [CONFIGURATION-DASHBOARD.md](CONFIGURATION-DASHBOARD.md). Native procedures and remaining release checks: [CONFIGURATION-DASHBOARD-NATIVE-ACCEPTANCE.md](CONFIGURATION-DASHBOARD-NATIVE-ACCEPTANCE.md).

## Automated evidence

Stage builds: models, aggregation, snapshot/cache, application adapters/reporting and WPF all compiled. Focused stage runs progressed through **4, 15, 22, 29, 39, 40 and 45 PASS** tests. The last 45-case focused run includes 43 dashboard cases plus the two existing full-window/tab-order checks. Builds used Release/net48/C# 7.3 and the existing pinned SDK; no new NuGet dependency.

Final x86/x64 results and measured timing table are generated below from final TRX/performance artifacts. Earlier `dashboard-regression-*` runs are retained as development evidence: their two failures were outdated hard-coded tab-count expectations (11 instead of 12), corrected while preserving/asserting all existing tab positions. They are not the accepted final run.

<!-- FINAL_RESULTS -->
| Release full regression | PASS | FAIL | NOT_RUN |
|---|---:|---:|---:|
| x86, build 0 warnings / 0 errors | 1612 | 0 | 65 |
| x64, build 0 warnings / 0 errors | 1612 | 0 | 65 |

Each architecture includes 43 dashboard Mock PASS and 20 new dashboard native NOT_RUN cases. The remaining 45 NOT_RUN cases are existing native tests. After the final stable environment-selector binding fix, the four affected dashboard/full-window/tab-order tests were rerun on each architecture: **4 PASS, 0 FAIL**. These repeat tests are not added to the unique test count.

| Normalized Mock scale measurement | x86 | x64 |
|---|---:|---:|
| Initial empty-cache load (ms) | 0.477 | 0.633 |
| Aggregation (ms) | 95.048 | 116.577 |
| Refresh including publication (ms) | 480.475 | 494.192 |
| Disk snapshot load (ms) | 782.076 | 885.081 |
| Memory cache load/clone (ms) | 235.45 | 282.79 |
| 100k-link evidence save (ms) | 1328.144 | 1299.861 |
| 100k-link evidence load (ms) | 1752.306 | 1764.469 |
| Managed net positive delta (bytes) | 0 | 0 |
| Peak entire test-process working set (bytes) | 662552576 | 916553728 |
| Native COM member attempts | 0 | 0 |
| Provider calls | 1 | 1 |
| Observed cache ratio | 0.333 | 0.333 |

Zero net managed increase can result from garbage collection; it is not zero memory usage/allocation. The initial empty-cache timing is not a measured WPF cold-start SLA.

Raw final evidence: `artifacts/Dashboard/Final/dashboard-final-{x86,x64}.trx`, `dashboard-selector-{x86,x64}.trx`, `final-summary.json`, `final-test-results.csv`, `{x86,x64}/performance-{x86,x64}.json` and WPF screenshots. Mock sample exports: `artifacts/Dashboard/SampleReports/ConfigurationSummary-{Json,Csv,Xlsx,Html}`.

These are measured normalized Mock aggregation/index/cache results, not native DOORS performance or certification claims.
<!-- END_FINAL_RESULTS -->

Mock tests do not prove native COM/DXL behavior. The new native dashboard suite has **10 scenarios for 9.5 and 10 for 9.7**, all NOT_RUN without the explicitly configured real environment. Other pre-existing native tests remain separately skipped/NOT_RUN. No actual DOORS data was changed.

## Required Mock scenario coverage

| # | Requested scenario | Deterministic evidence |
|---|---|---|
| 1 | Single project | `DashboardAggregationTests` / `ExistingMockAdapterRunsOverviewTraceQualityImpactAndCachesWithoutWritingDoors` |
| 2 | Multiple projects | `MultipleProjectsAndNestedFolderFiltersDoNotCrossCount` |
| 3 | Multiple folders | `MultiProjectAndFolderSelectionUsePathBoundariesAndPerFolderRecursion` |
| 4 | Subfolders | Same folder test plus nested-folder safety test |
| 5 | Duplicate module | `OverviewDeduplicatesOverlappingFoldersModulesAndObjects` |
| 6 | Never baselined | `BaselineHealthRequiresConclusiveLatestBaselineToCurrentEvidence` |
| 7 | Changed since baseline | Same conclusive-comparison test; stale evidence rejected separately |
| 8 | Missing set members | `MissingMembersAndOpenSetAreSeparateFromDeniedModule` |
| 9 | 100% expected coverage | `CompleteCoverageAndUnknownDenominatorAreDifferent` plus existing Mock adapter E2E |
| 10 | Missing expected links | `MissingLinkIsNotAnUnresolvedReference` |
| 11 | Unknown links | `CompleteCoverageAndUnknownDenominatorAreDifferent` |
| 12 | Incomplete verification | `CompleteObservedCoverageWithIncompleteAnalysisNeverClaimsVerifiedCompleteness` |
| 13 | Impact findings | `MultipleRootsDoNotDoubleCountUniqueImpactedRequirements` |
| 14 | Pending/local review | Same count test; `ExistingImpactResultsReReadApplicableLocalReviewDecisions` |
| 15 | Quality severities | `QualityFindingSeverityAndUniqueRequirementCountsRemainSeparate` |
| 16 | Stale analyses | Cache-only stale test, stale conclusive comparison and stale score tests |
| 17 | Cache invalidation | `InvalidationSurvivesRestartWithoutDeletingSnapshotHistory` |
| 18 | Refresh cancellation | `CancellationRetainsPreviouslyCommittedSnapshotAndDoesNotStartAnalysisOnRefresh` |
| 19 | AccessDenied | `AccessDeniedAndPartialInventoryNeverProduceVerifiedCompleteCoverage` |
| 20 | Partial collection | `MockAdapterPartialInventoryRetainsDeniedModuleAndReadableCounts`; corrupt analysis/cache warnings |
| 21 | Drill-down | `DrillDownPreservesExactVersionAndRejectsDeniedOrForeignSources`; WPF navigation/context E2E |
| 22 | Reports | Six report kinds × JSON/CSV/XLSX/HTML, Excel package parsing, CSV injection and HTML escaping checks |
| 23 | ≥10,000 requirements | `HundredModulesTenThousandRequirementsHundredThousandLinksAndThousandsOfFindings` |
| 24 | ≥100,000 links | Same scale fixture; normalized analysis persistence/reload validates all 100,000 links |
| 25 | Health score | `ConfigurableScoreHasTransparentWeightsAndRejectsStaleInputs` |

Additional invariants cover no raw requirement/link inventory in aggregate caches, cache-only zero provider calls, partial section timestamps, reusable protected profiles/path traversal rejection, different rule/comparison contexts kept separate, selected-version mismatch rejection, stable-identity uncertainty, native member-call metering and virtualized bounded WPF lists. WPF rendering tests produce PNG artifacts for visual inspection. No automated test changes DOORS records or accepts a dashboard write confirmation.

## Measurement interpretation

Hardware/environment observed: AMD Ryzen 5 3600 6-Core Processor, 12 logical processors, Windows NT 10.0.19045, CLR v4/net48 test host. Installed physical RAM could not be queried through the available CIM permissions and is not asserted. Runs below are Release x86/x64 test processes on that workstation, not dedicated native DOORS load tests. Both architecture regressions ran concurrently, so timings include workstation contention.

Scale: 100 formal modules across two simulated projects, 10,000 requirements, 100,000 normalized link relationships, three baseline-set count observations, 5,000 quality findings, 2,500 impact findings and 10,000 path-count observations. The fixture expands existing Mock typed schema/identity patterns; it is deliberately a normalized aggregation/index/cache benchmark, not proof of graph traversal or native server throughput. Actual MockDoorsAdapter E2E service tests run separately on its declared small baseline fixture.

Aggregation uses stable-key dictionaries/sets, grouped module counts and indexed membership rather than repeated finding × object scans. Only aggregate DTOs and bounded page projections reach WPF. Incremental refresh replaces requested sections by key while retaining other section timestamps; it does not guess object-level changes from stale cache. The benchmark records initial empty cache open, background refresh including cache publication, aggregation, disk snapshot reload, memory cache load, normalized analysis save/reload, managed memory delta, process peak working set, provider/COM call counts and observed cache ratio. Zero native COM calls in Mock is expected. Cached snapshots copy/validate their bounded aggregate payload; timings include that work.

## Limits and native acceptance still required

- No 9.5 or 9.7 native runtime, ProgID/session/bitness, typed historical link, permissions, schema, large-module or lock/dirty-state behavior was exercised here. The existing API compatibility gates remain authoritative.
- Native visibility filtering and cached baseline-set enumerations cannot prove hidden inventory completeness. Unknown counts/coverage remain explicit.
- Native Current reads are non-atomic; real cache freshness, observer consistency and the shared queue after client disconnect need the separate checklist. Timeout/cancellation never means the native operation definitely ended.
- Generic module-role graph coverage for mixed requirement types stays Unknown; typed baseline-set verification provides per-object policy mapping. Full historical attribute catalogs are not substituted with Current schema.
- Indexing covers bound existing Impact/Verification results and dashboard-created analyses. Unbound historical report files are not automatically reinterpreted. Detail navigation preserves normalized evidence and preselects a validated source; it does not fabricate a full graph from aggregate rows or implicitly rerun an analysis.
- Configuration score, quality findings, coverage and reports are engineering aids with explicit sources/limitations; none automatically approves certification, audit evidence or DO-178C/DO-200B/DO-330 compliance.

Java Code ↔ DOORS extension points and identity/cache/navigation rules are documented in the feature guide. Existing Independent Baseline and Task 1–30 write approval flows remain outside this read-only dashboard.
