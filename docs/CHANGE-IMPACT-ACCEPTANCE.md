# Task 30 — read-only acceptance and use

## Repeatable offline build/test

From the repository root:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Test-ChangeImpactAcceptance.ps1 -Architecture x64 -FullSuite
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Test-ChangeImpactAcceptance.ps1 -Architecture x86 -FullSuite
```

Without `-FullSuite`, only Task 30 tests run. The script forces `DOORS_TEST_MODE=disabled`, restores previous environment settings afterward, rejects an existing output directory, captures restore/build/test logs, TRX, JSON/CSV PASS/FAIL/NOT_RUN results, report examples, three WPF screenshots and Mock performance measurements. It runs the existing offline restore/build host (SDK 5.0.416, net48/C# 7.3), not a newly installed framework. Architecture restores must not overlap; never rebuild DLLs while the same architecture is testing. No dependency was added.

## WPF workflow

1. Open **Change Impact Analysis** (eleventh tab; existing tab positions remain intact). Mock/Real banners and the read-only analysis label remain visible independently of the existing write toggle.
2. Select ModuleBaselines, BaselineToCurrent or BaselineSets. The direction is always Source → Target. Module comparisons use repository identity, the native qualified module identity, canonical path and explicit major/minor/suffix. `Load Existing Baselines` reads metadata only. For Current, numeric target baseline inputs are ignored.
3. For set comparison, provide the folder, definition and two set identities, or copy the target already selected in Native Baseline Set Manager. Set mode compares the modules observed in the two sets; module-form inputs do not filter set membership. Set version and member baseline versions remain separate.
4. Choose Source, Target or Both for propagation. Both means separate traversals of two graphs, never a union graph that invents paths across versions. The graph display explicitly selects one snapshot at a time.
5. Load/edit project JSON policy, set maximum link depth, then Run Analysis. Global progress/cancellation belongs to the existing background tool pipeline. Cancellation stops additional work; a started COM call is not forcibly aborted. A source COM timeout prevents target dispatch and automatic retry.
6. Inspect Changes (100 requirements/page, up to 64 changed attribute rows per requirement), graph node details and review findings (first 1,000 matching findings). Search filters the full captured findings; reports retain all recorded attributes/findings. The graph draws at most 300 nodes/1,000 edges; Expand Graph loads another 100-node slice. Display search/depth/render caps do not silently truncate the exported recorded analysis. Analysis depth/node/path/time limits do mark the result Truncated/Incomplete.
7. Enter reviewer and rationale, then save a disposition, Approve Impact or Reject Impact. These are local engineering dispositions, not DOORS write approvals or certification decisions. Protected append-only review records survive restarts. Versions, evidence, direction, scope or policy changes require fresh review.
8. Export JSON, CSV, XLSX or HTML to a new directory. Unique identified requirement candidates, unverified references and path counts are separate metrics. Unverified references do not receive fabricated native requirement IDs. No path found is not proof of no engineering impact. Test candidates do not mandate test re-execution; an authorized engineer must approve the action.

## Project policy format

[impact-policy.example.json](impact-policy.example.json) is an illustrative, unapproved policy. This implementation accepts JSON through `IImpactPolicyProvider`; a YAML provider can be registered later without changing the engine. Defaults/criticality rules are project examples, not DO-178C/DO-200B/DO-330 requirements.

The existing .NET DataContract JSON convention serializes enums numerically:

| Setting | Values |
| --- | --- |
| Severity | 0 Critical, 1 High, 2 Medium, 3 Low, 4 Informational, 5 Unknown |
| Direction | 0 Incoming, 1 Outgoing, 2 Both |
| CycleBehavior | 0 Allow, 1 Report, 2 Prohibit |
| AttributeKinds | 0 Text, 1 Integer, 2 Real, 3 Boolean, 4 Date, 5 Enum, 6 MultiEnum, 7 Unknown |

Custom type names are not guessed; supply AttributeKinds when native type names are insufficient. Integer/decimal/Boolean comparisons use invariant parsing. Date comparisons accept ISO dates/timestamps with explicit UTC handling; ambiguous native locale strings are Unknown. MultiEnum uses a configured newline-separated representation, never an assumed arbitrary native serialization. Plain text is Unicode NFC/line-ending normalized. RTF/OLE remains opaque exact content; a representation-only change is not proof of semantic/visual equivalence. Parent identity and relative order of common siblings detect movement; inserting a new sibling alone does not move all later objects.

## Separate real 9.5 / 9.7 acceptance

**Real production readiness is not established.** Task 29 still ships unvalidated native set metadata and link compatibility providers. Task 30 does not enable those gates, invent DXL perms, or claim that the presence of scripts validates native behavior. Module diff uses existing read ports; real propagation additionally requires authoritative environment/operator/client facts and reviewed historical/current endpoint-version and link-module identity evidence. Exact Current, deleted object, rich text, lock/access and echo behavior must be checked in each supported release/build. DPAPI must be checked under the actual Windows account. No real environment was used for this task.

Existing scripts reused: `requirements.dxl`, `links.dxl`, `baseline-link-versions.dxl`, baseline metadata and native baseline-set read scripts. No Task 30 DXL mutation script is created. Current support in the shared resolver is opt-in for Task 30; Task 29's default remains baseline-only.

Use pre-existing read-only fixtures. Do not create/modify baselines, objects, links, attributes, modules, locks or sets as part of this acceptance run. Record build/patch level, architecture, database/repository identity, operator, exact source/target version locators, request/policy hashes, duration, access limitations, TRX and native reference evidence. Local audit/report output is not a replacement for DOORS history.

| Native scenario / expected observation | 9.5 | 9.7 |
| --- | --- | --- |
| BaselineLoad: stable module + Absolute Number mapping across two existing baselines | NOT_RUN | NOT_RUN |
| Current: explicit baseline → current read, no save/discard; non-atomic observation disclosed | NOT_RUN | NOT_RUN |
| Deleted: preserve source version/content; denied/unavailable inventory is not deletion | NOT_RUN | NOT_RUN |
| RichText: semantic versus opaque RTF/embedded OLE difference; no OLE activation | NOT_RUN | NOT_RUN |
| HistoricalLinks: actual sourceVersion/targetVersion/echo, source test links retained | NOT_RUN | NOT_RUN |
| BaselineSets: mixed member versions and same stable modules matched correctly | NOT_RUN | NOT_RUN |
| Permissions: inaccessible/locked/write-open module remains Unknown/AccessDenied/Incomplete; no lock changes | NOT_RUN | NOT_RUN |
| LargeModule: 10,000+ objects, measured read time/memory, bounded paths and cancellation | NOT_RUN | NOT_RUN |
| Transport: COM timeout leaves uncertainty, no automatic retry/target dispatch; drain/reconnect verified separately | NOT_RUN | NOT_RUN |
| Windows account protection: protected audit/review survives restart, tampering rejected | NOT_RUN | NOT_RUN |

Automatic native cases are separately categorized `ChangeImpactReal` (eight scenarios per release). To run in a prepared environment, explicitly set `DOORS_TEST_MODE=real-change-impact`, supply `DOORS_CHANGE_IMPACT_ENVIRONMENT_TYPE` as an assembly-qualified `IChangeImpactAcceptanceEnvironment`, then use a filtered `dotnet test` invocation. The fixture must supply exact client build, manual/runtime evidence, real-only read service and the eight named read-only requests. Missing environment, family or scenario yields NOT_RUN. The offline acceptance script intentionally never enables real mode.

Record PASS/FAIL only after observing the actual expected native behavior. Mock PASS never counts toward these cells. No automatic repair, link recreation, baseline mutation, rollback or retry exists in the impact engine.
