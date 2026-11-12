# Task 28 — Architecture and DXL compatibility review

## Existing solution analysis (Tasks 1–27)

The ten-project net48 solution already provides late-bound DOORS COM, the shared STA DxlExecutor/operation gate, external DXL scripts and escaped parameter files; immutable module IDs/canonical paths; lazy multi-folder selection; permission/lock/dirty preflight; explicit approvals; durable Independent job recovery; append-only audit with DPAPI protection; streaming JSON/CSV/OpenXML exports; LinkService/traceability; WPF MVVM and an offline acceptance package.

`BaselineMode.Independent`, its request validation, numbering, runner and journal wire formats remain unchanged. Native set membership creates baselines via a different native operation, so it must not be emulated by looping Independent create calls. Native sets receive separate definition/set/module-version models and a set-stage orchestrator. Shared components are selection, preflight facts, COM STA transport, operation coordination, journal protection/options, sanitized logging, link reader and export writers. The existing independent runner and native runner share the coordinator primitive rather than treating a set as a fictitious formal module.

## Compatibility findings before implementation

Primary documentation:

- [IBM 9.7.2 DXL reference](https://www.ibm.com/docs/en/SSYQBZ_9.7.2/com.ibm.doors.requirements.doc/topics/dxl_reference_manual.pdf): indexed declarations checked for definition create/lock/unlock, addBaselines and close. Direct PDF fetch returned 403, so this is documentary evidence, not local compilation/runtime validation.
- [IBM-authored 9.6.1 manual on jazz.net](https://jazz.net/dxl/resource/dxl_reference_manual.pdf), printed pp. 313–327: accessible corroboration; its cover explicitly says 9.6.1, not 9.5 or 9.7.
- [IBM link-loss alert](https://www.ibm.com/support/pages/ibm-doors-9xreproducible-scenario-where-baseline-set-links-are-lost): warns about shareable setup and incomplete set population. The listed tested builds include 9.7 releases. This review does not invent a tested 9.5 result.

| Operation | Documented form / handling | 9.7 | 9.5 |
|---|---|---|---|
| Definition creation | `create(Folder, string, string, BaselineSetDefinition&)` | Signature corroborated | Release-specific manual/runtime pending |
| Set creation | `create(BaselineSetDefinition, bool, string, string, BaselineSet&)` | Signature corroborated | Pending |
| Membership population | `addBaselines(Skip, BaselineSet)`; Skip values are ModName_ | Signature corroborated | Pending |
| Presence | `isBaselinePresent(BaselineSet, ModName_)` | Documentary corroboration | Pending |
| Open / close | `isOpen(BaselineSet)` / `close(BaselineSet)` | Signature corroborated | Pending |
| Definition edits | addModule/removeModule, rename/setDescription, save | Documentary corroboration | Pending |
| Lock lifecycle | lock/unlock on definition; check returned error strings | Signature corroborated | Pending |

Mutating operations return nullable error strings; successful definition locking requires explicit release. Set creation/closure and addBaselines require the definition lock. Definition edits need save. `read(bsd)` can discard unsaved definition edits and is not used as a harmless refresh. `addBaselines` creates module baselines; set versions and module baseline versions are not interchangeable. The API can reject an entire supplied list; application recovery still treats uncertain transport outcomes conservatively.

No unverified native definition GUID perm is invented. Native definition locators are explicitly identified as logical folder/name addresses unless a validated inspection provider supplies a native identity. Stable formal module identity is always required. A rename changes a logical locator and must be captured explicitly, never followed silently by path alone.

## Planned layers and safety invariants

- Core: definition/set DTOs, typed set version distinct from BaselineVersion, immutable mutation requests/preflight/authorization, adapter and journal contracts, link snapshots/diffs, and set workflow service.
- Mock: full definition/set state, permissions/locks/shareable/dirty fixtures, persisted state independent of the job journal, one open set per definition, deduplication, partial/unknown fault injection and reconciliation.
- DXL: separate read and write scripts using documented operations, supplied through the existing executor/STA queue. Default compatibility and native inspection reject. Enabling requires release/build/script/context evidence, validated state inspection and link behavior; there is no UI toggle bypass.
- Native job: durable intent before each confirmed mutation; post-read verification; uncertain/partial state remains open. Reconcile never blindly repeats creates. Close is a separately approved operation and requires complete membership plus verified pre-close links; post-close link integrity is recorded. No delete/rollback API.
- Link guard: capture source/target object/module/link-module identities and version context before set population, compare baseline-bound links before close and after close. Incomplete access/unresolved IDs are not evidence of integrity. Shareable configuration itself, not just current edit mode, is checked; unknown/risky configuration blocks writes.
- WPF: two explicit workspace modes inside the existing manager. Independent is default. Native uses existing folder/module selection and workspace progress/cancellation, but separate plans/approvals and definition/set controls. Mock/Real and disabled real writing remain clear.
- Reporting: use the existing streaming export framework with additive set/audit/link tables, JSON and XLSX. Audit plans/results and unknown/partial states remain distinct; reports do not replace DOORS native history.

## Stage validation plan

1. Baseline build and existing Task 27 acceptance before implementation.
2. Core model/workflow/persistence/link tests and build.
3. DXL protocol/gating tests; separate 9.5/9.7 live cases report NOT_RUN without a real validated environment and exact write consent.
4. Mock/WPF end-to-end/render tests, JSON/XLSX verification, final x86/x64 build and complete regression.

No native execution or DOORS data mutation is performed during this review. Documentary API checks alone never enable writes.
