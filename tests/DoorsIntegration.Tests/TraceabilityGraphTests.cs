using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using DoorsIntegration.Core;
using DoorsIntegration.Export;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    internal sealed class GraphTestData : IRequirementService, ILinkService
    {
        internal const string R = "/P/Requirements", S = "/P/Standards", L = "/P/TraceLinks";
        internal readonly List<LinkRecord> Rows = new List<LinkRecord>();
        internal int RequirementCalls, LinkCalls, Disposals;
        internal int[] Objects = { 1, 2, 3 };
        internal bool NativeLinkIds = true, NativeRequirementIds = true, LinkAnchorIds = true, Incomplete, DeletedLast;
        internal string Identifier = "=REQ-İ😀", NativeLinkId = "database:links-1";
        internal Func<LinkReadRequest, Exception> LinkFailure;
        internal Action<LinkReadRequest> BeforeLinks;
        internal Func<LinkReadRequest, string> LinkIdOverride;
        internal Func<RequirementReadRequest, string> RequirementIdOverride;
        internal readonly List<RequirementReadRequest> RequirementRequests = new List<RequirementReadRequest>();
        internal readonly List<LinkReadRequest> LinkRequests = new List<LinkReadRequest>();
        internal readonly IIntegrationLogger Log = new ExecutorTestLog();
        internal TraceabilityGraphService Service => new TraceabilityGraphService(this, this, Log);
        internal TraceabilityGraphRequest Request(TraceabilityPolicy policy = null, int maxNodes = 100000, int maxObservations = 250000, int maxFindings = 500000,
            IEnumerable<TraceabilityModuleScope> scopes = null, long maxMetadataCharacters = 67108864) => new TraceabilityGraphRequest("test-repository", scopes ?? new[] {
                new TraceabilityModuleScope(R), new TraceabilityModuleScope(S, "Standard") }, new[] { L }, policy ?? Policy(), maxNodes, maxObservations, maxFindings, maxMetadataCharacters: maxMetadataCharacters);
        internal static TraceabilityPolicy Policy(string native = null, int min = 1, int? max = null, TraceabilityVersionPolicy version = TraceabilityVersionPolicy.SameVersion) =>
            new TraceabilityPolicy("requirements-to-standards", new[] { new TraceabilityLinkRule("required-standard", "Requirement", new[] { "Standard" }, L, minimumLinks: min, maximumLinks: max, versionPolicy: version, requiredNativeLinkModuleId: native) });
        internal static LinkEndpoint End(string path, int? id, string baseline = null, LinkEndpointResolution resolution = LinkEndpointResolution.Resolved) =>
            new LinkEndpoint(path, baseline == null ? LinkVersionKind.Current : LinkVersionKind.Baseline, baseline ?? "", id, id?.ToString() ?? "", resolution);
        internal void Edge(int source, int target, string sourceModule = R, string targetModule = S, string sourceVersion = null, string targetVersion = null,
            LinkEndpointResolution targetAccess = LinkEndpointResolution.Resolved, bool echoed = false, LinkModuleResolution verification = LinkModuleResolution.Verified)
        { Rows.Add(new LinkRecord(Rows.Count + 1, LinkDirection.Outgoing, LinkRecordKind.Link, targetAccess == LinkEndpointResolution.Resolved ? LinkResolution.Resolved : LinkResolution.EndpointUnresolved,
            echoed, L, verification, End(sourceModule, source, sourceVersion), End(targetModule, target, targetVersion, targetAccess))); }
        public Task<IRequirementSnapshot> ReadAsync(RequirementReadRequest request, CancellationToken cancellationToken = default(CancellationToken))
        {
            RequirementCalls++; RequirementRequests.Add(request); cancellationToken.ThrowIfCancellationRequested();
            var rows = Objects.Select((id, order) => new RequirementRecord(order, id, Identifier + id, "1.1", RequirementValueStatus.Readable, 1, null, RequirementParentStatus.Root,
                DeletedLast && id == Objects.Last(), new RequirementValue[0])).ToArray();
            return Task.FromResult<IRequirementSnapshot>(new Requirements(this, new RequirementReadSummary(Guid.NewGuid(), request, RequirementModuleStatus.Available, RequirementModuleOwnership.OwnedReadOnly,
                RequirementCleanupStatus.Closed, rows.Length, rows.Length, 0, 0, 0, 0, Incomplete, false, NativeRequirementIds ? RequirementIdOverride?.Invoke(request) ?? "database:" + request.ModulePath : null), rows));
        }
        Task<ILinkSnapshot> ILinkService.ReadAsync(LinkReadRequest request, CancellationToken cancellationToken)
        {
            LinkCalls++; LinkRequests.Add(request); BeforeLinks?.Invoke(request); var exception = LinkFailure?.Invoke(request); if (exception != null) throw exception;
            var records = new List<LinkRecord>();
            foreach (var row in Rows)
            {
                if (row.Kind == LinkRecordKind.Link && Matches(row.Source, request)) records.Add(Copy(row, records.Count + 1, LinkDirection.Outgoing));
                if (Matches(row.Target, request)) records.Add(Copy(row, records.Count + 1, LinkDirection.Incoming));
            }
            bool partial = records.Any(r => r.Resolution != LinkResolution.Resolved || r.Kind != LinkRecordKind.Link);
            var summary = new LinkReadSummary(Guid.NewGuid(), request, RequirementModuleStatus.Available, LinkModuleStatus.Available, RequirementModuleOwnership.OwnedReadOnly,
                records.Count, Objects.Length, 0, 0, partial ? 1 : 0, records.Count(r => r.Kind == LinkRecordKind.IncomingSourceReference), Incomplete, false, 0, 0, 0,
                LinkAnchorIds ? "database:" + request.ModulePath : null, NativeLinkIds ? (LinkIdOverride == null ? NativeLinkId : LinkIdOverride(request)) : null);
            return Task.FromResult<ILinkSnapshot>(new Links(this, summary, records));
        }
        private static bool Matches(LinkEndpoint endpoint, LinkReadRequest request) => endpoint.ModulePath == request.ModulePath && (request.Baseline == null ? endpoint.VersionKind == LinkVersionKind.Current :
            endpoint.VersionKind == LinkVersionKind.Baseline && endpoint.Version == (request.Baseline.Major == 1 ? "opaque-first" : "opaque-second"));
        private static LinkRecord Copy(LinkRecord row, int sequence, LinkDirection direction) => new LinkRecord(sequence, direction, row.Kind, row.Resolution, row.IsEchoed, row.LinkModulePath, row.LinkModuleResolution, row.Source, row.Target);
        public Task<RequirementModuleSchema> GetAttributeSchemaAsync(RequirementWriteTarget target, CancellationToken cancellationToken = default(CancellationToken)) => throw new AssertFailedException("Graph must not load edit schema.");
        public Task<RequirementWritePreview> PreviewWriteAsync(RequirementWriteRequest request, CancellationToken cancellationToken = default(CancellationToken)) => throw new AssertFailedException("Graph must not preview writes.");
        public Task<RequirementWriteResult> ApplyWriteAsync(RequirementWritePreview preview, CancellationToken cancellationToken = default(CancellationToken)) => throw new AssertFailedException("Graph must not write.");
        public Task<LinkWritePreview> PreviewWriteAsync(LinkWriteRequest request, CancellationToken cancellationToken = default(CancellationToken)) => throw new AssertFailedException("Graph must not preview links.");
        public Task<LinkWriteResult> ApplyWriteAsync(LinkWritePreview preview, CancellationToken cancellationToken = default(CancellationToken)) => throw new AssertFailedException("Graph must not change links.");
        private sealed class Requirements : IRequirementSnapshot
        { private readonly GraphTestData data; private readonly RequirementRecord[] rows; internal Requirements(GraphTestData data, RequirementReadSummary summary, RequirementRecord[] rows) { this.data = data; Summary = summary; this.rows = rows; }
            public RequirementReadSummary Summary { get; } public IEnumerable<RequirementRecord> ReadObjects() => rows; public Stream OpenRawStream() => throw new AssertFailedException("No raw payload retention needed."); public void Dispose() { data.Disposals++; } }
        private sealed class Links : ILinkSnapshot
        { private readonly GraphTestData data; private readonly IEnumerable<LinkRecord> rows; internal Links(GraphTestData data, LinkReadSummary summary, IEnumerable<LinkRecord> rows) { this.data = data; Summary = summary; this.rows = rows; }
            public LinkReadSummary Summary { get; } public IEnumerable<LinkRecord> ReadLinks() => rows; public Stream OpenRawStream() => throw new AssertFailedException("No raw payload retention needed."); public void Dispose() { data.Disposals++; } }
    }
    [TestClass]
    public class TraceabilityGraphTests
    {
        private static TraceabilityNode Node(TraceabilityGraph g, string module, int id, string version = "") => g.Nodes.Values.Single(n => n.ModulePath == module && n.AbsoluteNumber == id && n.Version == version);
        [TestMethod]
        public async Task IncomingOutgoingDeduplicateAndPreserveNaturalDirectionAndIdentity()
        {
            var data = new GraphTestData(); data.Edge(1, 1); var graph = await data.Service.BuildAsync(data.Request());
            Assert.AreEqual(6, graph.Nodes.Count); Assert.AreEqual(1, graph.Edges.Count); Assert.AreEqual(2, graph.Observations.Count);
            var edge = graph.Edges.Single(); Assert.AreEqual(Node(graph, GraphTestData.R, 1).Id, edge.SourceId); Assert.AreEqual(Node(graph, GraphTestData.S, 1).Id, edge.TargetId);
            Assert.AreEqual(2, edge.ObservedDirections.Count); Assert.AreEqual(data.NativeLinkId, edge.QualifiedLinkModuleId); Assert.AreEqual(TraceabilityEdgeState.Confirmed, edge.State);
            Assert.AreEqual(4, data.Disposals); Assert.IsTrue(data.RequirementRequests.All(r => !r.AllowAttributeDxl && r.DeletedObjects == DeletedObjectMode.MetadataOnly));
            Assert.IsTrue(data.LinkRequests.All(r => r.Direction == LinkDirection.Both && !r.IncludeDeletedObjects));
        }
        [TestMethod]
        public async Task ExpectedStandardsLinksOrphansAndDuplicateCountsUseOnlyDistinctCounterparts()
        {
            var data = new GraphTestData(); data.Edge(1, 1); var graph = await data.Service.BuildAsync(data.Request(GraphTestData.Policy(min: 2)));
            var row = graph.Findings.Single(f => f.Code == "MissingExpectedLink" && f.NodeId == Node(graph, GraphTestData.R, 1).Id);
            Assert.AreEqual(1, row.ObservedCount); Assert.AreEqual(TraceabilityState.Violation, row.State);
            Assert.AreEqual(2, graph.Findings.Count(f => f.Code == "OrphanRequirement")); Assert.IsTrue(graph.IsCompleteWithinRequestedScope);
        }
        [TestMethod]
        public async Task AllowedModelMinimumMaximumAndWrongCounterpartAreSeparatelyValidated()
        {
            var data = new GraphTestData(); data.Edge(1, 1); data.Edge(1, 2); data.Edge(1, 2, targetModule: GraphTestData.R);
            var graph = await data.Service.BuildAsync(data.Request(GraphTestData.Policy(max: 1)));
            Assert.IsTrue(graph.Findings.Any(f => f.Code == "ExpectedLinkSatisfied")); Assert.IsTrue(graph.Findings.Any(f => f.Code == "MaximumLinksExceeded"));
            Assert.IsTrue(graph.Findings.Any(f => f.Code == "UnexpectedLinkModel"));
        }
        [DataTestMethod, DataRow(TraceabilityDirection.Incoming), DataRow(TraceabilityDirection.Outgoing)]
        public async Task ExpectedLinkPoliciesUseTheRequestedAnchorDirection(TraceabilityDirection direction)
        {
            var data = new GraphTestData(); data.Edge(1, 1); string anchor = direction == TraceabilityDirection.Incoming ? "Standard" : "Requirement";
            var policy = new TraceabilityPolicy("direction", new[] { new TraceabilityLinkRule("direction-rule", anchor, new[] { direction == TraceabilityDirection.Incoming ? "Requirement" : "Standard" }, GraphTestData.L, direction) });
            var graph = await data.Service.BuildAsync(data.Request(policy)); var satisfied = graph.Findings.Single(f => f.Code == "ExpectedLinkSatisfied");
            Assert.AreEqual(Node(graph, direction == TraceabilityDirection.Incoming ? GraphTestData.S : GraphTestData.R, 1).Id, satisfied.NodeId); Assert.AreEqual(1, satisfied.ObservedCount);
        }
        [DataTestMethod, DataRow(TraceabilityCycleBehavior.Allow), DataRow(TraceabilityCycleBehavior.Report), DataRow(TraceabilityCycleBehavior.Prohibit)]
        public async Task CyclesRespectExplicitPolicyInsteadOfAlwaysBecomingErrors(TraceabilityCycleBehavior behavior)
        {
            var data = new GraphTestData(); data.Edge(1, 2, targetModule: GraphTestData.R); data.Edge(2, 3, targetModule: GraphTestData.R); data.Edge(3, 1, targetModule: GraphTestData.R);
            var policy = new TraceabilityPolicy("cycles", cycleRules: new[] { new TraceabilityCycleRule("feedback", behavior, new[] { "Requirement" }, new[] { GraphTestData.L }) });
            var graph = await data.Service.BuildAsync(data.Request(policy)); Assert.AreEqual(1, graph.Cycles.Count); Assert.AreEqual(3, graph.Cycles.Single().NodeIds.Count);
            Assert.AreEqual(behavior == TraceabilityCycleBehavior.Prohibit, graph.Findings.Any(f => f.Severity == TraceabilitySeverity.Error));
        }
        [TestMethod]
        public async Task SelfLoopAndDisconnectedComponentsAreDetectedWithoutJoiningOneWayConnections()
        {
            var data = new GraphTestData(); data.Edge(1, 1, targetModule: GraphTestData.R); data.Edge(2, 3, targetModule: GraphTestData.R); data.Edge(3, 2, targetModule: GraphTestData.R); data.Edge(1, 2, targetModule: GraphTestData.R);
            var graph = await data.Service.BuildAsync(data.Request(new TraceabilityPolicy("cycles")));
            Assert.AreEqual(2, graph.Cycles.Count); CollectionAssert.AreEquivalent(new[] { 1, 2 }, graph.Cycles.Select(c => c.NodeIds.Count).ToArray());
        }
        [TestMethod]
        public async Task MultiHopImpactSupportsBothDirectionsAndBoundsAndCycleTermination()
        {
            var data = new GraphTestData(); data.Edge(1, 2, targetModule: GraphTestData.R); data.Edge(2, 3, targetModule: GraphTestData.R); data.Edge(3, 1, targetModule: GraphTestData.R);
            var service = data.Service; var graph = await service.BuildAsync(data.Request()); string seed = Node(graph, GraphTestData.R, 1).Id;
            var outgoing = service.AnalyzeImpact(graph, new[] { seed }); Assert.AreEqual(2, outgoing.Visits.Single(v => v.NodeId == Node(graph, GraphTestData.R, 3).Id).Distance);
            var incoming = service.AnalyzeImpact(graph, new[] { seed }, TraceabilityDirection.Incoming); Assert.AreEqual(1, incoming.Visits.Single(v => v.NodeId == Node(graph, GraphTestData.R, 3).Id).Distance);
            var both = service.AnalyzeImpact(graph, new[] { seed }, TraceabilityDirection.Both); Assert.AreEqual(3, both.Visits.Count);
            Assert.IsTrue(service.AnalyzeImpact(graph, new[] { seed }, maxDepth: 1).IsTruncated);
            Assert.IsTrue(service.AnalyzeImpact(graph, new[] { seed }, maxVisits: 1).IsTruncated);
            Assert.IsTrue(service.AnalyzeImpact(graph, new[] { seed }, maxTraversedEdges: 1).IsTruncated);
        }
        [TestMethod]
        public async Task InaccessibleTargetsStayPossibleAndDoNotProveMissingLinksOrOrphans()
        {
            var data = new GraphTestData(); data.Edge(1, 1, targetAccess: LinkEndpointResolution.AccessDenied); var service = data.Service; var graph = await service.BuildAsync(data.Request());
            Assert.IsTrue(graph.Findings.Any(f => f.Code == "InaccessibleTarget")); Assert.IsFalse(graph.Findings.Any(f => f.Code == "MissingExpectedLink" || f.Code == "OrphanRequirement"));
            Assert.AreEqual(TraceabilityEdgeState.Possible, graph.Edges.Single().State); string seed = Node(graph, GraphTestData.R, 1).Id;
            Assert.AreEqual(1, service.AnalyzeImpact(graph, new[] { seed }).Visits.Count);
            var possible = service.AnalyzeImpact(graph, new[] { seed }, includePossibleEdges: true); Assert.IsFalse(possible.Visits.Last().IsConfirmedPath); Assert.IsFalse(possible.IsConclusiveWithinRequestedScope);
        }
        [TestMethod]
        public async Task IncomingSourceReferencesKeepUnknownObjectIdentityAndNeverCountAsLinks()
        {
            var data = new GraphTestData(); data.Rows.Add(new LinkRecord(1, LinkDirection.Incoming, LinkRecordKind.IncomingSourceReference, LinkResolution.IncomingReferenceOnly, false, GraphTestData.L, LinkModuleResolution.FilterOnly,
                GraphTestData.End(GraphTestData.S, null, resolution: LinkEndpointResolution.DeniedOrNotFound), GraphTestData.End(GraphTestData.R, 1)));
            var graph = await data.Service.BuildAsync(data.Request()); Assert.AreEqual(TraceabilityEdgeState.Reference, graph.Edges.Single().State);
            Assert.IsTrue(graph.Nodes.Values.Any(n => !n.AbsoluteNumber.HasValue && n.ObjectIdentityEvidence.Contains("NoPersistent"))); Assert.AreEqual(0, graph.Cycles.Count);
        }
        [DataTestMethod, DataRow(true), DataRow(false)]
        public async Task EchoOrFilterOnlyEdgesCannotMakeImpactOrModelValidationConclusive(bool echoed)
        {
            var data = new GraphTestData(); data.Edge(1, 1, echoed: echoed, verification: echoed ? LinkModuleResolution.Verified : LinkModuleResolution.FilterOnly);
            var service = data.Service; var graph = await service.BuildAsync(data.Request(GraphTestData.Policy(min: 0)));
            Assert.IsTrue(graph.Findings.Any(f => f.Code == "LinkModelUnknown"));
            var impact = service.AnalyzeImpact(graph, new[] { Node(graph, GraphTestData.R, 1).Id }); Assert.IsFalse(impact.IsConclusiveWithinRequestedScope); Assert.IsTrue(impact.BoundaryNodeIds.Count > 0);
        }
        [TestMethod]
        public async Task BaselinesRemainDistinctAndOpaqueNativeLabelsBindOnlyThroughAnchorEvidence()
        {
            var data = new GraphTestData { Objects = new[] { 1 } }; data.Edge(1, 1, targetModule: GraphTestData.R, sourceVersion: "opaque-first", targetVersion: "opaque-second");
            var modules = new[] { new TraceabilityModuleScope(GraphTestData.R, baseline: new RequirementBaseline(1, 0, "A")), new TraceabilityModuleScope(GraphTestData.R, baseline: new RequirementBaseline(2, 0, "B")) };
            var policy = new TraceabilityPolicy("baseline", new[] { new TraceabilityLinkRule("same-version", "Requirement", new[] { "Requirement" }, GraphTestData.L) });
            var graph = await data.Service.BuildAsync(data.Request(policy, scopes: modules)); Assert.AreEqual(2, graph.Nodes.Count); Assert.AreEqual(1, graph.Edges.Count);
            Assert.AreNotEqual(Node(graph, GraphTestData.R, 1, "1.0[A]").Id, Node(graph, GraphTestData.R, 1, "2.0[B]").Id);
            Assert.IsTrue(graph.Findings.Any(f => f.Code == "UnexpectedLinkModel")); Assert.IsTrue(graph.Observations.Any(o => o.Record.Source.Version == "opaque-first"));
        }
        [TestMethod]
        public async Task UnknownNativeLinkIdCannotSatisfyANativeIdentityPolicy()
        {
            var data = new GraphTestData { NativeLinkIds = false }; data.Edge(1, 1); var graph = await data.Service.BuildAsync(data.Request(GraphTestData.Policy("native-required")));
            Assert.IsTrue(graph.Findings.Any(f => f.Code == "NativeLinkModuleIdUnavailable")); Assert.IsFalse(graph.Findings.Any(f => f.Code == "ExpectedLinkSatisfied" || f.Code == "MissingExpectedLink"));
            Assert.IsNull(graph.Edges.Single().QualifiedLinkModuleId); Assert.AreEqual("PathOnly_NativeIdUnavailable", graph.Edges.Single().LinkModuleIdentityEvidence);
        }
        [TestMethod]
        public async Task IncompleteEmptyScopeCannotProveMissingLinksOrAnOrphan()
        {
            var data = new GraphTestData { Incomplete = true }; var graph = await data.Service.BuildAsync(data.Request());
            Assert.IsFalse(graph.IsCompleteWithinRequestedScope); Assert.IsTrue(graph.Findings.Any(f => f.Code == "ScopeIncomplete"));
            Assert.IsFalse(graph.Findings.Any(f => f.Code == "MissingExpectedLink" || f.Code == "OrphanRequirement"));
        }
        [TestMethod]
        public async Task LegacyRequirementMetadataCanUseObservedAnchorIdentityWithoutDuplicatingInventoryNodes()
        {
            var data = new GraphTestData { NativeRequirementIds = false }; data.Edge(1, 1); var graph = await data.Service.BuildAsync(data.Request());
            Assert.AreEqual(6, graph.Nodes.Count); Assert.IsTrue(graph.Nodes.Values.All(n => n.IsInventoryObject && !string.IsNullOrEmpty(n.QualifiedModuleId)));
        }
        [TestMethod]
        public async Task NativeLinkIdentityIsNotImputedToAnObservationFromAnotherScope()
        {
            var data = new GraphTestData { LinkIdOverride = r => r.ModulePath == GraphTestData.R ? "database:links-1" : null }; data.Edge(1, 1);
            var graph = await data.Service.BuildAsync(data.Request()); Assert.AreEqual(2, graph.Edges.Count);
            Assert.AreEqual(1, graph.Edges.Count(e => e.QualifiedLinkModuleId == null)); Assert.AreEqual(1, graph.Edges.Count(e => e.QualifiedLinkModuleId == "database:links-1"));
        }
        [TestMethod]
        public async Task LegacyPathOnlyIdentitiesRemainExplicitlyUnverified()
        {
            var data = new GraphTestData { NativeRequirementIds = false, NativeLinkIds = false, LinkAnchorIds = false }; data.Edge(1, 1);
            var graph = await data.Service.BuildAsync(data.Request()); Assert.AreEqual(6, graph.Nodes.Count); Assert.IsTrue(graph.Nodes.Values.All(n => n.IdentityEvidence.Contains("PathScoped")));
        }
        [TestMethod]
        public async Task ConflictingCallerBaselineAliasesAreRejectedBeforeAnyNativeRead()
        {
            var data = new GraphTestData(); var modules = new[] { new TraceabilityModuleScope(GraphTestData.R, baseline: new RequirementBaseline(1, 0), observedBaselineVersion: "same-opaque"),
                new TraceabilityModuleScope(GraphTestData.R, baseline: new RequirementBaseline(2, 0), observedBaselineVersion: "same-opaque") };
            var ex = await Assert.ThrowsExceptionAsync<IntegrationException>(() => data.Service.BuildAsync(data.Request(scopes: modules)));
            Assert.AreEqual(IntegrationErrorCode.TraceabilityGraphFailed, ex.Code); Assert.AreEqual(0, data.RequirementCalls);
        }
        [DataTestMethod, DataRow(TraceabilityVersionPolicy.SameVersion, false), DataRow(TraceabilityVersionPolicy.CurrentCounterpart, true), DataRow(TraceabilityVersionPolicy.AnyExplicitVersion, true)]
        public async Task CrossVersionRelationshipsRequireTheConfiguredPolicy(TraceabilityVersionPolicy policy, bool allowed)
        {
            var data = new GraphTestData { Objects = new[] { 1 } }; data.Edge(1, 1, sourceVersion: "opaque-first");
            var modules = new[] { new TraceabilityModuleScope(GraphTestData.R, baseline: new RequirementBaseline(1, 0)), new TraceabilityModuleScope(GraphTestData.S, "Standard") };
            var graph = await data.Service.BuildAsync(data.Request(GraphTestData.Policy(version: policy), scopes: modules));
            Assert.AreEqual(allowed, graph.Findings.Any(f => f.Code == "ExpectedLinkSatisfied")); Assert.AreEqual(!allowed, graph.Findings.Any(f => f.Code == "UnexpectedLinkModel"));
        }
        [TestMethod]
        public async Task DeepChainAndThousandsOfIndependentCyclesUseIterativeBoundedAlgorithms()
        {
            var data = new GraphTestData { Objects = Enumerable.Range(1, 5000).ToArray() };
            for (int i = 1; i < 5000; i++) data.Edge(i, i + 1, targetModule: GraphTestData.R);
            var policy = new TraceabilityPolicy("large", linkRules: new TraceabilityLinkRule[0]); var service = data.Service; var graph = await service.BuildAsync(data.Request(policy));
            Assert.AreEqual(0, graph.Cycles.Count); var impact = service.AnalyzeImpact(graph, new[] { Node(graph, GraphTestData.R, 1).Id }, maxDepth: 10000);
            Assert.AreEqual(5000, impact.Visits.Count); Assert.AreEqual(4999, impact.Visits.Last().Distance); Assert.IsFalse(impact.IsTruncated);
            data.Rows.Clear(); for (int i = 1; i <= 5000; i++) data.Edge(i, i, targetModule: GraphTestData.R);
            graph = await service.BuildAsync(data.Request(policy)); Assert.AreEqual(5000, graph.Cycles.Count);
        }
        [TestMethod]
        public async Task UnknownRoleCreatesBoundaryWithoutInventingAStandardOrDiscoveringMoreModules()
        {
            var data = new GraphTestData(); data.Edge(1, 1, targetModule: "/Outside/Unknown"); var service = data.Service; var graph = await service.BuildAsync(data.Request());
            Assert.AreEqual(2, data.LinkCalls); Assert.IsTrue(graph.HasExternalBoundary); Assert.IsTrue(graph.Findings.Any(f => f.Code == "ExpectedLinkUnknown" && f.NodeId == Node(graph, GraphTestData.R, 1).Id));
            Assert.IsFalse(service.AnalyzeImpact(graph, new[] { Node(graph, GraphTestData.R, 1).Id }).IsConclusiveWithinRequestedScope);
        }
        [TestMethod]
        public async Task DeletedObjectsAreExcludedAndIdentityIgnoresDisplayNumberAndIdentifier()
        {
            var data = new GraphTestData { DeletedLast = true }; var first = await data.Service.BuildAsync(data.Request()); data.Identifier = "RENAMED"; var second = await data.Service.BuildAsync(data.Request());
            CollectionAssert.AreEquivalent(first.Nodes.Keys.ToArray(), second.Nodes.Keys.ToArray()); Assert.IsTrue(first.Findings.Any(f => f.Code == "DeletedObjectExcluded"));
            Assert.IsFalse(first.Findings.Any(f => f.Code == "MissingExpectedLink" && f.NodeId == Node(first, GraphTestData.R, 3).Id));
        }
        [DataTestMethod, DataRow(true), DataRow(false)]
        public async Task ChangingNativeIdentitiesAcrossReadsIsRejected(bool link)
        {
            var data = new GraphTestData(); if (link) data.LinkIdOverride = r => r.ModulePath; else data.RequirementIdOverride = r => "wrong-native-id";
            var ex = await Assert.ThrowsExceptionAsync<IntegrationException>(() => data.Service.BuildAsync(data.Request())); Assert.AreEqual(IntegrationErrorCode.TraceabilityGraphFailed, ex.Code);
        }
        [DataTestMethod, DataRow("nodes"), DataRow("observations"), DataRow("findings"), DataRow("metadata")]
        public async Task LimitsFailExplicitlyAndDisposeOwnedSnapshots(string limit)
        {
            var data = new GraphTestData(); data.Edge(1, 1);
            if (limit == "metadata") data.Identifier = new string('a', 900);
            var request = data.Request(maxNodes: limit == "nodes" ? 1 : 100, maxObservations: limit == "observations" ? 1 : 100,
                maxFindings: limit == "findings" ? 1 : 100, maxMetadataCharacters: limit == "metadata" ? 1024 : 100000);
            var ex = await Assert.ThrowsExceptionAsync<IntegrationException>(() => data.Service.BuildAsync(request)); Assert.AreEqual(IntegrationErrorCode.TraceabilityGraphLimitReached, ex.Code); Assert.IsTrue(data.Disposals > 0);
        }
        [TestMethod]
        public async Task NativeTimeoutStopsFurtherReadsAndNeverRetries()
        {
            var data = new GraphTestData { LinkFailure = r => new IntegrationException(IntegrationErrorCode.DxlTimeout, "Controlled timeout") };
            var ex = await Assert.ThrowsExceptionAsync<IntegrationException>(() => data.Service.BuildAsync(data.Request())); Assert.AreEqual(IntegrationErrorCode.DxlTimeout, ex.Code); Assert.AreEqual(1, data.LinkCalls); Assert.AreEqual(2, data.Disposals);
        }
        [TestMethod]
        public async Task CancellationStopsBetweenScopesAndDisposesTheCurrentSnapshot()
        {
            using (var cancellation = new CancellationTokenSource())
            {
                var data = new GraphTestData { BeforeLinks = r => cancellation.Cancel() }; data.Edge(1, 1);
                await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => data.Service.BuildAsync(data.Request(), cancellationToken: cancellation.Token));
                Assert.AreEqual(1, data.LinkCalls); Assert.AreEqual(3, data.Disposals);
            }
        }
        [DataTestMethod, DataRow(TraceabilityGraphFormat.Json), DataRow(TraceabilityGraphFormat.Csv), DataRow(TraceabilityGraphFormat.GraphMl)]
        public async Task ExportsRoundTripIdentityEvidenceUnicodeCyclesAndImpact(TraceabilityGraphFormat format)
        {
            var data = new GraphTestData(); data.Edge(1, 2, targetModule: GraphTestData.R); data.Edge(2, 1, targetModule: GraphTestData.R); data.Edge(1, 1);
            var service = data.Service; var graph = await service.BuildAsync(data.Request(new TraceabilityPolicy("export", new[] { new TraceabilityLinkRule("=DANGER", "Requirement", new[] { "Standard" }, GraphTestData.L) })));
            var impact = service.AnalyzeImpact(graph, new[] { Node(graph, GraphTestData.R, 1).Id });
            using (var stream = new MemoryStream())
            {
                new TraceabilityGraphExporter().Export(stream, graph, format, impact); Assert.IsTrue(stream.CanWrite); stream.Position = 0;
                if (format == TraceabilityGraphFormat.Json)
                {
                    var document = (GraphJsonDocument)new DataContractJsonSerializer(typeof(GraphJsonDocument)).ReadObject(stream);
                    Assert.AreEqual(graph.Nodes.Count, document.Records.Count(r => r.Kind == "Node")); Assert.IsTrue(document.Records.Any(r => r.ObjectIdentifier == data.Identifier + "1"));
                    Assert.AreEqual(graph.Edges.Count, document.Records.Count(r => r.Kind == "Edge")); Assert.IsTrue(document.Records.Any(r => r.Kind == "ImpactVisit"));
                }
                else if (format == TraceabilityGraphFormat.GraphMl)
                {
                    var xml = XDocument.Load(stream); XNamespace ns = "http://graphml.graphdrawing.org/xmlns";
                    Assert.AreEqual("directed", (string)xml.Root.Element(ns + "graph").Attribute("edgedefault"));
                    var ids = new HashSet<string>(xml.Descendants(ns + "node").Select(n => (string)n.Attribute("id")));
                    Assert.AreEqual(graph.Nodes.Count, ids.Count); Assert.IsTrue(xml.Descendants(ns + "edge").All(e => ids.Contains((string)e.Attribute("source")) && ids.Contains((string)e.Attribute("target"))));
                    StringAssert.Contains(xml.Root.Value, data.NativeLinkId);
                }
                else
                {
                    string text = Encoding.UTF8.GetString(stream.ToArray()); var rows = Csv(text); Assert.AreEqual(8, rows[0].Length); Assert.IsTrue(rows.All(r => r.Length == 8));
                    Assert.IsTrue(rows.Any(r => r[1] == "\t=DANGER")); Assert.AreEqual(graph.Nodes.Count, rows.Count(r => r[0] == "Node"));
                    foreach (var row in rows.Skip(1)) using (var json = new MemoryStream(Encoding.UTF8.GetBytes(row[7]))) Assert.IsNotNull(new DataContractJsonSerializer(typeof(GraphJsonRow)).ReadObject(json));
                }
            }
        }
        [TestMethod]
        public async Task ExportDoesNotOverwriteExistingFileAndRejectsForeignImpactAndCancelledPublication()
        {
            using (var files = new ExecutorTestContext())
            {
                var data = new GraphTestData(); var first = await data.Service.BuildAsync(data.Request()); var second = await data.Service.BuildAsync(data.Request()); var exporter = new TraceabilityGraphExporter();
                string path = Path.Combine(files.Root, "graph.json"); File.WriteAllText(path, "keep"); Assert.ThrowsException<IOException>(() => exporter.ExportFile(path, first, TraceabilityGraphFormat.Json)); Assert.AreEqual("keep", File.ReadAllText(path));
                using (var stream = new MemoryStream()) Assert.ThrowsException<ArgumentException>(() => exporter.Export(stream, first, TraceabilityGraphFormat.Json, data.Service.AnalyzeImpact(second, new[] { second.Nodes.Keys.First() })));
                using (var cancelled = new CancellationTokenSource()) { cancelled.Cancel(); string newPath = Path.Combine(files.Root, "cancelled.json"); Assert.ThrowsException<OperationCanceledException>(() => exporter.ExportFile(newPath, first, TraceabilityGraphFormat.Json, cancellationToken: cancelled.Token)); Assert.IsFalse(File.Exists(newPath)); }
                Assert.AreEqual(0, Directory.GetFiles(files.Root, ".traceability-graph-*.tmp").Length);
            }
        }
        [TestMethod]
        public async Task SeedCollectionIsNeverSilentlyTruncatedEvenWhenItContainsDuplicates()
        {
            var data = new GraphTestData(); var service = data.Service; var graph = await service.BuildAsync(data.Request()); string seed = graph.Nodes.Keys.First();
            Assert.ThrowsException<ArgumentException>(() => service.AnalyzeImpact(graph, new[] { seed, seed }, maxVisits: 1));
        }
        [TestMethod]
        public void PolicyAndRequestRejectAmbiguousUnboundedOrUnknownInputs()
        {
            Assert.ThrowsException<ArgumentException>(() => new TraceabilityLinkRule("id", "Requirement", new[] { "Standard" }, GraphTestData.L, TraceabilityDirection.Both));
            Assert.ThrowsException<ArgumentException>(() => new TraceabilityPolicy("p", new[] { new TraceabilityLinkRule("id", "R", new[] { "S" }, GraphTestData.L), new TraceabilityLinkRule("id", "R", new[] { "S" }, GraphTestData.L) }));
            Assert.ThrowsException<ArgumentException>(() => new GraphTestData().Request(scopes: new[] { new TraceabilityModuleScope(GraphTestData.R), new TraceabilityModuleScope(GraphTestData.R) }));
            Assert.ThrowsException<ArgumentException>(() => new TraceabilityGraphRequest("repo", new[] { new TraceabilityModuleScope(GraphTestData.R) }, new[] { GraphTestData.L, GraphTestData.L + "/" }, GraphTestData.Policy()));
        }
        [TestMethod]
        public void ConsoleHostExposesColdGraphServiceAndExporter()
        {
            foreach (bool desktop in new[] { false })
            { var services = new ServiceCollection(); DoorsIntegration.ConsoleApp.ServiceCollectionExtensions.AddDoorsIntegration(services);
                using (var provider = services.BuildServiceProvider()) { Assert.IsNotNull(provider.GetRequiredService<ITraceabilityGraphService>()); Assert.IsNotNull(provider.GetRequiredService<ITraceabilityGraphExporter>()); } }
        }
        private static List<string[]> Csv(string text)
        {
            var rows = new List<string[]>(); var cells = new List<string>(); var field = new StringBuilder(); bool quoted = false;
            for (int i = 0; i < text.Length; i++) { char c = text[i]; if (c == '"') { if (quoted && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; } else quoted = !quoted; }
                else if (!quoted && c == ',') { cells.Add(field.ToString()); field.Clear(); } else if (!quoted && c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') { cells.Add(field.ToString()); field.Clear(); rows.Add(cells.ToArray()); cells.Clear(); i++; } else field.Append(c); }
            return rows;
        }
        [DataContract] public sealed class GraphJsonDocument { [DataMember(Name = "records")] public GraphJsonRow[] Records { get; set; } }
        [DataContract] public sealed class GraphJsonRow { [DataMember(Name = "kind")] public string Kind { get; set; } [DataMember(Name = "objectIdentifier")] public string ObjectIdentifier { get; set; } }
    }
}
