# Classic Find — WPF requirement reader

Read a formal module, then use **Bul…** in Workspace or **Ctrl+F**. The owned modeless Find window lets you continue inspecting the module while retaining its query/options.

- **Find what**: literal text, 1–256 characters. Searches Object Identifier, readable Object Number and the readable plain-text representations of loaded attributes (including Heading/Text). Unrequested/denied values and opaque RTF/OLE payloads are not searched.
- **Match case**: ordinal case-sensitive comparison; unchecked uses ordinal case-insensitive comparison, independent of workstation culture.
- **Whole word**: Unicode-aware word boundaries. Letters, numbers, combining marks and connector punctuation such as underscore belong to a word. A hyphen separates words. This is literal search, not regular-expression syntax or Unicode normalization.
- **Next / Previous**: select the next/previous matching requirement in snapshot order, open its 100-row page, scroll the row into view and show attribute/link details from retained snapshots. Each matching requirement is one result, even if multiple fields/occurrences match. The first search after changing query/options can include the selected requirement.
- **Wrap around**: optionally continue from the beginning/end. The status identifies wrap and displays match ordinal/total; no-match and end-of-range messages retain the existing selection.
- **Enter / F3**: next; **Shift+F3**: previous; **Esc / Close**: close the dialog. F3 shortcuts need a valid previously entered query. Ctrl+F reuses an already open window; reopening retains query/options for the application session.

Search respects Workspace's existing **Ara** text and Active/Deleted filters, across the full retained snapshot rather than only the displayed page. Clear the quick filter when a full unfiltered module search is wanted. Current and selected baseline snapshots retain their original read context. A new/replaced/cleared module resets search status; Find is disabled until a readable snapshot exists. It does not search unloaded modules, evaluate Attribute DXL, perform additional COM reads, navigate the native DOORS client, modify DOORS data or change export scope.

Scanning/paging run through the existing background operation/cancellation/error pipeline; the UI holds only the current page and a few candidate records. Selection is applied on the dispatcher. Cancellation publishes no partial Find selection. Snapshot search and details use existing immutable records and retained links; no new DXL/COM API or dependency was introduced.

Implementation: `RequirementFindService` (literal matcher and streaming navigation), `RequirementFindViewModel`, `WorkspaceFind.cs` (existing Workspace paging/details integration), `FindWindow.xaml/.cs` (view/focus/lifetime), and MainWindow command/keyboard/scroll wiring. The original quick-search filter remains available.

Tests: `RequirementFindTests` covers case, Unicode boundaries, literal punctuation, readable-value filtering, snapshot-order navigation, wrap/no-wrap, filtered indices, 10,000-object streaming and cancellation. `RequirementFindWpfTests` covers off-page selection/details, unchanged adapter read count, existing filters, no-match, module invalidation, routed Find/Close commands, modeless single-window reuse, controls, bindings and rendered dialog. Test evidence and screenshots: `artifacts/RequirementFind`.
