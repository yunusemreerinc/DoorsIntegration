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
    internal sealed class LinkWriteTestContext : IDisposable
    {
        public ExecutorTestContext Transport { get; } = new ExecutorTestContext();
        public LinkWriteRequest Request { get; set; } = new LinkWriteRequest(LinkWriteAction.Create, "/P/Source", 1, "/P/Target", 2, "/P/Links");
        public TestLinkWriteAudit Audit { get; } = new TestLinkWriteAudit();
        public TestLinkConfirmation Confirmation { get; } = new TestLinkConfirmation();
        public DxlLinkWriteGateway Gateway { get; }
        public LinkService Service { get; }
        public List<string> Modes { get; } = new List<string>();
        public List<LinkTraceEdge> Before { get; } = new List<LinkTraceEdge>
        {
            new LinkTraceEdge("/P/Target", "db:target", LinkVersionKind.Current, "", 8),
            new LinkTraceEdge("/P/Target", "db:target", LinkVersionKind.Baseline, "1.0", 2)
        };
        public List<LinkWriteIssue> Issues { get; } = new List<LinkWriteIssue>();
        public string Database { get; set; } = "test-db";
        public string Actor { get; set; } = "test-user";
        public string SourceId { get; set; } = "db:source";
        public string TargetId { get; set; } = "db:target";
        public string LinkId { get; set; } = "db:link";
        public string StateSalt { get; set; } = "state-v1";
        public LinkWriteOutcome? ForcedOutcome { get; set; }
        public Func<string, string> Alter { get; set; }
        public LinkWriteTestContext()
        {
            File.Copy(new LinkWriteScriptOptions().ScriptPath, Transport.SourcePath, true);
            Gateway = new DxlLinkWriteGateway(Transport.Executor, new LinkWriteScriptOptions(Transport.SourcePath), Transport.Log, Audit);
            Service = NewService();
            Transport.Sessions.Session.OnRun = session =>
            {
                string mode = DiscoveryTestContext.Parameter(session.Script, "mode"); Modes.Add(mode);
                var issues = new List<LinkWriteIssue>(Issues);
                int matches = Before.Count(e => e.TargetModulePath == Request.TargetModulePath && e.TargetModuleId == TargetId &&
                    e.VersionKind == LinkVersionKind.Current && e.TargetAbsoluteNumber == Request.TargetAbsoluteNumber);
                if (Request.Action == LinkWriteAction.Create && matches != 0) issues.Add(LinkWriteIssue.DuplicateLink);
                if (Request.Action == LinkWriteAction.Create && Before.Count >= Request.MaxTraceLinks) issues.Add(LinkWriteIssue.LimitExceeded);
                if (Request.Action == LinkWriteAction.Remove && matches != 1) issues.Add(matches == 0 ? LinkWriteIssue.LinkMissing : LinkWriteIssue.AmbiguousLink);
                var outcome = mode == "preview" ? LinkWriteOutcome.Preview : ForcedOutcome ??
                    (DiscoveryTestContext.Parameter(session.Script, "expectedState") != State ? LinkWriteOutcome.ContextChanged :
                        issues.Count != 0 ? LinkWriteOutcome.Blocked : LinkWriteOutcome.Applied);
                if (outcome == LinkWriteOutcome.ContextChanged) issues.Add(LinkWriteIssue.ContextChanged);
                if (outcome == LinkWriteOutcome.PartialFailure || outcome == LinkWriteOutcome.OutcomeUnknown) issues.Add(LinkWriteIssue.NativeFailure);
                var id = Guid.ParseExact(session.Prefix.Split('|')[2], "N");
                string text = Body(id, mode, outcome, DiscoveryTestContext.Parameter(session.Script, "approvedPlan"), issues);
                session.Payload = Alter == null ? text : Alter(text);
            };
        }
        private string State => StateSalt + "|" + Database + "|" + Actor + "|" + SourceId + "|" + TargetId + "|" + LinkId +
            "|" + string.Join(";", Before.Select(e => e.Key).OrderBy(k => k, StringComparer.Ordinal));
        public LinkService NewService() => new LinkService(new UnusedLinkReader(), Gateway, Confirmation, Audit);
        public LinkTraceEdge Selected() => new LinkTraceEdge(Request.TargetModulePath, TargetId, LinkVersionKind.Current, "", Request.TargetAbsoluteNumber);
        private string Body(Guid id, string mode, LinkWriteOutcome outcome, string plan, IEnumerable<LinkWriteIssue> issues)
        {
            bool applied = outcome == LinkWriteOutcome.Applied;
            var after = new List<LinkTraceEdge>(Before);
            if (applied)
            {
                if (Request.Action == LinkWriteAction.Create) after.Add(Selected());
                else after.RemoveAt(after.FindIndex(e => e.Key == Selected().Key));
            }
            var b = new StringBuilder("DOORS_LINK_WRITE|1|" + id.ToString("N") + "\nR\t" + mode + "\t" + Request.Action + "\t" +
                Escape(Request.SourceModulePath) + "\t" + Request.SourceAbsoluteNumber + "\t" + Escape(Request.TargetModulePath) + "\t" + Request.TargetAbsoluteNumber +
                "\t" + Escape(Request.LinkModulePath) + "\t" + plan + "\nM\t" + Escape(Database) + "\t" + Escape(Actor) + "\t" +
                Escape(SourceId) + "\t" + Escape(TargetId) + "\t" + Escape(LinkId) + "\tSRC-1\tTGT-2\t" + Escape(State) + "\t" + outcome + "\t" +
                (applied ? "Saved" : outcome == LinkWriteOutcome.OutcomeUnknown ? "Unknown" : "NotAttempted") + "\t" +
                (outcome == LinkWriteOutcome.PartialFailure || outcome == LinkWriteOutcome.OutcomeUnknown ? "LeftOpenForReview" : "Closed") + "\t" +
                (applied ? "1" : "0") + "\tSelectedSourceObjectOutgoing\n");
            foreach (var edge in Before) Row(b, "B", edge);
            if (applied) foreach (var edge in after) Row(b, "A", edge);
            foreach (var issue in issues) b.Append("I\t").Append(issue).Append('\n');
            return b.Append("END\n").ToString();
        }
        public static void Row(StringBuilder b, string tag, LinkTraceEdge edge) => b.Append(tag).Append('\t').Append(Escape(edge.TargetModulePath)).Append('\t')
            .Append(Escape(edge.TargetModuleId)).Append('\t').Append(edge.VersionKind).Append('\t').Append(Escape(edge.Version)).Append('\t').Append(edge.TargetAbsoluteNumber).Append('\n');
        public static string Escape(string value) => RequirementWriteTestContext.Escape(value);
        public void Dispose() => Transport.Dispose();
    }
    internal sealed class UnusedLinkReader : ILinkReader
    {
        public Task<ILinkSnapshot> ReadAsync(LinkReadRequest request, CancellationToken cancellationToken = default(CancellationToken)) => throw new NotSupportedException();
    }
    internal sealed class TestLinkConfirmation : ILinkWriteConfirmation
    {
        public bool Approved, RemovalApproved;
        public int Calls, RemovalCalls;
        public Action<LinkWritePreview> BeforeReturn, BeforeRemovalReturn;
        public Task<bool> ConfirmAsync(LinkWritePreview preview, CancellationToken cancellationToken)
        { Calls++; BeforeReturn?.Invoke(preview); cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(Approved); }
        public Task<bool> ConfirmRemovalAsync(LinkWritePreview preview, CancellationToken cancellationToken)
        { RemovalCalls++; BeforeRemovalReturn?.Invoke(preview); cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(RemovalApproved); }
    }
    internal sealed class TestLinkWriteAudit : ILinkWriteAuditSink
    {
        public string FailPhase;
        public ConcurrentQueue<LinkWriteAuditEntry> Entries { get; } = new ConcurrentQueue<LinkWriteAuditEntry>();
        public void Record(LinkWriteAuditEntry entry)
        { if (entry.Phase == FailPhase) throw new IOException("PRIVATE_AUDIT_ERROR"); Entries.Enqueue(entry); }
    }
}
