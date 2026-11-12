# Dashboard — separate DOORS 9.5 and 9.7 native acceptance

**Current status: NOT_RUN for both releases. No actual DOORS runtime was used in Task 31. Mock/protocol/WPF results are not COM/DXL validation.** No new DXL function or version-compatibility assertion is introduced by this feature; it reuses the existing release-gated readers. Native writes stay disabled.

Run the following checklist twice, on an actual authorized read-only test repository: once on a recorded exact 9.5 client build and once on a recorded exact 9.7 build. Record client/server build, client/host bitness, ProgID registration, database/environment identity, operator, session, request/policy/profile versions, elapsed time and logs. Keep credentials out of logs/attachments.

| Check | Expected evidence | 9.5 | 9.7 |
|---|---|---|---|
| Project/module metadata | Compare discovered paths/types/QUIDs against visible DOORS scope; overlapping folders count once; denied/hidden inventory remains partial | NOT_RUN | NOT_RUN |
| Baseline metadata | Existing/latest numbering, suffix/date and deleted-baseline cases match existing readers; no change proof from dates alone | NOT_RUN | NOT_RUN |
| Baseline Set status | Match actual definition, set identity/open state and typed module baselines; hidden membership is Unknown, not Missing | NOT_RUN | NOT_RUN |
| Requirement counts | Match active/deleted accessible counts in explicit Current and historical scope; object keys use QUID/Absolute Number | NOT_RUN | NOT_RUN |
| Links | Verify source/target actual baseline versions, incoming/outgoing, echo, external and denied endpoints; policy coverage denominator retains unknowns | NOT_RUN | NOT_RUN |
| Attribute access | Read-only schema totals for Current; restricted definitions stay incomplete; historical catalog is not replaced with Current catalog | NOT_RUN | NOT_RUN |
| Read-only consistency | Before/after inspect DOORS object/module/baseline/link history, module edit/dirty/lock state; no create/save/discard/unlock/write; no Attribute DXL evaluation | NOT_RUN | NOT_RUN |
| COM exception / timeout | Use prepared denied/closed/unavailable fixture and controlled client disconnect; previous snapshot retained or partial status, no silent retry; native completion/session uncertainty/drain visible | NOT_RUN | NOT_RUN |
| Large module refresh | Explicit scope ≥10,000 objects; cancellation, UI response, counts, COM attempts, duration/memory and cleanup recorded against actual hardware | NOT_RUN | NOT_RUN |
| Cache / snapshot validity | Second cached open has zero additional COM attempts; policy/baseline/filter/environment/release changes miss cache; TTL/stale/as-of and corrupt-cache fallback checked | NOT_RUN | NOT_RUN |

Automated native smoke cases are `DashboardIntegrationTests.NativeReadOnlyDashboardReleaseAcceptance`, ten scenarios per release. They construct the existing Real client session and use the actual shared native queue. They check basic native observation/source/scope invariants; manual expected counts/history/link evidence above are still required for acceptance. No external mock test provider can make these tests native PASS.

To enable **only** these explicitly reviewed read-only tests:

```powershell
$env:DOORS_TEST_MODE='real-dashboard'
$env:DOORS_DASHBOARD_VERSION='9.5' # repeat separately with 9.7
$env:DOORS_DASHBOARD_SCENARIO_ROOT='C:\approved\dashboard-scenarios'
dotnet test tests/DoorsIntegration.Tests/DoorsIntegration.Tests.csproj -c Release -p:Platform=x86 --no-build --no-restore --filter TestCategory=DashboardReal -- RunConfiguration.TargetPlatform=x86
```

Prepare `9.5-Metadata.json`, `9.5-Baselines.json`, `9.5-BaselineSets.json`, `9.5-Requirements.json`, `9.5-Links.json`, `9.5-Attributes.json`, `9.5-ReadOnlyConsistency.json`, `9.5-ComExceptions.json`, `9.5-LargeRefresh.json`, `9.5-Cache.json` as serialized `DashboardRequest` files with actual observed environment/repository/QUIDs, `IsMock=false`, matching `ClientVersion`, explicit canonical scope and policies. Use separate `9.7-*.json` files on the other installation. The baseline-set case requires an existing selected set; the exception case requires an intentionally unavailable/denied read scope; the large case must actually contain ≥10,000 accessible requirements. Missing enablement, release or fixture files report NOT_RUN. Invalid/inconsistent configured scenarios fail instead of claiming native success.

Use deployment-approved COM client/host architecture. x64 application build success does not prove x64 COM registration/client compatibility. Preserve Task 28/29 shareable-edit/link-loss risk findings and original snapshots; dashboard never repairs suspected missing links or closes/reconciles native sets automatically.
