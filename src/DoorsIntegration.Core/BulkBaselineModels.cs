using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;

namespace DoorsIntegration.Core
{
    public enum BaselineType { Major, Minor }
    // BaselineSet is intentionally not an accepted value in Task 19.
    public enum BaselineMode { Independent }
    public enum BaselineMetadataStatus { Available, AccessDenied, NotFound, Unavailable }
    public enum BaselineCheckState { Passed, Failed, Unknown }
    public enum BaselinePlanItemStatus { Planned, Blocked, Unavailable }
    public enum BaselinePlanStatus { Ready, Blocked, Unavailable }
    public enum BaselinePreflightStatus { Passed, Blocked, Unavailable }
    public enum BaselineJobStatus { Planned, PreflightPassed, PreflightBlocked, Previewed }
    public enum BaselineExecutionStatus { NotRun }

    public sealed class BaselineVersion : IEquatable<BaselineVersion>
    {
        public BaselineVersion(int major, int minor, string suffix = "")
        {
            // Reuse the existing structured version validation without parsing display labels.
            var version = new RequirementBaseline(major, minor, suffix);
            Major = version.Major; Minor = version.Minor; Suffix = version.Suffix;
        }
        public int Major { get; }
        public int Minor { get; }
        public string Suffix { get; }
        public bool Equals(BaselineVersion other) => other != null && Major == other.Major && Minor == other.Minor && Suffix == other.Suffix;
        public override bool Equals(object obj) => Equals(obj as BaselineVersion);
        public override int GetHashCode() => Major.GetHashCode() ^ Minor.GetHashCode() ^ Suffix.GetHashCode();
        public override string ToString() => Major.ToString(CultureInfo.InvariantCulture) + "." + Minor.ToString(CultureInfo.InvariantCulture) + "[" + Suffix + "]";
    }

    public sealed class BaselineModuleReference
    {
        public BaselineModuleReference(string repositoryId, string moduleId, string canonicalFullPath)
        {
            RepositoryId = BulkBaselineGuard.Text(repositoryId, nameof(repositoryId), 1024);
            ModuleId = BulkBaselineGuard.Text(moduleId, nameof(moduleId), 4096);
            CanonicalFullPath = DoorsItemPath.Normalize(canonicalFullPath);
            if (CanonicalFullPath == "/" || CanonicalFullPath != canonicalFullPath)
                throw new ArgumentException("Supply the canonical full formal module path.", nameof(canonicalFullPath));
        }
        public string RepositoryId { get; }
        // Opaque, provider-observed stable DOORS identity; never derived from a display path.
        public string ModuleId { get; }
        public string CanonicalFullPath { get; }
        public bool Matches(BaselineModuleReference other) => other != null && RepositoryId == other.RepositoryId &&
            ModuleId == other.ModuleId && CanonicalFullPath == other.CanonicalFullPath;
    }

    public sealed class BaselineModuleSelection
    {
        public BaselineModuleSelection(BaselineModuleReference module, string suffixOverride = null, string annotationOverride = null)
        {
            Module = module ?? throw new ArgumentNullException(nameof(module));
            // null inherits the job value; an empty string is an explicit empty override.
            SuffixOverride = suffixOverride == null ? null : BulkBaselineGuard.Suffix(suffixOverride);
            AnnotationOverride = annotationOverride == null ? null : BulkBaselineGuard.Annotation(annotationOverride);
        }
        public BaselineModuleReference Module { get; }
        public string SuffixOverride { get; }
        public string AnnotationOverride { get; }
    }

