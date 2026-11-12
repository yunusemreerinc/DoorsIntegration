# TraceabilityGraphService

`DoorsIntegration.Core.TraceabilityGraphService`, mevcut `IRequirementService.ReadAsync` ve `ILinkService.ReadAsync` üzerinde salt okunur yönlü graph üretir. DOORS/COM bağımlılığı analiz algoritmalarına taşınmaz; policy, identity, graph ve impact modelleri Core içindedir. `DoorsIntegration.Export.TraceabilityGraphExporter` JSON, CSV ve GraphML biçiminde dışa aktarır. Yeni NuGet bağımlılığı veya DXL/COM API adı eklenmemiştir.

Console ve legacy Desktop DI hostları `ITraceabilityGraphService` / `ITraceabilityGraphExporter` sağlar. WPF session `CreateGraphService(logger)` / `CreateGraphExporter()` ile aynı servisleri sunar. Bu değişiklik servis/export API'sidir; WPF'in mevcut 150 ilişkilik çizimi otomatik olarak tam graph/impact analiz ekranına dönüştürülmemiştir.

## Kullanım

```csharp
var policy = new TraceabilityPolicy(
    "requirements-to-standards-v1",
    new[] {
        new TraceabilityLinkRule(
            "require-standard", "Requirement", new[] { "Standard" },
            "/Project/Trace links", TraceabilityDirection.Outgoing,
            minimumLinks: 1, maximumLinks: 5,
            versionPolicy: TraceabilityVersionPolicy.SameVersion)
    },
    cycleRules: new[] {
        new TraceabilityCycleRule("review-cycles", TraceabilityCycleBehavior.Report)
    });

var request = new TraceabilityGraphRequest(
    "caller-verified-repository-id",
    new[] {
        new TraceabilityModuleScope("/Project/Requirements", "Requirement"),
        new TraceabilityModuleScope("/Project/Standards", "Standard")
    },
    new[] { "/Project/Trace links" }, policy);

var graphService = new TraceabilityGraphService(requirements, links, logger);
var graph = await graphService.BuildAsync(request, progress, cancellationToken);

// Absolute Number belongs to a specific versioned formal module. Object Number is not an identity.
var seed = graph.Nodes.Values.Single(n => n.ModulePath == "/Project/Requirements"
    && n.VersionKind == LinkVersionKind.Current && n.AbsoluteNumber == 17);
var impact = graphService.AnalyzeImpact(graph, new[] { seed.Id },
    TraceabilityDirection.Outgoing, maxDepth: 5,
    includePossibleEdges: false, cancellationToken: cancellationToken);

var exporter = new TraceabilityGraphExporter();
exporter.ExportFile(@"C:\Reports\trace.json", graph, TraceabilityGraphFormat.Json, impact);
exporter.ExportFile(@"C:\Reports\trace.csv", graph, TraceabilityGraphFormat.Csv, impact);
exporter.ExportFile(@"C:\Reports\trace.graphml", graph, TraceabilityGraphFormat.GraphMl, impact);
```

Parent output directory must already exist; each destination must be a new file. Requirements/standards are caller-configured module **roles**, not guessed DOORS attribute names or certification claims. Other roles such as HighLevel, LowLevel or TestCase use the same API. Formal module paths and link-module paths are exact; no wildcard discovery is performed.

## Acquisition, scope and read-only guarantees

The request explicitly lists module/version scopes and link-module paths. Each module is read once with attribute DXL disabled and deleted objects metadata-only. Each module × link-module pair is read once with Both directions and endpoint resolution enabled. Native link readers may open endpoints according to their existing bounded read-only behavior, but this service does not recursively collect all linked modules or crawl the whole database. Add modules/versions explicitly to enlarge the multi-hop analysis scope.

Snapshots are enumerated once and disposed immediately. Requirement text/rich payload is not retained in the graph; identity, display Identifier and deleted state are retained. Link endpoint/reference/surrogate/access metadata is retained in observations. Graph, adjacency and analysis results are bounded in memory, not an out-of-core database. Defaults are 100,000 nodes, 250,000 observations, 500,000 findings and 64 Mi characters of retained metadata. Counts, native file budgets and read timeouts have distinct meanings. A graph budget fails with `TraceabilityGraphLimitReached`; a partial result is not returned as a complete graph.

