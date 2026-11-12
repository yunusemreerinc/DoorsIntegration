using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DoorsIntegration.Core
{
    public enum BaselineHistoryStatus { Available, ClosedByPolicy, AccessDenied, DeniedOrNotFound, NotFormalModule, ModuleDeleted, AlreadyOpenForWrite, IdentityMismatch, ReadFailed, ApiNotValidated }
    public enum BaselineDesignationStatus { Available, None, Unknown }
    public enum BaselineExistenceStatus { Exists, AbsentOrUnreadable, Unknown, NotRequested }
    public enum BaselineProposalStatus { Available, RequiresEnvironmentValidation, Unavailable, NotRequested }
    public enum BaselineMetadataSource { NativeDxl, Mock, NotRun }

    public sealed class BaselineHistoryRequest
    {
        public BaselineHistoryRequest(BaselineModuleReference module, bool openIfClosed = false, BaselineType? nextType = null,
            string suffix = "", BaselineVersion collisionTarget = null, int maxBaselines = 10000, long maxFileBytes = 16777216,
            TimeSpan? timeout = null, DoorsVersion target = DoorsVersion.V97, DoorsConnectionMode connectionMode = DoorsConnectionMode.AttachExisting)
        {
            Module = module ?? throw new ArgumentNullException(nameof(module));
            if (nextType.HasValue) BulkBaselineGuard.Defined(nextType.Value);
            Suffix = BulkBaselineGuard.Suffix(suffix); DoorsVersions.Format(target); BulkBaselineGuard.Defined(connectionMode);
            if (maxBaselines < 1 || maxBaselines > 100000 || maxFileBytes < 65536 || maxFileBytes > 268435456) throw new ArgumentOutOfRangeException(nameof(maxBaselines));
            Timeout = timeout ?? TimeSpan.FromMinutes(2);
            if (Timeout < TimeSpan.FromMilliseconds(1) || Timeout > TimeSpan.FromHours(24)) throw new ArgumentOutOfRangeException(nameof(timeout));
            OpenIfClosed = openIfClosed; NextType = nextType; CollisionTarget = collisionTarget; MaxBaselines = maxBaselines;
            MaxFileBytes = maxFileBytes; Target = target; ConnectionMode = connectionMode;
        }
        public BaselineModuleReference Module { get; }
        public bool OpenIfClosed { get; }
        public BaselineType? NextType { get; }
        public string Suffix { get; }
        public BaselineVersion CollisionTarget { get; }
        public int MaxBaselines { get; }
        public long MaxFileBytes { get; }
        public TimeSpan Timeout { get; }
        public DoorsVersion Target { get; }
        public DoorsConnectionMode ConnectionMode { get; }
        public bool IsReadOnly => true;
    }

    public sealed class BaselineIdentity
    {
        public BaselineIdentity(BaselineModuleReference module, BaselineVersion version, string nativeIdentity = null)
        { Module = module ?? throw new ArgumentNullException(nameof(module)); Version = version ?? throw new ArgumentNullException(nameof(version)); NativeIdentity = nativeIdentity == null ? null : BulkBaselineGuard.Text(nativeIdentity, nameof(nativeIdentity), 4096); }
        public BaselineModuleReference Module { get; }
        public BaselineVersion Version { get; }
        // Logical address, NOT an invented native GUID or proof of incarnation across delete/restore.
        public string Key => Part(Module.RepositoryId) + Part(Module.ModuleId) + Part(Version.Major.ToString(CultureInfo.InvariantCulture)) +
            Part(Version.Minor.ToString(CultureInfo.InvariantCulture)) + Part(Version.Suffix);
        public string NativeIdentity { get; }
        public bool NativeIdentityAvailable => NativeIdentity != null;
        private static string Part(string text) => text.Length.ToString(CultureInfo.InvariantCulture) + ":" + text;
    }
    public sealed class BaselineHistoryEntry
    {
        public BaselineHistoryEntry(BaselineIdentity identity, string annotation, string creator, DateTime? creationDateUtc,
            string creationDateRaw, BaselineExistenceStatus existence = BaselineExistenceStatus.Exists)
        {
            Identity = identity ?? throw new ArgumentNullException(nameof(identity)); Annotation = BulkBaselineGuard.Annotation(annotation);
            if (creator == null || creator.Length > 4096 || creationDateRaw == null || creationDateRaw.Length > 4096) throw new ArgumentException("Invalid baseline metadata field.");
            if (creationDateUtc.HasValue) BulkBaselineGuard.Utc(creationDateUtc.Value);
            if (existence != BaselineExistenceStatus.Exists && existence != BaselineExistenceStatus.AbsentOrUnreadable) throw new ArgumentOutOfRangeException(nameof(existence));
            Creator = creator; CreationDateUtc = creationDateUtc; CreationDateRaw = creationDateRaw; Existence = existence;
        }
        public BaselineIdentity Identity { get; }
        public string BaselineKey => Identity.Key;
        public int Major => Identity.Version.Major;
        public int Minor => Identity.Version.Minor;
        public string Suffix => Identity.Version.Suffix;
        public string Annotation { get; }
        public string Creator { get; }
        public DateTime? CreationDateUtc { get; }
        public string CreationDateRaw { get; }
        public bool CreationDateAvailable => CreationDateUtc.HasValue;
        public BaselineExistenceStatus Existence { get; }
    }
    public sealed class BaselineDesignation
    {
        public BaselineDesignation(BaselineDesignationStatus status, BaselineVersion version = null,
            BaselineExistenceStatus existence = BaselineExistenceStatus.Unknown)
        {
            BulkBaselineGuard.Defined(status); BulkBaselineGuard.Defined(existence);
            if ((status == BaselineDesignationStatus.Available) != (version != null)) throw new ArgumentException("Designation status/version mismatch.");
            if (status != BaselineDesignationStatus.Available && existence != BaselineExistenceStatus.Unknown) throw new ArgumentException("Unobserved designation cannot prove existence.");
            if (status == BaselineDesignationStatus.Available && existence == BaselineExistenceStatus.NotRequested) throw new ArgumentException("A designation requires an explicit existence observation or Unknown.");
            Status = status; Version = version; Existence = existence;
        }
        public BaselineDesignationStatus Status { get; }
        public BaselineVersion Version { get; }
        // False baselineExists is deliberately NOT a confirmed deletion.
        public BaselineExistenceStatus Existence { get; }
        public bool MayBeDeletedOrInaccessible => Existence == BaselineExistenceStatus.AbsentOrUnreadable;
    }
    public sealed class BaselineVersionProposal
    {
        public BaselineVersionProposal(BaselineType type, string suffix, BaselineProposalStatus status, BaselineVersion expectedVersion,
            BaselineMetadataSource source, string evidence, string failureReason = null, Guid observationId = default(Guid), BaselineModuleReference module = null)
        {
            BulkBaselineGuard.Defined(type); BulkBaselineGuard.Defined(status); BulkBaselineGuard.Defined(source);
            Suffix = BulkBaselineGuard.Suffix(suffix);
            if ((status == BaselineProposalStatus.Available) != (expectedVersion != null) || (expectedVersion != null && expectedVersion.Suffix != suffix))
                throw new ArgumentException("Expected version/status/suffix mismatch.");
            Evidence = BulkBaselineGuard.Text(evidence, nameof(evidence), 4096); Type = type; Status = status; ExpectedVersion = expectedVersion; Source = source; FailureReason = failureReason;
            if (status == BaselineProposalStatus.Available && (observationId == Guid.Empty || module == null)) throw new ArgumentException("A version proposal requires exact module/observation binding.");
            ObservationId = observationId; Module = module;
        }
        public BaselineType Type { get; }
        public string Suffix { get; }
        public BaselineProposalStatus Status { get; }
        public BaselineVersion ExpectedVersion { get; }
        public BaselineMetadataSource Source { get; }
        public string Evidence { get; }
        public string FailureReason { get; }
        public Guid ObservationId { get; }
        public BaselineModuleReference Module { get; }
        public bool IsReserved => false;
    }
    public sealed class BaselineCollisionResult
    {
        public BaselineCollisionResult(BaselineVersion target, BaselineExistenceStatus status, string explanation)
        {
            BulkBaselineGuard.Defined(status);
            if ((target == null) != (status == BaselineExistenceStatus.NotRequested)) throw new ArgumentException("Collision target/status mismatch.");
            Target = target; Status = status; Explanation = explanation ?? throw new ArgumentNullException(nameof(explanation));
        }
        public BaselineVersion Target { get; }
        public BaselineExistenceStatus Status { get; }
        public string Explanation { get; }
        public bool HasConfirmedCollision => Status == BaselineExistenceStatus.Exists;
        public bool AuthorizesCreation => false;
    }
    public sealed class BaselineMetadataSnapshot
    {
        public BaselineMetadataSnapshot(Guid observationId, BaselineHistoryRequest request, BaselineHistoryStatus status,
            IEnumerable<BaselineHistoryEntry> history, BaselineDesignation latestExisting, BaselineDesignation latestNumberingVersion,
            bool historyComplete, RequirementModuleOwnership ownership, RequirementCleanupStatus cleanup, DateTime observedAtUtc,
            BaselineMetadataSource source, string apiEvidence, BaselineCollisionResult collision, string failureReason = null)
        {
            if (observationId == Guid.Empty) throw new ArgumentException("Observation ID is required.");
            Request = request ?? throw new ArgumentNullException(nameof(request)); BulkBaselineGuard.Defined(status); BulkBaselineGuard.Defined(ownership);
            BulkBaselineGuard.Defined(cleanup); BulkBaselineGuard.Defined(source); ObservedAtUtc = BulkBaselineGuard.Utc(observedAtUtc);
            History = BulkBaselineGuard.List(history ?? throw new ArgumentNullException(nameof(history)), request.MaxBaselines);
            if (History.Any(e => !request.Module.Matches(e.Identity.Module)) || History.Select(e => e.BaselineKey).Distinct(StringComparer.Ordinal).Count() != History.Count)
                throw new ArgumentException("Duplicate or foreign baseline identity.");
            ObservationId = observationId; Status = status; LatestExisting = latestExisting ?? throw new ArgumentNullException(nameof(latestExisting));
            LatestNumberingVersion = latestNumberingVersion ?? throw new ArgumentNullException(nameof(latestNumberingVersion)); HistoryComplete = historyComplete;
            Ownership = ownership; Cleanup = cleanup; Source = source; ApiEvidence = BulkBaselineGuard.Text(apiEvidence, nameof(apiEvidence), 4096);
            Collision = collision ?? throw new ArgumentNullException(nameof(collision)); FailureReason = failureReason;
            if (latestExisting.Status == BaselineDesignationStatus.Available && latestExisting.Existence != BaselineExistenceStatus.Exists)
                throw new ArgumentException("Latest existing baseline must have positive existence evidence.");
            if ((status == BaselineHistoryStatus.Available && (ownership == RequirementModuleOwnership.NotLoaded || cleanup == RequirementCleanupStatus.Failed || source == BaselineMetadataSource.NotRun)) ||
                (ownership == RequirementModuleOwnership.OwnedReadOnly && cleanup != RequirementCleanupStatus.Closed && cleanup != RequirementCleanupStatus.Failed) ||
                (ownership != RequirementModuleOwnership.OwnedReadOnly && cleanup != RequirementCleanupStatus.NotOwned)) throw new ArgumentException("Invalid read-only ownership/cleanup evidence.");
            if (status != BaselineHistoryStatus.Available && (historyComplete || History.Count != 0 || latestExisting.Status != BaselineDesignationStatus.Unknown || latestNumberingVersion.Status != BaselineDesignationStatus.Unknown))
                throw new ArgumentException("Unavailable history cannot publish successful metadata.");
            if (historyComplete && ((latestExisting.Status == BaselineDesignationStatus.Available &&
                !History.Any(e => e.Identity.Version.Equals(latestExisting.Version) && e.Existence == BaselineExistenceStatus.Exists)) ||
                (latestExisting.Status == BaselineDesignationStatus.None && History.Any(e => e.Existence == BaselineExistenceStatus.Exists)) ||
                (latestNumberingVersion.Status == BaselineDesignationStatus.None && (History.Count > 0 || latestExisting.Status != BaselineDesignationStatus.None))))
                throw new ArgumentException("Latest baseline designations contradict complete history.");
        }
        public Guid ObservationId { get; }
        public BaselineHistoryRequest Request { get; }
        public BaselineModuleReference Module => Request.Module;
        public BaselineHistoryStatus Status { get; }
        public IReadOnlyList<BaselineHistoryEntry> History { get; }
        public BaselineDesignation LatestExisting { get; }
        public BaselineDesignation LatestNumberingVersion { get; }
        public bool HasNoBaselineHistory => Status == BaselineHistoryStatus.Available && HistoryComplete && History.Count == 0 &&
            LatestExisting.Status == BaselineDesignationStatus.None && LatestNumberingVersion.Status == BaselineDesignationStatus.None;
        public bool HistoryComplete { get; }
        public RequirementModuleOwnership Ownership { get; }
        public RequirementCleanupStatus Cleanup { get; }
        public DateTime ObservedAtUtc { get; }
        public BaselineMetadataSource Source { get; }
        public string ApiEvidence { get; }
        public BaselineCollisionResult Collision { get; }
        public string FailureReason { get; }
        public bool IsReadOnly => true;
        public bool RuntimeCompatibilityValidated => false;
        public static BaselineMetadataSnapshot Unavailable(BaselineHistoryRequest request, BaselineHistoryStatus status, string reason, string evidence = "Native validation NOT_RUN") =>
            new BaselineMetadataSnapshot(Guid.NewGuid(), request, status, Array.Empty<BaselineHistoryEntry>(), new BaselineDesignation(BaselineDesignationStatus.Unknown),
                new BaselineDesignation(BaselineDesignationStatus.Unknown), false, RequirementModuleOwnership.NotLoaded, RequirementCleanupStatus.NotOwned,
                DateTime.UtcNow, BaselineMetadataSource.NotRun, evidence, new BaselineCollisionResult(request.CollisionTarget,
                    request.CollisionTarget == null ? BaselineExistenceStatus.NotRequested : BaselineExistenceStatus.Unknown, reason), reason);
    }
    public sealed class BaselineMetadataResult
    {
        public BaselineMetadataResult(BaselineMetadataSnapshot snapshot, BaselineVersionProposal proposal, BaselineCollisionResult collision)
        { Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot)); Proposal = proposal; Collision = collision ?? throw new ArgumentNullException(nameof(collision)); }
        public BaselineMetadataSnapshot Snapshot { get; }
        public BaselineVersionProposal Proposal { get; }
        public BaselineCollisionResult Collision { get; }
        public BaselineVersion ExpectedVersion => Proposal?.ExpectedVersion;
        public bool CanCreateBaseline => false;
    }
    public interface IBaselineHistoryAdapter
    {
        Task<BaselineMetadataSnapshot> ReadAsync(BaselineHistoryRequest request, CancellationToken cancellationToken);
        Task<BaselineCollisionResult> CheckCollisionAsync(BaselineHistoryRequest request, BaselineVersion target, CancellationToken cancellationToken);
    }
    public interface IBaselineNextVersionAdapter
    {
        // A native implementation must resolve nextMajor/nextMinor in the exact target context; never derive it from LatestExisting.
        Task<BaselineVersionProposal> ProposeAsync(BaselineMetadataSnapshot snapshot, BaselineType type, string suffix, CancellationToken cancellationToken);
    }
    public interface IBaselineMetadataService
    {
        Task<BaselineMetadataResult> ReadAsync(BaselineHistoryRequest request, CancellationToken cancellationToken = default(CancellationToken));
    }
}
