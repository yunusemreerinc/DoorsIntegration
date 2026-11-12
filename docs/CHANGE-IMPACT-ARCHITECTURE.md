# Task 30 — Change impact analysis implementation plan

The solution targets net48/C# 7.3. Core contains read ports, baseline comparison and baseline-aware graph DTOs. Dxl owns file-based scripts/COM dispatch; the WPF session factory composes Mock/Real services. Export already streams JSON/CSV/XLSX/HTML. Existing Task 29 metadata and native link evidence gates are intentionally unvalidated in Real mode.

1. Add Core snapshot/selection/policy/change contracts. Extract the existing baseline attribute comparison implementation into a shared internal helper without changing its behavior. Add typed comparisons and separate persistent object identity from version identity.
2. Capture each requested read-only scope once; reuse RequirementService, LinkService, BaselineSetVerificationService, BaselineTraceabilityAnalyzer, BaselineTraceabilityComparisonService and the graph projection. Explicitly choose source/target/both graphs. Add bounded BFS with alternate simple paths, test candidates, progress/cancellation and uncertainty limits.
3. Persist reviewer decisions only locally, bound to version/policy/evidence hashes. Reuse the protected local journal protector and append-only audit pattern. Add export tables to the existing streaming export service.
4. Add a WPF/MVVM tab with selection, diff, bounded graph expansion, review and export. Exercise deterministic Mock scenarios and 10,000+ objects; run focused phase tests and full x86/x64 regression. Native 9.5/9.7 scenarios remain separately gated NOT_RUN without real evidence.

No write port is injected into the engine. No new DXL perm or native compatibility claim is introduced. Current-module captures are non-atomic observations; no hidden cache is used. Snapshot date/revision and complete evidence hashes are retained. Real baseline-set membership/shareable and historical endpoint-version support remain subject to Task 29's validation gates.

## Implemented components

| Component | Responsibility / reuse |
| --- | --- |
| IChangeImpactAnalysisService / ChangeImpactAnalysisService | Capture Source then Target; diff, compare links, classify, propagate, restore local reviews and append existing protected audit entries. Freeze mutable request/policy inputs. Timeout/uncertainty never dispatches an automatic retry. |
| IImpactSnapshotProvider / ServiceImpactSnapshotProvider | Existing RequirementService, LinkService and BaselineSetVerificationService read ports. Capture custom attributes/tombstones, validate request echoes and qualified module identity. Module comparisons let the resolver consume captured objects without a second COM object read. Set comparisons reuse full existing verification then enrich custom attributes with an additional read pass; a future batched provider can combine these reads. |
| MockDoorsAdapter / MockChangeImpactSnapshotProvider | Same application service pipeline with explicit synthetic A/B observations; independent selected member versions and injectable deterministic boundary fixtures. No native validation claim. |
| IRequirementChangeDetector / RequirementChangeDetector | Shared BaselineComparisonResult attribute implementation, plus typed canonical values, unknown attributes, lifecycle flags, parent identity and relative common-sibling order. A stable module identity plus native Absolute Number is the object key. |
| IImpactPolicyProvider / JsonImpactPolicyProvider | Bounded JSON configuration; extension port for YAML or project-approved policy storage. |
| ImpactClassificationService | Project severity candidates, separate criticality and review priority; no standard-defined severity claim. |
| IImpactPropagationEngine / ImpactPropagationEngine | Reuse BaselineTraceabilityAnalyzer endpoint classification and TraceabilityGraphService baseline projection/indexes. Bounded BFS enumerates alternate simple paths; Source and Target are traversed separately. |
| ImpactReviewService / IImpactReviewRepository / FileImpactReviewRepository | Protected local append-only, locked, atomic frames; same versions/policy/evidence required for decision restoration. No DOORS attribute/link write. |
| ImpactAnalysisReportService | Existing export spool, Unicode/chunks, CSV protection, Open XML XLSX and escaped HTML writers; additive export tables. |
| ChangeImpactSession / ChangeImpactViewModel / ChangeImpactView | Existing WPF background operation/error/cancellation pipeline, paged virtualized diff, bounded existing graph drawing surface, local review and report export. |

Public models include ChangeImpactRequest/Result, RequirementChange, AttributeChange, LinkChange, ImpactedRequirement, ImpactPropagationPath/ImpactPathStep, ImpactAnalysisPolicy, ImpactAnalysisFinding, ImpactReviewDecision, ImpactAnalysisSnapshot, ImpactRequirementVersion, ImpactAttributeValue and ImpactSnapshotSelection. BaselineVersionReference is reused; native module/object identity and version identity remain separate. A composite baseline locator is not advertised as a native baseline GUID.

## Semantics and limits

