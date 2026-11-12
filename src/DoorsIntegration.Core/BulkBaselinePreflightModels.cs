using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DoorsIntegration.Core
{
    public enum ExecutionPolicy { StopOnFirstError, ContinueOnError }
    public enum BulkBaselineItemStatus { Ready, Blocked, Skipped }
    public enum BaselineEditState { Closed, ReadOnly, ExclusiveCurrentSession, ExclusiveOtherSession, SharedCurrentSession, SharedOtherSession, Unknown }
    public enum BaselineLockState { Unlocked, OwnedByCurrentSession, OwnedByOtherSession, Unknown }
    public enum BaselineUnsavedState { None, CurrentSession, OtherSession, Unknown }
    public enum BaselineTargetAvailability { Free, Exists, Unknown }
    public enum BaselinePreflightReason
    {
        None, NativeStateUnavailable, MissingModule, UnknownModule, NotFormalModule, ReadDenied, CreateDenied, ModifyDenied,
        PermissionUnknown, OtherSessionLock, SharedEdit, LockUnknown, ExclusiveUnavailable, UnsavedCurrentSession,
        UnsavedOtherSession, UnsavedUnknown, SessionUnknown, ContextMismatch, RevisionUnavailable, InvalidInput,
        MetadataUnavailable, VersionUnverified, CollisionExists, CollisionUnknown, InvalidEvidence, ChangedDuringRead,
        PreconditionChanged, PlanExpired, ObservationExpired, StoppedAfterError, OperationUncertain, AdapterFailure
    }
    public sealed class BulkBaselinePreflightRequest
    {
        public BulkBaselinePreflightRequest(BulkBaselineRequest baseline, ExecutionPolicy executionPolicy = ExecutionPolicy.ContinueOnError,
            TimeSpan? maxPlanAge = null, TimeSpan? maxObservationAge = null, DoorsVersion target = DoorsVersion.V97,
            DoorsConnectionMode connectionMode = DoorsConnectionMode.AttachExisting, TimeSpan? timeout = null)
        {
            Baseline = baseline ?? throw new ArgumentNullException(nameof(baseline)); BulkBaselineGuard.Defined(executionPolicy);
            DoorsVersions.Format(target); BulkBaselineGuard.Defined(connectionMode);
            MaxPlanAge = maxPlanAge ?? TimeSpan.FromMinutes(5); MaxObservationAge = maxObservationAge ?? TimeSpan.FromMinutes(2);
            Timeout = timeout ?? TimeSpan.FromMinutes(2);
            if (MaxPlanAge <= TimeSpan.Zero || MaxPlanAge > TimeSpan.FromHours(1) || MaxObservationAge <= TimeSpan.Zero ||
                MaxObservationAge > TimeSpan.FromMinutes(10) || Timeout <= TimeSpan.Zero || Timeout > TimeSpan.FromHours(24)) throw new ArgumentOutOfRangeException(nameof(maxPlanAge));
            ExecutionPolicy = executionPolicy; Target = target; ConnectionMode = connectionMode;
        }
        public BulkBaselineRequest Baseline { get; }
        public ExecutionPolicy ExecutionPolicy { get; }
        public TimeSpan MaxPlanAge { get; }
        public TimeSpan MaxObservationAge { get; }
        public TimeSpan Timeout { get; }
        public DoorsVersion Target { get; }
        public DoorsConnectionMode ConnectionMode { get; }
        public bool SkipBlockedModules => true;
        public bool IsDryRun => true;
    }
    public sealed class BaselinePreflightProbe
    {
        public BaselinePreflightProbe(BulkBaselinePreflightRequest request, BaselineModuleSelection selection, BaselineVersion expectedVersion = null)
        {
            Request = request ?? throw new ArgumentNullException(nameof(request)); Selection = selection ?? throw new ArgumentNullException(nameof(selection));
            if (!request.Baseline.Modules.Contains(selection)) throw new ArgumentException("Selection must belong to this exact request.");
            Suffix = selection.SuffixOverride ?? request.Baseline.Suffix; Annotation = selection.AnnotationOverride ?? request.Baseline.Annotation;
            if (expectedVersion != null && expectedVersion.Suffix != Suffix) throw new ArgumentException("Expected version suffix mismatch.");
            ExpectedVersion = expectedVersion;
        }
        public BulkBaselinePreflightRequest Request { get; }
        public BaselineModuleSelection Selection { get; }
        public BaselineModuleReference Module => Selection.Module;
        public BaselineType Type => Request.Baseline.Type;
        public string Suffix { get; }
        public string Annotation { get; }
        public BaselineVersion ExpectedVersion { get; }
    }
    // Read-only observations only. Revision tokens must cover actual module content/history, ACL and lock changes.
    public sealed class BaselinePreflightFacts
    {
        public BaselinePreflightFacts(BaselineModuleReference module, BaselineCheckState exists, DoorsItemKind type,
            BaselineCheckState readAccess, BaselineCheckState createAccess, BaselineCheckState modifyAccess,
            BaselineEditState editState, BaselineLockState lockState, BaselineCheckState exclusiveAvailability,
            BaselineUnsavedState unsavedChanges, BaselineCheckState sessionValid, string databaseIdentity,
            string currentUserId, string sessionId, string connectionEpoch, string contentRevision, string permissionRevision,
            string lockRevision, BaselineCheckState revisionsReliable, BaselineCheckState suffixValid, BaselineCheckState annotationValid,
            BaselineTargetAvailability targetAvailability, BaselineMetadataSource source, string evidence,
            string lockOwnerSessionId = null, string lockOwnerUserId = null, string dirtyOwnerSessionId = null)
        {
            Module = module ?? throw new ArgumentNullException(nameof(module));
            BulkBaselineGuard.Defined(exists); BulkBaselineGuard.Defined(type); BulkBaselineGuard.Defined(readAccess);
            BulkBaselineGuard.Defined(createAccess); BulkBaselineGuard.Defined(modifyAccess); BulkBaselineGuard.Defined(editState);
            BulkBaselineGuard.Defined(lockState); BulkBaselineGuard.Defined(exclusiveAvailability); BulkBaselineGuard.Defined(unsavedChanges);
            BulkBaselineGuard.Defined(sessionValid); BulkBaselineGuard.Defined(revisionsReliable); BulkBaselineGuard.Defined(suffixValid);
            BulkBaselineGuard.Defined(annotationValid); BulkBaselineGuard.Defined(targetAvailability); BulkBaselineGuard.Defined(source);
            Exists = exists; Type = type; ReadAccess = readAccess; CreateAccess = createAccess; ModifyAccess = modifyAccess;
            EditState = editState; LockState = lockState; ExclusiveAvailability = exclusiveAvailability; UnsavedChanges = unsavedChanges;
            SessionValid = sessionValid; DatabaseIdentity = Token(databaseIdentity); CurrentUserId = Token(currentUserId); SessionId = Token(sessionId);
            ConnectionEpoch = Token(connectionEpoch); ContentRevision = Token(contentRevision); PermissionRevision = Token(permissionRevision); LockRevision = Token(lockRevision);
            RevisionsReliable = revisionsReliable; SuffixValid = suffixValid; AnnotationValid = annotationValid; TargetAvailability = targetAvailability;
            Source = source; Evidence = BulkBaselineGuard.Text(evidence, nameof(evidence), 4096);
            LockOwnerSessionId = Token(lockOwnerSessionId); LockOwnerUserId = Token(lockOwnerUserId); DirtyOwnerSessionId = Token(dirtyOwnerSessionId);
        }
        private static string Token(string value) => value == null ? null : BulkBaselineGuard.Text(value, nameof(value), 4096);
        public BaselineModuleReference Module { get; }
        public BaselineCheckState Exists { get; }
        public DoorsItemKind Type { get; }
        public BaselineCheckState ReadAccess { get; }
        public BaselineCheckState CreateAccess { get; }
        public BaselineCheckState ModifyAccess { get; }
        public BaselineEditState EditState { get; }
        public BaselineLockState LockState { get; }
        public BaselineCheckState ExclusiveAvailability { get; }
        public BaselineUnsavedState UnsavedChanges { get; }
        public BaselineCheckState SessionValid { get; }
        public string DatabaseIdentity { get; }
        public string CurrentUserId { get; }
        public string SessionId { get; }
        public string ConnectionEpoch { get; }
        public string ContentRevision { get; }
        public string PermissionRevision { get; }
        public string LockRevision { get; }
        public BaselineCheckState RevisionsReliable { get; }
        public BaselineCheckState SuffixValid { get; }
        public BaselineCheckState AnnotationValid { get; }
        public BaselineTargetAvailability TargetAvailability { get; }
        public BaselineMetadataSource Source { get; }
        public string Evidence { get; }
        public string LockOwnerSessionId { get; }
        public string LockOwnerUserId { get; }
        public string DirtyOwnerSessionId { get; }
    }
    public sealed class BaselinePreflightObservation
    {
        public BaselinePreflightObservation(BaselinePreflightProbe probe, BaselinePreflightFacts facts, DateTime observedAtUtc)
        { Probe = probe ?? throw new ArgumentNullException(nameof(probe)); Facts = facts ?? throw new ArgumentNullException(nameof(facts)); ObservedAtUtc = BulkBaselineGuard.Utc(observedAtUtc); }
        public BaselinePreflightProbe Probe { get; }
        public BaselinePreflightFacts Facts { get; }
        public DateTime ObservedAtUtc { get; }
    }
    public sealed class BaselinePrecondition
    {
        internal BaselinePrecondition(string fingerprint, string requestFingerprint, BaselinePreflightFacts facts, string metadataFingerprint, DateTime observedAtUtc)
        { Fingerprint = fingerprint; RequestFingerprint = requestFingerprint; Facts = facts; MetadataFingerprint = metadataFingerprint; ObservedAtUtc = observedAtUtc; }
        public int SchemaVersion => 1;
        public string Fingerprint { get; }
        public string RequestFingerprint { get; }
        public BaselinePreflightFacts Facts { get; }
        public string MetadataFingerprint { get; }
        public DateTime ObservedAtUtc { get; }
        public bool IsWriteAuthorization => false;
        // A batch approval may be narrowed to one module. The request-list hash differs,
        // but exact state/context, target availability and metadata must remain identical.
        public bool HasSameObservedState(BaselinePrecondition other) => other != null && Facts.Module.Matches(other.Facts.Module) &&
            Facts.TargetAvailability == other.Facts.TargetAvailability && MetadataFingerprint == other.MetadataFingerprint &&
            BaselinePreflightFingerprint.State(Facts) == BaselinePreflightFingerprint.State(other.Facts);
    }
    public sealed class BulkBaselineDryRunItem
    {
        internal BulkBaselineDryRunItem(BaselinePreflightProbe probe, BulkBaselineItemStatus status, BaselinePreflightReason code,
            string reason, string resolution, string warning, BaselineVersion current, BaselineVersion expected, BaselinePrecondition precondition,
            bool unsafeToContinue = false)
        { Probe = probe; Status = status; ReasonCode = code; Reason = reason; RecommendedResolution = resolution; Warning = warning; CurrentVersion = current; ExpectedVersion = expected; Precondition = precondition; UnsafeToContinue = unsafeToContinue; }
        public BaselinePreflightProbe Probe { get; }
        public BaselineModuleReference Module => Probe.Module;
        public string ModuleId => Module.ModuleId;
        public string FullPath => Module.CanonicalFullPath;
        public BaselineType Type => Probe.Type;
        public string Suffix => Probe.Suffix;
        public string Annotation => Probe.Annotation;
        public BaselineVersion CurrentVersion { get; }
        public BaselineVersion ExpectedVersion { get; }
        public BulkBaselineItemStatus Status { get; }
        public string CurrentVersionLabel => CurrentVersion?.ToString() ?? (Status == BulkBaselineItemStatus.Ready ? "None (first baseline)" : "Unavailable");
        public string ExpectedVersionLabel => ExpectedVersion?.ToString() ?? "Unavailable";
        public BaselinePreflightReason ReasonCode { get; }
        public string Reason { get; }
        public string Warning { get; }
        public string RecommendedResolution { get; }
        public BaselinePrecondition Precondition { get; }
        public bool WillBeSkipped => Status != BulkBaselineItemStatus.Ready;
        public bool UnsafeToContinue { get; }
    }
    public sealed class BulkBaselineDryRunPlan
    {
        internal BulkBaselineDryRunPlan(Guid id, BulkBaselinePreflightRequest request, IEnumerable<BulkBaselineDryRunItem> items,
            DateTime createdAtUtc, string requestFingerprint)
        {
            PlanId = id; Request = request; Items = BulkBaselineGuard.List(items, 10000); CreatedAtUtc = createdAtUtc; RequestFingerprint = requestFingerprint;
            Fingerprint = BaselinePreflightFingerprint.Plan(this);
        }
        public Guid PlanId { get; }
        public BulkBaselinePreflightRequest Request { get; }
        public IReadOnlyList<BulkBaselineDryRunItem> Items { get; }
        public DateTime CreatedAtUtc { get; }
        public string RequestFingerprint { get; }
        public string Fingerprint { get; }
        public int ReadyCount => Items.Count(i => i.Status == BulkBaselineItemStatus.Ready);
        public int BlockedCount => Items.Count(i => i.Status == BulkBaselineItemStatus.Blocked);
        public int SkippedCount => Items.Count(i => i.Status == BulkBaselineItemStatus.Skipped);
        public string Summary => "Ready=" + ReadyCount + "; Blocked=" + BlockedCount + "; Skipped=" + SkippedCount +
            (ReadyCount == 0 ? ". Uygun modül yok; hiçbir işlem başlatılamaz. Önerilen çözümleri inceleyin." : ". Bloklanan/atlanan modüller işleme alınmaz; yazma onayı verilmiş değildir.");
        public bool CanExecute => false;
    }
    public sealed class BaselineRevalidationItem
    {
        internal BaselineRevalidationItem(BulkBaselineDryRunItem original, BulkBaselineDryRunItem fresh, bool matches)
        { Original = original; Fresh = fresh; PreconditionsMatch = matches; }
        public BulkBaselineDryRunItem Original { get; }
        public BulkBaselineDryRunItem Fresh { get; }
        public bool PreconditionsMatch { get; }
        public bool EligibleForPreview => Original.Status == BulkBaselineItemStatus.Ready && Fresh.Status == BulkBaselineItemStatus.Ready && PreconditionsMatch;
    }
    public sealed class BaselinePreflightRevalidation
    {
        internal BaselinePreflightRevalidation(BulkBaselineDryRunPlan plan, IEnumerable<BaselineRevalidationItem> items, DateTime utc)
        { Plan = plan; Items = BulkBaselineGuard.List(items, 10000); RevalidatedAtUtc = utc; }
        public BulkBaselineDryRunPlan Plan { get; }
        public IReadOnlyList<BaselineRevalidationItem> Items { get; }
        public DateTime RevalidatedAtUtc { get; }
        public int EligibleCount => Items.Count(i => i.EligibleForPreview);
        public string Summary => "Dry Run · Eligible=" + EligibleCount + "; Skipped=" + (Items.Count - EligibleCount) +
            "; NotRun · WriteAttempted=False" + (EligibleCount == 0 ? ". Tüm modüller engelli veya atlandı; önerilen çözümleri uygulayıp yeni plan oluşturun." : ". Ön kontrol yeniden okundu; hiçbir baseline veya kilit oluşturulmadı.");
        public bool CanExecute => false;
        public bool WriteAttempted => false;
        public bool LockChanged => false;
        public bool ModuleContentChanged => false;
    }
    public interface IBaselinePreflightClock { DateTime UtcNow { get; } }
    public sealed class BaselinePreflightClock : IBaselinePreflightClock { public DateTime UtcNow => DateTime.UtcNow; }
    public interface IBaselinePreflightStateReader
    {
        Task<BaselinePreflightObservation> ReadAsync(BaselinePreflightProbe probe, CancellationToken cancellationToken);
    }
    public interface IBulkBaselinePreflightService
    {
        Task<BulkBaselineDryRunPlan> CreatePlanAsync(BulkBaselinePreflightRequest request, CancellationToken cancellationToken = default(CancellationToken));
        Task<BaselinePreflightRevalidation> RevalidateAsync(BulkBaselineDryRunPlan plan, CancellationToken cancellationToken = default(CancellationToken));
        Task<BaselinePreflightRevalidation> DryRunAsync(BulkBaselinePreflightRequest request, CancellationToken cancellationToken = default(CancellationToken));
    }
}
