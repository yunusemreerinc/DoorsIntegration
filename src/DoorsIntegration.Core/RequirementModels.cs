using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DoorsIntegration.Core
{
    public enum DeletedObjectMode { Exclude, MetadataOnly, IncludeContent }
    public enum RequirementValueStatus { Readable, DeniedOrNotFound, AccessDenied, ReadFailed, NotRequested, DxlEvaluationDisabled, NotObjectAttribute, NotApplicable, TooLarge }
    public enum RequirementModuleStatus { Available, DeniedOrNotFound, AccessDenied, NotFormalModule, ModuleDeleted, BaselineUnavailable, AlreadyOpenForWrite, NotLoadedByPolicy, ReadFailed }
    public enum RequirementModuleOwnership { NotLoaded, BorrowedReadOnly, OwnedReadOnly }
    public enum RequirementCleanupStatus { NotOwned, Closed, Failed }
    public enum RequirementParentStatus { Root, Resolved, Unavailable }

    public sealed class RequirementBaseline
    {
        public RequirementBaseline(int major, int minor, string suffix = "")
        {
            if (major < 0 || minor < 0 || major > 1000000 || minor > 1000000 || suffix == null || suffix.Length > 128 ||
                suffix.Any(char.IsControl)) throw Invalid();
            ValidateUnicode(suffix);
            Major = major; Minor = minor; Suffix = suffix;
        }
        public int Major { get; }
        public int Minor { get; }
        public string Suffix { get; }
        internal static void ValidateUnicode(string value)
        {
            for (int index = 0; index < value.Length; index++)
            {
                char c = value[index];
                if (c == '\u2028' || c == '\u2029') throw Invalid();
                if (char.IsHighSurrogate(c))
                {
                    if (++index >= value.Length || !char.IsLowSurrogate(value[index])) throw Invalid();
                }
                else if (char.IsLowSurrogate(c)) throw Invalid();
            }
        }
        internal static IntegrationException Invalid() => new IntegrationException(IntegrationErrorCode.InvalidConfiguration,
            "Gereksinim okuma isteği, sürüm veya aktarım sınırları geçersiz.");
    }

    public sealed class RequirementReadRequest
    {
        public RequirementReadRequest(string modulePath, IEnumerable<string> customAttributes = null,
            RequirementBaseline baseline = null, DeletedObjectMode deletedObjects = DeletedObjectMode.Exclude,
            bool allowAttributeDxl = false, bool openIfNeeded = true, int maxObjects = 1000000,
            int maxFieldCharacters = 4194304, int maxObjectCharacters = 16777216, long maxFileBytes = 268435456,
            TimeSpan? timeout = null, DoorsConnectionMode connectionMode = DoorsConnectionMode.AttachExisting,
            DoorsVersion target = DoorsVersion.V97)
        {
            ModulePath = DoorsItemPath.Normalize(modulePath);
            if (ModulePath == "/" || !Enum.IsDefined(typeof(DeletedObjectMode), deletedObjects) || maxObjects < 1 || maxObjects > 1000000 ||
                maxFieldCharacters < 1 || maxFieldCharacters > 16777216 || maxObjectCharacters < 1024 || maxObjectCharacters > 67108864 ||
                maxFileBytes < 65536 || maxFileBytes > 1073741824 ||
                (connectionMode != DoorsConnectionMode.Activate && connectionMode != DoorsConnectionMode.AttachExisting)) throw RequirementBaseline.Invalid();
            var attributes = new List<string>();
            var unique = new HashSet<string>(StringComparer.Ordinal);
            if (customAttributes != null) foreach (string attribute in customAttributes)
            {
                if (attributes.Count >= 64 || string.IsNullOrWhiteSpace(attribute) || attribute.Length > 256 || attribute.Any(char.IsControl) ||
                    !unique.Add(attribute) || attribute == "Object Heading" || attribute == "Object Text" ||
                    attribute == "Object Identifier" || attribute == "Object Number" || attribute == "Absolute Number") throw RequirementBaseline.Invalid();
                RequirementBaseline.ValidateUnicode(attribute);
                attributes.Add(attribute);
            }
            Timeout = timeout ?? TimeSpan.FromMinutes(2);
            if (Timeout < TimeSpan.FromMilliseconds(1) || Timeout > TimeSpan.FromHours(24)) throw RequirementBaseline.Invalid();
            DoorsVersions.Format(target);
            CustomAttributes = new ReadOnlyCollection<string>(attributes);
            Baseline = baseline; DeletedObjects = deletedObjects; AllowAttributeDxl = allowAttributeDxl; OpenIfNeeded = openIfNeeded;
            MaxObjects = maxObjects; MaxFieldCharacters = maxFieldCharacters; MaxObjectCharacters = maxObjectCharacters;
            MaxFileBytes = maxFileBytes; ConnectionMode = connectionMode; Target = target;
        }
        public string ModulePath { get; }
        public IReadOnlyList<string> CustomAttributes { get; }
        public RequirementBaseline Baseline { get; }
        public DeletedObjectMode DeletedObjects { get; }
        public bool AllowAttributeDxl { get; }
        public bool OpenIfNeeded { get; }
        public int MaxObjects { get; }
        public int MaxFieldCharacters { get; }
        public int MaxObjectCharacters { get; }
        public long MaxFileBytes { get; }
        public TimeSpan Timeout { get; }
        public DoorsConnectionMode ConnectionMode { get; }
        public DoorsVersion Target { get; }
    }

    public sealed class RequirementValue
    {
        public RequirementValue(string name, string typeName, RequirementValueStatus plainStatus, string plainText,
            RequirementValueStatus richTextStatus, string richTextWithOle)
        {
            Name = name; TypeName = typeName; PlainStatus = plainStatus; PlainText = plainText;
            RichTextStatus = richTextStatus; RichTextWithOle = richTextWithOle;
        }
        public string Name { get; }
        public string TypeName { get; }
        public RequirementValueStatus PlainStatus { get; }
        public string PlainText { get; }
        public RequirementValueStatus RichTextStatus { get; }
        // Opaque DOORS RTF/RTF fragment, including embedded OLE data. Never auto-activate/render it.
        public string RichTextWithOle { get; }
        public string RichTextFormat => RichTextStatus == RequirementValueStatus.Readable ? "DoorsRtfWithOle" : "None";
    }

    public sealed class RequirementRecord
    {
        public RequirementRecord(int sourceOrder, int absoluteNumber, string objectIdentifier, string objectNumber,
            RequirementValueStatus numberStatus, int hierarchyLevel, int? parentAbsoluteNumber, RequirementParentStatus parentStatus,
            bool isDeleted, IEnumerable<RequirementValue> values)
        {
            SourceOrder = sourceOrder; AbsoluteNumber = absoluteNumber; ObjectIdentifier = objectIdentifier;
            ObjectNumber = objectNumber; NumberStatus = numberStatus; HierarchyLevel = hierarchyLevel;
            ParentAbsoluteNumber = parentAbsoluteNumber; ParentStatus = parentStatus; IsDeleted = isDeleted;
            Values = new ReadOnlyDictionary<string, RequirementValue>(values.ToDictionary(value => value.Name, StringComparer.Ordinal));
        }
        public int SourceOrder { get; }
        public int AbsoluteNumber { get; }
        public string ObjectIdentifier { get; }
        public string ObjectNumber { get; }
        public RequirementValueStatus NumberStatus { get; }
        public int HierarchyLevel { get; }
        public int? ParentAbsoluteNumber { get; }
        public RequirementParentStatus ParentStatus { get; }
        public bool IsDeleted { get; }
        public IReadOnlyDictionary<string, RequirementValue> Values { get; }
        public RequirementValue Heading => Values["Object Heading"];
        public RequirementValue Text => Values["Object Text"];
    }

    public sealed class RequirementReadSummary
    {
        public RequirementReadSummary(Guid correlationId, RequirementReadRequest request, RequirementModuleStatus moduleStatus,
            RequirementModuleOwnership ownership, RequirementCleanupStatus cleanupStatus, int objects, int visited, int denied,
            int deletedExcluded, int unavailable, long fieldProblems, bool truncated, bool enumerationFailed, string qualifiedModuleId = null)
        {
            CorrelationId = correlationId; Request = request; ModuleStatus = moduleStatus; Ownership = ownership;
            CleanupStatus = cleanupStatus; ObjectCount = objects; VisitedObjectCount = visited; DeniedObjectCount = denied;
            DeletedExcludedCount = deletedExcluded; UnavailableObjectCount = unavailable; FieldProblemCount = fieldProblems;
            IsTruncated = truncated; EnumerationFailed = enumerationFailed; QualifiedModuleId = qualifiedModuleId;
        }
        public Guid CorrelationId { get; }
        public RequirementReadRequest Request { get; }
        public RequirementModuleStatus ModuleStatus { get; }
        public RequirementModuleOwnership Ownership { get; }
        public RequirementCleanupStatus CleanupStatus { get; }
        public int ObjectCount { get; }
        public int VisitedObjectCount { get; }
        public int DeniedObjectCount { get; }
        public int DeletedExcludedCount { get; }
        public int UnavailableObjectCount { get; }
        public long FieldProblemCount { get; }
        public bool IsTruncated { get; }
        public bool EnumerationFailed { get; }
        // Native qualifiedUniqueID, captured in the same read as the objects. Null for legacy protocol v1.
        public string QualifiedModuleId { get; }
        public string Visibility => "ReadableObjectsOnly";
        public bool IsCompleteForReadableObjects => ModuleStatus == RequirementModuleStatus.Available && !IsTruncated &&
            !EnumerationFailed && DeniedObjectCount == 0 && UnavailableObjectCount == 0 && FieldProblemCount == 0 && CleanupStatus != RequirementCleanupStatus.Failed;
    }

    public interface IRequirementSnapshot : IDisposable
    {
        RequirementReadSummary Summary { get; }
        IEnumerable<RequirementRecord> ReadObjects();
        System.IO.Stream OpenRawStream();
    }
    public interface IRequirementReader
    {
        Task<IRequirementSnapshot> ReadAsync(RequirementReadRequest request, CancellationToken cancellationToken = default(CancellationToken));
    }
    public interface IRequirementService
    {
        Task<IRequirementSnapshot> ReadAsync(RequirementReadRequest request, CancellationToken cancellationToken = default(CancellationToken));
        Task<RequirementModuleSchema> GetAttributeSchemaAsync(RequirementWriteTarget target, CancellationToken cancellationToken = default(CancellationToken));
        Task<RequirementWritePreview> PreviewWriteAsync(RequirementWriteRequest request, CancellationToken cancellationToken = default(CancellationToken));
        Task<RequirementWriteResult> ApplyWriteAsync(RequirementWritePreview preview, CancellationToken cancellationToken = default(CancellationToken));
    }
}
