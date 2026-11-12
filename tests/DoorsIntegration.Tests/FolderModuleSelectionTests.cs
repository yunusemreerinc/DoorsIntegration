using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;
using DoorsIntegration.Wpf.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    internal sealed class SelectionTestAdapter : IDoorsHierarchyReader, IModuleSelectionMetadataProvider
    {
        internal readonly MockModuleSelectionAdapter Source = new MockModuleSelectionAdapter();
        internal readonly List<string> FolderReads = new List<string>();
        internal readonly List<string> ModuleReads = new List<string>();
        internal Func<DiscoveryScope, CancellationToken, Task<DiscoverySnapshot>> Folder;
        internal Func<string, CancellationToken, Task<ModuleSelectionMetadata>> Module;
        public Task<DiscoverySnapshot> DiscoverAsync(DiscoveryKind kind, DiscoveryScope scope, CancellationToken token)
        { Assert.IsFalse(scope.Recursive); FolderReads.Add(scope.RootPath); return Folder == null ? Source.DiscoverAsync(kind, scope, token) : Folder(scope, token); }
        public Task<ModuleSelectionMetadata> ReadAsync(string repository, string path, DoorsVersion target, DoorsConnectionMode mode, TimeSpan timeout, CancellationToken token)
        { ModuleReads.Add(path); return Module == null ? Source.ReadAsync(repository, path, target, mode, timeout, token) : Module(path, token); }
        internal ModuleSelectionSession Create(ModuleSelectionRequest request = null, ModuleSelectionProfile profile = null) =>
            new FolderModuleSelectionService(this, this, new BaselineTestLog()).Create(request ?? Request(), profile);
        internal static ModuleSelectionRequest Request(string path = "/Bulk", bool recursive = true, int maxModules = 10000, int maxFolders = 10000, int maxDepth = 128) =>
            new ModuleSelectionRequest(MockDoorsAdapter.BaselineRepositoryId, new[] { new ModuleSelectionFolder(path, recursive) }, maxModules: maxModules, maxFolders: maxFolders, maxDepth: maxDepth);
        internal static async Task<ModuleSelectionSnapshot> Complete(ModuleSelectionSession session, int budget = 100)
        { int turns = 0; while (session.HasMore) { Assert.IsTrue(++turns < 5000); await session.LoadNextAsync(budget, 5); } return session.Snapshot(); }
    }
    [TestClass]
    public class FolderModuleSelectionTests
    {
        [DataTestMethod, DataRow(1), DataRow(2), DataRow(3)]
        public async Task RepeatedLargeOverlappingTreesKeepOneRowPerStableModule(int repeat)
        {
            Assert.IsTrue(repeat > 0); var adapter = new SelectionTestAdapter();
            var request = new ModuleSelectionRequest(MockDoorsAdapter.BaselineRepositoryId, new[] { new ModuleSelectionFolder("/Bulk"),
                new ModuleSelectionFolder("/Bulk/P01"), new ModuleSelectionFolder("/Bulk/P01/F01", false) });
            var session = adapter.Create(request); var snapshot = await SelectionTestAdapter.Complete(session, 17);
            Assert.AreEqual(1248, snapshot.Candidates.Count); Assert.AreEqual(1209, snapshot.Candidates.Count(c => c.CanSelect));
            Assert.AreEqual(adapter.FolderReads.Count, adapter.FolderReads.Distinct().Count());
            Assert.AreEqual(adapter.ModuleReads.Count, adapter.ModuleReads.Distinct().Count());
            Assert.AreEqual(1209, adapter.ModuleReads.Count); Assert.IsTrue(snapshot.IsIncomplete);
            Assert.IsTrue(snapshot.Candidates.All(c => c.Type == DoorsItemKind.FormalModule));
            Assert.AreEqual(snapshot.Candidates.Count(c => c.Module != null), snapshot.Candidates.Where(c => c.Module != null).Select(c => c.Module.ModuleId).Distinct().Count());
            Assert.IsFalse(adapter.FolderReads.Any(p => p.StartsWith("/Bulk/P06", StringComparison.Ordinal) || p.StartsWith("/Bulk/P02/F08", StringComparison.Ordinal)));
            session.SelectFiltered(true); Assert.AreEqual(1209, session.GetSelectedModules().Count);
            Assert.AreEqual(1209, session.GetSelectedModules().Select(m => m.Module.ModuleId).Distinct().Count());
        }
        [TestMethod]
        public async Task LazyCursorDoesNoPrefetchAndHonorsBothPageBudgets()
        {
            var adapter = new SelectionTestAdapter(); var session = adapter.Create(SelectionTestAdapter.Request("/Bulk/P01/F01"));
            Assert.AreEqual(0, adapter.FolderReads.Count); Assert.AreEqual(0, adapter.ModuleReads.Count);
            var page = await session.LoadNextAsync(7, 1); Assert.AreEqual(7, page.Candidates.Count);
            Assert.AreEqual(1, adapter.FolderReads.Count); Assert.AreEqual(7, adapter.ModuleReads.Count); Assert.IsTrue(page.HasMore);
            await Task.Delay(20); Assert.AreEqual(7, adapter.ModuleReads.Count);
            await session.LoadNextAsync(7, 1); Assert.AreEqual(14, session.Snapshot().Candidates.Count); Assert.AreEqual(1, adapter.FolderReads.Count);
        }
        [DataTestMethod, DataRow(false, 0), DataRow(true, 256)]
        public async Task PerFolderRecursionControlsFormalModuleScope(bool recursive, int count)
        {
            var adapter = new SelectionTestAdapter(); var snapshot = await SelectionTestAdapter.Complete(adapter.Create(SelectionTestAdapter.Request("/Bulk/P01", recursive)));
            Assert.AreEqual(count, snapshot.Candidates.Count);
        }
        [TestMethod]
        public async Task DifferentProjectsKeepIndependentRecursionAndInheritedAccessPolicies()
        {
            var adapter = new SelectionTestAdapter(); var request = new ModuleSelectionRequest(MockDoorsAdapter.BaselineRepositoryId,
                new[] { new ModuleSelectionFolder("/Bulk/P01", false), new ModuleSelectionFolder("/Bulk/P02"), new ModuleSelectionFolder("/Bulk/P06/F01") });
            var snapshot = await SelectionTestAdapter.Complete(adapter.Create(request));
            Assert.AreEqual(224, snapshot.Candidates.Count); Assert.IsTrue(snapshot.Candidates.All(c => c.FullPath.StartsWith("/Bulk/P02/", StringComparison.Ordinal)));
            Assert.IsTrue(snapshot.Issues.Any(i => i.Contains("/Bulk/P06/F01")));
            Assert.IsFalse(adapter.ModuleReads.Any(p => p.StartsWith("/Bulk/P06", StringComparison.Ordinal)));
        }
        [TestMethod]
        public async Task IndividualFolderAndFilteredSelectionDoNotSelectUnloadedOrDeniedRows()
        {
            var adapter = new SelectionTestAdapter(); var request = new ModuleSelectionRequest(MockDoorsAdapter.BaselineRepositoryId,
                new[] { new ModuleSelectionFolder("/Bulk/P01/F01", false), new ModuleSelectionFolder("/Bulk/P01/F02", false) });
            var session = adapter.Create(request); await session.LoadNextAsync(5, 1);
            session.SelectFiltered(true); Assert.AreEqual(5, session.SelectedCount);
            await SelectionTestAdapter.Complete(session); Assert.AreEqual(5, session.SelectedCount);
            session.SelectFolder("/Bulk/P01/F02", true); Assert.AreEqual(36, session.SelectedCount);
            session.SelectFiltered(true, new ModuleSelectionFilter("module 00", ModuleSelectionAccessFilter.Selectable));
            Assert.AreEqual(40, session.SelectedCount);
            var only = session.Query(new ModuleSelectionFilter(selectedOnly: true)); Assert.AreEqual(40, only.Count);
            session.SelectFiltered(false, new ModuleSelectionFilter("/F02/", selectedOnly: true)); Assert.AreEqual(9, session.SelectedCount);
            var module = session.Query().First(c => c.CanSelect); session.SetSelected(module.Module.ModuleId, false); Assert.AreEqual(8, session.SelectedCount);
            Assert.AreEqual(2, session.Query(new ModuleSelectionFilter(access: ModuleSelectionAccessFilter.Restricted)).Count);
            session.DeselectAll(); Assert.AreEqual(0, session.SelectedCount);
        }
        [TestMethod]
        public async Task LatestBaselineNoneIsDistinctFromUnavailableAndDoesNotInventZeroVersion()
        {
            var session = new SelectionTestAdapter().Create(SelectionTestAdapter.Request("/Bulk/P01/F01")); await SelectionTestAdapter.Complete(session);
            var none = session.Query().Single(c => c.Name.EndsWith("007", StringComparison.Ordinal));
            Assert.AreEqual(SelectionBaselineStatus.None, none.Metadata.BaselineStatus); Assert.IsNull(none.Metadata.LatestBaseline); Assert.IsTrue(none.CanSelect);
            var available = session.Query().First(c => c.Metadata.BaselineStatus == SelectionBaselineStatus.Available);
            Assert.AreEqual(new BaselineVersion(1, 2, "demo"), available.Metadata.LatestBaseline);
        }
        [TestMethod]
        public async Task ProfileRoundTripRevalidatesStableIdsAndPreservesUnicodeAndRecursion()
        {
            using (var files = new ExecutorTestContext())
            {
                var adapter = new SelectionTestAdapter(); var request = SelectionTestAdapter.Request("/Bulk/P01/F01", false); var session = adapter.Create(request);
                await SelectionTestAdapter.Complete(session); session.SelectFiltered(true, new ModuleSelectionFilter("module 00"));
                var profile = session.CreateProfile("Türkçe seçim 第二"); var store = new JsonModuleSelectionProfileStore(); var path = Path.Combine(files.Root, "selection.json"); store.Save(path, profile);
                Assert.ThrowsException<IOException>(() => store.Save(path, profile));
                var loaded = store.Load(path); Assert.AreEqual(profile.Name, loaded.Name); Assert.IsFalse(loaded.Folders[0].IncludeSubfolders);
                CollectionAssert.AreEqual(profile.SelectedModules.Select(m => m.ModuleId).ToArray(), loaded.SelectedModules.Select(m => m.ModuleId).ToArray());
                var restored = adapter.Create(request, loaded); Assert.AreEqual(0, restored.SelectedCount); Assert.AreEqual(9, restored.Snapshot().PendingProfileSelections);
                await SelectionTestAdapter.Complete(restored); Assert.AreEqual(9, restored.SelectedCount); Assert.AreEqual(0, restored.Snapshot().PendingProfileSelections);
            }
        }
        [TestMethod]
        public async Task SavedIdsAreNeverRestoredByPathAloneAndMissingIntentCanBeCleared()
        {
            var adapter = new SelectionTestAdapter(); var request = SelectionTestAdapter.Request("/Bulk/P01/F01", false); var original = adapter.Create(request);
            await original.LoadNextAsync(1); original.SelectFiltered(true); var profile = original.CreateProfile("saved");
            adapter.Module = (path, token) => Task.FromResult(new ModuleSelectionMetadata(path, new BaselineModuleReference(request.RepositoryId, "replacement-" + path, path),
                DoorsAccessStatus.Readable, SelectionBaselineStatus.None));
            var restored = adapter.Create(request, profile); await SelectionTestAdapter.Complete(restored);
            Assert.AreEqual(0, restored.SelectedCount); Assert.AreEqual(1, restored.Snapshot().PendingProfileSelections);
            Assert.AreEqual(1, restored.CreateProfile("resave").SelectedModules.Count);
            restored.DeselectAll(); Assert.AreEqual(0, restored.Snapshot().PendingProfileSelections);
        }
        [TestMethod]
        public async Task OneNativeIdAtConflictingPathsIsListedOnceAndCannotBeSelected()
        {
            var adapter = new SelectionTestAdapter { Module = (path, token) => Task.FromResult(new ModuleSelectionMetadata(path,
                new BaselineModuleReference(MockDoorsAdapter.BaselineRepositoryId, "same-stable-id", path), DoorsAccessStatus.Readable, SelectionBaselineStatus.None)) };
            var session = adapter.Create(SelectionTestAdapter.Request("/Bulk/P01/F01")); await SelectionTestAdapter.Complete(session);
            Assert.AreEqual(1, session.Query().Count(c => c.Module != null)); Assert.IsTrue(session.Query().First(c => c.Module != null).HasIdentityConflict);
            Assert.ThrowsException<InvalidOperationException>(() => session.SetSelected("same-stable-id", true));
        }
        [TestMethod]
        public async Task ForeignRepositoryOrWrongMetadataPathCannotBecomeSelected()
        {
            var adapter = new SelectionTestAdapter { Module = (path, token) => Task.FromResult(new ModuleSelectionMetadata(path,
                new BaselineModuleReference("foreign-repository", "id", path), DoorsAccessStatus.Readable, SelectionBaselineStatus.None)) };
            var session = adapter.Create(SelectionTestAdapter.Request("/Bulk/P01/F01")); await session.LoadNextAsync(1);
            Assert.IsFalse(session.Query()[0].CanSelect); Assert.IsNull(session.Query()[0].Module);
        }
        [TestMethod]
        public async Task CancellationKeepsOnlyCommittedRowsAndReleasesCursorWithoutPrefetch()
        {
            using (var cancellation = new CancellationTokenSource())
            {
                var adapter = new SelectionTestAdapter();
                adapter.Module = async (path, token) =>
                {
                    var value = await adapter.Source.ReadAsync(MockDoorsAdapter.BaselineRepositoryId, path, DoorsVersion.V97, DoorsConnectionMode.AttachExisting, TimeSpan.FromSeconds(1), token);
                    if (adapter.ModuleReads.Count == 2) cancellation.Cancel(); return value;
                };
                var session = adapter.Create(SelectionTestAdapter.Request("/Bulk/P01/F01"));
                await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => session.LoadNextAsync(10, 1, cancellation.Token));
                Assert.AreEqual(1, session.Snapshot().Candidates.Count); Assert.AreEqual(2, adapter.ModuleReads.Count); Assert.IsTrue(session.HasMore);
                adapter.Module = null; await session.LoadNextAsync(1); Assert.AreEqual(2, session.Snapshot().Candidates.Count);
            }
        }
        [TestMethod]
        public async Task PreCancelledCursorDoesNotReadAnyFolder()
        {
            using (var cancellation = new CancellationTokenSource())
            { cancellation.Cancel(); var adapter = new SelectionTestAdapter(); await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => adapter.Create().LoadNextAsync(cancellationToken: cancellation.Token)); Assert.AreEqual(0, adapter.FolderReads.Count); }
        }
        [TestMethod]
        public async Task NativeFailureStopsTheCursorWithoutRetryingOtherFoldersOrModules()
        {
            var adapter = new SelectionTestAdapter { Module = (path, token) => throw new TimeoutException("SECRET") };
            var session = adapter.Create(SelectionTestAdapter.Request("/Bulk/P01/F01"));
            await Assert.ThrowsExceptionAsync<TimeoutException>(() => session.LoadNextAsync()); Assert.IsFalse(session.HasMore);
            await session.LoadNextAsync(); Assert.AreEqual(1, adapter.ModuleReads.Count); Assert.IsFalse(string.Join(" ", session.Snapshot().Issues).Contains("SECRET"));
        }
        [DataTestMethod, DataRow(10, 10000, 128), DataRow(10000, 1, 128), DataRow(10000, 10000, 1)]
        public async Task ScopeBoundsAreReportedAsPartial(int modules, int folders, int depth)
        {
            var session = new SelectionTestAdapter().Create(SelectionTestAdapter.Request(maxModules: modules, maxFolders: folders, maxDepth: depth));
            var result = await SelectionTestAdapter.Complete(session); Assert.IsTrue(result.IsIncomplete); Assert.IsTrue(result.Candidates.Count <= modules); Assert.IsTrue(result.VisitedFolders <= folders);
        }
        [TestMethod]
        public async Task ConcurrentCursorAdvanceIsRejected()
        {
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var adapter = new SelectionTestAdapter(); adapter.Folder = async (scope, token) => { entered.SetResult(true); await release.Task; return await adapter.Source.DiscoverAsync(DiscoveryKind.Hierarchy, scope, token); };
            var session = adapter.Create(); var first = session.LoadNextAsync(); await entered.Task;
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => session.LoadNextAsync()); release.SetResult(true); await first;
        }
        [TestMethod]
        public async Task OutOfScopeDiscoveryCannotBeUsedAsASelectionInventory()
        {
            var adapter = new SelectionTestAdapter { Folder = (scope, token) => Task.FromResult(new DiscoverySnapshot(Guid.NewGuid(), DiscoveryKind.Hierarchy, scope,
                new[] { new DoorsDiscoveryItem("/Foreign/Module", "Module", DoorsItemKind.FormalModule, DoorsAccessStatus.Readable) }, Array.Empty<DiscoveryIssue>(), 1, false, DateTime.UtcNow)) };
            await Assert.ThrowsExceptionAsync<IntegrationException>(() => adapter.Create().LoadNextAsync()); Assert.AreEqual(0, adapter.ModuleReads.Count);
        }
        [DataTestMethod, DataRow("unknown"), DataRow("duplicate"), DataRow("wrong-schema"), DataRow("wrong-boolean")]
        public void InvalidProfilesAreRejectedWithoutRestoringSelection(string mutation)
        {
            using (var files = new ExecutorTestContext())
            {
                var store = new JsonModuleSelectionProfileStore(); var path = Path.Combine(files.Root, "profile.json");
                store.Save(path, new ModuleSelectionProfile("name", "repo", new[] { new ModuleSelectionFolder("/P") }, Array.Empty<BaselineModuleReference>(), DateTime.UtcNow));
                string json = File.ReadAllText(path);
                if (mutation == "unknown") json = json.Replace("\"Name\":", "\"Extra\":0,\"Name\":");
                else if (mutation == "duplicate") json = json.Replace("\"Name\":", "\"Name\":\"duplicate\",\"Name\":");
                else if (mutation == "wrong-schema") json = json.Replace("\"SchemaVersion\":1", "\"SchemaVersion\":2");
                else json = json.Replace("\"IncludeSubfolders\":true", "\"IncludeSubfolders\":1");
                File.WriteAllText(path, json); Assert.ThrowsException<InvalidDataException>(() => store.Load(path));
            }
        }
        [TestMethod]
        public void ProfilesAndRequestsRejectCrossRepositoryOrInvalidFolderBindings()
        {
            var profile = new ModuleSelectionProfile("name", "repo", new[] { new ModuleSelectionFolder("/P") }, new[] { new BaselineModuleReference("repo", "native", "/P/M") }, DateTime.UtcNow);
            Assert.ThrowsException<ArgumentException>(() => new SelectionTestAdapter().Create(new ModuleSelectionRequest("other", profile.Folders), profile));
            Assert.ThrowsException<ArgumentException>(() => new ModuleSelectionProfile("name", "repo", profile.Folders, new[] { new BaselineModuleReference("repo", "native", "/Q/M") }, DateTime.UtcNow));
            Assert.ThrowsException<ArgumentException>(() => new ModuleSelectionRequest("repo", new[] { new ModuleSelectionFolder("/P"), new ModuleSelectionFolder("/P") }));
        }
    }
}