`BuildAsync` runs acquisition/analysis off the caller's thread; the existing DXL executor still owns its COM STA. It calls no create/update/delete, edit schema, write-preview or write-apply API. No new DXL script is introduced. Snapshot IDs/counts/echoed module/version/profile/connection contexts are validated. Incompatible native IDs, duplicate object identities or malformed metadata fail explicitly. Observer/logger failures cannot trigger native retries.

Cancellation is cooperative. Native wait timeout/disconnect/cancel exceptions propagate immediately; the graph service does **not** catch-and-continue, repeat a read, force-release COM or submit another scope. The original executor completion remains owned by the existing adapter/host. Hosts must retain their normal drain/close lock until the original native call and cleanup settle. Read-only does not make a blocked COM call safely abortable. The graph spans sequential, non-atomic snapshots.

## Object, baseline and link-module identities

- Identity includes the caller-bound repository namespace, native qualified formal-module ID when observed, version context and **Absolute Number**. Object Identifier is display data; Object Number, source order and heading are never join keys.
- If native module identity is unavailable, the node explicitly uses a path-scoped identity. Unknown source object references receive observation-local placeholders, not fabricated persistent object IDs. Current and baseline identities remain distinct.
- Baseline scope uses structured major/minor/suffix. Native `versionString` is opaque. Source/target anchor evidence binds its raw label to the selected request; all anchor labels are collected before endpoints are joined. An optional `observedBaselineVersion` is a caller-supplied binding and must agree with observed anchor evidence. Ambiguous or conflicting bindings fail. An unbound external baseline is retained as an opaque version; it is not guessed from formatting or silently mapped to current.
- `SameVersion` requires equal kind and version context, and returns Unknown when a selected baseline would need comparison with an unbound opaque label. This is a policy comparison of version labels, not proof that independently numbered module baselines belong to the same baseline set. `CurrentCounterpart` and `AnyExplicitVersion` allow explicitly configured cross-version relationships.
- Every edge retains the exact link-module path. `LinkReadSummary` now additionally accepts optional **adapter-observed** `QualifiedAnchorModuleId` and `QualifiedLinkModuleId`; the original constructor is preserved for source/binary compatibility. The current native v1 link protocol supplies paths, not those optional IDs. No native IDs are inferred from paths, registration or another link observation.
- A policy may require a specific native link-module ID. Missing evidence returns Unknown; an observed different ID is a model violation. A path-only native reader cannot satisfy an ID-required policy. Native ID drift between snapshots fails; a native ID observed in one scope is not imputed to a legacy/path-only observation in another scope.

Incoming reads retain **source → target** direction. Compatible outgoing/incoming observations of the same directed relation share an edge. Observed directions, scope IDs, sequence numbers, raw endpoint versions, access and surrogate metadata remain separately exportable. Observation count is not the physical count of duplicate DOORS links; policy cardinality counts distinct counterpart objects. Edges with different native/path-only identity evidence remain distinguishable.

## Policy results

`TraceabilityLinkRule` configures anchor role, expected counterpart roles, exact link-module path, direction, minimum/maximum distinct counterparts, version policy and optional required native link-module ID. `RejectUnexpectedCounterparts` controls model violations. Unknown role/version/identity or unconfirmed relationship evidence is separately reported as `LinkModelUnknown`, even when minimum is zero.

| Code | Meaning |
|---|---|
| ExpectedLinkSatisfied | Enough distinct confirmed counterpart objects were observed |
| MissingExpectedLink | Below minimum with complete relevant inventory/link scope and no eligible uncertain relation |
| ExpectedLinkUnknown | A missing-link conclusion would depend on incomplete/ambiguous evidence |
| MaximumLinksExceeded | Positively observed distinct counterparts exceed the configured maximum |
| UnexpectedLinkModel | Confirmed counterpart role/version/native link-module identity violates the rule |
| OrphanRequirement | No confirmed or possible incident relation within all requested link modules, with complete relevant scopes |
| OrphanUnknown | An orphan conclusion is unsafe because scope or relationship evidence is incomplete |
| InaccessibleSource / InaccessibleTarget | Original access/resolution reason is retained |
| DeletedObjectExcluded | Deleted inventory object is excluded from active expected-link/orphan checks |
| ScopeIncomplete | Native summary reports truncation, denied/unresolved/reference/cleanup or other incomplete evidence |
| NativeLinkModuleIdUnavailable | Exact path is retained, but native link-module ID was not observed |

Orphan evaluation defaults to role Requirement and Warning severity; configure `orphanRoles` / `orphanSeverity` as needed. It is scoped to the requested link modules, not a whole-database assertion. Incoming source references, echoed links, FilterOnly link-module matches and unresolved/inaccessible/deleted endpoints do not count as confirmed coverage.