    public sealed class BulkBaselineRequest
    {
        public BulkBaselineRequest(IEnumerable<BaselineModuleSelection> modules, BaselineType type = BaselineType.Minor,
            BaselineMode mode = BaselineMode.Independent, string suffix = "", string annotation = "")
        {
            if (!Enum.IsDefined(typeof(BaselineType), type) || mode != BaselineMode.Independent) throw new ArgumentOutOfRangeException(nameof(mode));
            Type = type; Mode = mode; Suffix = BulkBaselineGuard.Suffix(suffix); Annotation = BulkBaselineGuard.Annotation(annotation);
            var items = new List<BaselineModuleSelection>();
            var ids = new HashSet<string>(StringComparer.Ordinal); var paths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in modules ?? throw new ArgumentNullException(nameof(modules)))
            {
                if (item == null || items.Count == 10000) throw new ArgumentException("Select 1 to 10000 formal modules.", nameof(modules));
                if (items.Count > 0 && items[0].Module.RepositoryId != item.Module.RepositoryId)
                    throw new ArgumentException("A baseline job must be scoped to one repository.", nameof(modules));
                if (!ids.Add(item.Module.ModuleId) || !paths.Add(item.Module.CanonicalFullPath))
                    throw new ArgumentException("Duplicate stable module identity or canonical path.", nameof(modules));
                items.Add(item);
            }
            if (items.Count == 0) throw new ArgumentException("Select at least one formal module.", nameof(modules));
            Modules = new ReadOnlyCollection<BaselineModuleSelection>(items);
        }
        public IReadOnlyList<BaselineModuleSelection> Modules { get; }
        public BaselineType Type { get; }
        public BaselineMode Mode { get; }
        public string Suffix { get; }
        public string Annotation { get; }
        public bool IsReadOnly => true;
    }

    public sealed class BaselineModuleMetadata
    {
        public BaselineModuleMetadata(BaselineModuleReference module, BaselineMetadataStatus status, BaselineVersion currentVersion,
            BaselineCheckState formalModule, BaselineCheckState createPermission, BaselineCheckState lockAvailable,
            IEnumerable<BaselineVersion> existingVersions, bool versionListComplete, DateTime observedAtUtc, string observationId,
            string failureReason = null)
        {
            Module = module ?? throw new ArgumentNullException(nameof(module));
            BulkBaselineGuard.Defined(status); BulkBaselineGuard.Defined(formalModule); BulkBaselineGuard.Defined(createPermission); BulkBaselineGuard.Defined(lockAvailable);
            ObservedAtUtc = BulkBaselineGuard.Utc(observedAtUtc); ObservationId = BulkBaselineGuard.Text(observationId, nameof(observationId), 256);
            Status = status; CurrentVersion = currentVersion; FormalModule = formalModule; CreatePermission = createPermission; LockAvailable = lockAvailable;
            ExistingVersions = BulkBaselineGuard.List(existingVersions ?? Array.Empty<BaselineVersion>(), 10000);
            VersionListComplete = versionListComplete; FailureReason = failureReason;
            if (status == BaselineMetadataStatus.Available && currentVersion == null) throw new ArgumentException("Available metadata requires a structured current version.");
            if (ExistingVersions.Distinct().Count() != ExistingVersions.Count) throw new ArgumentException("Duplicate baseline versions.");
        }
        public BaselineModuleReference Module { get; }
        public BaselineMetadataStatus Status { get; }
        public BaselineVersion CurrentVersion { get; }
        public BaselineCheckState FormalModule { get; }
        public BaselineCheckState CreatePermission { get; }
        // An observation only; no lock is acquired or retained by this feature.
        public BaselineCheckState LockAvailable { get; }
        public IReadOnlyList<BaselineVersion> ExistingVersions { get; }
        public bool VersionListComplete { get; }
        public DateTime ObservedAtUtc { get; }
        public string ObservationId { get; }
        public string FailureReason { get; }
        public static BaselineModuleMetadata Unavailable(BaselineModuleReference module, string reason, DateTime utc) =>
            new BaselineModuleMetadata(module, BaselineMetadataStatus.Unavailable, null, BaselineCheckState.Unknown,
                BaselineCheckState.Unknown, BaselineCheckState.Unknown, null, false, utc, Guid.NewGuid().ToString("N"), reason);
    }

    public sealed class BaselinePlanItem
    {
        internal BaselinePlanItem(Guid jobId, BaselineModuleSelection selection, BaselineModuleMetadata metadata,
            BaselineVersion expectedVersion, string suffix, string annotation, BaselinePlanItemStatus status, string failureReason)
        { JobId = jobId; Module = selection.Module; Metadata = metadata; ExpectedVersion = expectedVersion; Suffix = suffix; Annotation = annotation; Status = status; FailureReason = failureReason; }
        public Guid JobId { get; }
        public BaselineModuleReference Module { get; }
        public string ModuleId => Module.ModuleId;
        public BaselineModuleMetadata Metadata { get; }
        public BaselineVersion CurrentVersion => Metadata.CurrentVersion;
        // A proposal for review, not a server-reserved or created version.
        public BaselineVersion ExpectedVersion { get; }
        public string Suffix { get; }
        public string Annotation { get; }
        public BaselinePlanItemStatus Status { get; }
        public string FailureReason { get; }
    }

    public sealed class BaselinePlan
    {
        internal BaselinePlan(Guid jobId, BulkBaselineRequest request, IEnumerable<BaselinePlanItem> items, DateTime plannedAtUtc)
        { JobId = jobId; Request = request; Items = BulkBaselineGuard.List(items, 10000); PlannedAtUtc = BulkBaselineGuard.Utc(plannedAtUtc); }
        public Guid JobId { get; }
        public BulkBaselineRequest Request { get; }
        public IReadOnlyList<BaselinePlanItem> Items { get; }
        public DateTime PlannedAtUtc { get; }
        public bool IsReadyForPreflight => Items.All(item => item.Status == BaselinePlanItemStatus.Planned);
        public BaselinePlanStatus Status => Items.Any(item => item.Status == BaselinePlanItemStatus.Blocked) ? BaselinePlanStatus.Blocked :
            Items.Any(item => item.Status == BaselinePlanItemStatus.Unavailable) ? BaselinePlanStatus.Unavailable : BaselinePlanStatus.Ready;
        public string FailureReason => IsReadyForPreflight ? null : "One or more modules could not be planned; inspect item failure reasons.";
        public bool IsReadOnly => true;
    }

    public sealed class BaselinePreflightItemResult
    {
        internal BaselinePreflightItemResult(BaselinePlanItem item, BaselineModuleMetadata metadata, BaselinePreflightStatus status, string reason)
        { Item = item; Metadata = metadata; Status = status; FailureReason = reason; }
        public BaselinePlanItem Item { get; }
        public BaselineModuleMetadata Metadata { get; }
        public BaselinePreflightStatus Status { get; }
        public string FailureReason { get; }
    }

    public sealed class BaselinePreflightResult
    {
        internal BaselinePreflightResult(BaselinePlan plan, IEnumerable<BaselinePreflightItemResult> items, DateTime checkedAtUtc)
        { Plan = plan; Items = BulkBaselineGuard.List(items, 10000); CheckedAtUtc = BulkBaselineGuard.Utc(checkedAtUtc); }
        public Guid JobId => Plan.JobId;
        public BaselinePlan Plan { get; }
        public IReadOnlyList<BaselinePreflightItemResult> Items { get; }
        public DateTime CheckedAtUtc { get; }
        public bool Passed => Items.All(item => item.Status == BaselinePreflightStatus.Passed);
        public BaselinePreflightStatus Status => Items.Any(item => item.Status == BaselinePreflightStatus.Blocked) ? BaselinePreflightStatus.Blocked :
            Items.Any(item => item.Status == BaselinePreflightStatus.Unavailable) ? BaselinePreflightStatus.Unavailable : BaselinePreflightStatus.Passed;
        public string FailureReason => Passed ? null : "One or more module preflight checks did not pass; inspect item failure reasons.";
        // A successful read-only check is not write authorization.
        public bool CanExecute => false;
    }

    public sealed class BaselineModuleResult
    {
        internal BaselineModuleResult(BaselinePlanItem item, string reason, DateTime startedAtUtc, DateTime completedAtUtc)
        { Item = item; FailureReason = reason; StartedAtUtc = startedAtUtc; CompletedAtUtc = completedAtUtc; }
        public BaselinePlanItem Item { get; }
        public Guid JobId => Item.JobId;
        public string ModuleId => Item.ModuleId;
        public BaselineVersion CurrentVersion => Item.CurrentVersion;
        public BaselineVersion ExpectedVersion => Item.ExpectedVersion;
        public string Suffix => Item.Suffix;
        public string Annotation => Item.Annotation;
        public BaselineExecutionStatus Status => BaselineExecutionStatus.NotRun;
        public string FailureReason { get; }
        public DateTime StartedAtUtc { get; }
        public DateTime CompletedAtUtc { get; }
        public bool WriteAttempted => false;
        public string CreatedBaselineId => null;
    }

    public sealed class BaselineExecutionResult
    {
        internal BaselineExecutionResult(BaselinePlan plan, BaselinePreflightResult preflight, DateTime startedAtUtc, DateTime completedAtUtc)
        {
            if (plan == null || preflight == null || !ReferenceEquals(plan, preflight.Plan)) throw new ArgumentException("Preflight must belong to the exact plan.");
            StartedAtUtc = BulkBaselineGuard.Utc(startedAtUtc); CompletedAtUtc = BulkBaselineGuard.Utc(completedAtUtc);
            if (completedAtUtc < startedAtUtc) throw new ArgumentException("Completion precedes start.");
            Plan = plan; Preflight = preflight;
            Modules = BulkBaselineGuard.List(preflight.Items.Select(item => new BaselineModuleResult(item.Item,
                item.Status == BaselinePreflightStatus.Passed ? DisabledReason : item.FailureReason, startedAtUtc, completedAtUtc)), 10000);
        }
        public const string DisabledReason = "NOT_RUN: baseline creation is disabled in Task 19; this is a read-only execution preview.";
        public Guid JobId => Plan.JobId;
        public BaselinePlan Plan { get; }
        public BaselinePreflightResult Preflight { get; }
        public IReadOnlyList<BaselineModuleResult> Modules { get; }
        public BaselineExecutionStatus Status => BaselineExecutionStatus.NotRun;
        public string FailureReason => DisabledReason;
        public bool IsPreview => true;
        public bool WriteAttempted => false;
        public DateTime StartedAtUtc { get; }
        public DateTime CompletedAtUtc { get; }
        public static BaselineExecutionResult Preview(BaselinePlan plan, BaselinePreflightResult preflight, DateTime startedAtUtc, DateTime completedAtUtc) =>
            new BaselineExecutionResult(plan, preflight, startedAtUtc, completedAtUtc);
    }

    public sealed class BaselineJob
    {
        internal BaselineJob(BaselinePlan plan, BaselinePreflightResult preflight, BaselineExecutionResult execution,
            long revision, DateTime createdAtUtc, DateTime updatedAtUtc)
        {
            Plan = plan; Preflight = preflight; Execution = execution; Revision = revision; CreatedAtUtc = createdAtUtc; UpdatedAtUtc = updatedAtUtc;
            Status = execution != null ? BaselineJobStatus.Previewed : preflight == null ? BaselineJobStatus.Planned :
                preflight.Passed ? BaselineJobStatus.PreflightPassed : BaselineJobStatus.PreflightBlocked;
        }
        public Guid JobId => Plan.JobId;
        public BulkBaselineRequest Request => Plan.Request;
        public BaselinePlan Plan { get; }
        public BaselinePreflightResult Preflight { get; }
        public BaselineExecutionResult Execution { get; }
        public BaselineJobStatus Status { get; }
        public string FailureReason => Execution?.FailureReason ?? (Preflight != null && !Preflight.Passed ? "One or more module preflight checks did not pass." :
            !Plan.IsReadyForPreflight ? "One or more modules could not be planned." : null);
        public string Suffix => Request.Suffix;
        public string Annotation => Request.Annotation;
        public long Revision { get; }
        public DateTime CreatedAtUtc { get; }
        public DateTime UpdatedAtUtc { get; }
        public DateTime? ExecutionStartedAtUtc => Execution?.StartedAtUtc;
        public DateTime? ExecutionCompletedAtUtc => Execution?.CompletedAtUtc;
        public bool IsReadOnly => true;
        internal BaselineJob WithPreflight(BaselinePreflightResult preflight, DateTime utc) => new BaselineJob(Plan, preflight, null, checked(Revision + 1), CreatedAtUtc, utc);
        internal BaselineJob WithPreview(BaselineExecutionResult preview, DateTime utc) => new BaselineJob(Plan, Preflight, preview, checked(Revision + 1), CreatedAtUtc, utc);
    }

    internal static class BulkBaselineGuard
    {
        internal static string Text(string value, string name, int max)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > max || value.Any(char.IsControl)) throw new ArgumentException("Invalid " + name + ".", name);
            RequirementBaseline.ValidateUnicode(value); return value;
        }
        internal static string Suffix(string value) => new RequirementBaseline(0, 0, value).Suffix;
        internal static string Annotation(string value)
        {
            if (value == null || value.Length > 16384 || value.Any(c => char.IsControl(c) && c != '\r' && c != '\n' && c != '\t'))
                throw new ArgumentException("Invalid baseline annotation.", nameof(value));
            RequirementBaseline.ValidateUnicode(value); return value;
        }
        internal static DateTime Utc(DateTime utc)
        { if (utc.Kind != DateTimeKind.Utc) throw new ArgumentException("A UTC timestamp is required."); return utc; }
        internal static void Defined<T>(T value) where T : struct
        { if (!Enum.IsDefined(typeof(T), value)) throw new ArgumentOutOfRangeException(nameof(value)); }
        internal static IReadOnlyList<T> List<T>(IEnumerable<T> values, int max) where T : class
        {
            var list = new List<T>();
            foreach (var value in values) { if (value == null || list.Count == max) throw new ArgumentException("Invalid or oversized baseline collection."); list.Add(value); }
            return new ReadOnlyCollection<T>(list);
        }
        internal static IntegrationException Failure(string message) => new IntegrationException(IntegrationErrorCode.BulkBaselineFailed, message);
    }
}
