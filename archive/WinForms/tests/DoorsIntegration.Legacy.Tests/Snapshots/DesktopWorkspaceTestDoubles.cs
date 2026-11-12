using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DoorsIntegration.Core;
using DoorsIntegration.Desktop;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    internal sealed class WorkspaceTestData : IRequirementReader, ILinkReader, IRequirementWriteGateway, IFormalModuleCreationGateway, IDoorsHealthChecks, IDesktopReviewDialog
    {
        public int RequirementReads, LinkReads, SchemaReads, WritePreviews, WriteCommits, ModulePreviews, ModuleCommits, DialogCalls, DiagnosticCalls;
        public RequirementReadRequest ReadRequest;
        public LinkReadRequest LinkRequest;
        public RequirementWriteRequest WriteRequest;
        public bool Approve;
        public Action<DesktopWriteReview> OnReview;
        public DesktopWriteReview LastReview;
        public TaskCompletionSource<bool> ReadRelease;
        public bool PartialWrite;
        public ApartmentState DiagnosticApartment;
        public bool DiagnosticLive;
        public RequirementWriteResult Result;
        public Task<IRequirementSnapshot> ReadAsync(RequirementReadRequest request, CancellationToken token = default(CancellationToken)) => ReadRequirementsAsync(request, token);
        private async Task<IRequirementSnapshot> ReadRequirementsAsync(RequirementReadRequest request, CancellationToken token)
        {
            RequirementReads++; ReadRequest = request;
            if (ReadRelease != null) await ReadRelease.Task;
            token.ThrowIfCancellationRequested(); return new WorkspaceRequirementSnapshot(request, 251);
        }
        Task<ILinkSnapshot> ILinkReader.ReadAsync(LinkReadRequest request, CancellationToken token)
        { LinkReads++; LinkRequest = request; return Task.FromResult<ILinkSnapshot>(new WorkspaceLinkSnapshot(request)); }
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
        public bool Show(Control owner, DesktopWriteReview review, CancellationToken token)
        { DialogCalls++; LastReview = review; OnReview?.Invoke(review); return Approve; }
        public DoorsHealthReport Run(DoorsVersion target, Guid id, bool live = false, DoorsConnectionMode mode = DoorsConnectionMode.AttachExisting, Action<DoorsHealthStepResult> progress = null)
        {
            DiagnosticCalls++; DiagnosticApartment = Thread.CurrentThread.GetApartmentState(); DiagnosticLive = live;
            var step = new DoorsHealthStepResult(DoorsHealthStep.ComRegistration, DoorsHealthStatus.Passed, "Controlled registration"); progress?.Invoke(step);
            return new DoorsHealthReport(id, new DoorsCompatibilityReport(target, true, Environment.Is64BitProcess, DiagnosticApartment.ToString(),
                new[] { new ComRegistrationSnapshot(ComArchitecture.X86, ComRegistrationState.Registered, serverPath: "fake-doors.exe", fileVersion: "9.7", versionMajor: 9, versionMinor: 7) }), new[] { step });
        }
        public void Register(ServiceCollection services)
        {
            services.AddSingleton<IRequirementReader>(this); services.AddSingleton<ILinkReader>(this);
            services.AddSingleton<IRequirementWriteGateway>(this); services.AddSingleton<IFormalModuleCreationGateway>(this);
            services.AddSingleton<IDesktopReviewDialog>(this); services.AddSingleton<IDoorsHealthChecks>(this);
            services.AddSingleton<IDoorsHierarchyReader>(new RecordingHierarchyReader());
            services.AddSingleton<IRequirementWriteAuditSink>(new TestRequirementWriteAudit());
            services.AddSingleton<IDoorsOperationGate>(new DoorsOperationGate());
            services.AddSingleton<IDoorsDiagnostics>(new FakeDiagnostics());
        }
    }

    internal sealed class WorkspaceRequirementSnapshot : IRequirementSnapshot
    {
        private readonly int count;
        public bool Disposed;
        public int Enumerated;
        public WorkspaceRequirementSnapshot(RequirementReadRequest request, int count)
        {
            this.count = count; Summary = new RequirementReadSummary(Guid.NewGuid(), request, RequirementModuleStatus.Available,
                RequirementModuleOwnership.OwnedReadOnly, RequirementCleanupStatus.Closed, count, count, 0, 0, 0, 0, false, false);
        }
        public RequirementReadSummary Summary { get; }
        public IEnumerable<RequirementRecord> ReadObjects()
        {
            if (Disposed) throw new ObjectDisposedException(nameof(WorkspaceRequirementSnapshot));
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
    internal sealed class WorkspaceLinkSnapshot : ILinkSnapshot
    {
        public bool Disposed;
        public WorkspaceLinkSnapshot(LinkReadRequest request)
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

    internal static class WorkspaceUiTest
    {
        public static T Find<T>(Control window, string name) where T : Control => window.Controls.Find(name, true).OfType<T>().Single();
        public static void Select(Control window, string controlName)
        {
            var chain = new List<TabPage>();
            for (Control c = Find<Control>(window, controlName); c != null; c = c.Parent) if (c is TabPage page) chain.Add(page);
            chain.Reverse(); foreach (var page in chain) ((TabControl)page.Parent).SelectedTab = page;
        }
        public static async Task Wait(Func<bool> predicate)
        {
            var until = DateTime.UtcNow.AddSeconds(12);
            while (!predicate() && DateTime.UtcNow < until) await Task.Delay(15);
            Assert.IsTrue(predicate(), "UI operation did not reach the expected state.");
        }
        public static Task Idle(Control window) => Wait(() => Find<ComboBox>(window, "TargetProfileComboBox").Enabled);
        public static void Run(WorkspaceTestData data, Func<MainForm, IServiceProvider, Task> action, Action<ServiceCollection> configure = null)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    var services = new ServiceCollection(); data.Register(services); configure?.Invoke(services); services.AddDoorsDesktop();
                    using (var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true }))
                    {
                        var window = provider.GetRequiredService<MainForm>(); window.StartPosition = FormStartPosition.Manual;
                        window.Location = new System.Drawing.Point(-15000, -15000); window.ShowInTaskbar = false;
                        window.Shown += async (s, e) =>
                        {
                            try { await action(window, provider); }
                            catch (Exception ex) { failure = ex; }
                            finally
                            {
                                data.ReadRelease?.TrySetResult(true);
                                try { await Idle(window); } catch { /* Preserve the primary test failure. */ }
                                window.Close();
                            }
                        };
                        Application.Run(window);
                    }
                }
                catch (Exception ex) { failure = ex; }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA); thread.Start();
            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(45)), "Workspace UI test exceeded its deadline.");
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
