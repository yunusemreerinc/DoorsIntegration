# Native Baseline Set acceptance (Task 28)

Native mode is separate from Independent Module Baselines, which remains the default. Mock exercises the application workflow only. It does not compile or validate DXL/COM. Both real release families remain **NOT_RUN**. No DOORS writes were performed during development.

## Build and offline acceptance

From the repository root, run `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Test-NativeBaselineSetAcceptance.ps1 -Architecture x64` and repeat for `x86`. This changes execution policy only for that process. The runner forcibly sets `DOORS_TEST_MODE=disabled`, restores it afterward, creates a fresh evidence directory, builds all ten projects and executes the Mock and gated native cases. Use `-NoBuild` only with matching current Release binaries. Do not run restores for different architectures concurrently; project assets are shared.

The runner produces PASS/FAIL/NOT_RUN JSON/CSV, TRX, logs, Mock JSON/CSV/XLSX examples and a rendered WPF screenshot. Full regression: from `artifacts/SmokeBuildHost`, `dotnet test ../../tests/DoorsIntegration.Tests/DoorsIntegration.Tests.csproj -c Release -p:Platform=x64 --no-build --no-restore -- RunConfiguration.TargetPlatform=x64` (repeat for x86 after building that architecture).

## Offline scenarios

| Scenario | Evidence |
|---|---|
| Multi-folder formal membership, deduplication and definition CRUD | NativeBaselineSetTests.DefinitionCrudUsesMultiFolderStableIdsAndDeduplicates |
| Major/Minor, suffix, annotation, separate set/member versions | CompleteWorkflowSeparatesSetVersionFromModuleVersion (two cases) |
| One open set; open set prevents definition edits | OpenSetBlocksSecondSetAndDefinitionChanges (five cases) |
| Missing module / partial population remains open; explicit recovery | PartialPopulationStaysOpenAndReportsMissingWithoutAutomaticClose |
| Timeout after commit, process/session recreation, no duplicate create | UnknownCommitSurvivesRestartAndReconcilesWithoutRepeatingCreate |
| Locks, permission, dirty and shareable configuration/mode | PreflightBlocksUnsafeOrUnknownConfiguration (fixture cases; see TRX for names) |
| Link loss after close, wrong version binding, missing duplicate edge | post-close loss test and LinkVerificationDetectsDroppedDuplicateAndWrongBaselineBinding |
| Denied, changed, tampered, read-only or expired approval | Core and WPF approval tests |
| Append-only revisions, protected state, corrupt journal fails closed | journal/restart tests (test protector; not DPAPI evidence) |
| Strict correlation, escaped Unicode, truncated/incomplete metadata | ProtocolPreservesSeparateSetAndMemberVersionsAndEscapedUnicode |
| Default native gate never starts COM | DefaultNativeCompatibilityPerformsNoComOrScriptExecution |
| Reuse existing selection; create/populate/verify/close/restart via MVVM | MockUiEndToEndAndRestartUsesExistingSelectionAndExplicitReview |
| Dark WPF view, native visibility and virtualized shared grid | NativeModeRendersAlongsideSharedVirtualizedSelection |
| Approved intent, actual state and distinct versions in JSON/CSV/XLSX | ReportExportsApprovedIntentActualStateMissingAndVersionSeparation |

The partial-population fault is synthetic. It is not evidence that IBM addBaselines commits a partial input list. An uncertain transport outcome must still be reconciled.

## WPF usage

1. Connect in Mock mode. Open Bulk Baseline Manager; select folders, Include Subfolders and formal modules using the shared selector. Search/filter preserves selection.
2. Select **NativeBaselineSet** in Baseline Mode. Enter a project/folder path; List Definitions then Load Definition / Sets for existing items.
3. For a new definition, enter Name/Description and choose CreateDefinition. For existing definitions choose UpdateDefinition, AddDefinitionModules or RemoveDefinitionModules; module operations use the checked selection above.
4. Choose CreateMajorSet or CreateMinorSet and enter Suffix/Annotation. Each action requires its own Preflight/Dry Run, Review and explicit approval, then Execute Approved Action. Read-only must be deliberately disabled for writes. All affected definition modules appear in the review.
5. After set creation, choose AddModuleBaselines with all definition modules selected. Native mode requires all modules together, clean/unloaded state and no shareable configuration. Mock can simulate missing modules and explicit missing-only recovery.
6. Inspect missing members and module baseline versions. Verify Links is read-only. CloseSet is a separate reviewed action, available only with complete membership and verified pre-close traceability. Recheck links after close.
7. For Partial/Unknown/NeedsReconciliation: preserve the set, drain any active COM operation, open the recorded job and Reconcile read-only. Do not recreate the set or repeat addBaselines blindly. Native partial population requires controlled manual investigation; no automated close, deletion or rollback is provided.
8. Job History opens a recorded snapshot without writing. Export Current Job includes its append-only revision prefix, approved intent, before/after metadata and link findings. Export completion is not a claim that the DOORS operation succeeded.

