> Bu belge legacy WinForms istemcisini açıklar. Ana Desktop paketi artık birleşik WPF uygulamasıdır: [birleşik kullanım rehberi](UNIFIED-DESKTOP.md). Legacy paket adı `DoorsIntegration-WinForms-*` olur.

# Desktop application

`DoorsIntegration.Desktop` adds a Windows Forms application targeting .NET Framework 4.8 to the existing solution. The Console application remains available. Both hosts use the same Core, COM and DXL implementations; the desktop project does not depend on the Console executable.

## Launch

Workspace now includes **Link editor**: preview one Classic link creation/removal between exact current requirement objects, then explicitly approve it. Removal opens a second distinct deletion approval. Existing linksets, permissions, locks, duplicate detection and before/after scoped traceability are checked. See [LINK-WRITING.md](LINK-WRITING.md); native 9.5/9.7 acceptance remains to be performed in a test database.

The **Settings** tab contains **Enable DXL execution-cycle watchdog (recommended)**, enabled on first use. Change it and press **Save settings**. Subsequent integration DXL calls explicitly use a 1,000,000-cycle limit when enabled, or `runLim, 0` when disabled. The preference persists per Windows user in `%LOCALAPPDATA%\DoorsIntegration\settings.xml`; missing/corrupt settings default to enabled. Saving stays offline and is unavailable during an active operation. See [DXL-WATCHDOG.md](DXL-WATCHDOG.md) for scope, cancellation limits and manual verification.

On Windows with .NET Framework 4.8 or a compatible newer 4.x runtime:

1. Extract **all** files from `artifacts/DoorsIntegration-Desktop-x86.zip` into a folder.
2. Double-click `DoorsIntegration.Desktop.exe` in the extracted folder.
3. Keep the accompanying DLLs and `DoorsIntegration.Desktop.exe.config` beside the executable.

For an update, close the previous window after active operations finish, extract the new ZIP completely and launch its EXE. An already running window keeps its loaded UI; it does not reload edited source or a newly built package. Per-user Settings remain in the existing local settings file.

No installer or administrator rights are required by the application. IBM DOORS is needed only for a connected probe. The package contains the application dependencies; it does not contain the .NET Framework runtime or the IBM client.

From source, build the solution using the README commands, then run:

```powershell
& '.\src\DoorsIntegration.Desktop\bin\x86\Release\net48\DoorsIntegration.Desktop.exe'
```

In Visual Studio, set `DoorsIntegration.Desktop` as the startup project and press F5. For a release package:

```powershell
.\scripts\Package-Desktop.ps1
# Builds and tests Release x86, then creates the portable ZIP.
# If that configuration has already been built and tested:
.\scripts\Package-Desktop.ps1 -SkipBuild
```

If PowerShell script execution is disabled, use the direct `dotnet` build/test commands in the README. Then archive the EXE, DLLs and EXE config from the desktop output folder together with this guide.

## Use the window

**Workspace** adds Requirements, Requirement editor, Create module, Links, Link editor, Export, Run DXL and Diagnostics tabs. It exposes the existing services, paged retained snapshots, reviewed writes and custom scripts, JSON/CSV/XLSX export and step-by-step diagnostics. Main tabs stay at the top; profile/connection/native prompt controls remain in a fixed bottom strip. Switching tabs no longer hides headers or changes outer padding, and changing profile preserves the selected tab. Preview/probe actions are inside DXL preview; Save activity is inside Activity. See the complete [Workspace guide and manual acceptance checks](DESKTOP-WORKSPACE.md).

The EXE and main window use a navy/cobalt/cyan doorway icon, embedded with sizes from 16 to 256 pixels for Windows Explorer, the title bar and taskbar. No external asset file is needed to launch the application. Editable master, packaged icon and generation/conversion notes are in `src/DoorsIntegration.Desktop/Assets/README.md` in the source archive.

The window uses a dark blue palette by default: navy backgrounds, blue action/selection accents, dark DXL/activity panes and light text. Hover, pressed, keyboard focus and disabled button states are painted consistently. Dropdowns retain native selection/keyboard behavior with a dark frame and arrow. The busy indicator uses the same blue accent, and success/error messages use bright green/pink text. The palette and presentation controls are in `DarkBlueTheme.cs`; no external theme package is required.

