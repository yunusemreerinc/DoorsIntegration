# DOORS discovery diagnostics — first-stage delivery (2026-11-05)

This delivery adds diagnosis, protected response capture, and an independent read-only ping. The reported **4119-character real response is not available**. Its exact contents and root cause have not been determined. No speculative DXL or parser-format correction has been applied.

## Confirmed rejection and expected protocol

`DoorsIntegration.Dxl/DiscoveryProtocol.cs`, `DiscoveryProtocol.Parse` rejects `text.Split('\n').Length < 4` or `> 10020` with `InvalidLineCount`. This happens before processing records, so `Line=0` is the parser's counter, not a DXL source line number.

Before that check, the parser requires a nonempty response, at most 2,097,152 UTF-16 code units, ending in LF. A 4119-character response cannot contain more than 10,020 split entries. Therefore the reported error proves that the response had **one or two LF characters**, fewer than the minimum three. It does not prove why the response had that shape.

The minimum discovery payload is:

```text
DOORS_DISCOVERY|1|<32-hex request ID>\n
S\t<escaped full path>\t<escaped name>\t<type>\t<access>\n
END\t0\t0\n
```

Here `\t` and `\n` describe actual delimiters. Additional `I` child records precede optional `W` warnings; `END` includes the exact child count and truncation flag. Paths, direct-child scope, duplicate records, enums, escaping, warnings, header version/request ID and footer remain strictly validated. The outer executor protocol is a different layer: `DOORS_DXL|1|<fresh ID>|OK|<discovery payload>`.

Valid LF, CRLF and mixed LF/CRLF delimiters were already supported. A response BOM is currently rejected as `HeaderMismatch` when enough records are present. The UTF-8 script loader already removes a source-file BOM. Capture preserves response BOMs, bare CRs, tabs, LF/CRLF, literal backslash escapes and even invalid UTF-16 code units. It does not trim, reinterpret escapes or silently repair output. Accepting a response BOM is deferred until the actual payload establishes whether a transport normalization change is appropriate.

## Capture and confidentiality

`DxlExecutor.ExecuteWorker` records the original COM `Result` **before** validating its outer completion frame. `DxlHierarchyReader.ReadBatchAsync` records the decoded discovery payload **before** parsing it. Rejection records include the exact parser reason and exception details. Accepted batches are recorded too, allowing a later recursive-folder failure to be traced without returning a partial successful inventory.

Default location:

```text
%LOCALAPPDATA%\DoorsIntegration\discovery-debug\<DXL RequestId>.dpapi
```

Each record includes discovery/DXL request IDs, UTC capture time/stage, requested profile, process architecture, `runStr`/`runFile` and `Result`, script path, executed source and dispatched-script SHA-256 hashes (UTF-16 code units), raw COM response, raw discovery payload, character/line/LF/CRLF/bare-CR counts, BOM flag, parser reason and exception details. Raw strings use lossless Base64-encoded UTF-16 code units inside the encrypted JSON record.

Observed `doors.exe` PIDs, executable paths and file versions are included when accessible. These are **passive process-file observations**, not proof that a particular process owns the attached COM ROT object. Unknown or inaccessible client metadata remains explicitly unverified; `--target 9.7` is a requested profile, not client-version detection. Existing `diagnose` registry/file-version checks, including mismatch handling, remain separate.

Files use CurrentUser DPAPI and ACLs allowing only the current Windows identity and LocalSystem; inherited broad ACLs are removed. Files are atomically replaced after flushing a private temporary file. Reparse-point directory paths are refused. Capture is bounded to 256 request files, approximately 128 MiB total before admitting another request and 32 MiB serialized per record. No automatic retention deletion is performed: archive/remove the application-owned protected files locally when the quota is reached. These are debug files, not audit journals.

Normal logs and error screens contain only fixed reasons, numeric metadata, capture status and request IDs. Raw data, database paths and arbitrary DXL error text are excluded. A protocol rejection displays `DebugRequestId=...; ProtectedCapture=Saved|Unavailable`. If protection, ACL setup or storage fails, the original operation outcome is preserved, a safe `DiscoveryDebugUnavailable` event is logged and **no plaintext fallback** is written. The test runner's Windows profile cannot use DPAPI here; its real protection round trip is NOT_RUN. File IO/ACL/atomic replacement tests use the repository's explicit test-only authenticated protector. Production composition always uses DPAPI.