## Real 9.5 acceptance — NOT_RUN

Record exact executable build, architecture, database ID, operator, host PCs, script SHA256 and both approvals. Obtain the installed **9.5-specific** DXL manual; the later manual is insufficient. Use only a disposable test project and prepared source/target links.

| Check | Result |
|---|---|
| Verify all documented overloads compile for this 9.5 build; null error handling | NOT_RUN |
| Read-only definition/set/member enumeration incl denied/deleted members | NOT_RUN |
| Validate definition cache dirty state without read/save/discard of borrowed edits | NOT_RUN |
| Validate configured-shareable detection, edit mode, permission/lock inspection | NOT_RUN |
| Definition create/update/dedup/multi-folder membership and own lock release | NOT_RUN |
| Major then Minor; set version distinct from actual member baseline version | NOT_RUN |
| Reject second open set and definition changes while open | NOT_RUN |
| Add all definition modules together; exact isBaselinePresent verification | NOT_RUN |
| Denied/locked/loaded-dirty module; no silent user save/discard | NOT_RUN |
| Close only complete set; post-close traceability and version binding | NOT_RUN |
| Two-PC link scenario from IBM alert; risky configurations blocked | NOT_RUN |
| Application/client crash, reconnect, queue drain, no duplicate; reconciliation | NOT_RUN |
| Real Windows account DPAPI recovery and sanitized exports/audit | NOT_RUN |

## Real 9.7 acceptance — NOT_RUN

Repeat every 9.5 checklist item independently against the exact 9.7 executable build and its installed manual. Documentary 9.7 signature checks are not runtime acceptance. In particular repeat the IBM two-PC link scenario, shareable configuration guard, dirty cache behavior, own-lock lifecycle, all-at-once population and post-close link inspection. **All items: NOT_RUN.**

## Integration fixture and deployment gate

`NativeBaselineSetIntegrationTests` contains separate 9.5/9.7 read-only and explicitly authorized write cases. Default runs report four NOT_RUN results. A controlled site may supply an assembly-qualified `INativeBaselineSetAcceptanceEnvironment` via `DOORS_NATIVE_SET_TEST_ENVIRONMENT_TYPE` and set `DOORS_TEST_MODE=real-native-sets`. Read-only cases never invoke the writer. Write cases additionally require `DOORS_NATIVE_SET_TEST_WRITE_ACK=CREATE_NATIVE_BASELINE_SET_IN_TEST_FOLDER`, a disposable folder/unused definition name, a validated service and fresh interactive approval plus second Real approval for each action. Successful artifacts are retained for audit; tests never delete/rollback them.

The shipped host always uses UnvalidatedNativeBaselineSetCompatibility, UnvalidatedNativeBaselineSetSafetyProvider and UnconfiguredBaselineSetLinkScopeProvider. Replace through DI only after manual/runtime review. ReviewedNativeBaselineSetCompatibility pins target, exact client build, database, user, both script hashes and separate manual/runtime/link/state evidence. Safety provider must inspect permissions, definition borrowed dirty/cache state, session/locks and each module's **configured shareable flag**, dirty/edit state, and stable identity. Its final dispatch guard must fail if evidence is stale. The link scope provider must enumerate the complete approved link scope, supply observed stable module/link-module IDs and exact baseline read requests. Native v1 link results lacking stable IDs cannot pass by treating paths as IDs.

Definition IDs from the documentary adapter are explicitly logical folder-identity/name locators; a rename changes the locator and requires recorded reconciliation. No undocumented definition GUID is fabricated. Set IDs bind that definition locator and the native set version. Formal module IDs remain stable DOORS qualified IDs plus canonical paths.

The default native gate also prevents read-script dispatch while safe cache inspection is unvalidated; metadata calls then return explanatory NOT_RUN. This is an intentional unresolved native verification point, not a completed native compatibility claim. Journal encryption uses DPAPI CurrentUser in production; tests inject an ephemeral test protector. This environment has not established working native DPAPI, native DXL compilation, or link-loss mitigation.
