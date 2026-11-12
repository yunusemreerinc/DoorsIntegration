using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DoorsIntegration.Core
{
    public sealed class BulkBaselineJournalException : IntegrationException
    {
        public BulkBaselineJournalException(string message) : base(IntegrationErrorCode.BulkBaselineJobFailed, message) { }
    }
    public enum BulkBaselineModuleStatus { Pending, Prechecking, Ready, Running, Succeeded, Skipped, Failed, Unknown, CancelRequested, NeedsReconciliation }
    public enum BaselineJobVerificationStatus { ExistsMatching, AbsentAuthoritatively, ExistsConflicting, Unknown }

    [DataContract]
    internal sealed class JournalBaselineVersion
    {
        [DataMember(Order = 0, IsRequired = true)] internal int Major;
        [DataMember(Order = 1, IsRequired = true)] internal int Minor;
        [DataMember(Order = 2, IsRequired = true)] internal string Suffix;
        internal static JournalBaselineVersion From(BaselineVersion version) => version == null ? null : new JournalBaselineVersion { Major = version.Major, Minor = version.Minor, Suffix = version.Suffix };
        internal BaselineVersion Value => new BaselineVersion(Major, Minor, Suffix);
    }

    // Immutable public snapshots. No approval capability, COM handle or Task is serialized.
    [DataContract]
    public sealed class BulkBaselineJobModule
    {
        [DataMember(Order = 0, IsRequired = true)] public string RepositoryId { get; private set; }
        [DataMember(Order = 1, IsRequired = true)] public string ModuleId { get; private set; }
        [DataMember(Order = 2, IsRequired = true)] public string CanonicalFullPath { get; private set; }
        [DataMember(Order = 3, IsRequired = true)] private JournalBaselineVersion current;
        [DataMember(Order = 4, IsRequired = true)] private JournalBaselineVersion expected;
        [DataMember(Order = 5, IsRequired = true)] public string Suffix { get; private set; }
        [DataMember(Order = 6, IsRequired = true)] public string Annotation { get; private set; }
        [DataMember(Order = 7, IsRequired = true)] public string DatabaseIdentity { get; private set; }
        [DataMember(Order = 8, IsRequired = true)] public string UserId { get; private set; }
        [DataMember(Order = 9, IsRequired = true)] public string OriginalPrecondition { get; private set; }
        [DataMember(Order = 10, IsRequired = true)] public BulkBaselineModuleStatus Status { get; private set; }
        [DataMember(Order = 11, IsRequired = true)] public string FailureReason { get; private set; }
        [DataMember(Order = 12, IsRequired = true)] public int Attempts { get; private set; }
        [DataMember(Order = 13, IsRequired = true)] public Guid? AttemptId { get; private set; }
        [DataMember(Order = 14, IsRequired = true)] public Guid? OperationId { get; private set; }
        [DataMember(Order = 15, IsRequired = true)] public bool WriteMayHaveOccurred { get; private set; }
        [DataMember(Order = 16, IsRequired = true)] public DateTime? StartedAtUtc { get; private set; }
        [DataMember(Order = 17, IsRequired = true)] public DateTime? CompletedAtUtc { get; private set; }
        [DataMember(Order = 18, IsRequired = true)] public DateTime? SuccessObservedAtUtc { get; private set; }
        [DataMember(Order = 19, IsRequired = true)] public string LastPrecondition { get; private set; }
        [DataMember(Order = 20, IsRequired = true)] public BaselineJobVerificationStatus LastVerification { get; private set; }
        [DataMember(Order = 21, IsRequired = true)] public DateTime? VerifiedAtUtc { get; private set; }
        [DataMember(Order = 22, IsRequired = true)] public string VerificationEvidence { get; private set; }
        [DataMember(Order = 23, EmitDefaultValue = false)] public Guid? AcceptedApprovalId { get; private set; }
        [DataMember(Order = 24, EmitDefaultValue = false)] public DateTime? ApprovalAcceptedAtUtc { get; private set; }
        [DataMember(Order = 25, EmitDefaultValue = false)] public BaselinePreflightReason? PreflightReason { get; private set; }
        [DataMember(Order = 26, EmitDefaultValue = false)] public BaselineLockState? ObservedLockState { get; private set; }
        public BaselineModuleReference Module => new BaselineModuleReference(RepositoryId, ModuleId, CanonicalFullPath);
        public BaselineVersion CurrentVersion => current?.Value;
        public BaselineVersion ExpectedVersion => expected?.Value;
        // Sticky even if a subsequently missing/inaccessible baseline contradicts a success record.
        public bool EverSucceeded => SuccessObservedAtUtc.HasValue;
        public bool AutomaticRetryAllowed => false;
        internal BulkBaselineJobModule(BulkBaselineDryRunItem item)
        {
            RepositoryId = item.Module.RepositoryId; ModuleId = item.ModuleId; CanonicalFullPath = item.FullPath;
            current = JournalBaselineVersion.From(item.CurrentVersion); expected = JournalBaselineVersion.From(item.ExpectedVersion);
            Suffix = item.Suffix; Annotation = item.Annotation; DatabaseIdentity = item.Precondition?.Facts.DatabaseIdentity ?? "";
            UserId = item.Precondition?.Facts.CurrentUserId ?? ""; OriginalPrecondition = item.Precondition?.Fingerprint ?? "";
            LastPrecondition = OriginalPrecondition; Status = item.Status == BulkBaselineItemStatus.Ready ? BulkBaselineModuleStatus.Pending : BulkBaselineModuleStatus.Skipped;
            PreflightReason = item.ReasonCode; ObservedLockState = item.Precondition?.Facts.LockState;
            FailureReason = item.Status == BulkBaselineItemStatus.Ready ? "" : item.Reason;
            LastVerification = BaselineJobVerificationStatus.Unknown; VerificationEvidence = ""; Validate();
        }
        internal BulkBaselineJobModule With(BulkBaselineModuleStatus status, string reason, DateTime now,
            bool beginAttempt = false, BaselineModuleExecutionResult result = null, BaselineJobVerification verification = null, string precondition = null, bool? mayHaveWritten = null,
            BulkBaselineDryRunItem preflight = null, BaselineApprovedPlan approvedPlan = null)
        {
            var copy = (BulkBaselineJobModule)MemberwiseClone(); copy.Status = status; copy.FailureReason = reason;
            if (beginAttempt)
            {
                copy.Attempts = checked(Attempts + 1); copy.AttemptId = Guid.NewGuid(); copy.OperationId = null;
                copy.StartedAtUtc = now; copy.CompletedAtUtc = null; copy.WriteMayHaveOccurred = true;
            }
            if (result != null) { copy.OperationId = result.OperationId; copy.WriteMayHaveOccurred = result.WriteAttempted; }
            if (status == BulkBaselineModuleStatus.Succeeded)
                copy.SuccessObservedAtUtc = SuccessObservedAtUtc ?? now;
            // Read-only reconciliation changes evidence/status, not the completed attempt's duration.
            // Only beginAttempt above clears completion for an explicitly approved new attempt.
            if (!copy.CompletedAtUtc.HasValue && (status == BulkBaselineModuleStatus.Succeeded || status == BulkBaselineModuleStatus.Failed ||
                status == BulkBaselineModuleStatus.Skipped || status == BulkBaselineModuleStatus.Unknown || status == BulkBaselineModuleStatus.NeedsReconciliation)) copy.CompletedAtUtc = now;
            if (verification != null)
            { copy.LastVerification = verification.Status; copy.VerifiedAtUtc = verification.ObservedAtUtc; copy.VerificationEvidence = verification.Evidence; }
            if (precondition != null) copy.LastPrecondition = precondition;
            if (mayHaveWritten.HasValue) copy.WriteMayHaveOccurred = mayHaveWritten.Value;
            if (preflight != null) { copy.PreflightReason = preflight.ReasonCode; copy.ObservedLockState = preflight.Precondition?.Facts.LockState; }
            if (approvedPlan != null) { copy.AcceptedApprovalId = approvedPlan.ApprovalId; copy.ApprovalAcceptedAtUtc = now; }
            copy.Validate(); return copy;
        }
        internal void Validate()
        {
            var reference = Module; var old = CurrentVersion; var target = ExpectedVersion;
            BulkBaselineGuard.Suffix(Suffix); BulkBaselineGuard.Annotation(Annotation); BulkBaselineGuard.Defined(Status); BulkBaselineGuard.Defined(LastVerification);
            BaselineJobGuard.Text(DatabaseIdentity, 4096); BaselineJobGuard.Text(UserId, 4096); BaselineJobGuard.Text(FailureReason, 4096);
            BaselineJobGuard.Text(VerificationEvidence, 4096); BaselineJobGuard.HashOrEmpty(OriginalPrecondition); BaselineJobGuard.HashOrEmpty(LastPrecondition);
            if (PreflightReason.HasValue) BulkBaselineGuard.Defined(PreflightReason.Value); if (ObservedLockState.HasValue) BulkBaselineGuard.Defined(ObservedLockState.Value);
            if (ApprovalAcceptedAtUtc.HasValue) BulkBaselineGuard.Utc(ApprovalAcceptedAtUtc.Value);
            if (target != null && target.Suffix != Suffix) throw new SerializationException("Journal target suffix mismatch.");
            if (Status != BulkBaselineModuleStatus.Skipped && target == null) throw new SerializationException("Journal target is missing.");
            if (Attempts < 0 || Attempts > 1000 || (Attempts == 0) != !AttemptId.HasValue || (Attempts == 0) != !StartedAtUtc.HasValue || AttemptId == Guid.Empty || OperationId == Guid.Empty)
                throw new SerializationException("Invalid journal attempt.");
            if ((Status == BulkBaselineModuleStatus.Running || Status == BulkBaselineModuleStatus.Unknown || Status == BulkBaselineModuleStatus.Succeeded) && Attempts == 0) throw new SerializationException("Missing dispatch intent.");
            if (EverSucceeded && (Attempts == 0 || target == null)) throw new SerializationException("Invalid sticky success marker.");
            if (Status == BulkBaselineModuleStatus.Succeeded && !EverSucceeded) throw new SerializationException("Success must be verified.");
            foreach (var utc in new[] { StartedAtUtc, CompletedAtUtc, SuccessObservedAtUtc, VerifiedAtUtc }) if (utc.HasValue) BulkBaselineGuard.Utc(utc.Value);
            if (StartedAtUtc.HasValue && CompletedAtUtc.HasValue && CompletedAtUtc < StartedAtUtc) throw new SerializationException("Invalid journal timestamps.");
        }
    }

    [DataContract]
    public sealed class BulkBaselineJobRecord
    {
        [DataMember(Order = 0, IsRequired = true)] public Guid JobId { get; private set; }
        [DataMember(Order = 1, IsRequired = true)] public long Revision { get; private set; }
        [DataMember(Order = 2, IsRequired = true)] public Guid SourcePlanId { get; private set; }
        [DataMember(Order = 3, IsRequired = true)] public string SourcePlanFingerprint { get; private set; }
        [DataMember(Order = 4, IsRequired = true)] public string InputFingerprint { get; private set; }
        [DataMember(Order = 5, IsRequired = true)] public BaselineType Type { get; private set; }
        [DataMember(Order = 6, IsRequired = true)] public ExecutionPolicy Policy { get; private set; }
        [DataMember(Order = 7, IsRequired = true)] public BaselineMetadataSource Source { get; private set; }
        [DataMember(Order = 8, IsRequired = true)] public DoorsVersion Target { get; private set; }
        [DataMember(Order = 9, IsRequired = true)] public DoorsConnectionMode ConnectionMode { get; private set; }
        [DataMember(Order = 10, IsRequired = true)] private long timeoutTicks;
        [DataMember(Order = 11, IsRequired = true)] private long maxPlanAgeTicks;
        [DataMember(Order = 12, IsRequired = true)] private long maxObservationAgeTicks;
        [DataMember(Order = 13, IsRequired = true)] private List<BulkBaselineJobModule> modules;
        [DataMember(Order = 14, IsRequired = true)] public BulkBaselineModuleStatus Status { get; private set; }
        [DataMember(Order = 15, IsRequired = true)] public bool CancellationRequested { get; private set; }
        [DataMember(Order = 16, IsRequired = true)] public DateTime CreatedAtUtc { get; private set; }
        [DataMember(Order = 17, IsRequired = true)] public DateTime UpdatedAtUtc { get; private set; }
        [DataMember(Order = 18, IsRequired = true)] private long elapsedTicks;
        [DataMember(Order = 19, EmitDefaultValue = false)] public BaselineJobAuditContext AuditContext { get; private set; }
        [DataMember(Order = 20, EmitDefaultValue = false)] public string JobSuffix { get; private set; }
        [DataMember(Order = 21, EmitDefaultValue = false)] public string JobAnnotation { get; private set; }
        [DataMember(Order = 22, EmitDefaultValue = false)] public DateTime? ExecutionStartedAtUtc { get; private set; }
        [DataMember(Order = 23, EmitDefaultValue = false)] public DateTime? ExecutionEndedAtUtc { get; private set; }
        [DataMember(Order = 24, EmitDefaultValue = false)] public BaselineAuditAction LastAuditAction { get; private set; }
        [DataMember(Order = 25, EmitDefaultValue = false)] public BaselineApprovedPlan LastApprovedPlan { get; private set; }
        public IReadOnlyList<BulkBaselineJobModule> Modules => modules.AsReadOnly();
        public TimeSpan Timeout => TimeSpan.FromTicks(timeoutTicks);
        public TimeSpan MaxPlanAge => TimeSpan.FromTicks(maxPlanAgeTicks);
        public TimeSpan MaxObservationAge => TimeSpan.FromTicks(maxObservationAgeTicks);
        public TimeSpan Elapsed => TimeSpan.FromTicks(elapsedTicks);
        public bool IsTransactional => false;
        public bool AutomaticRetryAllowed => false;
        internal BulkBaselineJobRecord(BulkBaselineDryRunPlan plan, BaselineMetadataSource source, DateTime now)
            : this(plan, source, now, new BaselineJobAuditContextProvider().Capture(plan, source)) { }
        internal BulkBaselineJobRecord(BulkBaselineDryRunPlan plan, BaselineMetadataSource source, DateTime now, BaselineJobAuditContext context)
        {
            JobId = Guid.NewGuid(); SourcePlanId = plan.PlanId; SourcePlanFingerprint = plan.Fingerprint;
            Type = plan.Request.Baseline.Type; Policy = plan.Request.ExecutionPolicy; Source = source; Target = plan.Request.Target; ConnectionMode = plan.Request.ConnectionMode;
            timeoutTicks = plan.Request.Timeout.Ticks; maxPlanAgeTicks = plan.Request.MaxPlanAge.Ticks; maxObservationAgeTicks = plan.Request.MaxObservationAge.Ticks;
            modules = plan.Items.Select(i => new BulkBaselineJobModule(i)).ToList(); Status = BulkBaselineModuleStatus.Pending; CreatedAtUtc = UpdatedAtUtc = now;
            AuditContext = context; JobSuffix = plan.Request.Baseline.Suffix; JobAnnotation = plan.Request.Baseline.Annotation; LastAuditAction = BaselineAuditAction.Created;
            InputFingerprint = BaselineJobGuard.InputHash(this); Validate();
        }
        internal BulkBaselineJobRecord With(BulkBaselineModuleStatus status, DateTime now, TimeSpan elapsed, int index = -1, BulkBaselineJobModule item = null, bool? cancelled = null,
            BaselineAuditAction action = BaselineAuditAction.Checkpoint, BaselineApprovedPlan approval = null)
        {
            var copy = (BulkBaselineJobRecord)MemberwiseClone(); copy.Revision = checked(Revision + 1); copy.Status = status; copy.UpdatedAtUtc = now;
            copy.elapsedTicks = elapsed.Ticks; copy.modules = new List<BulkBaselineJobModule>(modules);
            if (index >= 0) copy.modules[index] = item ?? throw new ArgumentNullException(nameof(item));
            if (cancelled.HasValue) copy.CancellationRequested = cancelled.Value;
            copy.LastAuditAction = action;
            if (action == BaselineAuditAction.RunStarted) { copy.ExecutionStartedAtUtc = ExecutionStartedAtUtc ?? now; copy.ExecutionEndedAtUtc = null; }
            if (action == BaselineAuditAction.RunCompleted) copy.ExecutionEndedAtUtc = now;
            if (approval != null) copy.LastApprovedPlan = approval;
            copy.Validate(); return copy;
        }
        internal BulkBaselineJobRecord Replay(long revision, BulkBaselineModuleStatus status, DateTime updatedUtc, TimeSpan elapsed, bool cancelled,
            BaselineAuditAction action, DateTime? started, DateTime? ended, BaselineApprovedPlan approval, IEnumerable<KeyValuePair<int, BulkBaselineJobModule>> changes)
        {
            if (revision != Revision + 1) throw new SerializationException("Audit revision discontinuity.");
            var copy = (BulkBaselineJobRecord)MemberwiseClone(); copy.Revision = revision; copy.Status = status; copy.UpdatedAtUtc = updatedUtc; copy.elapsedTicks = elapsed.Ticks;
            copy.CancellationRequested = cancelled; copy.LastAuditAction = action; copy.ExecutionStartedAtUtc = started; copy.ExecutionEndedAtUtc = ended;
            copy.LastApprovedPlan = approval ?? LastApprovedPlan; copy.modules = new List<BulkBaselineJobModule>(modules);
            foreach (var change in changes)
            {
                if (change.Key < 0 || change.Key >= modules.Count || change.Value == null || !change.Value.Module.Matches(modules[change.Key].Module)) throw new SerializationException("Foreign audit delta.");
                copy.modules[change.Key] = change.Value;
            }
            return copy;
        }
        internal BulkBaselinePreflightRequest Request(BulkBaselineJobModule item) => new BulkBaselinePreflightRequest(new BulkBaselineRequest(
            new[] { new BaselineModuleSelection(item.Module) }, Type, suffix: item.Suffix, annotation: item.Annotation), Policy, MaxPlanAge, MaxObservationAge, Target, ConnectionMode, Timeout);
        internal void Validate()
        {
            if (JobId == Guid.Empty || SourcePlanId == Guid.Empty || Revision < 0 || elapsedTicks < 0) throw new SerializationException("Invalid journal identity/revision.");
            BulkBaselineGuard.Utc(CreatedAtUtc); BulkBaselineGuard.Utc(UpdatedAtUtc); if (UpdatedAtUtc < CreatedAtUtc) throw new SerializationException("Invalid journal clock.");
            BulkBaselineGuard.Defined(Type); BulkBaselineGuard.Defined(Policy); BulkBaselineGuard.Defined(Source); BulkBaselineGuard.Defined(Status); DoorsVersions.Format(Target); BulkBaselineGuard.Defined(ConnectionMode);
            BaselineJobGuard.HashOrEmpty(SourcePlanFingerprint); BaselineJobGuard.HashOrEmpty(InputFingerprint);
            if (SourcePlanFingerprint.Length != 64 || InputFingerprint.Length != 64 || modules == null || modules.Count == 0 || modules.Count > 10000) throw new SerializationException("Incomplete journal.");
            var ids = new HashSet<string>(StringComparer.Ordinal); var paths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in modules)
            {
                if (item == null) throw new SerializationException("Null journal module."); item.Validate();
                if (!ids.Add(item.ModuleId) || !paths.Add(item.CanonicalFullPath) || item.RepositoryId != modules[0].RepositoryId) throw new SerializationException("Duplicate or mixed-repository journal module.");
                var request = Request(item);
            }
            if (InputFingerprint != BaselineJobGuard.InputHash(this)) throw new SerializationException("Journal input fingerprint mismatch.");
            BulkBaselineGuard.Defined(LastAuditAction); if (ExecutionStartedAtUtc.HasValue) BulkBaselineGuard.Utc(ExecutionStartedAtUtc.Value); if (ExecutionEndedAtUtc.HasValue) BulkBaselineGuard.Utc(ExecutionEndedAtUtc.Value);
            if (LastApprovedPlan != null && LastApprovedPlan.JobId != JobId) throw new SerializationException("Foreign job approval.");
        }
    }

    public sealed class BaselineJobVerification
    {
        public BaselineJobVerification(BaselineModuleReference module, BaselineVersion expectedVersion, string annotation, string databaseIdentity,
            string userId, BaselineMetadataSource source, BaselineJobVerificationStatus status, DateTime observedAtUtc, string evidence)
        {
            Module = module ?? throw new ArgumentNullException(nameof(module)); ExpectedVersion = expectedVersion ?? throw new ArgumentNullException(nameof(expectedVersion));
            Annotation = BulkBaselineGuard.Annotation(annotation); DatabaseIdentity = databaseIdentity ?? ""; UserId = userId ?? "";
            BulkBaselineGuard.Defined(source); BulkBaselineGuard.Defined(status); Source = source; Status = status; ObservedAtUtc = BulkBaselineGuard.Utc(observedAtUtc);
            Evidence = BulkBaselineGuard.Text(evidence, nameof(evidence), 4096);
        }
        public BaselineModuleReference Module { get; }
        public BaselineVersion ExpectedVersion { get; }
        public string Annotation { get; }
        public string DatabaseIdentity { get; }
        public string UserId { get; }
        public BaselineMetadataSource Source { get; }
        public BaselineJobVerificationStatus Status { get; }
        public DateTime ObservedAtUtc { get; }
        public string Evidence { get; }
    }
    public interface IBaselineJobStateVerifier
    {
        // Read-only. AbsentAuthoritatively requires visibility, exact-target and quiescent-session evidence,
        // not just a false baselineExists. Exceptions/unknown evidence NEVER authorize retry.
        Task<BaselineJobVerification> VerifyAsync(BulkBaselineJobRecord job, BulkBaselineJobModule module, CancellationToken cancellationToken);
    }
    public sealed class BaselineJobRetryReview
    {
        internal BaselineJobRetryReview(BulkBaselineJobRecord job, BulkBaselineJobModule module, BaselineJobVerification verification, BulkBaselineDryRunPlan plan)
        { Job = job; Module = module; Verification = verification; FreshPlan = plan; }
        public BulkBaselineJobRecord Job { get; }
        public BulkBaselineJobModule Module { get; }
        public BaselineJobVerification Verification { get; }
        public BulkBaselineDryRunPlan FreshPlan { get; }
        public bool CanUndo => false;
    }
    public interface IBulkBaselineRetryConfirmation
    { Task<bool> ConfirmAsync(BaselineJobRetryReview review, CancellationToken cancellationToken); }
    public sealed class RejectBulkBaselineRetryConfirmation : IBulkBaselineRetryConfirmation
    { public Task<bool> ConfirmAsync(BaselineJobRetryReview review, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(false); } }
    public interface IBulkBaselineJobJournal
    { IBulkBaselineJobJournalLease Acquire(Guid jobId); }
    public interface IBulkBaselineJobJournalLease : IDisposable
    {
        BulkBaselineJobRecord Current { get; }
        void Save(BulkBaselineJobRecord record);
    }
    public interface IBulkBaselineJobRunner
    {
        Task<BulkBaselineJobRecord> CreateAsync(BulkBaselineDryRunPlan plan, CancellationToken cancellationToken = default(CancellationToken));
        Task<BulkBaselineJobRecord> OpenAsync(Guid jobId, CancellationToken cancellationToken = default(CancellationToken));
        Task<BulkBaselineJobRecord> RunAsync(Guid jobId, IProgress<BulkBaselineJobProgress> progress = null, CancellationToken cancellationToken = default(CancellationToken));
        Task<BulkBaselineJobRecord> RetryAsync(Guid jobId, string moduleId, IProgress<BulkBaselineJobProgress> progress = null, CancellationToken cancellationToken = default(CancellationToken));
    }
    public sealed class BulkBaselineJobProgress
    {
        internal BulkBaselineJobProgress(BulkBaselineJobRecord job, BulkBaselineJobModule current, TimeSpan elapsed)
        { Job = job; CurrentModule = current; Elapsed = elapsed; }
        public BulkBaselineJobRecord Job { get; }
        public Guid JobId => Job.JobId;
        public BulkBaselineJobModule CurrentModule { get; }
        public TimeSpan Elapsed { get; }
        public int Total => Job.Modules.Count;
        public int Succeeded => Job.Modules.Count(m => m.Status == BulkBaselineModuleStatus.Succeeded);
        public int Settled => Job.Modules.Count(m => m.Status == BulkBaselineModuleStatus.Succeeded || m.Status == BulkBaselineModuleStatus.Skipped || m.Status == BulkBaselineModuleStatus.Failed || m.Status == BulkBaselineModuleStatus.NeedsReconciliation || m.Status == BulkBaselineModuleStatus.Unknown);
    }
    internal static class BaselineJobGuard
    {
        internal static void Text(string value, int max) { if (value == null || value.Length > max || value.Any(char.IsControl)) throw new SerializationException("Invalid journal text."); RequirementBaseline.ValidateUnicode(value); }
        internal static void HashOrEmpty(string value) { if (value == null || (value.Length != 0 && (value.Length != 64 || value.Any(c => !Uri.IsHexDigit(c))))) throw new SerializationException("Invalid journal hash."); }
        internal static string InputHash(BulkBaselineJobRecord job)
        {
            using (var sha = SHA256.Create()) using (var stream = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write)) using (var w = new BinaryWriter(stream, new UTF8Encoding(false, true), true))
            {
                w.Write("BulkBaselineJob-v1"); w.Write(job.JobId.ToString("N")); w.Write(job.SourcePlanId.ToString("N")); w.Write(job.SourcePlanFingerprint);
                w.Write((int)job.Type); w.Write((int)job.Policy); w.Write((int)job.Source); w.Write((int)job.Target); w.Write((int)job.ConnectionMode);
                w.Write(job.Timeout.Ticks); w.Write(job.MaxPlanAge.Ticks); w.Write(job.MaxObservationAge.Ticks); w.Write(job.Modules.Count);
                foreach (var m in job.Modules)
                {
                    foreach (string field in new[] { m.RepositoryId, m.ModuleId, m.CanonicalFullPath, m.Suffix, m.Annotation, m.DatabaseIdentity, m.UserId, m.OriginalPrecondition }) w.Write(field);
                    foreach (var v in new[] { m.CurrentVersion, m.ExpectedVersion }) { w.Write(v != null); if (v != null) { w.Write(v.Major); w.Write(v.Minor); w.Write(v.Suffix); } }
                }
                if (job.AuditContext != null)
                { w.Write("AuditContext-v1"); foreach (var value in new[] { job.AuditContext.Operator, job.AuditContext.DatabaseIdentifier, job.AuditContext.EnvironmentIdentifier, job.AuditContext.ClientVersion, job.AuditContext.ClientVersionEvidence, job.JobSuffix, job.JobAnnotation }) w.Write(value ?? ""); }
                w.Flush(); stream.FlushFinalBlock(); return BitConverter.ToString(sha.Hash).Replace("-", "");
            }
        }
        internal static IntegrationException Failure(string reason) => new IntegrationException(IntegrationErrorCode.BulkBaselineJobFailed, reason);
    }
}