## Reproduce and inspect the response

1. Extract the entire new x86 or x64 WPF package; do not mix old executables, DLLs or DXL files. Select Real and the intended 9.7 profile, attaching to the intended running client.
2. Run **DXL Ping (isolated)** in the top toolbar. Mock mode reports NOT_RUN. The existing **Ping / connection** and offline diagnostics remain available separately.
3. Retry the same discovery/dashboard scope. On rejection, retain the `DebugRequestId` shown in the error. Inspect the encrypted file under the location above under the same Windows account.
4. For explicit local inspection, use the packaged script (Windows PowerShell; source-tree users must supply their built application directory):

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Read-DiscoveryDebug.ps1 `
  -RequestId '<DebugRequestId>' -OutputFile "$env:LOCALAPPDATA\DoorsIntegration\discovery-response.json"
```

The script creates a **new**, non-inheriting ACL-protected UTF-8 JSON file with metadata and decoded raw response strings, without printing the content to the console. It refuses to overwrite an existing file. This explicit decrypted export can contain requirement text, confidential paths and runtime errors; inspect/redact it locally before sharing. Do not paste unreviewed raw content into a public issue. DPAPI decryption requires the capturing Windows identity/profile. Script syntax and restricted CreateNew output IO were checked here; a full production-DPAPI export remains environment-dependent.

Compare raw `Result`, decoded payload and actual delimiters with the protocol above. Check syntax/runtime diagnostics, print/debug prefixes, literal `\\n` instead of LF, missing header/footer, protocol ID/version, hashes/path of the deployed script, and observed executable/file versions. Do not turn compiler errors into inventory, globally replace escape sequences, suppress count checks or substitute an empty successful inventory. Decide the smallest correction only after inspecting this evidence; retain malformed-response rejection tests.

## Independent COM/DXL ping

External `scripts/dxl/discovery-ping.dxl` contains:

```dxl
oleSetResult("DXL_PING_OK");
```

`DxlPingService` uses `runStr` and `Result` on the existing STA executor. Before dispatch, the executor sets a fresh GUID-bearing pending marker. Its completion frame must contain that same GUID; then the decoded ping payload must be **exactly** `DXL_PING_OK` (no trimming or case folding). Thus the raw COM `Result` includes the executor envelope; the ping script payload is the exact constant. Old frames and a plain stale `DXL_PING_OK` cannot pass transport validation. This is independent of discovery APIs/parser and makes no database changes. Timeout/cancellation stops waiting, does not kill COM or retry, and retains late-result ownership until the worker completes.

From a built Console output directory:

```powershell
.\DoorsIntegration.Console.exe ping --dxl-only --target 9.7 --connection attach
.\DoorsIntegration.Console.exe diagnose --target 9.7
```

The WPF ZIP remains the only desktop distribution; it includes the UI ping, external scripts and capture-reader script. The diagnostic Console executable is built separately and is not another desktop client.

## Automated and native acceptance

New deterministic tests cover valid LF/CRLF, the synthetic exact 4119-character rejection, empty output, unexpected debug prefixes, response BOM preservation/rejection, protocol version mismatch, incorrect footer counts, simulated compiler output before frame validation, stale/plain Result, runStr/runFile capture, exact ping success/failure, STA ownership, timeout without cancellation/retry, protected file IO/ACLs, log failure isolation, partial discovery rejection and Mock UI NOT_RUN. Existing `DoorsHealthChecksTests` cover unknown/mismatched registered client file versions separately.

Release builds use .NET Framework 4.8, C# 7.3 and the repository SDK host. Focused run (set `DOORS_TEST_MODE=disabled`):

```powershell
dotnet test tests/DoorsIntegration.Tests/DoorsIntegration.Tests.csproj -c Release -p:Platform=x86 `
  --filter 'TestCategory=DiscoveryDiagnostics|TestCategory=DiscoveryDiagnosticsReal|TestCategory=DiscoveryDebugProtectionEnvironment|FullyQualifiedName~DoorsHealthChecksTests|FullyQualifiedName~DiscoveryProtocolTests' `
  -- RunConfiguration.TargetPlatform=x86
```

