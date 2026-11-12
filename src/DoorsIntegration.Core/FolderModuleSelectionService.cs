using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DoorsIntegration.Core
{
    public sealed class FolderModuleSelectionService : IFolderModuleSelectionService
    {
        private readonly IDoorsHierarchyReader hierarchy;
        private readonly IModuleSelectionMetadataProvider metadata;
        private readonly IIntegrationLogger logger;
        public FolderModuleSelectionService(IDoorsHierarchyReader hierarchy, IModuleSelectionMetadataProvider metadata, IIntegrationLogger logger)
        { this.hierarchy = hierarchy ?? throw new ArgumentNullException(nameof(hierarchy)); this.metadata = metadata ?? throw new ArgumentNullException(nameof(metadata)); this.logger = logger ?? throw new ArgumentNullException(nameof(logger)); }
        public ModuleSelectionSession Create(ModuleSelectionRequest request, ModuleSelectionProfile profile = null) =>
            new ModuleSelectionSession(request, hierarchy, metadata, logger, profile);
    }

    // UI-independent cursor. Only an explicit LoadNext advances discovery; no recursive background prefetch.
    public sealed class ModuleSelectionSession
    {
        private readonly object sync = new object();
        private readonly SemaphoreSlim loader = new SemaphoreSlim(1, 1);
        private readonly IDoorsHierarchyReader hierarchy;
        private readonly IModuleSelectionMetadataProvider metadata;
        private readonly IIntegrationLogger logger;
        private readonly Queue<Tuple<string, int>> folders = new Queue<Tuple<string, int>>();
        private readonly Queue<DoorsDiscoveryItem> pending = new Queue<DoorsDiscoveryItem>();
        private readonly HashSet<string> queued = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, ModuleSelectionCandidate> byPath = new Dictionary<string, ModuleSelectionCandidate>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> idToPath = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, BaselineModuleReference> desired = new Dictionary<string, BaselineModuleReference>(StringComparer.Ordinal);
        private readonly List<string> issues = new List<string>();
        private bool incomplete, stopped;
        private int visited;
        internal ModuleSelectionSession(ModuleSelectionRequest request, IDoorsHierarchyReader hierarchy, IModuleSelectionMetadataProvider metadata,
            IIntegrationLogger logger, ModuleSelectionProfile profile)
        {
            Request = request ?? throw new ArgumentNullException(nameof(request)); this.hierarchy = hierarchy; this.metadata = metadata; this.logger = logger;
            if (profile != null)
            {
                if (profile.RepositoryId != request.RepositoryId || !profile.Folders.Select(FolderKey).SequenceEqual(request.Folders.Select(FolderKey)))
                    throw new ArgumentException("Profile repository/folder scope must match the request.");
                foreach (var module in profile.SelectedModules) desired.Add(module.ModuleId, module);
            }
            foreach (var folder in request.Folders) Enqueue(folder.CanonicalFullPath, 0);
        }
        public ModuleSelectionRequest Request { get; }
        public bool HasMore { get { lock (sync) return !stopped && (folders.Count > 0 || pending.Count > 0); } }
        public int SelectedCount { get { lock (sync) return byPath.Values.Count(c => c.IsSelected); } }
        private static string FolderKey(ModuleSelectionFolder folder) => folder.CanonicalFullPath + "\n" + folder.IncludeSubfolders;
        private void Enqueue(string path, int depth) { if (queued.Add(path)) folders.Enqueue(Tuple.Create(path, depth)); }
        private void Issue(string message) { incomplete = true; if (issues.Count < 10000) issues.Add(message); }
        public ModuleSelectionSnapshot Snapshot()
        {
            lock (sync) return new ModuleSelectionSnapshot(byPath.Values.OrderBy(c => c.FullPath, StringComparer.Ordinal), issues,
                !stopped && (folders.Count > 0 || pending.Count > 0), incomplete, visited, desired.Count);
        }
        public async Task<ModuleSelectionSnapshot> LoadNextAsync(int moduleBudget = 100, int folderBudget = 1,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (moduleBudget < 1 || moduleBudget > 500 || folderBudget < 1 || folderBudget > 50) throw new ArgumentOutOfRangeException(nameof(moduleBudget));
            cancellationToken.ThrowIfCancellationRequested();
            if (!await loader.WaitAsync(0, cancellationToken).ConfigureAwait(false)) throw new InvalidOperationException("A selection page is already loading.");
            int processed = 0, readFolders = 0;
            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested(); DoorsDiscoveryItem item = null; Tuple<string, int> folder = null;
                    lock (sync)
                    {
                        if (stopped) break;
                        if (pending.Count > 0 && processed < moduleBudget) item = pending.Peek();
                        else if (pending.Count == 0 && folders.Count > 0 && readFolders < folderBudget) folder = folders.Peek();
                        else break;
                        if (byPath.Count >= Request.MaxModules && item != null) { Issue("Module limit reached; remaining scope was not loaded."); stopped = true; break; }
                        if (visited >= Request.MaxFolders && folder != null) { Issue("Folder limit reached; remaining scope was not loaded."); stopped = true; break; }
                    }
                    if (folder != null)
                    {
                        DiscoverySnapshot snapshot;
                        try
                        {
                            snapshot = await hierarchy.DiscoverAsync(DiscoveryKind.Hierarchy, new DiscoveryScope(folder.Item1, false, 10000,
                                timeout: Request.Timeout, connectionMode: Request.ConnectionMode, target: Request.Target), cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (!(ex is OperationCanceledException) && !cancellationToken.IsCancellationRequested)
                        {
                            // A timed out COM call may still be active. Do not start another call automatically.
                            lock (sync) { Issue("Folder read failed; refresh after diagnostics. No automatic retry: " + folder.Item1); stopped = true; }
                            LogFailure(ex); throw;
                        }
                        cancellationToken.ThrowIfCancellationRequested();
                        lock (sync)
                        {
                            if (snapshot == null || snapshot.Items.Count > 10000 || snapshot.Items.Select(c => c.FullPath).Distinct(StringComparer.Ordinal).Count() != snapshot.Items.Count ||
                                snapshot.Kind != DiscoveryKind.Hierarchy || snapshot.Scope.RootPath != folder.Item1 || snapshot.Scope.Recursive ||
                                snapshot.Items.Any(c => c.FullPath != folder.Item1 && !DoorsItemPath.IsDirectChild(folder.Item1, c.FullPath)))
                                throw new IntegrationException(IntegrationErrorCode.DiscoveryProtocolFailure, "Selection discovery scope mismatch.");
                            folders.Dequeue(); visited++; readFolders++;
                            if (snapshot.IsTruncated || snapshot.Issues.Count > 0) Issue("Partial/restricted folder discovery: " + folder.Item1);
                            var scopeItem = snapshot.Items.FirstOrDefault(c => c.FullPath == folder.Item1);
                            if (scopeItem == null || !scopeItem.MetadataVerified || scopeItem.AccessStatus != DoorsAccessStatus.Readable ||
                                (scopeItem.Type != DoorsItemKind.DatabaseRoot && scopeItem.Type != DoorsItemKind.Project && scopeItem.Type != DoorsItemKind.Folder))
                            { Issue("Project/folder is inaccessible or is not a confirmed container: " + folder.Item1); continue; }
                            foreach (var child in snapshot.Items.Where(c => c.FullPath != folder.Item1))
                            {
                                if (child.Type == DoorsItemKind.FormalModule) pending.Enqueue(child);
                                else if ((child.Type == DoorsItemKind.Project || child.Type == DoorsItemKind.Folder) && child.AccessStatus == DoorsAccessStatus.Readable && child.MetadataVerified &&
                                    Request.Folders.Any(root => root.IncludeSubfolders && (folder.Item1 == root.CanonicalFullPath || folder.Item1.StartsWith(root.CanonicalFullPath == "/" ? "/" : root.CanonicalFullPath + "/", StringComparison.Ordinal))))
                                {
                                    if (folder.Item2 + 1 >= Request.MaxDepth) Issue("Folder depth limit reached: " + child.FullPath);
                                    else Enqueue(child.FullPath, folder.Item2 + 1);
                                }
                                else if ((child.Type == DoorsItemKind.Project || child.Type == DoorsItemKind.Folder) && child.AccessStatus != DoorsAccessStatus.Readable)
                                    Issue("Subfolder access is restricted: " + child.FullPath);
                            }
                        }
                    }
                    else
                    {
                        ModuleSelectionMetadata observed;
                        bool duplicate;
                        lock (sync) duplicate = byPath.ContainsKey(item.FullPath);
                        if (duplicate || item.AccessStatus != DoorsAccessStatus.Readable || !item.MetadataVerified)
                            observed = ModuleSelectionMetadata.Unavailable(item.FullPath, "Module hierarchy metadata is restricted or unverified.");
                        else
                        {
                            try { observed = await metadata.ReadAsync(Request.RepositoryId, item.FullPath, Request.Target, Request.ConnectionMode, Request.Timeout, cancellationToken).ConfigureAwait(false); }
                            catch (Exception ex) when (!(ex is OperationCanceledException) && !cancellationToken.IsCancellationRequested)
                            { lock (sync) { Issue("Module metadata read failed; refresh after diagnostics. No automatic retry: " + item.FullPath); stopped = true; } LogFailure(ex); throw; }
                        }
                        cancellationToken.ThrowIfCancellationRequested();
                        lock (sync)
                        {
                            pending.Dequeue(); processed++;
                            if (duplicate)
                            {
                                var previous = byPath[item.FullPath];
                                if (item.AccessStatus != DoorsAccessStatus.Readable || !item.MetadataVerified)
                                    byPath[item.FullPath] = new ModuleSelectionCandidate(item, previous.Metadata, false, previous.HasIdentityConflict);
                                continue;
                            }
                            if (observed == null || observed.CanonicalFullPath != item.FullPath || (observed.Module != null && observed.Module.RepositoryId != Request.RepositoryId))
                                observed = ModuleSelectionMetadata.Unavailable(item.FullPath, "Metadata repository/path binding failed.");
                            bool conflict = false, selected = false;
                            if (observed.Module != null)
                            {
                                string id = observed.Module.ModuleId;
                                if (idToPath.TryGetValue(id, out var priorPath) && priorPath != item.FullPath)
                                {
                                    var previous = byPath[priorPath]; byPath[priorPath] = new ModuleSelectionCandidate(previous.Item, previous.Metadata, false, true);
                                    conflict = true; Issue("One stable module identity has conflicting paths; both observations require review.");
                                    // Keep one row per stable identity; never guess which renamed path is authoritative.
                                    continue;
                                }
                                else idToPath[id] = item.FullPath;
                                if (desired.TryGetValue(id, out var saved))
                                {
                                    if (saved.Matches(observed.Module)) selected = true;
                                    else { conflict = true; Issue("Saved module identity has a different canonical path; automatic reselection refused."); }
                                }
                            }
                            var candidate = new ModuleSelectionCandidate(item, observed, selected, conflict);
                            if (!candidate.CanSelect) { candidate = new ModuleSelectionCandidate(item, observed, false, conflict); Issue("Module is not selectable: " + item.FullPath); }
                            else if (selected) desired.Remove(observed.Module.ModuleId);
                            byPath.Add(item.FullPath, candidate);
                        }
                    }
                }
                return Snapshot();
            }
            finally { loader.Release(); }
        }
        public IReadOnlyList<ModuleSelectionCandidate> Query(ModuleSelectionFilter filter = null)
        { filter = filter ?? new ModuleSelectionFilter(); lock (sync) return BulkBaselineGuard.List(byPath.Values.Where(filter.Matches).OrderBy(c => c.FullPath, StringComparer.Ordinal), 100000); }
        public void SetSelected(string moduleId, bool selected)
        {
            lock (sync)
            {
                if (!idToPath.TryGetValue(moduleId, out var path)) throw new ArgumentException("Module identity has not been loaded.");
                Set(byPath[path], selected);
            }
        }
        private void Set(ModuleSelectionCandidate candidate, bool selected)
        {
            if (selected && !candidate.CanSelect) throw new InvalidOperationException("Restricted/unverified module cannot be selected.");
            byPath[candidate.FullPath] = new ModuleSelectionCandidate(candidate.Item, candidate.Metadata, selected, candidate.HasIdentityConflict);
            if (candidate.Module != null) desired.Remove(candidate.Module.ModuleId);
        }
        public void SelectFiltered(bool selected, ModuleSelectionFilter filter = null)
        {
            filter = filter ?? new ModuleSelectionFilter();
            lock (sync) foreach (var candidate in byPath.Values.Where(filter.Matches).ToArray()) if (!selected || candidate.CanSelect) Set(candidate, selected);
        }
        public void SelectFolder(string canonicalFolderPath, bool selected)
        {
            lock (sync)
            {
                var folder = Request.Folders.Single(f => f.CanonicalFullPath == canonicalFolderPath);
                foreach (var candidate in byPath.Values.Where(c => folder.ContainsModule(c.FullPath)).ToArray()) if (!selected || candidate.CanSelect) Set(candidate, selected);
                if (!selected) foreach (var id in desired.Where(p => folder.ContainsModule(p.Value.CanonicalFullPath)).Select(p => p.Key).ToArray()) desired.Remove(id);
            }
        }
        public void DeselectAll() { lock (sync) { desired.Clear(); foreach (var candidate in byPath.Values.ToArray()) Set(candidate, false); } }
        public IReadOnlyList<BaselineModuleSelection> GetSelectedModules() => Query(new ModuleSelectionFilter(selectedOnly: true))
            .Select(c => new BaselineModuleSelection(c.Module)).ToArray();
        public ModuleSelectionProfile CreateProfile(string name)
        {
            lock (sync) return new ModuleSelectionProfile(name, Request.RepositoryId, Request.Folders,
                byPath.Values.Where(c => c.IsSelected && c.CanSelect).Select(c => c.Module).Concat(desired.Values), DateTime.UtcNow);
        }
        private void LogFailure(Exception ex)
        { try { logger.Write(IntegrationLogLevel.Error, "ModuleSelectionReadFailed", Guid.NewGuid(), "ExceptionType=" + ex.GetType().FullName + "; AutomaticRetry=False"); } catch { } }
    }
}
