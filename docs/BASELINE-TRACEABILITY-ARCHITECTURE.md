# Task 29 — baseline-set traceability verification design

## Existing architecture reviewed before implementation

Task 28 provides Native definition/set/member DTOs, a read adapter, protected append-only intent history, a reference-link snapshot, guarded native deployment, and a separate WPF Native mode. Independent jobs remain separate. LinkService already reads baseline ModuleVersion endpoints, echo and access outcomes through external `links.dxl` on the shared STA executor. TraceabilityGraphService already has version-aware node IDs, directed edges, iterative strongly connected components and bounded multi-hop impact. BaselineComparisonService already keys objects by qualified module + persistent Absolute Number, preserves rich attributes and treats incomplete reads conservatively. ExportService already streams JSON/CSV/OpenXML with Unicode, UTC dates, text neutralization, bounds, progress and cancellation.

The new engine will compose those services. It will not call the Native writer, Independent runner, baseline/link/module/requirement mutation services, save, delete or repair. Jobs here are read-only verification jobs, not baseline creation jobs. Existing journal protection/audit sanitation and export contracts are reused.

## Semantics and boundaries

- A BaselineVersionReference binds repository + stable module ID + typed major/minor/suffix (or explicit Current), separately from the enclosing set version. A module version locator is not represented as a fabricated native GUID. Object identity is stable module identity + Absolute Number; graph identity additionally includes module baseline identity. Display Object Number/Identifier never matches records.
- Definition membership, set membership and requested verification subset are distinct. Absence is confirmed only with authoritative complete membership evidence; hidden/inaccessible records become AccessDenied/Unknown. Open partial sets remain Incomplete. Closed sets with confirmed missing members fail.
- The resolver consumes actual versioned link observations through LinkService plus adapter-observed stable identity/version bindings. Paths and current module state cannot manufacture historical version evidence. Native incoming LinkRef has sourceVersion but no documented targetVersion(LinkRef); target evidence must be the observed anchor ModuleVersion or a resolved Link, explicitly identified as such.
- Existing graph representations/algorithms are reused; the new analyzer adds set-aware binding, requirement-type rules, coverage and chain propagation. SameVersion rules in this engine mean the selected baseline of each counterpart module, not equal numeric versions across different modules. Cycles follow project policy.
- Coverage is over policy-required objects only. Known unresolved objects remain in the denominator. Unenumerated modules/unknown policy eligibility make the overall denominator indeterminate; percentage is nullable and known-scope bounds are reported separately. Missing and unknown are never interchangeable.
- A reference snapshot is optional. Without one there are no historical removed-link or link-loss claims. Comparison distinguishes confirmed identity changes, version changes, explicitly evidenced expected rebinding, unresolved evidence and observation-vs-physical duplicate uncertainty. Incoming/outgoing/echo observations are not automatically physical duplicates.
- Shareable configuration itself is distinct from current edit mode. Unknown configuration is disclosed. Removed reference links under complete comparable scopes and risky configuration produce SuspectedLinkLoss for investigation, never automatic repair or a proven data-loss assertion.
- Status precedence preserves Unknown/Incomplete ahead of a misleading Passed; confirmed failures remain explicit findings even with incomplete scope. Open complete sets receive a warning. All results retain environment, source (Mock/Native), timestamps and limitations.

## Primary API review

[IBM 9.7.2 DXL Reference](https://www.ibm.com/docs/en/SSYQBZ_9.7.2/com.ibm.doors.requirements.doc/topics/dxl_reference_manual.pdf) documents `ModuleVersion sourceVersion(Link|LinkRef)`, `ModuleVersion targetVersion(Link)`, `bool echo(Link)`, `bool echo(LinkRef)`. Echo represents a baseline link lacking a reciprocal link back to that baseline; links within the same module/set have different expected behavior from external echoes. Echo alone does not establish loss.

The [IBM-authored manual hosted on jazz.net](https://jazz.net/dxl/resource/dxl_reference_manual.pdf), printed pp. 310–312 and 403, corroborates ModuleVersion, baseline(ModuleVersion), moduleVersion, data/load, sourceVersion/targetVersion and echo. Its cover is **9.6.1**, not 9.5. Release-specific 9.5 documentation/runtime verification remains required; 9.7 documentary review is not runtime acceptance. Unverified shareable-configuration or native identity perms will not be invented.

[IBM's edit-shareable baseline-set link-loss alert](https://www.ibm.com/support/pages/ibm-doors-9xreproducible-scenario-where-baseline-set-links-are-lost) describes silent loss in a two-PC sequence and mitigations including avoiding configured-shareable modules and populating all definition modules together. This task only inspects evidence and reports risk. It does not reproduce destructive steps or modify DOORS.

## Stages

1. Baseline build/Task 28 Mock regression; API review.
2. Core DTOs, ports, snapshot/resolution, graph projection, analyzer/comparison/orchestration; Mock scenario and performance tests.
3. Native read-only adapters, explicit API/runtime gates, version-evidence script, audit and JSON/CSV/XLSX/HTML reports; boundary tests.
4. WPF Baseline Traceability tab, bounded graph/search/detail UI and MVVM/render tests.
5. Release x86/x64 builds, full regression, separate native 9.5/9.7 NOT_RUN cases, final changed-file/evidence report.

Real writes remain disabled; this feature exposes no DOORS mutation method. Mock results never validate COM/DXL.