Use the repository build/SDK instructions and restore for the selected platform first. Real tests are opt-in only. For each separately installed/selected 9.5 and 9.7 test environment, manually verify the client/version/COM registration and readable scope, set `DOORS_TEST_MODE=real-readonly`, `DOORS_DISCOVERY_DIAGNOSTICS_VERSION=9.5` or `9.7`, and `DOORS_DISCOVERY_DIAGNOSTICS_PATH` to a known folder; run only `TestCategory=DiscoveryDiagnosticsReal`. Do not enable existing write-test flags. Check ping first, discovery separately, inspect captured native errors, then verify the dashboard inventory against DOORS and confirm it does not replace a failed collection with partial success. Repeat with LF/Unicode names, denied access, wrong registered version and an intentionally interrupted client where safe. No baseline/module/link/requirement mutation is part of this procedure. A matching configuration flag alone does not prove the active COM client's version; record the actual deployment/client evidence in the native acceptance record.

Native 9.5 ping/discovery: **NOT_RUN**. Native 9.7 ping/discovery: **NOT_RUN**. The reported production response: **not captured yet**. Mock/protocol tests do not establish native compatibility. Build and regression evidence is recorded in `artifacts/DiscoveryDiagnostics/summary.json` and TRX files.

Final local results on 2026-11-05:

| Validation | x86 | x64 |
| --- | --- | --- |
| Release solution build | PASS, 0 warnings/errors | PASS, 0 warnings/errors |
| Focused diagnostics + existing parser/health checks | 92 PASS / 0 FAIL / 5 NOT_RUN | 92 PASS / 0 FAIL / 5 NOT_RUN |
| Full regression | 1672 PASS / 0 FAIL / 70 NOT_RUN | 1672 PASS / 0 FAIL / 70 NOT_RUN |

The five focused NOT_RUN cases are four explicit 9.5/9.7 native cases plus the Windows DPAPI environment probe. Full regression retains the existing gated native cases. Initial development-run failures are not acceptance results; their TRX files are retained under the intermediate evidence folder. The final named focused/regression TRX files are authoritative for this delivery. Settings/watchdog/icon hashes remain unchanged. No native client calls or DOORS database mutations were made during these runs.

## Changed files

- `src/DoorsIntegration.Dxl/DiscoveryDebugCapture.cs`: record, protected store, internal capture lifecycle.
- `src/DoorsIntegration.Dxl/DxlExecutionModels.cs`, `DxlExecutor.cs`: opt-in capture context and raw COM interception.
- `src/DoorsIntegration.Dxl/DxlHierarchyReader.cs`, `DiscoveryProtocol.cs`: pre-parser capture and safe error reference; validation rules unchanged.
- `src/DoorsIntegration.Dxl/DxlPingService.cs`, `DoorsIntegration.Dxl.csproj`: isolated ping and packaged reader utility.
- `scripts/dxl/discovery-ping.dxl`, `scripts/Read-DiscoveryDebug.ps1`.
- `src/DoorsIntegration.Console/CommandLineOptions.cs`, `ConsoleApplication.cs`, `ServiceCollectionExtensions.cs`: opt-in CLI and DI registrations.
- `src/DoorsIntegration.Wpf/Services/ClientSession.cs`, `ViewModels/WorkspaceViewModel.cs`, `MainWindow.xaml`: Real service and toolbar action; Mock NOT_RUN.
- `tests/DoorsIntegration.Tests/DiscoveryTestDoubles.cs`, `DiscoveryDiagnosticsTests.cs`, `DiscoveryDiagnosticsWpfTests.cs`, `DiscoveryDiagnosticsIntegrationTests.cs`, `DiscoveryDebugProtectionEnvironmentTests.cs`.
- `README.md`, this document and packaging validation.

No COM method names, discovery DXL APIs, watchdog settings, existing write approvals or Tasks 1–31 business workflows are changed. The final root-cause fix remains dependent on the next real protected capture.