Cycles use iterative strongly connected component detection. Self-loops are included. A result lists the SCC's member node/edge IDs; it does not enumerate all elementary cycles, which can be exponential. A default cycle is **reported as Information**, not an automatic error. Rules can Allow, Report or Prohibit cycles and filter by participating roles and link-module paths. Supplied rules are evaluated independently; they are not first-match overrides. Prohibit alone produces `CycleProhibited` / Error. Cycles are detected on confirmed edges; zero observed cycles is not proof of global acyclicity, especially in incomplete scope.

## Multi-hop impact

`AnalyzeImpact` supports Outgoing, Incoming and Both, multiple seed node IDs, hop depth, visit and traversed-edge budgets. Iterative BFS terminates on cycles and reports shortest hop distance in the selected adjacency. Visits include seeds at distance zero, predecessor node/edge and confidence of that selected path. It does not automatically classify a downstream requirement as semantically affected or certify impact completeness.

The default follows confirmed edges. Skipped possible/reference edges mark an uncertain frontier rather than producing a conclusive “no impact” result. Optional possible-edge traversal propagates uncertainty along its predecessor path. External/uninventoried or inaccessible endpoints, depth/size limits and incomplete graph scope prevent a conclusive impact result. Alternative paths are not ranked for semantic confidence; a reported possible path remains possible even if another path may be confirmed. Unknown seed IDs and oversized seed collections are rejected, never silently truncated.

Impact traverses observed relationships, including confirmed links that violate the expected model; policy violations are not silently removed from the graph. Conclusive traversal within the requested evidence scope is not a policy-compliance assertion, a global database claim or proof that non-atomic reads describe one instant.

## Exports

The exporter streams records from the bounded graph without making another native call. UTF-8, invariant numbers and UTC timestamps are used; streams supplied by the caller remain open. `ExportFile` writes a unique private staging file, flushes it, checks cancellation and moves it to a new destination. Existing files are not replaced; failures/cancellation do not publish the staging payload.

- **JSON**: schemaVersion 1, format `doors-traceability-graph`, typed `records` tagged by kind. Graph, ModuleScope, LinkRule, CycleRule, ReadScope, Node, Edge, Observation, Finding, Cycle/CycleNode/CycleEdge and optional Impact/ImpactVisit/ImpactBoundary/ImpactEdge records preserve context and evidence. Unicode JSON escapes round-trip exact strings, including surrogate pairs.
- **CSV**: one normalized table with `RecordType, Id, SourceId, TargetId, NodeId, RuleId, Code, DataJson`. DataJson contains the complete typed record. All cells use RFC-style double quoting; the shared CSV injection detector protects formula-like cells, including leading controls/format characters and full-width prefixes. A protective tab affects the presentation cell only; exact original values remain in DataJson. SCC membership and impact edges are separate records rather than oversized membership cells.
- **GraphML**: directed graph in `http://graphml.graphdrawing.org/xmlns`, real node/edge IDs and source/target references, JSON metadata data keys. Auxiliary graph evidence is streamed as JSON Lines inside a graph data element. It is a GraphML-style interchange with custom metadata, not a guarantee that every third-party viewer understands those annotations. XML structure, declared keys and endpoint integrity are unit tested; official XSD/third-party desktop viewer validation was not performed. No external entities or embedded executable content are emitted.

Impact exports must belong to the same GraphId. Requirement text/RTF bodies are outside this graph format; use the existing requirement/export APIs to retain full rich text.

## Tests and native acceptance

Unit tests use mock services and prohibit every write/edit-schema method. They cover direction/deduplication, identity and native evidence, opaque baseline binding, expected/missing/max/model policies, access/reference/echo/filter cases, default/allowed/prohibited cycles, SCC separation/self-loops, bounded multi-hop impact, deleted objects, cancellation/no retry, budgets, Unicode/injection/no-overwrite exports and DI composition. A 5,000-object chain and 5,000 independent self-loops exercise iterative algorithms; this is not a million-node memory/performance benchmark.

Real DOORS 9.5/9.7 acceptance remains NOT_RUN in this environment. On an approved read-only test scope, compare sampled edges/versions/link-module paths and access states with the native client, test baseline opaque labels and incomplete permissions, and inspect each export in the intended consumer. Current v1 native metadata cannot prove native link-module ID policies. No database writes are required for these checks.