The title bar requests the documented optional [DWM dark-mode attribute](https://learn.microsoft.com/windows/apps/desktop/modernize/apply-windows-themes). Unsupported Windows versions retain the native title bar. Native file dialogs and scrollbars follow Windows styling. This release provides one default palette; there is no theme switch. Auto-sized action/status panels can shrink when the window is reduced, and explanatory text is constrained to the available width.

- **Target profile:** choose 9.5 or 9.7. This does not detect the installed version or switch COM registration.
- **Connection mode:** activate the registered COM server (which may launch or reuse a client), or attach to an accessible running ROT object. Attach never falls back to activation. A running DOORS window alone does not prove ROT availability.
- **Auto-select DOORS 'Double execution time':** use **Bind running DOORS...** with one signed-in client, verify its executable/PID, then enable before the next operation. Recognised modal DXL prompts receive at most four Double selections per COM run. Unsupported prompts remain manual. This option starts off on every launch. Real 9.5/9.7 UI validation remains required. See [DXL-WATCHDOG.md](DXL-WATCHDOG.md).
- **Preview DXL:** creates a new offline preview. The window opens with a preview for 9.7. Selecting a different profile refreshes the preview. The script area is read-only.
- **Run read-only probe:** explicitly contacts the registered IBM DOORS client and checks the existing fixed diagnostic protocol. Sign in to DOORS first. This may activate the client or wait for a client dialog.
- **Activity:** shows up to 500 recent sanitized log entries, including request IDs, success and error events.
- **Save activity:** exports that activity buffer to a text file you choose. No database data is exported.
- **Discovery:** explicitly scans project/folder/formal-module metadata under a full DOORS path, or checks one known module path. The read-only grid shows path/name/type/access, with visibility/partial-result issues below. Modules remain closed. **Export JSON...** saves the snapshot and issues as UTF-8 JSON. Startup and tab selection remain offline. See [DISCOVERY.md](DISCOVERY.md) for API, limits, access semantics and manual tests.

The preview and each probe receive separate request IDs. The displayed DXL belongs to the preview request. A probe creates its own fixed script and fresh request ID; the preview is not an editable execution input.

The UI disables actions during a probe. It stays responsive while the synchronous COM operation runs on a separate STA thread. Closing the window during a probe defers shutdown until the operation finishes, allowing COM cleanup on its owning thread. Resolve any DOORS sign-in or modal dialog if a probe is waiting; there is no forced cancellation or timeout.

Startup, tab changes, offline previews and exports do not contact DOORS. Fixed probes and discovery are database-read-only. Workspace requirement/module/link writes require a fresh dry-run plus a separate modal review, exact typed target and acknowledgement. Custom DXL execution and opt-in installed attribute DXL evaluation also require explicit review. Desktop confirmation denies when unbound, hidden, expired or cancelled; Console keeps rejecting writes by default. No automatic approval/retry is provided. Building and the default tests do not contact DOORS. Keep all distributed `scripts/dxl` files beside the EXE in their original directories.

## Architecture and extensions

Workspace → Requirements has Read, Batch import / update, Quality and Coverage subtabs. The new `DoorsIntegration.Processing` assembly has no Core/COM/DXL dependency; native writes delegate to the existing RequirementService and retain per-record explicit review. CSV/JSONL input, durable idempotency, audit and safe retry are available. Analysis of retained snapshots stays offline. Standard-context profiles provide review aids, not automatic compliance or tool qualification. See [PROCESSING.md](PROCESSING.md).

`MainForm` handles presentation. `DesktopOperations` implements preview/probe use cases and delegates errors to the existing `IntegrationErrorHandler`. `StaOperationRunner` owns background STA execution and rejects overlapping operations. The full `ComDoorsDiagnostics.Probe` call, including creation and release of the COM reference, runs inside one worker delegate.

`DesktopWorkspaceServices` supplies the existing service ports to separate feature panels. `DesktopWorkspace` coordinates a single active UI operation, cancellation, retained late completion and errors. `DesktopSnapshotStore` owns requirement/link backing files through paging and export. `DesktopWriteConfirmation` marshals complete reviews to its visible UI owner; the injected `IDesktopReviewDialog` collects explicit target/acknowledgement consent. It fails closed without that owner. The Console composition root is unchanged. No new native DXL API or third-party package is introduced by the Workspace UI.

`AddDoorsDesktop()` is a separate composition root registration extension using `TryAdd`. A host can register an alternative `IDoorsDiagnostics`, `IDxlProbeProtocol`, `IIntegrationLogger` or `IStaOperationRunner` before calling it. `MainForm` and `DesktopOperations` use constructor injection. Only `Program` resolves services from the provider.

`DesktopActivityLog` provides a thread-safe bounded buffer and the `DoorsIntegration.Desktop` TraceSource. The UI reads snapshots on a Windows Forms timer; worker threads never touch controls. The default `IIntegrationLogger` is the shared activity buffer. If replacing it, forward records to that buffer as well if you want them in the Activity tab.

The default external trace listener writes to debug output. You can configure additional listeners in the desktop EXE config or use a company log adapter. No exception messages, requirement content, passwords or DXL payloads are written by the integration logger. Preview text appears only in the dedicated read-only preview area.

## Validation

The net48 Release builds and tests cover desktop initialization, offline preview, profile validation, connection-mode forwarding, STA execution, single-operation enforcement, COM cleanup thread ownership, centralized failures and bounded activity snapshots. Current counts and x86/x64 evidence are in [VALIDATION.md](VALIDATION.md). Real DOORS 9.5/9.7 probes were not run.

For visual checks, open the window at its default and minimum sizes, switch both tabs and profile/connection dropdowns, then verify hover/focus/disabled button contrast. Busy, success and error states were also exercised with a fake diagnostic service in an offscreen Windows Forms message loop. Check native title-bar styling and 125–200% display scaling on the deployment workstation; bitmap rendering does not capture RichTextBox text or prove interactive high-DPI behavior.

The Console host additionally provides offline registry/architecture/version commands; see [COM session diagnostics](COM-SESSION.md).

Both hosts also register the general `IDxlExecutor` library service. Workspace exposes it through captured-source review and retains file results for full save; a bounded text preview does not replace the complete result. Timeout/cancellation keeps the original COM call owned until completion and cleanup. See [DxlExecutor](DXL-EXECUTOR.md) and [Workspace](DESKTOP-WORKSPACE.md).

See [VALIDATION.md](VALIDATION.md) for commands and evidence. WinForms and STA behavior follow the [Windows Forms documentation](https://learn.microsoft.com/en-us/dotnet/desktop/winforms/overview/) and [Thread.SetApartmentState documentation](https://learn.microsoft.com/en-us/dotnet/api/system.threading.thread.setapartmentstate?view=netframework-4.8).
