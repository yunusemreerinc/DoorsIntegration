using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using DoorsIntegration.Core;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    internal sealed class WpfWorkspaceTestData : IRequirementReader, ILinkReader, IRequirementWriteGateway, IFormalModuleCreationGateway, IDoorsHealthChecks
    {
        public int RequirementReads, LinkReads, SchemaReads, WritePreviews, WriteCommits, ModulePreviews, ModuleCommits, DiagnosticCalls;
        public RequirementReadRequest ReadRequest;
        public LinkReadRequest LinkRequest;
        public RequirementWriteRequest WriteRequest;
        public TaskCompletionSource<bool> ReadRelease { get; set; }
        public bool PartialWrite { get; set; }
        public ApartmentState DiagnosticApartment;
        public bool DiagnosticLive;
        public RequirementWriteResult Result;
        public Task<IRequirementSnapshot> ReadAsync(RequirementReadRequest request, CancellationToken token = default(CancellationToken)) => ReadRequirementsAsync(request, token);
        private async Task<IRequirementSnapshot> ReadRequirementsAsync(RequirementReadRequest request, CancellationToken token)
        {
            RequirementReads++; ReadRequest = request;
            if (ReadRelease != null) await ReadRelease.Task;
            token.ThrowIfCancellationRequested(); return new WpfWorkspaceRequirementSnapshot(request, 251);
        }
        Task<ILinkSnapshot> ILinkReader.ReadAsync(LinkReadRequest request, CancellationToken token)
        { LinkReads++; LinkRequest = request; return Task.FromResult<ILinkSnapshot>(new WpfWorkspaceLinkSnapshot(request)); }
        public Task<RequirementModuleSchema> GetAttributeSchemaAsync(RequirementWriteTarget target, CancellationToken token)
        { SchemaReads++; return Task.FromResult(Schema(target.ModulePath)); }
        public Task<RequirementWriteResult> PreviewAsync(RequirementWriteRequest request, CancellationToken token)
        {
            WritePreviews++; WriteRequest = request;
            return Task.FromResult(MakeResult(request, RequirementWriteOutcome.Preview));
        }
        public Task<RequirementWriteResult> CommitAsync(RequirementWritePreview preview, CancellationToken token)
        {
            Assert.IsTrue(preview.TryBeginCommit()); WriteCommits++;
            Result = MakeResult(preview.Request, PartialWrite ? RequirementWriteOutcome.PartialFailure : RequirementWriteOutcome.Applied);
            return Task.FromResult(Result);
        }
        private RequirementWriteResult MakeResult(RequirementWriteRequest request, RequirementWriteOutcome outcome)
        {
            var steps = new List<RequirementWriteStep>();
            var status = outcome == RequirementWriteOutcome.Applied ? RequirementWriteStepStatus.Applied : RequirementWriteStepStatus.Skipped;
            if (request.IsCreate) { steps.Add(new RequirementWriteStep(RequirementWriteStepKind.Create, "", status)); steps.Add(new RequirementWriteStep(RequirementWriteStepKind.Position, "", status)); }
            steps.AddRange(request.Changes.Select(c => new RequirementWriteStep(RequirementWriteStepKind.Attribute, c.Name, status)));
            if (!request.IsCreate && request.Position.Kind != RequirementPositionKind.Keep) steps.Add(new RequirementWriteStep(RequirementWriteStepKind.Position, "", status));
            steps.Add(new RequirementWriteStep(RequirementWriteStepKind.Save, "", status)); steps.Add(new RequirementWriteStep(RequirementWriteStepKind.Verify, "", status));
            if (outcome == RequirementWriteOutcome.PartialFailure)
            {
                steps[0] = new RequirementWriteStep(steps[0].Kind, steps[0].AttributeName, RequirementWriteStepStatus.Applied);
                steps[1] = new RequirementWriteStep(steps[1].Kind, steps[1].AttributeName, RequirementWriteStepStatus.Failed, RequirementWriteIssueCode.NativeFailure);
            }
            return new RequirementWriteResult(Schema(request.Target.ModulePath), "state", outcome,
                outcome == RequirementWriteOutcome.Applied ? RequirementWritePersistence.Saved : RequirementWritePersistence.NotAttempted,
                outcome == RequirementWriteOutcome.PartialFailure ? RequirementWriteCleanup.LeftOpenForReview : RequirementWriteCleanup.Closed,
                request.IsCreate && outcome == RequirementWriteOutcome.Preview ? (int?)null : 42, "REQ-42", null, 1, steps,
                beforeValues: request.IsCreate ? null : request.Changes.Select(c => new RequirementAttributeBeforeValue(c.Name, "OLD PLAIN", "{\\b OLD RICH}")));
        }
        public static RequirementModuleSchema Schema(string path) => new RequirementModuleSchema(Guid.NewGuid(), path, "test-db", "test-actor", "module-id", "test-db:module-id",
            new[] { Attribute("Object Heading", "Text", true), Attribute("Object Text", "Text", true), Attribute("Priority", "Enumeration"), Attribute("Calculated", "Text", false, true) },
            new[] { new RequirementWriteIssue(RequirementWriteIssueSeverity.Warning, RequirementWriteIssueCode.RequiredRulesNotDeclared) });
        private static RequirementAttributeSchema Attribute(string name, string type, bool system = false, bool dxl = false) => new RequirementAttributeSchema(name,
            type, type, true, dxl, system, false, true, !dxl, type == "Enumeration" ? new[] { "Low", "High" } : null);
        public Task<FormalModuleCreationChecks> PreviewAsync(FormalModuleCreateRequest request, CancellationToken token)
        { ModulePreviews++; return Task.FromResult(ModuleCreationTestContext.Ready()); }
        public Task<FormalModuleCreationResult> CommitAsync(FormalModuleCreationPreview preview, CancellationToken token)
        {
            Assert.IsTrue(preview.TryBeginCommit()); ModuleCommits++;
            return Task.FromResult(new FormalModuleCreationResult(Guid.NewGuid(), preview.Request.FullPath, preview.Checks, ModuleCreationOutcome.Created, "new-module", "test-db:new-module", ModuleCreationCleanup.Closed));
        }
        public DoorsHealthReport Run(DoorsVersion target, Guid id, bool live = false, DoorsConnectionMode mode = DoorsConnectionMode.AttachExisting, Action<DoorsHealthStepResult> progress = null)
        {
            DiagnosticCalls++; DiagnosticApartment = Thread.CurrentThread.GetApartmentState(); DiagnosticLive = live;
            var step = new DoorsHealthStepResult(DoorsHealthStep.ComRegistration, DoorsHealthStatus.Passed, "Controlled registration"); progress?.Invoke(step);
            return new DoorsHealthReport(id, new DoorsCompatibilityReport(target, true, Environment.Is64BitProcess, DiagnosticApartment.ToString(),
                new[] { new ComRegistrationSnapshot(ComArchitecture.X86, ComRegistrationState.Registered, serverPath: "fake-doors.exe", fileVersion: "9.7", versionMajor: 9, versionMinor: 7) }), new[] { step });
        }
    }

    internal sealed class WpfWorkspaceRequirementSnapshot : IRequirementSnapshot
    {
        private readonly int count;
        public bool Disposed;
        public int Enumerated;
        public WpfWorkspaceRequirementSnapshot(RequirementReadRequest request, int count)
        {
            this.count = count; Summary = new RequirementReadSummary(Guid.NewGuid(), request, RequirementModuleStatus.Available,
                RequirementModuleOwnership.OwnedReadOnly, RequirementCleanupStatus.Closed, count, count, 0, 0, 0, 0, false, false);
        }
        public RequirementReadSummary Summary { get; }
        public IEnumerable<RequirementRecord> ReadObjects()
        {
            if (Disposed) throw new ObjectDisposedException(nameof(WpfWorkspaceRequirementSnapshot));
            for (int i = 1; i <= count; i++)
            {
                Enumerated++;
                yield return new RequirementRecord(i, i, "REQ-" + i, i.ToString(), RequirementValueStatus.Readable, 1, null, RequirementParentStatus.Root, false,
                    new[] { new RequirementValue("Object Heading", "Text", RequirementValueStatus.Readable, "Heading " + i, RequirementValueStatus.Readable, "{\\b Heading}"),
                        new RequirementValue("Object Text", "Text", RequirementValueStatus.Readable, "Türkçe 👋 " + new string('x', 1000), RequirementValueStatus.Readable, "{\\rtf1 RAW OLE}"),
                        new RequirementValue("Priority", "Priority", RequirementValueStatus.Readable, "High", RequirementValueStatus.NotApplicable, "") });
            }
        }
        public Stream OpenRawStream() => throw new NotSupportedException();
        public void Dispose() { Disposed = true; }
    }
    internal sealed class WpfWorkspaceLinkSnapshot : ILinkSnapshot
    {
        public bool Disposed;
        public WpfWorkspaceLinkSnapshot(LinkReadRequest request)
        { Summary = new LinkReadSummary(Guid.NewGuid(), request, RequirementModuleStatus.Available, LinkModuleStatus.Available, RequirementModuleOwnership.OwnedReadOnly, 1, 1, 0, 0, 1, 0, false, false, 1, 0, 1); }
        public LinkReadSummary Summary { get; }
        public IEnumerable<LinkRecord> ReadLinks()
        {
            yield return new LinkRecord(1, LinkDirection.Outgoing, LinkRecordKind.Link, LinkResolution.EndpointUnresolved, false, "/P/Links", LinkModuleResolution.Verified,
                new LinkEndpoint("/P/M", LinkVersionKind.Current, "", 1, "REQ-1", LinkEndpointResolution.Resolved),
                new LinkEndpoint("/P/N", LinkVersionKind.Current, "", 20, "", LinkEndpointResolution.AccessDenied, SurrogateStatus.Unavailable));
        }
        public Stream OpenRawStream() => throw new NotSupportedException();
        public void Dispose() { Disposed = true; }
    }

}
