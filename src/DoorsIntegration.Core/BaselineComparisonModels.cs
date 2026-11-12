using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DoorsIntegration.Core
{
    public enum BaselineChangeStatus { Added, Deleted, Modified, Unchanged, Indeterminate }
    public enum BaselineAttributeStatus { Added, Deleted, Modified, Unchanged, NormalizationOnly, Unavailable }
    public enum BaselineTextNormalization { Exact, UnicodeNfcAndLineEndings }

    public sealed class BaselineComparisonRequest
    {
        public BaselineComparisonRequest(string modulePath, RequirementBaseline before, RequirementBaseline after,
            IEnumerable<string> customAttributes = null, BaselineTextNormalization normalization = BaselineTextNormalization.UnicodeNfcAndLineEndings,
            int maxObjects = 100000, long maxSpoolBytes = 1073741824, TimeSpan? timeout = null,
            DoorsVersion target = DoorsVersion.V97, DoorsConnectionMode connectionMode = DoorsConnectionMode.AttachExisting)
        {
            Before = before ?? throw new ArgumentNullException(nameof(before));
            After = after ?? throw new ArgumentNullException(nameof(after));
            if (Version(before) == Version(after)) throw new ArgumentException("Select two distinct baselines.");
            if (!Enum.IsDefined(typeof(BaselineTextNormalization), normalization) || maxSpoolBytes < 65536 || maxSpoolBytes > 8589934592)
                throw new ArgumentOutOfRangeException(nameof(maxSpoolBytes));
            // This reader port has no write methods. Attribute DXL is always disabled; tombstones are metadata only.
            BeforeRead = new RequirementReadRequest(modulePath, customAttributes, before, DeletedObjectMode.MetadataOnly,
                false, true, maxObjects, timeout: timeout, target: target, connectionMode: connectionMode);
            AfterRead = new RequirementReadRequest(modulePath, BeforeRead.CustomAttributes, after, DeletedObjectMode.MetadataOnly,
                false, true, maxObjects, timeout: timeout, target: target, connectionMode: connectionMode);
            Normalization = normalization; MaxSpoolBytes = maxSpoolBytes;
        }
        public RequirementBaseline Before { get; }
        public RequirementBaseline After { get; }
        public string ModulePath => BeforeRead.ModulePath;
        public RequirementReadRequest BeforeRead { get; }
        public RequirementReadRequest AfterRead { get; }
        public BaselineTextNormalization Normalization { get; }
        public long MaxSpoolBytes { get; }
        // Structured major/minor/suffix remain available: the suffix is not parsed from a display label.
        public static string Version(RequirementBaseline baseline) => baseline.Major.ToString(CultureInfo.InvariantCulture) + "." +
            baseline.Minor.ToString(CultureInfo.InvariantCulture) + "[" + baseline.Suffix + "]";
    }

    public sealed class BaselineObjectIdentity
    {
        public BaselineObjectIdentity(string qualifiedModuleId, int absoluteNumber)
        {
            if (string.IsNullOrWhiteSpace(qualifiedModuleId) || qualifiedModuleId.Length > 4096 || qualifiedModuleId.Any(char.IsControl) || absoluteNumber < 1)
                throw new ArgumentException("A native qualified module ID and positive Absolute Number are required.");
            QualifiedModuleId = qualifiedModuleId; AbsoluteNumber = absoluteNumber;
        }
        public string QualifiedModuleId { get; }
        public int AbsoluteNumber { get; }
        public string Key => QualifiedModuleId.Length.ToString(CultureInfo.InvariantCulture) + ":" + QualifiedModuleId + ":" + AbsoluteNumber.ToString(CultureInfo.InvariantCulture);
    }

    public sealed class BaselineAttributeChange
    {
        internal BaselineAttributeChange(string name, RequirementValue before, RequirementValue after, BaselineAttributeStatus status,
            bool plainChanged, bool richChanged, bool typeChanged, bool normalizationOnly, bool conclusive)
        {
            Name = name; Before = before; After = after; Status = status; PlainTextChanged = plainChanged;
            RichTextChanged = richChanged; TypeChanged = typeChanged; HasNormalizationDifference = normalizationOnly; IsConclusive = conclusive;
        }
        public string Name { get; }
        public RequirementValue Before { get; }
        public RequirementValue After { get; }
        public BaselineAttributeStatus Status { get; }
        public bool PlainTextChanged { get; }
        // Exact opaque representation comparison. Does not claim semantic or visual RTF equivalence.
        public bool RichTextChanged { get; }
        public bool TypeChanged { get; }
        public bool HasNormalizationDifference { get; }
        public bool IsConclusive { get; }
    }

    public sealed class BaselineObjectChange
    {
        internal BaselineObjectChange(BaselineObjectIdentity identity, RequirementRecord before, RequirementRecord after,
            BaselineChangeStatus status, bool hierarchyChanged, bool conclusive, IEnumerable<BaselineAttributeChange> attributes)
        {
            Identity = identity; Before = before; After = after; Status = status; HierarchyChanged = hierarchyChanged;
            IsConclusive = conclusive; Attributes = new ReadOnlyCollection<BaselineAttributeChange>(attributes.ToList());
        }
        public BaselineObjectIdentity Identity { get; }
        public RequirementRecord Before { get; }
        public RequirementRecord After { get; }
        public BaselineChangeStatus Status { get; }
        // Parent absolute identity and hierarchy level; display numbering and order are not semantic identity.
        public bool HierarchyChanged { get; }
        public bool IsConclusive { get; }
        public IReadOnlyList<BaselineAttributeChange> Attributes { get; }
    }

    public sealed class BaselineChangeEvent
    {
        internal BaselineChangeEvent(string eventId, IBaselineComparisonResult result, BaselineObjectChange change)
        { EventId = eventId; ComparisonId = result.ComparisonId; Request = result.Request; Change = change; }
        public int SchemaVersion => 1;
        public string EventId { get; }
        public Guid ComparisonId { get; }
        public BaselineComparisonRequest Request { get; }
        public BaselineObjectChange Change { get; }
        public bool RequiresReview => !Change.IsConclusive;
        // Impact candidates, not proof that downstream requirements changed or coverage is complete.
        public bool IsImpactCandidate => Change.Status != BaselineChangeStatus.Unchanged;
    }

    public sealed class BaselineComparisonSummary
    {
        internal BaselineComparisonSummary(IDictionary<BaselineChangeStatus, int> counts, bool conclusive, int normalizationDifferences)
        { Counts = new ReadOnlyDictionary<BaselineChangeStatus, int>(new Dictionary<BaselineChangeStatus, int>(counts)); IsConclusive = conclusive; NormalizationDifferenceCount = normalizationDifferences; }
        public IReadOnlyDictionary<BaselineChangeStatus, int> Counts { get; }
        public bool IsConclusive { get; }
        public int NormalizationDifferenceCount { get; }
    }

    public interface IBaselineComparisonResult : IDisposable
    {
        Guid ComparisonId { get; }
        DateTime ComparedAtUtc { get; }
        BaselineComparisonRequest Request { get; }
        string QualifiedModuleId { get; }
        BaselineComparisonSummary Summary { get; }
        IEnumerable<BaselineObjectChange> ReadChanges(CancellationToken cancellationToken = default(CancellationToken));
        IEnumerable<BaselineChangeEvent> ReadEvents(CancellationToken cancellationToken = default(CancellationToken));
    }
    public interface IBaselineComparisonService
    {
        Task<IBaselineComparisonResult> CompareAsync(BaselineComparisonRequest request, CancellationToken cancellationToken = default(CancellationToken));
    }

    internal static class BaselineComparisonGuard
    {
        internal static IntegrationException Failure(string message, Exception cause = null) =>
            new IntegrationException(IntegrationErrorCode.BaselineComparisonFailed, message, cause);
        internal static string Normalize(string value, BaselineTextNormalization policy) => policy == BaselineTextNormalization.Exact ? value :
            value?.Replace("\r\n", "\n").Replace("\r", "\n").Normalize(NormalizationForm.FormC);
        internal static bool Available(RequirementValue value) => value != null && !string.IsNullOrWhiteSpace(value.TypeName) && value.PlainStatus == RequirementValueStatus.Readable &&
            (value.RichTextStatus == RequirementValueStatus.Readable || value.RichTextStatus == RequirementValueStatus.NotApplicable);
    }
}
