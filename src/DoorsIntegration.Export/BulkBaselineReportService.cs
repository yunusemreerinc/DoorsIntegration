using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;

namespace DoorsIntegration.Export
{
    public sealed class BulkBaselineReportService : IBulkBaselineReportService
    {
        private readonly IBulkBaselineJobJournal journal;
        private readonly IBaselineJobAuditStore audit;
        private readonly IExportService exporter;
        public BulkBaselineReportService(IBulkBaselineJobJournal journal, IBaselineJobAuditStore audit, IExportService exporter)
        { this.journal = journal ?? throw new ArgumentNullException(nameof(journal)); this.audit = audit ?? throw new ArgumentNullException(nameof(audit)); this.exporter = exporter ?? throw new ArgumentNullException(nameof(exporter)); }
        public Task<BulkBaselineReport> ReadAsync(Guid id, CancellationToken token = default(CancellationToken)) => Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            using (var lease = journal.Acquire(id))
            {
                var job = lease.Current ?? throw new InvalidOperationException("Job not found."); long? first = null; string head = null; long last = -1; bool imported = false;
                foreach (var entry in audit.ReadAudit(id, job.Revision, token)) { if (!first.HasValue) imported = entry.Action == BaselineAuditAction.LegacyImported.ToString(); first = first ?? entry.Revision; last = entry.Revision; head = entry.Hash; }
                if (first.HasValue && last != job.Revision) throw new BulkBaselineJournalException("Report audit prefix does not match the captured job revision.");
                string coverage = !first.HasValue ? "LegacySnapshotOnly; approved plan and prior events unavailable" : first == 0 && !imported ? "FromCreation" : "ImportedFromRevision=" + first + "; earlier events unavailable";
                return BulkBaselineReport.Create(job, t => ReadCapturedPrefix(id, job.Revision, head, t), coverage, head);
            }
        }, token);
        private IEnumerable<BaselineJobAuditEntry> ReadCapturedPrefix(Guid id, long revision, string head, CancellationToken token)
        {
            string actual = null; long last = -1;
            foreach (var entry in audit.ReadAudit(id, revision, token)) { actual = entry.Hash; last = entry.Revision; yield return entry; }
            if (actual != head || head != null && last != revision) throw new BulkBaselineJournalException("Captured audit prefix changed or is unavailable; report was not completed.");
        }
        public async Task<ExportResult> ExportAsync(Guid id, ExportRequest request, IProgress<ExportProgress> progress = null, CancellationToken cancellationToken = default(CancellationToken))
        { var report = await ReadAsync(id, cancellationToken).ConfigureAwait(false); return await exporter.ExportAsync(request, new BulkBaselineReportSource(report), progress, cancellationToken).ConfigureAwait(false); }
    }
    public sealed class BulkBaselineReportSource : IExportSource
    {
        private readonly BulkBaselineReport report;
        public BulkBaselineReportSource(BulkBaselineReport report) { this.report = report ?? throw new ArgumentNullException(nameof(report)); }
        public long? EstimatedRecordCount => null;
        public IEnumerable<ExportRecord> Read(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); string id = report.JobId.ToString("N"); var c = report.Context;
            yield return Row(ExportTableKind.Scopes, "scopeId", id, "sourceKind", "BulkBaselineAuditReport", "isComplete", report.UnknownCount == 0 && report.AuditCoverage == "FromCreation", "visibility", "RecordedJobOutcomesOnly", "details", BulkBaselineReport.Disclosure);
            yield return Row(ExportTableKind.BulkBaselineJobs, "jobId", id, "revision", report.Revision, "operator", c.Operator, "databaseIdentifier", c.DatabaseIdentifier, "environmentIdentifier", c.EnvironmentIdentifier,
                "clientVersion", c.ClientVersion, "clientVersionEvidence", c.ClientVersionEvidence, "source", report.Source.ToString(), "baselineType", report.Type.ToString(), "suffix", report.Suffix, "annotation", report.Annotation,
                "createdAtUtc", report.CreatedAtUtc, "startedAtUtc", report.StartedAtUtc, "endedAtUtc", report.EndedAtUtc, "outcome", report.Outcome.ToString(), "status", report.Status.ToString(), "isFullSuccess", report.IsFullSuccess,
                "moduleCount", report.Modules.Count, "successCount", report.SuccessCount, "failedCount", report.FailedCount, "skippedCount", report.SkippedCount, "lockedCount", report.LockedCount, "unknownCount", report.UnknownCount,
                "sourcePlanId", report.SourcePlanId.ToString("N"), "sourcePlanFingerprint", report.SourcePlanFingerprint, "inputFingerprint", report.InputFingerprint, "auditCoverage", report.AuditCoverage, "auditHeadHash", report.AuditHeadHash, "disclosure", BulkBaselineReport.Disclosure);
            foreach (var group in Enum.GetValues(typeof(BaselineReportGroup)).Cast<BaselineReportGroup>())
                foreach (var m in report.Group(group))
                {
                    token.ThrowIfCancellationRequested();
                    yield return Row(ExportTableKind.BulkBaselineModules, "jobId", id, "stableIdentity", m.StableIdentity, "fullPath", m.FullPath, "previousBaseline", m.PreviousBaseline, "expectedBaseline", m.ExpectedBaseline,
                        "actualBaseline", m.ActualBaseline, "actualEvidence", m.ActualEvidence, "group", m.Group, "resultStatus", m.ResultStatus, "durationSeconds", m.DurationSeconds, "errorMessage", m.ErrorMessage,
                        "suffix", m.Suffix, "annotation", m.Annotation, "attempts", m.Attempts, "approvalId", m.ApprovalId?.ToString("N"), "operationId", m.OperationId?.ToString("N"), "startedAtUtc", m.StartedAtUtc, "endedAtUtc", m.EndedAtUtc);
                }
            var plans = new HashSet<Guid>();
            foreach (var entry in report.ReadAudit(token))
            {
                token.ThrowIfCancellationRequested();
                var plan = entry.ApprovedPlan;
                if (plan != null && plans.Add(plan.ApprovalId))
                    foreach (var m in plan.Modules)
                        yield return Row(ExportTableKind.BulkBaselineApprovedPlans, "jobId", id, "approvalId", plan.ApprovalId.ToString("N"), "planId", plan.PlanId.ToString("N"), "fingerprint", plan.Fingerprint,
                            "approvedAtUtc", plan.ApprovedAtUtc, "operator", plan.Operator, "scope", plan.Scope, "secondRealConfirmation", plan.SecondRealConfirmation, "baselineType", plan.Type.ToString(),
                            "stableIdentity", m.StableIdentity, "fullPath", m.FullPath, "previousBaseline", m.PreviousBaseline, "expectedBaseline", m.ExpectedBaseline, "suffix", m.Suffix, "annotation", m.Annotation, "preconditionFingerprint", m.PreconditionFingerprint, "warning", m.Warning);
                foreach (var m in entry.Modules.Count == 0 ? new BaselineAuditModuleState[] { null } : entry.Modules)
                    yield return Row(ExportTableKind.BulkBaselineAuditEvents, "jobId", id, "revision", entry.Revision, "recordedAtUtc", entry.RecordedAtUtc, "action", entry.Action, "status", entry.Status, "previousHash", entry.PreviousHash, "hash", entry.Hash,
                        "stableIdentity", m?.StableIdentity, "fullPath", m?.FullPath, "previousBaseline", m?.PreviousBaseline, "expectedBaseline", m?.ExpectedBaseline, "actualBaseline", m?.ActualBaseline, "resultStatus", m?.ResultStatus,
                        "errorMessage", m?.ErrorMessage, "approvalId", m?.ApprovalId?.ToString("N"), "operationId", m?.OperationId?.ToString("N"));
            }
        }
        private static ExportRecord Row(ExportTableKind table, params object[] values)
        { var fields = new Dictionary<string, object>(StringComparer.Ordinal); for (int i = 0; i < values.Length; i += 2) fields.Add((string)values[i], values[i + 1]); return new ExportRecord(table, fields); }
    }
}
