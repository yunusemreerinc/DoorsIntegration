using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;
using DoorsIntegration.Dxl;

namespace DoorsIntegration.Tests
{
    internal sealed class RequirementWriteTestContext : IDisposable
    {
        public ExecutorTestContext Transport { get; } = new ExecutorTestContext();
        public RequirementWriteRequest Request { get; set; } = new RequirementWriteRequest(new RequirementWriteTarget("/P/M"),
            new[] { new RequirementAttributeChange("Object Heading", "Heading"), new RequirementAttributeChange("Object Text", "Unicode gövde 👋") });
        public TestRequirementWriteAudit Audit { get; } = new TestRequirementWriteAudit();
        public TestRequirementConfirmation Confirmation { get; } = new TestRequirementConfirmation();
        public DxlRequirementWriteGateway Gateway { get; }
        public RequirementService Service { get; }
        public List<string> Modes { get; } = new List<string>();
        public string State { get; set; } = "state-v1";
        public string Database { get; set; } = "db-1";
        public string Actor { get; set; } = "test-user";
        public string ModuleId { get; set; } = "module-1";
        public string QualifiedId { get; set; } = "db-1:module-1";
        private string ContextState => State + "|" + Database + "|" + Actor + "|" + ModuleId + "|" + QualifiedId;
        public List<RequirementWriteIssue> Issues { get; } = new List<RequirementWriteIssue>();
        public RequirementWriteOutcome? ForcedOutcome { get; set; }
        public int? FailStep { get; set; }
        public Func<string, string> Alter { get; set; }
        public Func<string> OverrideBody { get; set; }
        public RequirementWriteTestContext()
        {
            File.Copy(new RequirementWriteScriptOptions().ScriptPath, Transport.SourcePath, true);
            Gateway = new DxlRequirementWriteGateway(Transport.Executor, new RequirementWriteScriptOptions(Transport.SourcePath), Transport.Log, Audit);
            Service = NewService();
            Transport.Sessions.Session.OnRun = session =>
            {
                string mode = DiscoveryTestContext.Parameter(session.Script, "mode");
                Modes.Add(mode);
                string expected = DiscoveryTestContext.Parameter(session.Script, "expectedState");
                var outcome = mode == "schema" ? RequirementWriteOutcome.Schema : mode == "preview" ? RequirementWriteOutcome.Preview :
                    ForcedOutcome ?? (expected != ContextState ? RequirementWriteOutcome.ContextChanged : Issues.Any(i => i.Severity == RequirementWriteIssueSeverity.Error) ?
                        RequirementWriteOutcome.Blocked : FailStep.HasValue ? RequirementWriteOutcome.PartialFailure : RequirementWriteOutcome.Applied);
                var id = Guid.ParseExact(session.Prefix.Split('|')[2], "N");
                var text = OverrideBody != null ? OverrideBody() : Body(id, mode, outcome);
                session.Payload = Alter == null ? text : Alter(text);
            };
        }
        public RequirementService NewService() => new RequirementService(new UnusedReader(), Gateway, Confirmation, Audit);
        public string Body(Guid id, string mode, RequirementWriteOutcome outcome)
        {
            var plan = RequirementWriteProtocol.Plan(mode == "schema" ? null : Request);
            bool applied = outcome == RequirementWriteOutcome.Applied;
            bool partial = outcome == RequirementWriteOutcome.PartialFailure || outcome == RequirementWriteOutcome.OutcomeUnknown;
            bool hasObject = Request.AbsoluteNumber.HasValue || applied || (partial && (!FailStep.HasValue || FailStep.Value > 0));
            int absolute = hasObject ? Request.AbsoluteNumber ?? 42 : 0;
            int parent = hasObject && (Request.Position.Kind == RequirementPositionKind.FirstChild || Request.Position.Kind == RequirementPositionKind.LastChild) ? Request.Position.AnchorAbsoluteNumber.Value : 0;
            var persistence = applied ? RequirementWritePersistence.Saved : outcome == RequirementWriteOutcome.OutcomeUnknown ? RequirementWritePersistence.Unknown : RequirementWritePersistence.NotAttempted;
            var cleanup = partial ? RequirementWriteCleanup.LeftOpenForReview : RequirementWriteCleanup.Closed;
            var b = new StringBuilder("DOORS_REQUIREMENT_WRITE|1|" + id.ToString("N") + "\nM\t" + mode + "\t" + Escape(Request.Target.ModulePath) + "\t" +
                Escape(Database) + "\t" + Escape(Actor) + "\t" + Escape(ModuleId) + "\t" + Escape(QualifiedId) + "\t" + Escape(mode == "schema" ? "" : ContextState) +
                "\t" + outcome + "\t" + persistence + "\t" + cleanup + "\t" + absolute + "\t" + (hasObject ? "REQ-" + absolute : "") + "\t" + parent + "\t" + (hasObject ? parent == 0 ? 1 : 2 : 0) + "\n");
            b.Append("A\tObject Heading\tText\tText\t1\t0\t1\t0\t1\t1\n");
            b.Append("A\tObject Text\tText\tText\t1\t0\t1\t0\t1\t1\n");
            b.Append("A\tPriority\tPriority Type\tEnumeration\t1\t0\t0\t0\t1\t1\nE\tPriority\tLow\nE\tPriority\tHigh\n");
            b.Append("A\tCost\tInteger\tInteger\t1\t0\t0\t0\t1\t1\n");
            b.Append("A\tCalculated\tText\tText\t1\t1\t0\t0\t1\t0\n");
            if (mode != "schema" && Request.AbsoluteNumber.HasValue)
                foreach (var change in Request.Changes) b.Append("B\t").Append(Escape(change.Name)).Append("\told plain\t{\\\\b old plain}\n");
            foreach (var issue in Issues) b.Append("I\t").Append(issue.Severity).Append('\t').Append(issue.Code).Append('\t').Append(Escape(issue.AttributeName)).Append('\n');
            b.Append("I\tWarning\tRequiredRulesNotDeclared\t\n");
            if (outcome == RequirementWriteOutcome.ContextChanged) b.Append("I\tError\tContextChanged\t\n");
            for (int i = 0; i < plan.Count; i++)
            {
                var status = applied || (partial && i < (FailStep ?? 2)) ? "Applied" : partial && i == (FailStep ?? 2) ? "Failed" : "Skipped";
                b.Append("S\t").Append(plan[i].Kind).Append('\t').Append(Escape(plan[i].AttributeName)).Append('\t').Append(status).Append('\t')
                    .Append(status == "Failed" ? "NativeFailure" : "").Append('\n');
            }
            return b.Append("END\n").ToString();
        }
        public static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n");
        public void Dispose() => Transport.Dispose();
        private sealed class UnusedReader : IRequirementReader
        {
            public Task<IRequirementSnapshot> ReadAsync(RequirementReadRequest request, CancellationToken token = default(CancellationToken)) => throw new NotSupportedException();
        }
    }

    internal sealed class TestRequirementConfirmation : IRequirementWriteConfirmation
    {
        public bool Approved { get; set; }
        public int Calls { get; private set; }
        public Action<RequirementWritePreview> BeforeReturn { get; set; }
        public Task<bool> ConfirmAsync(RequirementWritePreview preview, CancellationToken cancellationToken)
        {
            Calls++; BeforeReturn?.Invoke(preview); cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Approved);
        }
    }
    internal sealed class TestRequirementWriteAudit : IRequirementWriteAuditSink
    {
        public ConcurrentQueue<RequirementWriteAuditEntry> Entries { get; } = new ConcurrentQueue<RequirementWriteAuditEntry>();
        public string FailPhase { get; set; }
        public Action<RequirementWriteAuditEntry> BeforeRecord { get; set; }
        public void Record(RequirementWriteAuditEntry entry)
        {
            BeforeRecord?.Invoke(entry);
            if (entry.Phase == FailPhase) throw new IOException("PRIVATE_CONTENT");
            Entries.Enqueue(entry);
        }
    }
}
