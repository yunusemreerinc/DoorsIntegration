using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;
using DoorsIntegration.Dxl;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass]
    public class DiscoveryTests
    {
        [TestMethod]
        public async Task WindowsResponseWorksThroughExecutorAndHierarchyReader()
        {
            using (var context = new DiscoveryTestContext())
            {
                context.Replies["/"] = id => DiscoveryTestContext.Batch(id, "/", "DatabaseRoot",
                    children: new[] { DiscoveryTestContext.Row("I", "/Özel Proje", "Project") }).Replace("\n", "\r\n");
                var result = await context.Reader.DiscoverAsync(DiscoveryKind.Projects, new DiscoveryScope(recursive: false));
                Assert.IsTrue(result.IsCompleteForVisibleScope);
                Assert.AreEqual("/Özel Proje", result.Items.Single().FullPath);
                Assert.IsFalse(context.Transport.Gate.IsBusy);
            }
        }

        [TestMethod]
        public async Task RejectionLogsSafeDetailsWithDiscoveryAndDxlRequestIds()
        {
            using (var context = new DiscoveryTestContext())
            {
                context.Replies["/PRIVATE_PROJECT"] = id => DiscoveryTestContext.Batch(id, "/PRIVATE_PROJECT", "Project",
                    children: new[] { DiscoveryTestContext.Row("I", "/PRIVATE_PROJECT/PRIVATE_MODULE", "PRIVATE_TYPE") });
                var failure = await Assert.ThrowsExceptionAsync<DiscoveryProtocolException>(async () =>
                    await context.Reader.DiscoverAsync(DiscoveryKind.Hierarchy, new DiscoveryScope("/PRIVATE_PROJECT")));
                var rejection = context.Transport.Log.Entries.Single(entry => entry.StartsWith("DiscoveryBatchRejected:"));
                StringAssert.Contains(rejection, failure.DiagnosticMetadata);
                StringAssert.Contains(rejection, "DxlRequestId=");
                string discoveryId = rejection.Split(':')[1];
                Assert.IsTrue(context.Transport.Log.Entries.Any(entry => entry.StartsWith("DiscoveryStarted:" + discoveryId + ":")));
                Assert.IsFalse(string.Join(" ", context.Transport.Log.Entries).Contains("PRIVATE_"));
                Assert.IsFalse(failure.Message.Contains("PRIVATE_"));
                Assert.IsFalse(context.Transport.Gate.IsBusy);
                Assert.IsTrue(context.Transport.Sessions.Session.Disposed);
            }
        }

        [TestMethod]
        public async Task HierarchyTraversesFoldersAndNestedProjectsWithoutOpeningModules()
        {
            using (var context = new DiscoveryTestContext())
            {
                context.Reply("/", "DatabaseRoot", DiscoveryTestContext.Row("I", "/P", "Project"));
                context.Reply("/P", "Project", DiscoveryTestContext.Row("I", "/P/F", "Folder"), DiscoveryTestContext.Row("I", "/P/L", "Link"));
                context.Reply("/P/F", "Folder", DiscoveryTestContext.Row("I", "/P/F/M", "Formal"), DiscoveryTestContext.Row("I", "/P/F/Nested", "Project"));
                context.Reply("/P/F/Nested", "Project");
                var result = await new ProjectService(context.Reader).DiscoverHierarchyAsync(new DiscoveryScope());
                Assert.IsTrue(result.IsCompleteForVisibleScope);
                Assert.AreEqual("ReadableItemsOnly", result.Visibility);
                Assert.AreEqual(5, result.Items.Count);
                Assert.AreEqual(4, result.VisitedFolders);
                Assert.IsTrue(result.Items.Any(item => item.Type == DoorsItemKind.FormalModule && item.Name == "M"));
                Assert.IsFalse(result.Items.Any(item => item.Type == DoorsItemKind.LinkModule));
                CollectionAssert.AreEqual(new[] { "/", "/P", "/P/F", "/P/F/Nested" }, context.Visits.ToArray());
                Assert.AreEqual(DoorsConnectionMode.AttachExisting, context.Transport.Sessions.Mode);
                Assert.IsFalse(context.Transport.Sessions.Session.Calls.Any(call => call == "read" || call == "edit"));
                Assert.IsTrue(context.Transport.Sessions.Session.Disposed);
            }
        }

        [DataTestMethod]
        [DataRow(DiscoveryKind.Projects, DoorsItemKind.Project)]
        [DataRow(DiscoveryKind.Folders, DoorsItemKind.Folder)]
        [DataRow(DiscoveryKind.FormalModules, DoorsItemKind.FormalModule)]
        public async Task ServiceFiltersPreserveTraversalAndSelectOnlyRequestedTypes(DiscoveryKind kind, DoorsItemKind expected)
        {
            using (var context = new DiscoveryTestContext())
            {
                context.Reply("/P", "Project", DiscoveryTestContext.Row("I", "/P/F", "Folder"), DiscoveryTestContext.Row("I", "/P/M", "Formal"));
                context.Reply("/P/F", "Folder");
                var scope = new DiscoveryScope("/P");
                var projects = new ProjectService(context.Reader);
                var result = kind == DiscoveryKind.Projects ? await projects.DiscoverProjectsAsync(scope) :
                    kind == DiscoveryKind.Folders ? await projects.DiscoverFoldersAsync(scope) :
                    await new ModuleService(context.Reader).DiscoverFormalModulesAsync(scope);
                Assert.AreEqual(kind, result.Kind);
                Assert.IsTrue(result.Items.All(item => item.Type == expected));
                Assert.AreEqual(1, result.Items.Count);
                Assert.AreEqual(2, context.Visits.Count);
            }
        }

        [TestMethod]
        public async Task NonRecursiveScopeDoesNotQueryChildContainers()
        {
            using (var context = new DiscoveryTestContext())
            {
                context.Reply("/P", "Project", DiscoveryTestContext.Row("I", "/P/F", "Folder"));
                var result = await context.Reader.DiscoverAsync(DiscoveryKind.Hierarchy, new DiscoveryScope("/P", recursive: false));
                Assert.AreEqual(1, context.Visits.Count);
                Assert.IsFalse(result.IsTruncated);
                Assert.IsTrue(result.IsCompleteForVisibleScope);
            }
        }

        [DataTestMethod]
        [DataRow("DeniedOrNotFound", "AccessRestricted", DoorsAccessStatus.DeniedOrNotFound)]
        [DataRow("Unavailable", "LookupFailed", DoorsAccessStatus.Unavailable)]
        public async Task KnownHiddenOrUnavailableModuleIsReportedWithoutInventingMetadata(string access, string warning, DoorsAccessStatus expected)
        {
            using (var context = new DiscoveryTestContext())
            {
                context.Replies["/P/Hidden"] = id => DiscoveryTestContext.Batch(id, "/P/Hidden", "Unknown", access, warning: warning);
                var result = await new ModuleService(context.Reader).GetFormalModuleAsync(new DiscoveryScope("/P/Hidden"));
                var item = result.Items.Single();
                Assert.AreEqual(DoorsItemKind.Unknown, item.Type);
                Assert.AreEqual(expected, item.AccessStatus);
                Assert.IsFalse(item.MetadataVerified);
                Assert.IsFalse(result.IsCompleteForVisibleScope);
                Assert.AreEqual(1, result.Issues.Count);
                Assert.AreEqual("lookup", context.LastMode);
                Assert.AreEqual(0, result.VisitedFolders);
            }
        }

        [TestMethod]
        public async Task ReadableKnownFormalModuleReturnsMetadataWithoutTraversal()
        {
            using (var context = new DiscoveryTestContext())
            {
                context.Reply("/P/M", "Formal");
                var result = await new ModuleService(context.Reader).GetFormalModuleAsync(new DiscoveryScope("/P/M"));
                Assert.IsTrue(result.IsCompleteForVisibleScope);
                Assert.AreEqual(DoorsItemKind.FormalModule, result.Items.Single().Type);
                StringAssert.Contains(result.Items[0].AccessDescription, "modül açılmadı");
                Assert.AreEqual(1, context.Visits.Count);
            }
        }

        [TestMethod]
        public async Task WrongKnownModuleTypeIsExplicitAndNotOpened()
        {
            using (var context = new DiscoveryTestContext())
            {
                context.Replies["/P/F"] = id => DiscoveryTestContext.Batch(id, "/P/F", "Folder", warning: "NotFormalModule");
                var result = await new ModuleService(context.Reader).GetFormalModuleAsync(new DiscoveryScope("/P/F"));
                Assert.AreEqual(DoorsItemKind.Folder, result.Items.Single().Type);
                Assert.AreEqual(DiscoveryIssueCode.NotFormalModule, result.Issues.Single().Code);
                Assert.IsFalse(result.IsCompleteForVisibleScope);
            }
        }

        [TestMethod]
        public async Task UnavailableSubfolderRetainsEarlierIdentityAndOtherFoldersContinue()
        {
            using (var context = new DiscoveryTestContext())
            {
                context.Reply("/P", "Project", DiscoveryTestContext.Row("I", "/P/A", "Folder"), DiscoveryTestContext.Row("I", "/P/B", "Folder"));
                context.Replies["/P/A"] = id => DiscoveryTestContext.Batch(id, "/P/A", "Unknown", "DeniedOrNotFound", warning: "AccessRestricted");
                context.Reply("/P/B", "Folder", DiscoveryTestContext.Row("I", "/P/B/M", "Formal"));
                var result = await context.Reader.DiscoverAsync(DiscoveryKind.Hierarchy, new DiscoveryScope("/P"));
                var denied = result.Items.Single(item => item.FullPath == "/P/A");
                Assert.AreEqual(DoorsItemKind.Folder, denied.Type);
                Assert.AreEqual(DoorsAccessStatus.DeniedOrNotFound, denied.AccessStatus);
                Assert.IsTrue(result.Items.Any(item => item.FullPath == "/P/B/M"));
                Assert.IsFalse(result.IsCompleteForVisibleScope);
            }
        }

        [TestMethod]
        public async Task FolderEnumerationFailureRetainsRowsAndReportsPartialScope()
        {
            using (var context = new DiscoveryTestContext())
            {
                context.Replies["/P"] = id => DiscoveryTestContext.Batch(id, "/P", "Project",
                    children: new[] { DiscoveryTestContext.Row("I", "/P/M", "Formal") }, warning: "EnumerationFailed");
                var result = await context.Reader.DiscoverAsync(DiscoveryKind.FormalModules, new DiscoveryScope("/P"));
                Assert.AreEqual("/P/M", result.Items.Single().FullPath);
                Assert.AreEqual(DiscoveryIssueCode.EnumerationFailed, result.Issues.Single().Code);
                Assert.IsFalse(result.IsCompleteForVisibleScope);
            }
        }

        [DataTestMethod]
        [DataRow("items", DiscoveryIssueCode.MaxItemsReached)]
        [DataRow("depth", DiscoveryIssueCode.MaxDepthReached)]
        [DataRow("folders", DiscoveryIssueCode.MaxFoldersReached)]
        public async Task BudgetsReportTruncationInsteadOfCompleteInventory(string budget, DiscoveryIssueCode code)
        {
            using (var context = new DiscoveryTestContext())
            {
                context.Reply("/P", "Project", DiscoveryTestContext.Row("I", "/P/F", "Folder"), DiscoveryTestContext.Row("I", "/P/M", "Formal"));
                context.Reply("/P/F", "Folder");
                var scope = new DiscoveryScope("/P", maxItems: budget == "items" ? 2 : 100, maxDepth: budget == "depth" ? 1 : 32,
                    maxFolders: budget == "folders" ? 1 : 512);
                var result = await context.Reader.DiscoverAsync(DiscoveryKind.Hierarchy, scope);
                Assert.IsTrue(result.IsTruncated);
                Assert.IsFalse(result.IsCompleteForVisibleScope);
                Assert.IsTrue(result.Issues.Any(issue => issue.Code == code));
                Assert.IsTrue(result.Items.Count <= scope.MaxItems);
                Assert.AreEqual(1, context.Visits.Count);
            }
        }

        [TestMethod]
        public async Task DxlBatchOutputLimitRemainsVisibleInSnapshot()
        {
            using (var context = new DiscoveryTestContext())
            {
                context.Replies["/P"] = id => DiscoveryTestContext.Batch(id, "/P", "Project", truncated: true);
                var result = await context.Reader.DiscoverAsync(DiscoveryKind.Hierarchy, new DiscoveryScope("/P"));
                Assert.IsTrue(result.IsTruncated);
                Assert.AreEqual(DiscoveryIssueCode.MaxItemsReached, result.Issues.Single().Code);
            }
        }

        [TestMethod]
        public async Task UnicodeQuotesAndBackslashesAreEscapedAndExcludedFromLogs()
        {
            using (var context = new DiscoveryTestContext())
            {
                string path = "/Özel İçerik/Modül \"A\"\\v";
                context.Reply(path, "Formal");
                var result = await new ModuleService(context.Reader).GetFormalModuleAsync(new DiscoveryScope(path));
                Assert.AreEqual(path, result.Items.Single().FullPath);
                StringAssert.Contains(context.Transport.Sessions.Session.Script, "\\\"A\\\"\\\\v");
                Assert.IsFalse(string.Join(" ", context.Transport.Log.Entries).Contains("Özel İçerik"));
            }
        }

        [TestMethod]
        public async Task TimeoutRetainsComOwnershipAndDoesNotContinueTraversal()
        {
            using (var context = new DiscoveryTestContext())
            {
                context.Transport.Sessions.Session.BlockRun = true;
                context.Reply("/P", "Project", DiscoveryTestContext.Row("I", "/P/F", "Folder"));
                var pending = context.Reader.DiscoverAsync(DiscoveryKind.Hierarchy, new DiscoveryScope("/P", timeout: TimeSpan.FromMilliseconds(100)));
                Assert.IsTrue(context.Transport.Sessions.Session.EnteredRun.Wait(TimeSpan.FromSeconds(5)));
                var failure = await Assert.ThrowsExceptionAsync<DxlExecutionWaitException>(async () => await pending);
                Assert.AreEqual(IntegrationErrorCode.DxlTimeout, failure.Code);
                Assert.IsTrue(context.Transport.Gate.IsBusy);
                Assert.IsFalse(context.Transport.Sessions.Session.Disposed);
                context.Transport.Sessions.Session.ReleaseRun.Set();
                using (await failure.Operation.Completion) { }
                Assert.IsFalse(context.Transport.Gate.IsBusy);
                Assert.AreEqual(1, context.Visits.Count);
            }
        }

        [TestMethod]
        public async Task PreCancelledDiscoveryNeverStartsDxl()
        {
            using (var context = new DiscoveryTestContext())
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                var failure = await Assert.ThrowsExceptionAsync<IntegrationException>(async () =>
                    await context.Reader.DiscoverAsync(DiscoveryKind.Hierarchy, new DiscoveryScope(), cancellation.Token));
                Assert.AreEqual(IntegrationErrorCode.DxlWaitCancelled, failure.Code);
                Assert.AreEqual(0, context.Transport.Sessions.OpenCalls);
            }
        }

        [TestMethod]
        public async Task MissingDeployedScriptFailsBeforeComConnection()
        {
            using (var context = new DiscoveryTestContext())
            {
                var reader = new DxlHierarchyReader(context.Transport.Executor,
                    new DiscoveryScriptOptions(System.IO.Path.Combine(context.Transport.Root, "absent.dxl")), context.Transport.Log);
                var failure = await Assert.ThrowsExceptionAsync<IntegrationException>(async () =>
                    await reader.DiscoverAsync(DiscoveryKind.Hierarchy, new DiscoveryScope()));
                Assert.AreEqual(IntegrationErrorCode.DxlScriptLoadFailed, failure.Code);
                Assert.AreEqual(0, context.Transport.Sessions.OpenCalls);
            }
        }
    }
}
