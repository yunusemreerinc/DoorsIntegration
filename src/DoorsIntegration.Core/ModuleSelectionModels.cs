using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DoorsIntegration.Core
{
    public enum SelectionBaselineStatus { Available, None, Unavailable }
    public enum ModuleSelectionAccessFilter { All, Selectable, Restricted }
    public sealed class ModuleSelectionFolder
    {
        public ModuleSelectionFolder(string canonicalFullPath, bool includeSubfolders = true)
        {
            CanonicalFullPath = DoorsItemPath.Normalize(canonicalFullPath);
            if (canonicalFullPath != CanonicalFullPath) throw new ArgumentException("Supply a canonical folder path.");
            IncludeSubfolders = includeSubfolders;
        }
        public string CanonicalFullPath { get; }
        public bool IncludeSubfolders { get; }
        internal bool ContainsModule(string path) => DoorsItemPath.IsDirectChild(CanonicalFullPath, path) ||
            (IncludeSubfolders && path.StartsWith(CanonicalFullPath == "/" ? "/" : CanonicalFullPath + "/", StringComparison.Ordinal));
    }
    public sealed class ModuleSelectionRequest
    {
        public ModuleSelectionRequest(string repositoryId, IEnumerable<ModuleSelectionFolder> folders, DoorsVersion target = DoorsVersion.V97,
            DoorsConnectionMode connectionMode = DoorsConnectionMode.AttachExisting, TimeSpan? timeout = null,
            int maxModules = 10000, int maxFolders = 10000, int maxDepth = 128)
        {
            RepositoryId = BulkBaselineGuard.Text(repositoryId, nameof(repositoryId), 1024);
            Folders = BulkBaselineGuard.List(folders ?? throw new ArgumentNullException(nameof(folders)), 256);
            if (Folders.Count == 0 || Folders.Select(f => f.CanonicalFullPath).Distinct(StringComparer.Ordinal).Count() != Folders.Count)
                throw new ArgumentException("Select 1 to 256 distinct project/folder paths.");
            DoorsVersions.Format(target); BulkBaselineGuard.Defined(connectionMode);
            Timeout = timeout ?? TimeSpan.FromSeconds(30);
            if (maxModules < 1 || maxModules > 100000 || maxFolders < 1 || maxFolders > 100000 || maxDepth < 1 || maxDepth > 128 ||
                Timeout < TimeSpan.FromMilliseconds(1) || Timeout > TimeSpan.FromHours(24)) throw new ArgumentOutOfRangeException(nameof(maxModules));
            Target = target; ConnectionMode = connectionMode; MaxModules = maxModules; MaxFolders = maxFolders; MaxDepth = maxDepth;
        }
        public string RepositoryId { get; }
        public IReadOnlyList<ModuleSelectionFolder> Folders { get; }
        public DoorsVersion Target { get; }
        public DoorsConnectionMode ConnectionMode { get; }
        public TimeSpan Timeout { get; }
        public int MaxModules { get; }
        public int MaxFolders { get; }
        public int MaxDepth { get; }
    }
    public sealed class ModuleSelectionMetadata
    {
        public ModuleSelectionMetadata(string canonicalFullPath, BaselineModuleReference module, DoorsAccessStatus access,
            SelectionBaselineStatus baselineStatus, BaselineVersion latestBaseline = null, string annotation = "", string failureReason = null)
        {
            CanonicalFullPath = DoorsItemPath.Normalize(canonicalFullPath);
            if (CanonicalFullPath != canonicalFullPath || (module != null && module.CanonicalFullPath != canonicalFullPath)) throw new ArgumentException("Metadata path mismatch.");
            BulkBaselineGuard.Defined(access); BulkBaselineGuard.Defined(baselineStatus);
            if ((baselineStatus == SelectionBaselineStatus.Available) != (latestBaseline != null)) throw new ArgumentException("Latest baseline status/value mismatch.");
            Module = module; Access = access; BaselineStatus = baselineStatus; LatestBaseline = latestBaseline;
            Annotation = BulkBaselineGuard.Annotation(annotation); FailureReason = failureReason;
        }
        public string CanonicalFullPath { get; }
        public BaselineModuleReference Module { get; }
        public DoorsAccessStatus Access { get; }
        public SelectionBaselineStatus BaselineStatus { get; }
        public BaselineVersion LatestBaseline { get; }
        public string Annotation { get; }
        public string FailureReason { get; }
        public static ModuleSelectionMetadata Unavailable(string path, string reason) => new ModuleSelectionMetadata(path, null,
            DoorsAccessStatus.Unavailable, SelectionBaselineStatus.Unavailable, failureReason: reason);
    }
    public sealed class ModuleSelectionCandidate
    {
        internal ModuleSelectionCandidate(DoorsDiscoveryItem item, ModuleSelectionMetadata metadata, bool selected, bool conflict)
        { Item = item; Metadata = metadata; IsSelected = selected; HasIdentityConflict = conflict; }
        public DoorsDiscoveryItem Item { get; }
        public string Name => Item.Name;
        public string FullPath => Item.FullPath;
        public DoorsItemKind Type => Item.Type;
        public DoorsAccessStatus Access => Item.AccessStatus != DoorsAccessStatus.Readable ? Item.AccessStatus : Metadata.Access;
        public ModuleSelectionMetadata Metadata { get; }
        public BaselineModuleReference Module => Metadata.Module;
        public bool HasIdentityConflict { get; }
        public bool CanSelect => !HasIdentityConflict && Item.MetadataVerified && Type == DoorsItemKind.FormalModule &&
            Access == DoorsAccessStatus.Readable && Module != null;
        public bool IsSelected { get; }
        public string FailureReason => HasIdentityConflict ? "Stable identity/path conflict; refresh and review the selection." : Metadata.FailureReason;
    }
    public sealed class ModuleSelectionFilter
    {
        public ModuleSelectionFilter(string search = "", ModuleSelectionAccessFilter access = ModuleSelectionAccessFilter.All, bool selectedOnly = false)
        {
            if (search == null || search.Length > 256) throw new ArgumentException("Search limit is 256 characters.");
            BulkBaselineGuard.Defined(access); Search = search; Access = access; SelectedOnly = selectedOnly;
        }
        public string Search { get; }
        public ModuleSelectionAccessFilter Access { get; }
        public bool SelectedOnly { get; }
        internal bool Matches(ModuleSelectionCandidate item) => (!SelectedOnly || item.IsSelected) &&
            (Access == ModuleSelectionAccessFilter.All || (Access == ModuleSelectionAccessFilter.Selectable ? item.CanSelect : !item.CanSelect)) &&
            (Search.Length == 0 || item.Name.IndexOf(Search, StringComparison.OrdinalIgnoreCase) >= 0 ||
                item.FullPath.IndexOf(Search, StringComparison.OrdinalIgnoreCase) >= 0 || (item.Module?.ModuleId.IndexOf(Search, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0);
    }
    public sealed class ModuleSelectionSnapshot
    {
        internal ModuleSelectionSnapshot(IEnumerable<ModuleSelectionCandidate> candidates, IEnumerable<string> issues,
            bool hasMore, bool incomplete, int visitedFolders, int pendingSelections)
        { Candidates = BulkBaselineGuard.List(candidates, 100000); Issues = BulkBaselineGuard.List(issues, 10000); HasMore = hasMore; IsIncomplete = incomplete; VisitedFolders = visitedFolders; PendingProfileSelections = pendingSelections; }
        public IReadOnlyList<ModuleSelectionCandidate> Candidates { get; }
        public IReadOnlyList<string> Issues { get; }
        public bool HasMore { get; }
        public bool IsIncomplete { get; }
        public int VisitedFolders { get; }
        public int PendingProfileSelections { get; }
        public int SelectedCount => Candidates.Count(c => c.IsSelected);
    }
    public interface IModuleSelectionMetadataProvider
    {
        Task<ModuleSelectionMetadata> ReadAsync(string repositoryId, string canonicalModulePath, DoorsVersion target,
            DoorsConnectionMode connectionMode, TimeSpan timeout, CancellationToken cancellationToken);
    }
    public interface IFolderModuleSelectionService
    {
        ModuleSelectionSession Create(ModuleSelectionRequest request, ModuleSelectionProfile profile = null);
    }
    public interface IModuleSelectionProfileStore
    {
        void Save(string path, ModuleSelectionProfile profile);
        ModuleSelectionProfile Load(string path);
    }
    public sealed class UnavailableModuleSelectionMetadataProvider : IModuleSelectionMetadataProvider
    {
        public Task<ModuleSelectionMetadata> ReadAsync(string repositoryId, string canonicalModulePath, DoorsVersion target,
            DoorsConnectionMode connectionMode, TimeSpan timeout, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(ModuleSelectionMetadata.Unavailable(canonicalModulePath, "NOT_RUN: verified selection metadata provider is not configured.")); }
    }
}
