# Baseline Traceability Verification — usage and acceptance

This feature is read-only. It has no baseline creation, module/requirement update, link recreation, save/discard, lock/unlock, repair or rollback operation. Local audit/report files are written. Mock PASS is application verification only, not DOORS integration, link-loss reproduction, DPAPI validation or a certification decision.

## Build / repeat offline acceptance

From the repository root:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Test-BaselineTraceabilityAcceptance.ps1 -Architecture x64
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Test-BaselineTraceabilityAcceptance.ps1 -Architecture x86
```

`-FullSuite` runs all regression tests; otherwise it runs the Task 29 Mock and separately gated native cases. `-NoBuild` requires matching current Release binaries. The process-scoped execution-policy argument does not change persistent policy. The runner always forces `DOORS_TEST_MODE=disabled`, restores environment settings afterward and preserves previous evidence. Do not overlap a rebuild with a test process using the same architecture's DLLs; do not restore different architectures concurrently.

Builds use net48/C# 7.3 and SDK 5.0.416 through `artifacts/SmokeBuildHost`, the existing offline NuGet configuration and packages. No package dependency was added. Direct build: `dotnet build ../../DoorsIntegration.sln -c Release -p:Platform=x64 --no-restore` from that host after restoring for x64 (repeat x86). Evidence includes TRX, PASS/FAIL/NOT_RUN JSON/CSV, logs, a WPF PNG, JSON/CSV/XLSX/HTML examples and Mock-only performance metrics.

## WPF workflow

1. Open **Baseline Traceability** (the tenth tab; existing tab indices are preserved). Read-only default mode can remain enabled.
2. Use **Mock Demo** for a prepared offline SYS → HLR → LLR → TEST chain. It seeds read fixtures in memory; it does not call any baseline/link creation API. Alternatively select an existing Native Manager set, then **Use Selected Native Set**.
3. Set the exact folder, definition identity and set identity. Explicit link-module paths are separated by semicolons/newlines. Selected module IDs can narrow requirement/link analysis; blank selects all definition modules. Set membership validation still describes the whole set.
4. Expand the project policy JSON. Configure stable module-ID roles or a read-only custom type attribute, direction, expected counterpart types, minimum/maximum counts, version policy and cycle behavior. Minimum links defaults to one when omitted; an explicit zero remains optional. `SameVersion` here means the selected set baseline of each counterpart module; different modules do not need equal version numbers. Review the project profile before relying on its results.
5. **Verify Read-only** captures metadata before and after the non-atomic requirement/link read. The overview distinguishes closed/open, full set scope, selected verification scope, confirmed missing members and denied/unknown members.
6. Select a graph node for stable module/baseline/object identities, type/status, heading/text, original rich-text representation and requirement findings. Select a directed edge for source/target versions, link-module identity and echo/resolution. Search/type filtering and incoming/outgoing/both multi-hop impact use the captured graph, without COM calls.
7. The drawing surface is capped at 300 nodes and 1,000 edges, with scrollbars; result grids show at most 2,000 rows with virtualization. Those display bounds do not truncate export. Capture/analysis bounds are separate and explicitly mark results incomplete when reached. The existing graph scope-header limit is 64 module versions.
8. **Capture Reference Now** preserves the state observed now, not a fabricated pre-creation state. **Import Recorded Reference** reads a matching Task 28 create-set journal's actual stored reference. Its legacy link-only shape lacks complete requirement inventory/typed historical baseline evidence; limitations are retained and absence may remain indeterminate. No opaque historical label is parsed as a guessed current version. The API accepts richer externally supplied snapshots and explicit adapter-validated expected rebindings.
9. Results distinguish errors, warnings, information, unknown/access-denied and requirement-level findings. Coverage is over policy-required objects. Unknown objects remain counted; unknown inventory/eligibility makes the overall denominator and percentage indeterminate. Known-scope lower/upper bounds are separate. Minimum-link evidence can be valid while incomplete enumeration still makes overall status Incomplete.
10. **Export Report** supports JSON, CSV, XLSX and HTML through the existing bounded spool/atomic-publication pipeline. CSV text is neutralized; XLSX uses string cells; HTML escapes all content and has a restrictive CSP. Credential-like content is redacted. Raw rich/OLE text is retained as a representation, not executed. Export completion is not verification success.

Unknown/Incomplete is never presented as Passed. Confirmed policy errors can make overall status Failed while access limitations remain explicit. An open complete set is PassedWithWarnings; an open partial set is Incomplete. Confirmed missing baselines in a closed set fail. Echoed external links with verified endpoints can be valid; an internal echo with contradictory context needs review rather than an automatic data-loss accusation. Cycles follow Allow/Report/Prohibit policy.

## Automated Mock scenarios

| Required scenario | Test evidence |
|---|---|
| Complete SYS → HLR → LLR → TEST, closed set | ReadOnlyScenarioClassification / CompleteChain |
| Missing HLR → LLR, upstream broken chains | ReadOnlyScenarioClassification / MissingHlrLlr |
| Wrong target baseline version | ReadOnlyScenarioClassification / WrongTargetVersion |
| Inaccessible target / permission denied, not Missing | InaccessibleTarget, AccessDenied and requirement-denial boundary case |
| Confirmed missing module baseline | MissingModuleBaseline |
| Open complete / open partial set | OpenComplete / OpenPartial |
| Valid external links and expected external echo | ValidExternalLink / EchoedExternalLink |
| Same persistent requirement in two baseline versions | DifferentVersionsOfSamePersistentObjectAreDifferentGraphNodes |
| Cycles Allow/Report/Prohibit | CyclesFollowProjectPolicy |
| Incoming/outgoing multi-hop and subset vs full set | GraphImpactIncomingOutgoingAndSubsetRemainVersionBound |
| Before/after, changed target, expected version rebinding | snapshot/comparison tests |
| Shareable risk and suspected loss vs inaccessible evidence | SnapshotComparisonDistinguishesAccessUncertaintyFromSuspectedLoss |
| Incoming/outgoing observations vs physical duplicates | IncomingOutgoingObservationsAreNotPhysicalDuplicates / DuplicateLink |
| 12,000 requirements and 90,000 directed links | TwelveThousandRequirementsNinetyThousandLinksUseBoundedIterativeAnalysis (90-second bound) |
| Non-atomic drift, scope limits, unknown policy eligibility | MetadataDriftLimitsAndUnknownEligibilityCannotPass |
| Protected append-only audit / persistence failure | ProtectedAuditIsAppendOnlyAndAuditFailureCannotBePassed |
| All four formats, Unicode, rich text, CSV/HTML injection | AllReportsPreserveVersionIdentityStatusCoverageRichTextAndSafeText |
| Native protocol correlation, typed actual endpoint versions | NativeVersionProtocolUsesTypedActualVersionsAndEchoWithoutCurrentFallback |
| Default native gate does not start COM | UnvalidatedNativeVersionGateNeverStartsCom |
| Verification never calls adapter.ExecuteAsync; legacy import read-only | AdapterMetadataAndLegacyReferenceImportNeverDispatchNativeMutations |
| Missing typed fact, incomplete enumeration with satisfied minimums | boundary tests; cannot Pass |
| MVVM default read-only, graph/details, impact, references, reports | BaselineVerificationWpfTests |

The dense graph uses the existing iterative component/impact algorithms; no recursive traversal proportional to requirement depth is added. Stable version/object hash caches and shallow copies of immutable references avoid serializing the same version once per dense edge. Default capture limits are 100,000 requirement nodes, 250,000 observations, 500,000 findings and 64 MiB metadata text. Limits and cancellation are explicit. No retry is issued after native read timeout; the original executor completion owns COM/result cleanup.

## Native 9.5 checklist — NOT_RUN

Use an **already prepared** read-only fixture. Do not create, close, baseline, edit or repair anything as part of this suite. Record exact build, architecture, database/user/session, scripts' SHA256 and evidence references.

| Check | Status |
|---|---|
| Installed 9.5-specific manual: sourceVersion(Link/LinkRef), targetVersion(Link), ModuleVersion, baseline(ModuleVersion), echo overloads | NOT_RUN |
| Read-only definition/set/member identity, typed major/minor/suffix, open/closed | NOT_RUN |
| Membership visibility including denied/deleted/purged modules; false baselineExists is not proof of absence | NOT_RUN |
| Closed-set definition revision/historical membership (definition may have changed since creation) | NOT_RUN |
| Actual outgoing sourceVersion/targetVersion vs current module; incoming LinkRef target anchor evidence | NOT_RUN |
| Internal set links, external echoes, same-module links and reciprocal resolution | NOT_RUN |
| Module/object/link-module access denied and unresolvable endpoints | NOT_RUN |
| Configured-shareable flag separate from current edit mode; report unknown when unsupported | NOT_RUN |
| Existing before/after snapshots and validated expected rebinding; no retrospective invention | NOT_RUN |
| Prepared risky two-PC history/snapshots from IBM alert, inspection only | NOT_RUN |
| Queue timeout/cancellation, native completion drain, no automatic retry/repair | NOT_RUN |
| Owned read-only handle cleanup; borrowed/edit modules left intact | NOT_RUN |
| Actual DPAPI account/profile and sanitized audit/report outputs | NOT_RUN |

## Native 9.7 checklist — NOT_RUN

Run independently with the exact 9.7 build and its manual, using prepared read-only data.

| Check | Status |
|---|---|
| Compile/review actual sourceVersion, targetVersion, ModuleVersion, baseline, echo overloads | NOT_RUN |
| Exact definition/set/module-baseline identities and typed versions | NOT_RUN |
| Full membership/access evidence and historical definition revision | NOT_RUN |
| Actual outgoing source/target versions and incoming LinkRef anchor semantics | NOT_RUN |
| Expected external echoes / reciprocal links within the same set | NOT_RUN |
| Denied modules/objects/link modules and unresolved references | NOT_RUN |
| Configured-shareable risk vs edit mode | NOT_RUN |
| Complete comparable snapshots and expected version rebinding | NOT_RUN |
| Existing two-PC link-loss evidence, inspection only; no reproduction writes | NOT_RUN |
| STA timeout/queue drain/cancellation and cleanup | NOT_RUN |
| Native Windows DPAPI and exported audit/report integrity | NOT_RUN |

## Native integration fixture / unresolved behavior

`BaselineVerificationIntegrationTests` contains separate 9.5 and 9.7 read-only cases. Default runs are **NOT_RUN**. A controlled site may set `DOORS_TEST_MODE=real-baseline-verification` and `DOORS_BASELINE_VERIFICATION_ENVIRONMENT_TYPE` to an assembly-qualified `IBaselineTraceabilityAcceptanceEnvironment`. Run only `--filter TestCategory=BaselineVerificationReal`. The fixture must provide an actual read-only service, prepared nonempty link data, exact client build/database and reviewed manual/runtime evidence. There is no write acknowledgement or native mutation call in these cases.

The shipped Real composition deliberately uses unvalidated metadata and version-evidence gates. The Task 28 native read/cache gate also remains intact. Therefore Real verification reports Unknown/NOT_RUN until validated providers are supplied through DI. It does not silently run against current modules.

`IBaselineSetVerificationMetadataProvider` must provide authoritative per-module membership/access facts, stable IDs, typed baseline references and correct current/historical definition scope. Native iterators may hide inaccessible members. `baselineExists(ModuleVersion)` also checks readability, so false is insufficient to label Missing. The provider must query configured-shareable state using a verified release API, not infer it from `isShare` or an exclusive/read-only edit state. Unsupported configuration stays Unknown. No unverified perm is invented.

`ReviewedBaselineLinkEvidenceCompatibility` pins target family, exact build, database, user and the new external script hash, requiring release/manual and runtime source/target/echo evidence. `DxlBaselineNativeLinkEvidenceReader` runs only on the shared STA executor, attach-only, with a final dispatch guard. It obtains typed outgoing `sourceVersion(Link)` / `targetVersion(Link)`; incoming uses `sourceVersion(LinkRef)` and the observed target anchor ModuleVersion because `targetVersion(LinkRef)` is not documented. LinkService still supplies actual object access/resolution. Ambiguous joins remain Unknown, not guessed versions. The script only loads/closes its own read-only anchor and releases ModuleVersion metadata handles; it does not delete database baselines.

Native shareable detection, historical definition visibility, endpoint/echo runtime semantics, dense native performance, native cleanup/timeout behavior and DPAPI are **not validated here**. Mock cannot satisfy those acceptance items.

## IBM warning

[IBM's DOORS 9.x baseline-set link-loss alert](https://www.ibm.com/support/pages/ibm-doors-9xreproducible-scenario-where-baseline-set-links-are-lost) describes silent loss with configured edit-shareable modules and a two-PC sequence. This engine reports risk and SuspectedLinkLoss only with suitable recorded/comparable evidence; it does not prove causality, automatically recreate links or execute the alert's destructive reproduction steps. See [architecture and API evidence](BASELINE-TRACEABILITY-ARCHITECTURE.md).
