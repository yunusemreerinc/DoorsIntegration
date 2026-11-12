using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.ConsoleApp;
using DoorsIntegration.Core;
using DoorsIntegration.Dxl;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass]
    public class LinkTests
    {
        [TestMethod]
        public async Task ServiceMapsBothDirectionsWithoutReversingRealSourceAndTarget()
        {
            using (var context = new LinkTestContext())
            {
                context.Reply = id => LinkTestContext.Batch(id, context.Request, LinkTestContext.Row(1) + LinkTestContext.Row(2, "Incoming",
                    source: LinkTestContext.Endpoint("/P/S", 99, "SRC-99"), target: LinkTestContext.Endpoint("/P/M", 10, "REQ-10")), count: 2);
                using (var snapshot = await new LinkService(context.Reader).ReadAsync(context.Request))
                {
                    var rows = snapshot.ReadLinks().ToArray();
                    Assert.AreEqual(2, snapshot.Summary.LinkCount);
                    Assert.IsTrue(snapshot.Summary.IsCompleteForReadableScope);
                    Assert.AreEqual("/P/N", rows[0].Target.ModulePath);
                    Assert.AreEqual(20, rows[0].Target.AbsoluteNumber);
                    Assert.AreEqual("SRC-99", rows[1].Source.ObjectIdentifier);
                    Assert.AreEqual(10, rows[1].Target.AbsoluteNumber);
                    Assert.AreEqual(LinkDirection.Incoming, rows[1].Direction);
                    Assert.AreEqual("/P/Links", rows[1].LinkModulePath);
                    Assert.AreEqual(ApartmentState.STA, context.Transport.Sessions.OpenApartment);
                    CollectionAssert.Contains(context.Transport.Sessions.Session.Calls, "runFile");
                    Assert.IsTrue(context.Transport.Sessions.Session.Disposed);
                }
                Assert.IsFalse(File.Exists(context.OutputPath));
            }
        }

        [DataTestMethod]
        [DataRow("DeniedOrNotFound")]
        [DataRow("AccessDenied")]
        [DataRow("ModuleDeleted")]
        [DataRow("NotLoadedByPolicy")]
        [DataRow("ModuleLimit")]
        [DataRow("AlreadyOpenForWrite")]
        [DataRow("UnresolvedObject")]
        [DataRow("ReadFailed")]
        public async Task UnresolvedTargetRetainsStoredAbsoluteNumberWithoutInventingDisplayIdentifier(string status)
        {
            using (var context = new LinkTestContext())
            {
                context.Reply = id => LinkTestContext.Batch(id, context.Request, LinkTestContext.Row(1, status: "EndpointUnresolved",
                    target: LinkTestContext.Endpoint("/P/N", 20, "", status)), unresolved: 1);
                using (var snapshot = await context.Reader.ReadAsync(context.Request))
                {
                    var row = snapshot.ReadLinks().Single();
                    Assert.AreEqual(20, row.Target.AbsoluteNumber);
                    Assert.AreEqual("", row.Target.ObjectIdentifier);
                    Assert.AreEqual(status, row.Target.Resolution.ToString());
                    Assert.AreEqual(1, snapshot.Summary.UnresolvedLinkCount);
                    Assert.IsFalse(snapshot.Summary.IsCompleteForReadableScope);
                }
            }
        }
        [TestMethod]
        public async Task DeletedTargetIsRetainedAndNeverBecomesResolvedMatrixEdge()
        {
            using (var context = new LinkTestContext())
            {
                context.Reply = id => LinkTestContext.Batch(id, context.Request, LinkTestContext.Row(1, status: "EndpointUnresolved",
                    target: LinkTestContext.Endpoint("/P/N", 20, "SYS-20", "DeletedObject")), unresolved: 1);
                using (var snapshot = await context.Reader.ReadAsync(context.Request))
                {
                    Assert.AreEqual(LinkEndpointResolution.DeletedObject, snapshot.ReadLinks().Single().Target.Resolution);
                    Assert.IsFalse(snapshot.Summary.IsCompleteForReadableScope);
                }
            }
        }
        [TestMethod]
        public async Task IncomingReferenceSurvivesInaccessibleSourceAndEchoedBaseline()
        {
            using (var context = new LinkTestContext())
            {
                context.Reply = id => LinkTestContext.Batch(id, context.Request, LinkTestContext.Row(1, "Incoming", status: "IncomingReferenceOnly", echoed: true,
                    verification: "FilterOnly", source: LinkTestContext.Endpoint("/P/S", 99, "", "AccessDenied", "Baseline", "2.1(review)"),
                    target: LinkTestContext.Endpoint("/P/M", 10, "REQ-10")), unresolved: 1);
                using (var snapshot = await context.Reader.ReadAsync(context.Request))
                {
                    var row = snapshot.ReadLinks().Single();
                    Assert.IsTrue(row.IsEchoed);
                    Assert.AreEqual(LinkResolution.IncomingReferenceOnly, row.Resolution);
                    Assert.AreEqual("2.1(review)", row.Source.Version);
                    Assert.AreEqual(99, row.Source.AbsoluteNumber);
                    Assert.AreEqual(1, snapshot.Summary.LinkCount);
                }
            }
        }
        [TestMethod]
        public async Task ModuleOnlyIncomingReferenceIsNotCountedAsKnownObjectLink()
        {
            using (var context = new LinkTestContext())
            {
                context.Reply = id => LinkTestContext.Batch(id, context.Request, LinkTestContext.Row(1, "Incoming", "IncomingSourceReference", "SourceReferenceOnly",
                    verification: "FilterOnly", source: LinkTestContext.Endpoint("/P/S", null, "", "UnresolvedObject", "Unknown"),
                    target: LinkTestContext.Endpoint("/P/M", 10, "REQ-10")), gaps: 1);
                using (var snapshot = await context.Reader.ReadAsync(context.Request))
                {
                    Assert.AreEqual(0, snapshot.Summary.LinkCount);
                    Assert.AreEqual(1, snapshot.Summary.SourceReferenceCount);
                    Assert.IsNull(snapshot.ReadLinks().Single().Source.AbsoluteNumber);
                    Assert.IsFalse(snapshot.Summary.IsCompleteForReadableScope);
                }
            }
        }
        [DataTestMethod]
        [DataRow("Matched", "Readable")]
        [DataRow("Matched", "AccessDenied")]
        [DataRow("Matched", "TooLarge")]
        [DataRow("Matched", "DxlEvaluationDisabled")]
        [DataRow("NotMatched", "NotRequested")]
        [DataRow("Unavailable", "NotRequested")]
        public async Task SurrogateMappingPreservesClassificationAndExternalIdentityStatus(string surrogate, string reference)
        {
            var request = new LinkReadRequest("/P/M", "/P/Links", surrogateMapping: new SurrogateMapping("Kind", "Proxy", "External ID"));
            using (var context = new LinkTestContext(request))
            {
                string external = reference == "Readable" ? "external://Türkçe/😀\t\n\"id\"" : "";
                context.Reply = id => LinkTestContext.Batch(id, request, LinkTestContext.Row(1,
                    source: LinkTestContext.Endpoint("/P/M", 10, "REQ-10", surrogate: "NotMatched"),
                    target: LinkTestContext.Endpoint("/P/N", 20, "SYS-20", surrogate: surrogate, external: external, reference: reference)));
                using (var snapshot = await context.Reader.ReadAsync(request))
                {
                    var endpoint = snapshot.ReadLinks().Single().Target;
                    Assert.AreEqual(surrogate, endpoint.SurrogateStatus.ToString());
                    Assert.AreEqual(external, endpoint.ExternalReference);
                    Assert.AreEqual(reference, endpoint.ReferenceStatus.ToString());
                    Assert.AreEqual(surrogate == "Unavailable" || (surrogate == "Matched" && reference != "Readable") ? 1 : 0, snapshot.Summary.SurrogateProblemCount);
                    Assert.IsFalse(string.Join(" ", context.Transport.Log.Entries).Contains("external://"));
                }
            }
        }
        [TestMethod]
        public async Task BaselineEscapingLimitsAndResolutionPolicyAreForwardedWithoutCurrentFallback()
        {
            var request = new LinkReadRequest("/P/\"M\\Türkçe", "/P/\"L\\", LinkDirection.Incoming,
                new RequirementBaseline(2, 1, "review"), openIfNeeded: false, resolveEndpoints: false, includeDeletedObjects: true,
                surrogateMapping: new SurrogateMapping("a\"; halt; //", "proxy", "URI"), maxLinkedModules: 0, target: DoorsVersion.V95);
            using (var context = new LinkTestContext(request))
            {
                context.Reply = id => LinkTestContext.Batch(id, request, count: 0);
                using (await context.Reader.ReadAsync(request))
                {
                    var script = context.Transport.Sessions.Session.Script;
                    Assert.AreEqual(request.ModulePath, DiscoveryTestContext.Parameter(script, "path"));
                    Assert.AreEqual(request.LinkModulePath, DiscoveryTestContext.Parameter(script, "linkPath"));
                    Assert.AreEqual("Baseline", DiscoveryTestContext.Parameter(script, "version"));
                    Assert.AreEqual("Incoming", DiscoveryTestContext.Parameter(script, "direction"));
                    Assert.AreEqual("0", DiscoveryTestContext.Parameter(script, "resolve"));
                    Assert.AreEqual("0", DiscoveryTestContext.Parameter(script, "open"));
                    Assert.AreEqual("0", DiscoveryTestContext.Parameter(script, "maxModules"));
                    Assert.AreEqual(request.SurrogateMapping.MarkerAttribute, DiscoveryTestContext.Parameter(script, "marker"));
                }
            }
        }
        [DataTestMethod]
        [DataRow("DeniedOrNotFound", "Available")]
        [DataRow("BaselineUnavailable", "Available")]
        [DataRow("AlreadyOpenForWrite", "Available")]
        [DataRow("NotFormalModule", "Available")]
        [DataRow("NotLoadedByPolicy", "AccessDenied")]
        [DataRow("NotLoadedByPolicy", "NotLinkModule")]
        [DataRow("NotLoadedByPolicy", "ModuleDeleted")]
        [DataRow("NotLoadedByPolicy", "DeniedOrNotFound")]
        public async Task UnavailableScopeRemainsDifferentFromSuccessfulEmptyMatrix(string module, string link)
        {
            using (var context = new LinkTestContext())
            {
                context.Reply = id => LinkTestContext.Batch(id, context.Request, count: 0, visited: 0, moduleStatus: module, linkStatus: link, ownership: "NotLoaded", opened: 0);
                using (var snapshot = await context.Reader.ReadAsync(context.Request))
                {
                    Assert.AreEqual(module, snapshot.Summary.ModuleStatus.ToString());
                    Assert.AreEqual(link, snapshot.Summary.LinkModuleStatus.ToString());
                    Assert.IsFalse(snapshot.Summary.IsCompleteForReadableScope);
                }
            }
        }
        [TestMethod]
        public async Task PartialEnumerationAndCleanupAreVisible()
        {
            using (var context = new LinkTestContext())
            {
                context.Reply = id => LinkTestContext.Batch(id, context.Request, LinkTestContext.Row(1), visited: 3, denied: 1, deleted: 1,
                    truncated: true, failed: true, opened: 2, cleanupFailures: 1);
                using (var snapshot = await context.Reader.ReadAsync(context.Request))
                {
                    Assert.IsTrue(snapshot.Summary.IsTruncated);
                    Assert.IsTrue(snapshot.Summary.EnumerationFailed);
                    Assert.AreEqual(1, snapshot.Summary.CleanupFailureCount);
                    Assert.AreEqual(1, snapshot.Summary.DeniedObjectCount);
                    Assert.AreEqual(1, snapshot.Summary.DeletedExcludedCount);
                    Assert.IsFalse(snapshot.Summary.IsCompleteForReadableScope);
                }
            }
        }
        [TestMethod]
        public async Task ReadableEmptyScopeIsCompleteAndBorrowedAnchorNeedsNoClose()
        {
            using (var context = new LinkTestContext())
            {
                context.Reply = id => LinkTestContext.Batch(id, context.Request, count: 0, visited: 3, ownership: "BorrowedReadOnly", opened: 0);
                using (var snapshot = await context.Reader.ReadAsync(context.Request))
                {
                    Assert.IsTrue(snapshot.Summary.IsCompleteForReadableScope);
                    Assert.AreEqual(0, snapshot.Summary.LinkCount);
                    Assert.AreEqual(RequirementModuleOwnership.BorrowedReadOnly, snapshot.Summary.Ownership);
                    Assert.AreEqual(0, snapshot.Summary.OpenedModuleCount);
                    Assert.AreEqual(0, snapshot.ReadLinks().Count());
                }
            }
        }
        [TestMethod]
        public async Task InternalLinkBothDirectionsPreservesTwoObservationsWithSameRealEndpoints()
        {
            using (var context = new LinkTestContext())
            {
                string source = LinkTestContext.Endpoint("/P/M", 10, "REQ-10"), target = LinkTestContext.Endpoint("/P/M", 20, "REQ-20");
                context.Reply = id => LinkTestContext.Batch(id, context.Request,
                    LinkTestContext.Row(1, source: source, target: target) + LinkTestContext.Row(2, "Incoming", source: source, target: target), count: 2, visited: 2);
                using (var snapshot = await context.Reader.ReadAsync(context.Request))
                {
                    var rows = snapshot.ReadLinks().ToArray();
                    Assert.AreEqual(2, snapshot.Summary.LinkCount);
                    Assert.AreEqual(rows[0].Source.AbsoluteNumber, rows[1].Source.AbsoluteNumber);
                    Assert.AreEqual(rows[0].Target.AbsoluteNumber, rows[1].Target.AbsoluteNumber);
                    Assert.AreNotEqual(rows[0].Direction, rows[1].Direction);
                }
            }
        }
        [TestMethod]
        public async Task ActiveReaderRetainsFileAfterSnapshotDispose()
        {
            using (var context = new LinkTestContext())
            {
                var snapshot = await context.Reader.ReadAsync(context.Request);
                using (var rows = snapshot.ReadLinks().GetEnumerator())
                {
                    Assert.IsTrue(rows.MoveNext());
                    snapshot.Dispose();
                    Assert.IsTrue(File.Exists(context.OutputPath));
                    Assert.ThrowsException<ObjectDisposedException>(() => snapshot.OpenRawStream());
                }
                Assert.IsFalse(File.Exists(context.OutputPath));
                snapshot.Dispose();
            }
        }
        [TestMethod]
        public async Task TimeoutKeepsComAndGateAliveThenDeletesLateResult()
        {
            var request = new LinkReadRequest("/P/M", "/P/Links", timeout: TimeSpan.FromMilliseconds(100));
            using (var context = new LinkTestContext(request))
            {
                context.Transport.Sessions.Session.BlockRun = true;
                var waiting = context.Reader.ReadAsync(request);
                Assert.IsTrue(context.Transport.Sessions.Session.EnteredRun.Wait(TimeSpan.FromSeconds(5)));
                var operation = context.Transport.Executor.ActiveOperation;
                var error = await Assert.ThrowsExceptionAsync<DxlExecutionWaitException>(() => waiting);
                Assert.AreEqual(IntegrationErrorCode.DxlTimeout, error.Code);
                Assert.IsTrue(context.Transport.Executor.IsBusy);
                context.Transport.Sessions.Session.ReleaseRun.Set();
                await operation.Completion;
                Assert.IsTrue(SpinWait.SpinUntil(() => context.OutputPath != null && !File.Exists(context.OutputPath), TimeSpan.FromSeconds(5)));
                // Late cleanup removes the result file before its workspace. Do not race
                // that continuation with the fixture's recursive parent-directory cleanup.
                Assert.IsTrue(SpinWait.SpinUntil(() => !Directory.Exists(operation.WorkspaceDirectory), TimeSpan.FromSeconds(5)));
            }
        }
        [TestMethod]
        public async Task PreCancellationDoesNotConnect()
        {
            using (var context = new LinkTestContext())
            {
                var error = await Assert.ThrowsExceptionAsync<IntegrationException>(() => context.Reader.ReadAsync(context.Request, new CancellationToken(true)));
                Assert.AreEqual(IntegrationErrorCode.DxlWaitCancelled, error.Code);
                Assert.AreEqual(0, context.Transport.Sessions.OpenCalls);
            }
        }
        [TestMethod]
        public void HostOverrideAndFacadeForwardRequestAndToken()
        {
            var reader = new StubReader();
            var services = new ServiceCollection().AddSingleton<ILinkReader>(reader).AddDoorsIntegration();
            using (var provider = services.BuildServiceProvider())
            {
                var request = new LinkReadRequest("/P/M", "/P/Links");
                var token = new CancellationToken(true);
                provider.GetRequiredService<ILinkService>().ReadAsync(request, token).GetAwaiter().GetResult();
                Assert.AreSame(request, reader.Request); Assert.AreEqual(token, reader.Token);
                Assert.IsNotNull(provider.GetRequiredService<ITraceabilityExporter>());
            }
        }
        private sealed class StubReader : ILinkReader
        {
            public LinkReadRequest Request;
            public CancellationToken Token;
            public Task<ILinkSnapshot> ReadAsync(LinkReadRequest request, CancellationToken cancellationToken = default(CancellationToken))
            { Request = request; Token = cancellationToken; return Task.FromResult<ILinkSnapshot>(null); }
        }
    }
}