- A record can have content, hierarchy, custom attribute and link flags together. PrimaryType is a display summary. Unavailable attributes set Unknown even if another observed attribute changed; absence from an incomplete inventory cannot prove Added/Deleted. Deleted records retain the source object/version/content.
- Rich text is opaque exact representation. A rich-only difference is a formatting/representation candidate; equivalence of rendered RTF/OLE is not asserted. Numeric/Boolean/ISO date values are typed when parseable; unknown/custom/locale-specific representations require a policy mapping and otherwise remain Unknown.
- Link diffs reuse BaselineTraceabilityComparisonService. Version mappings are based on independently observed selected module identities/versions. They do not assert undocumented native automatic echo behavior. Access/unresolved/absent-reference observations are not link deletion or loss. Incoming endpoint changes can seed a selected target requirement too.
- Each path stores root persistent/version identities, graph snapshot identity/label, every versioned edge identity, link module/type/direction, depth and baseline context. The two graphs are never joined into a path. Different routes are kept up to configured limits; a node never repeats within one simple path. Cycle components follow the existing project policy and remain visible without declaring every cycle an error.
- MaximumRequirements/Links/SnapshotCharacters bound capture; MaximumDepth/Nodes/Paths/PathsPerRequirement/Timeout bound propagation. Reaching a limit means Truncated/Incomplete. Traversal node budget counts distinct root/target-version states; UniqueImpactedRequirementCount instead counts distinct persistent target identities across roots/versions. Path count is a third, separate metric.
- Direct/indirect is available through path depth even when a candidate is categorized External/Test. A possible or access-denied edge is an UnresolvedImpact and is not used to invent a further chain. Source historical test candidates do not satisfy target test coverage. Test rerun recommendations remain engineering-review candidates.
- Unidentified graph references have a separate ReferenceIdentity and IdentityVerified=false; they are not given a fabricated persistent requirement identity. UniqueImpactedRequirementCount counts identified persistent candidates only; UnverifiedImpactReferenceCount reports unverified references separately. The graph can display unresolved nodes, and its depth projection uses paths from the displayed snapshot only, including when other modules keep the same baseline across the two selections.
- Current capture is non-atomic. No implicit snapshot cache or incremental diff cache is used: avoiding stale/native-version substitution takes priority in this first implementation. CacheKey retains environment, repository, full evidence fingerprint and capture date. A supplied stale snapshot forces Incomplete. Future incremental providers must verify immutable version/revision bindings first.
- Reviewer restoration uses exact source/target snapshot evidence, comparison direction, policy, versioned finding/path and reason hashes, excluding only volatile run/capture timestamps for otherwise identical evidence. A new run ID alone does not erase a decision. Changed evidence/versions/policy do. API-managed append-only files are not WORM storage or a replacement for native history.
- ManagedMemoryDeltaBytes is a before/after managed-heap sample, not peak process memory, private bytes or COM memory. Elapsed time is end-to-end service time; the performance test also records outer wall time. Native performance remains unmeasured.

## Native integration boundary

No new DXL perm was added. Existing scripts/COM STA dispatch are reused; analysis never calls create/save/delete/link/update or a write approval flow. A COM timeout is uncertain; source timeout stops target capture, and readers retain responsibility for late result cleanup.

The default Real composition is deliberately **not production-ready**. The inherited native set metadata provider and link compatibility provider remain unvalidated. Single-module environment/repository facts are caller-bound until an authoritative provider supplies them; the reviewed native link reader additionally requires exact observed client/operator/environment/link-module evidence. The shipped historical native link reader explicitly declines Current modules; Current propagation is implemented/tested at the resolver/engine ports, but a verified current-version native evidence reader is still required. Merely approving a compatibility gate is insufficient. Native set membership/history/shareable metadata, actual historical/current source/target versions and echo, deleted content/RTF, and target-account DPAPI must be separately implemented/validated in 9.5 and 9.7. Tests remain NOT_RUN without such a fixture; Mock results do not substitute for those checks.

## Task 31 integration points

| Port / output | Extension point |
| --- | --- |
| IImpactSnapshotProvider | Add an authoritative release-specific native provider, immutable archived snapshots or a validated incremental provider; preserve repository/module/version and uncertainty metadata. |
| IBaselineNativeLinkEvidenceReader | Supply verified Current/historical version evidence with qualified link-module IDs; retain Task 29 runtime/manual gates and the single STA queue. |
| IImpactPolicyProvider | Add YAML/project policy repository, approval/versioning and organization-specific rule mappings. |
| IRequirementChangeDetector / IImpactPropagationEngine | Replace policy/comparison/traversal strategies without changing WPF or native adapters; preserve resource bounds and read-only contracts. |
| ChangeImpactResult / ImpactAnalysisSnapshot | Stable identities, versioned paths, fingerprints and analysis ID support downstream change-event/review workflows. Future consumers must not equate a candidate with an approved write/test action. |
| IImpactReviewRepository | Add a governed local/team review store and concurrency policies; do not silently reuse decisions against changed evidence. No external messaging is configured. |
| ImpactAnalysisReportService / additive ExportTableKind values | Add report layouts or delivery connectors through the existing export service, with explicit user authorization for external publication. |
| ChangeImpactSession | Inject alternate providers into WPF composition. The engine has no dependency on WPF or progress screens. |
