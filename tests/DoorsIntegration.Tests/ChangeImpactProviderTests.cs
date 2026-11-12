using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass, TestCategory("ChangeImpactMock")]
    public class ChangeImpactProviderTests
    {
        [DataTestMethod, DataRow(false), DataRow(true)]
        public async Task ExistingServicesReadOnceAndKnownEmptyLinkScopeRetainsObservedIdentity(bool current)
        {
            var request = MockChangeImpactSnapshotProvider.DemoRequest(kind: current ? ChangeImpactComparisonKind.BaselineToCurrent : ChangeImpactComparisonKind.ModuleBaselines);
            var requirements = new ReadRequirements(); var links = new ReadLinks(); var native = new ReadNativeEvidence();
            var provider = new ServiceImpactSnapshotProvider(requirements, links, null, native);
            var snapshot = await provider.CaptureAsync(current ? request.Target : request.Source, request.Policy, null, CancellationToken.None);
            Assert.AreEqual(1, requirements.Calls, "Resolver must reuse captured attributes instead of issuing another COM object read."); Assert.AreEqual(1, links.Calls); Assert.AreEqual(1, native.Calls);
            Assert.IsTrue(snapshot.Links.Context.Modules.Single().LinksComplete); CollectionAssert.Contains(snapshot.Links.CapturedLinkModuleIdentities, "OBSERVED-LINK-ID");
            Assert.AreEqual(current, snapshot.Requirements.Single().Version.IsCurrent); Assert.AreEqual(1, snapshot.Links.Requirements.Count); Assert.AreEqual(0, snapshot.Links.Links.Count);
            Assert.IsFalse(requirements.Last.AllowAttributeDxl); Assert.AreEqual(DeletedObjectMode.IncludeContent, requirements.Last.DeletedObjects); Assert.AreEqual(0, requirements.Writes + links.Writes);
        }
        [TestMethod]
        public async Task UnvalidatedNativeEvidenceCannotBecomeCompleteOrDispatchLinkRead()
        {
            var r = MockChangeImpactSnapshotProvider.DemoRequest(); var requirements = new ReadRequirements(); var links = new ReadLinks();
            var s = await new ServiceImpactSnapshotProvider(requirements, links, null, new UnvalidatedBaselineNativeLinkEvidenceReader()).CaptureAsync(r.Source, r.Policy, null, CancellationToken.None);
            Assert.IsFalse(s.Links.Context.Modules.Single().LinksComplete); Assert.AreEqual(0, links.Calls); Assert.IsTrue(s.Links.Limitations.Any(l => l.Contains("NOT_RUN"))); Assert.AreEqual(1, requirements.Calls);
        }
        [TestMethod]
        public async Task RealPortTimeoutAndIdentityMismatchRemainReadOnlyUnknown()
        {
            var r = MockChangeImpactSnapshotProvider.DemoRequest(); var requirements = new ReadRequirements { Timeout = true }; var links = new ReadLinks(); var native = new ReadNativeEvidence();
            var provider = new ServiceImpactSnapshotProvider(requirements, links, null, native); var timed = await provider.CaptureAsync(r.Source, r.Policy, null, CancellationToken.None);
            Assert.IsTrue(timed.TransportUncertain); Assert.AreEqual(1, requirements.Calls); Assert.AreEqual(0, links.Calls + native.Calls);
            requirements.Timeout = false; requirements.WrongIdentity = true; var mismatch = await provider.CaptureAsync(r.Source, r.Policy, null, CancellationToken.None);
            Assert.AreEqual(BaselineVerificationReadState.Unknown, mismatch.Links.Context.Modules.Single().State); Assert.AreEqual(0, mismatch.Requirements.Count); Assert.AreEqual(0, requirements.Writes + links.Writes);
        }
        private sealed class ReadRequirements : IRequirementService
        {
            internal int Calls, Writes; internal bool Timeout, WrongIdentity; internal RequirementReadRequest Last;
            public Task<IRequirementSnapshot> ReadAsync(RequirementReadRequest request, CancellationToken token)
            { Calls++; Last = request; if (Timeout) throw new IntegrationException(IntegrationErrorCode.DxlTimeout, "Mock timeout; native not validated."); return Task.FromResult<IRequirementSnapshot>(new Snapshot(request, WrongIdentity)); }
            public Task<RequirementModuleSchema> GetAttributeSchemaAsync(RequirementWriteTarget t, CancellationToken c) { Writes++; throw new AssertFailedException("No schema/write workflow permitted."); }
            public Task<RequirementWritePreview> PreviewWriteAsync(RequirementWriteRequest r, CancellationToken c) { Writes++; throw new AssertFailedException("No write workflow permitted."); }
            public Task<RequirementWriteResult> ApplyWriteAsync(RequirementWritePreview p, CancellationToken c) { Writes++; throw new AssertFailedException("No write permitted."); }
        }
        private sealed class Snapshot : IRequirementSnapshot
        {
            internal Snapshot(RequirementReadRequest request, bool wrongIdentity) { Summary = new RequirementReadSummary(Guid.NewGuid(), request, RequirementModuleStatus.Available, RequirementModuleOwnership.OwnedReadOnly, RequirementCleanupStatus.Closed, 1, 1, 0, 0, 0, 0, false, false, wrongIdentity ? "OTHER" : "SYS"); }
            public RequirementReadSummary Summary { get; }
            public IEnumerable<RequirementRecord> ReadObjects()
            {
                var values = new[] { "Object Heading", "Object Text" }.Concat(Summary.Request.CustomAttributes).Select(n => new RequirementValue(n, "Text", RequirementValueStatus.Readable, n == "Requirement Type" ? "SYS" : "Observed test value", RequirementValueStatus.NotApplicable, null));
                yield return new RequirementRecord(1, 1, "DISPLAY-NOT-A-KEY", "1", RequirementValueStatus.Readable, 1, null, RequirementParentStatus.Root, false, values);
            }
            public Stream OpenRawStream() => throw new NotSupportedException(); public void Dispose() { }
        }
        private sealed class ReadLinks : ILinkService
        {
            internal int Calls, Writes;
            public Task<ILinkSnapshot> ReadAsync(LinkReadRequest r, CancellationToken c) { Calls++; return Task.FromResult<ILinkSnapshot>(new EmptyLinks(r)); }
            public Task<LinkWritePreview> PreviewWriteAsync(LinkWriteRequest r, CancellationToken c) { Writes++; throw new AssertFailedException("No link write workflow permitted."); }
            public Task<LinkWriteResult> ApplyWriteAsync(LinkWritePreview p, CancellationToken c) { Writes++; throw new AssertFailedException("No link write permitted."); }
        }
        private sealed class EmptyLinks : ILinkSnapshot
        {
            internal EmptyLinks(LinkReadRequest r) { Summary = new LinkReadSummary(Guid.NewGuid(), r, RequirementModuleStatus.Available, LinkModuleStatus.Available, RequirementModuleOwnership.OwnedReadOnly, 0, 1, 0, 0, 0, 0, false, false, 1, 0, 0, "SYS", "OBSERVED-LINK-ID"); }
            public LinkReadSummary Summary { get; } public IEnumerable<LinkRecord> ReadLinks() => Array.Empty<LinkRecord>(); public Stream OpenRawStream() => throw new NotSupportedException(); public void Dispose() { }
        }
        private sealed class ReadNativeEvidence : IBaselineNativeLinkEvidenceReader
        {
            internal int Calls;
            public Task<BaselineNativeLinkEvidence> ReadAsync(BaselineSetVerificationRequest r, BaselineVerificationContext c, ModuleBaselineVerificationResult m, string path, CancellationToken t) { Calls++; return Task.FromResult(new BaselineNativeLinkEvidence { Complete = true, Reason = "Mock port behavior only; not native validation." }); }
        }
    }
}
