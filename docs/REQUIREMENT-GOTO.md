# Classic Go To — WPF requirement reader

Read a module, then use **Git… / Ctrl+G** in Workspace. The modeless owned Go To window retains its target and kind for the application session. **Enter** goes to the target; **Esc / Close** closes the window. Opening it again reuses the existing window.

Choose one explicit lookup kind:

| Kind | Input | Matching |
|---|---|---|
| AbsoluteNumber (default) | Positive 32-bit integer, e.g. `220` | Actual snapshot object Absolute Number |
| ObjectIdentifier | e.g. `FLT-220` | Full identifier; ordinal case-insensitive, no substring/fallback |
| ObjectNumber | e.g. `1.2.3` | Full readable numbering string; no numeric coercion/prefix match |

Object Identifier and Object Number are lookup/display values. Selection binds the matched record's actual Absolute Number within the retained module/baseline. Object Number can change with hierarchy and is never treated as a permanent object identity. Multiple matching records are reported as ambiguous; no arbitrary object is chosen.

Go To scans the complete retained snapshot and opens the target's 100-row page, selects and scrolls its row, and displays its attributes and already-retained links. Find and Go To share this page/detail projection. No extra module/link read, native DOORS UI automation, DXL evaluation or DOORS write occurs.

Existing quick-search and Active/Deleted filters remain in force by default. If the target is present but hidden, the window explains it. **Filtreleri temizleyerek git** explicitly permits clearing the quick filter and selecting All objects after successful lookup. Missing/ambiguous targets, invalid input and cancellation do not clear filters or replace the selection. Deleted objects can be opened only if present in the snapshot; unreadable/unrequested numbering is not trusted. Missing means not found in the retained snapshot, not proven absent from DOORS.

Current and historical baseline context are retained. Module/profile/snapshot changes invalidate status and disable Go To until a readable snapshot is loaded; there is no automatic switch to Current or another module. Scanning and projection run off the UI dispatcher through existing progress/cancellation/error handling. Only a candidate record and one page of UI rows are retained.

Implementation: `RequirementGoToService`, `RequirementGoToViewModel`, `GoToWindow`, `WorkspaceNavigationCommands` and `WorkspaceNavigation.cs`; existing Find now reuses the common page/attribute/link projection. No new package, DXL API or write pathway was introduced. `RequirementGoToTests` and `RequirementGoToWpfTests` cover validation, explicit kinds, exact/ambiguous/unreadable matches, filter handling, 10,000-record streaming, cancellation, off-page selection, unchanged adapter read count, baseline context, Ctrl+G, modeless lifetime, UI bindings and rendering. Evidence: `artifacts/RequirementGoTo`.
