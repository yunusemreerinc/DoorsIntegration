using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DoorsIntegration.Core
{
    public sealed class BulkBaselineReport
    {
        public const string Disclosure = "Application audit/report only; does not replace DOORS native history. Batch is not transactional; unknown/partial outcomes are not full success.";
        public static BulkBaselineReport Create(BulkBaselineJobRecord job, Func<CancellationToken, IEnumerable<BaselineJobAuditEntry>> audit, string coverage, string headHash) => new BulkBaselineReport(job, audit, coverage, headHash);
        private readonly Func<CancellationToken, IEnumerable<BaselineJobAuditEntry>> audit;
        internal BulkBaselineReport(BulkBaselineJobRecord job, Func<CancellationToken, IEnumerable<BaselineJobAuditEntry>> audit, string coverage, string headHash)
        {
            JobId = job.JobId; Revision = job.Revision; Type = job.Type; Source = job.Source; Status = job.Status;
            Context = job.AuditContext ?? new BaselineJobAuditContext(Join(job.Modules.Select(m => m.UserId)), Join(job.Modules.Select(m => m.DatabaseIdentity)),
                Join(job.Modules.Select(m => m.RepositoryId)) + " / " + job.Source, "Requested profile " + DoorsVersions.Format(job.Target), "Legacy snapshot: observed executable version NOT_COLLECTED");
            Suffix = BaselineAuditSanitizer.Text(job.JobSuffix ?? "[Legacy job default unavailable; see modules]");
            Annotation = BaselineAuditSanitizer.Text(job.JobAnnotation ?? "[Legacy job default unavailable; see modules]");
            CreatedAtUtc = job.CreatedAtUtc; StartedAtUtc = job.ExecutionStartedAtUtc; EndedAtUtc = job.ExecutionEndedAtUtc;
            Elapsed = job.Elapsed; AuditCoverage = coverage; AuditHeadHash = headHash; this.audit = audit;
            SourcePlanId = job.SourcePlanId; SourcePlanFingerprint = job.SourcePlanFingerprint; InputFingerprint = job.InputFingerprint;
            Modules = job.Modules.Select(m => new BaselineAuditModuleState(m)).ToArray();
            SuccessCount = Count(BaselineReportGroup.Successful); FailedCount = Count(BaselineReportGroup.Failed); LockedCount = Count(BaselineReportGroup.Locked);
            SkippedCount = Count(BaselineReportGroup.Skipped) + LockedCount; UnknownCount = Count(BaselineReportGroup.Unverifiable);
            IsFullSuccess = Modules.Count > 0 && SuccessCount == Modules.Count && job.Status == BulkBaselineModuleStatus.Succeeded && StartedAtUtc.HasValue && EndedAtUtc.HasValue;
            Outcome = IsFullSuccess ? BaselineBatchOutcome.Success : UnknownCount > 0 || !EndedAtUtc.HasValue ? BaselineBatchOutcome.Indeterminate :
                SuccessCount > 0 ? BaselineBatchOutcome.PartialSuccess : FailedCount > 0 ? BaselineBatchOutcome.Failed : BaselineBatchOutcome.Skipped;
        }
        private int Count(BaselineReportGroup group) => Modules.Count(m => m.Group == group.ToString());
        private static string Join(IEnumerable<string> values) => string.Join("; ", values.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct());
        public Guid JobId { get; }
        public long Revision { get; }
        public BaselineType Type { get; }
        public BaselineMetadataSource Source { get; }
        public BulkBaselineModuleStatus Status { get; }
        public BaselineJobAuditContext Context { get; }
        public string Suffix { get; }
        public string Annotation { get; }
        public DateTime CreatedAtUtc { get; }
        public DateTime? StartedAtUtc { get; }
        public DateTime? EndedAtUtc { get; }
        public TimeSpan Elapsed { get; }
        public Guid SourcePlanId { get; }
        public string SourcePlanFingerprint { get; }
        public string InputFingerprint { get; }
        public string AuditCoverage { get; }
        public string AuditHeadHash { get; }
        public IReadOnlyList<BaselineAuditModuleState> Modules { get; }
        public int SuccessCount { get; }
        public int FailedCount { get; }
        public int SkippedCount { get; }
        public int LockedCount { get; }
        public int UnknownCount { get; }
        public bool IsFullSuccess { get; }
        public BaselineBatchOutcome Outcome { get; }
        public IEnumerable<BaselineAuditModuleState> Group(BaselineReportGroup group) => Modules.Where(m => m.Group == group.ToString());
        public IEnumerable<BaselineJobAuditEntry> ReadAudit(CancellationToken token = default(CancellationToken)) => audit(token);
    }
    public interface IBulkBaselineReportService
    {
        Task<BulkBaselineReport> ReadAsync(Guid jobId, CancellationToken cancellationToken = default(CancellationToken));
        Task<ExportResult> ExportAsync(Guid jobId, ExportRequest request, IProgress<ExportProgress> progress = null, CancellationToken cancellationToken = default(CancellationToken));
    }
    public sealed class BaselineJobHistoryFilter
    {
        public BaselineJobHistoryFilter(string search = "", string actor = "", string database = "", BaselineMetadataSource? source = null,
            string repository = "", DoorsVersion? target = null, DateTime? fromUtc = null, DateTime? toUtc = null, BaselineBatchOutcome? outcome = null, int offset = 0, int limit = 100, int maxScan = 10000)
        {
            if (search == null || actor == null || database == null || repository == null || search.Length > 256 || actor.Length > 1024 || database.Length > 4096 || offset < 0 || limit < 1 || limit > 1000 || maxScan < 1 || maxScan > 100000) throw new ArgumentException("Invalid history filter.");
            if (fromUtc.HasValue) BulkBaselineGuard.Utc(fromUtc.Value); if (toUtc.HasValue) BulkBaselineGuard.Utc(toUtc.Value);
            if (fromUtc > toUtc) throw new ArgumentException("Invalid history date range."); if (outcome.HasValue) BulkBaselineGuard.Defined(outcome.Value);
            Search = search; Operator = actor; Database = database; Source = source; Repository = repository; Target = target; FromUtc = fromUtc; ToUtc = toUtc; Outcome = outcome; Offset = offset; Limit = limit; MaxScan = maxScan;
        }
        public string Search { get; }
        public string Operator { get; }
        public string Database { get; }
        public BaselineMetadataSource? Source { get; }
        public string Repository { get; }
        public DoorsVersion? Target { get; }
        public DateTime? FromUtc { get; }
        public DateTime? ToUtc { get; }
        public BaselineBatchOutcome? Outcome { get; }
        public int Offset { get; }
        public int Limit { get; }
        public int MaxScan { get; }
    }
    public sealed class BaselineJobHistoryItem
    {
        internal BaselineJobHistoryItem(BulkBaselineJobRecord job, BulkBaselineReport summary) { Snapshot = job; Report = summary; }
        public BulkBaselineJobRecord Snapshot { get; }
        public BulkBaselineReport Report { get; }
        public Guid JobId => Snapshot.JobId;
        public string Operator => Report.Context.Operator;
        public string Database => Report.Context.DatabaseIdentifier;
        public BaselineBatchOutcome Outcome => Report.Outcome;
        public string Counts => "S=" + Report.SuccessCount + "; F=" + Report.FailedCount + "; Skip=" + Report.SkippedCount + "; Unknown=" + Report.UnknownCount;
        public DateTime UpdatedAtUtc => Snapshot.UpdatedAtUtc;
    }
    public sealed class BaselineJobHistoryPage
    {
        internal BaselineJobHistoryPage(IEnumerable<BaselineJobHistoryItem> items, int matched, int offset, bool scanLimited, IEnumerable<string> issues)
        { Items = items.ToArray(); Matched = matched; Offset = offset; ScanLimited = scanLimited; Issues = issues.ToArray(); }
        public IReadOnlyList<BaselineJobHistoryItem> Items { get; }
        public int Matched { get; }
        public int Offset { get; }
        public bool ScanLimited { get; }
        public IReadOnlyList<string> Issues { get; }
    }
    public interface IJobHistoryService
    {
        Task<BaselineJobHistoryPage> QueryAsync(BaselineJobHistoryFilter filter, CancellationToken cancellationToken = default(CancellationToken));
        Task<BulkBaselineJobRecord> OpenAsync(Guid jobId, CancellationToken cancellationToken = default(CancellationToken));
    }
    public sealed class JobHistoryService : IJobHistoryService
    {
        private readonly IBulkBaselineJobJournal journal;
        private readonly IBaselineJobAuditStore audit;
        public JobHistoryService(IBulkBaselineJobJournal journal, IBaselineJobAuditStore audit) { this.journal = journal; this.audit = audit; }
        private sealed class Candidate
        {
            internal Guid JobId;
            internal long Revision;
            internal DateTime UpdatedAtUtc;
        }
        public Task<BulkBaselineJobRecord> OpenAsync(Guid id, CancellationToken token = default(CancellationToken)) => Task.Run(() =>
        { token.ThrowIfCancellationRequested(); using (var lease = journal.Acquire(id)) return lease.Current ?? throw BaselineJobGuard.Failure("Job not found."); }, token);
        public Task<BaselineJobHistoryPage> QueryAsync(BaselineJobHistoryFilter filter, CancellationToken token = default(CancellationToken)) => Task.Run(() =>
        {
            if (filter == null) throw new ArgumentNullException(nameof(filter)); var candidates = new List<Candidate>(); var issues = new List<string>(); int scanned = 0; bool limited = false;
            foreach (Guid id in audit.EnumerateJobIds(token))
            {
                token.ThrowIfCancellationRequested(); if (++scanned > filter.MaxScan) { limited = true; break; }
                try
                {
                    using (var lease = journal.Acquire(id))
                    {
                        var job = lease.Current; if (job == null || filter.Source.HasValue && job.Source != filter.Source.Value || filter.Target.HasValue && job.Target != filter.Target.Value ||
                            filter.Repository.Length > 0 && job.Modules.Any(m => m.RepositoryId != filter.Repository) || filter.FromUtc.HasValue && job.CreatedAtUtc < filter.FromUtc || filter.ToUtc.HasValue && job.CreatedAtUtc > filter.ToUtc) continue;
                        var summary = new BulkBaselineReport(job, t => audit.ReadAudit(id, job.Revision, t), "ReadOnlyHistoryProjection", null);
                        if (!Contains(summary.Context.Operator, filter.Operator) || !Contains(summary.Context.DatabaseIdentifier, filter.Database) || filter.Outcome.HasValue && summary.Outcome != filter.Outcome.Value) continue;
                        if (filter.Search.Length > 0 && !Contains(id.ToString(), filter.Search) && !Contains(summary.Context.EnvironmentIdentifier, filter.Search) &&
                            !summary.Modules.Any(m => Contains(m.StableIdentity, filter.Search) || Contains(m.FullPath, filter.Search))) continue;
                        // Keep lightweight sort keys only; do not retain every job's module/annotation payload.
                        candidates.Add(new Candidate { JobId = job.JobId, Revision = job.Revision, UpdatedAtUtc = job.UpdatedAtUtc });
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { issues.Add(id.ToString("N") + ": unavailable/corrupt/busy; no success was assumed."); }
            }
            var result = new List<BaselineJobHistoryItem>();
            foreach (var candidate in candidates.OrderByDescending(i => i.UpdatedAtUtc).ThenBy(i => i.JobId).Skip(filter.Offset).Take(filter.Limit))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    using (var lease = journal.Acquire(candidate.JobId))
                    {
                        var job = lease.Current;
                        if (job == null || job.Revision != candidate.Revision || job.UpdatedAtUtc != candidate.UpdatedAtUtc)
                        { issues.Add(candidate.JobId.ToString("N") + ": changed during history query; refresh to view the new revision."); continue; }
                        result.Add(new BaselineJobHistoryItem(job, new BulkBaselineReport(job, t => audit.ReadAudit(job.JobId, job.Revision, t), "ReadOnlyHistoryProjection", null)));
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { issues.Add(candidate.JobId.ToString("N") + ": unavailable/corrupt/busy; refresh to retry this read."); }
            }
            return new BaselineJobHistoryPage(result, candidates.Count, filter.Offset, limited, issues);
        }, token);
        private static bool Contains(string value, string filter) => filter.Length == 0 || value != null && value.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
