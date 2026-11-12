using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DoorsIntegration.Core
{
    public enum LinkDirection { Incoming, Outgoing, Both }
    public enum LinkRecordKind { Link, IncomingSourceReference }
    public enum LinkResolution { Resolved, EndpointUnresolved, IncomingReferenceOnly, SourceReferenceOnly, ReadFailed }
    public enum LinkModuleStatus { Available, DeniedOrNotFound, AccessDenied, NotLinkModule, ModuleDeleted, ReadFailed }
    public enum LinkModuleResolution { Verified, FilterOnly }
    public enum LinkVersionKind { Unknown, Current, Baseline }
    public enum LinkEndpointResolution { Resolved, DeniedOrNotFound, AccessDenied, ModuleDeleted, NotLoadedByPolicy, ModuleLimit, AlreadyOpenForWrite, UnresolvedObject, DeletedObject, ReadFailed }
    public enum SurrogateStatus { NotConfigured, Matched, NotMatched, Unavailable }
    public enum TraceabilityFormat { Json, Csv }

    public sealed class SurrogateMapping
    {
        public SurrogateMapping(string markerAttribute, string markerValue, string referenceAttribute = null)
        {
            ValidateName(markerAttribute);
            if (string.IsNullOrEmpty(markerValue) || markerValue.Length > 256 || markerValue.Any(char.IsControl)) throw LinkReadRequest.Invalid();
            RequirementBaseline.ValidateUnicode(markerValue);
            if (referenceAttribute != null) ValidateName(referenceAttribute);
            MarkerAttribute = markerAttribute; MarkerValue = markerValue; ReferenceAttribute = referenceAttribute;
        }
        private static void ValidateName(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl)) throw LinkReadRequest.Invalid();
            RequirementBaseline.ValidateUnicode(value);
        }
        public string MarkerAttribute { get; }
        public string MarkerValue { get; }
        public string ReferenceAttribute { get; }
    }

    public sealed class LinkReadRequest
    {
        // One explicit link module per request prevents ambiguous same-name/wildcard matches.
        public LinkReadRequest(string modulePath, string linkModulePath, LinkDirection direction = LinkDirection.Both,
            RequirementBaseline baseline = null, bool openIfNeeded = true, bool resolveEndpoints = true,
            bool includeDeletedObjects = false, SurrogateMapping surrogateMapping = null, int maxObjects = 1000000,
            int maxLinks = 100000, int maxLinkedModules = 64, long maxFileBytes = 268435456,
            TimeSpan? timeout = null, DoorsConnectionMode connectionMode = DoorsConnectionMode.AttachExisting, DoorsVersion target = DoorsVersion.V97)
        {
            ModulePath = DoorsItemPath.Normalize(modulePath); LinkModulePath = DoorsItemPath.Normalize(linkModulePath);
            Timeout = timeout ?? TimeSpan.FromMinutes(2);
            if (ModulePath == "/" || LinkModulePath == "/" || ModulePath == LinkModulePath ||
                ModulePath.Contains("*") || LinkModulePath.Contains("*") || !Enum.IsDefined(typeof(LinkDirection), direction) ||
                maxObjects < 1 || maxObjects > 1000000 || maxLinks < 1 || maxLinks > 1000000 || maxLinkedModules < 0 || maxLinkedModules > 256 ||
                maxFileBytes < 65536 || maxFileBytes > 1073741824 || Timeout < TimeSpan.FromMilliseconds(1) || Timeout > TimeSpan.FromHours(24) ||
                (connectionMode != DoorsConnectionMode.AttachExisting && connectionMode != DoorsConnectionMode.Activate)) throw Invalid();
            DoorsVersions.Format(target);
            Direction = direction; Baseline = baseline; OpenIfNeeded = openIfNeeded; ResolveEndpoints = resolveEndpoints;
            IncludeDeletedObjects = includeDeletedObjects; SurrogateMapping = surrogateMapping; MaxObjects = maxObjects;
            MaxLinks = maxLinks; MaxLinkedModules = maxLinkedModules; MaxFileBytes = maxFileBytes; ConnectionMode = connectionMode; Target = target;
        }
        public string ModulePath { get; }
        public string LinkModulePath { get; }
        public LinkDirection Direction { get; }
        public RequirementBaseline Baseline { get; }
        public bool OpenIfNeeded { get; }
        public bool ResolveEndpoints { get; }
        public bool IncludeDeletedObjects { get; }
        public SurrogateMapping SurrogateMapping { get; }
        public int MaxObjects { get; }
        public int MaxLinks { get; }
        public int MaxLinkedModules { get; }
        public long MaxFileBytes { get; }
        public TimeSpan Timeout { get; }
        public DoorsConnectionMode ConnectionMode { get; }
        public DoorsVersion Target { get; }
        internal static IntegrationException Invalid() => new IntegrationException(IntegrationErrorCode.InvalidConfiguration,
            "Link okuma isteği, tam modül yolları, surrogate eşlemesi veya aktarım sınırları geçersiz.");
    }

    public sealed class LinkEndpoint
    {
        public LinkEndpoint(string modulePath, LinkVersionKind versionKind, string version, int? absoluteNumber, string objectIdentifier,
            LinkEndpointResolution resolution, SurrogateStatus surrogateStatus = SurrogateStatus.NotConfigured,
            string externalReference = "", RequirementValueStatus referenceStatus = RequirementValueStatus.NotRequested)
        {
            ModulePath = modulePath; VersionKind = versionKind; Version = version; AbsoluteNumber = absoluteNumber;
            ObjectIdentifier = objectIdentifier; Resolution = resolution; SurrogateStatus = surrogateStatus;
            ExternalReference = externalReference; ReferenceStatus = referenceStatus;
        }
        public string ModulePath { get; }
        public LinkVersionKind VersionKind { get; }
        public string Version { get; }
        public int? AbsoluteNumber { get; }
        public string ObjectIdentifier { get; }
        public LinkEndpointResolution Resolution { get; }
        public SurrogateStatus SurrogateStatus { get; }
        public string ExternalReference { get; }
        public RequirementValueStatus ReferenceStatus { get; }
        public bool IsReadable => Resolution == LinkEndpointResolution.Resolved || Resolution == LinkEndpointResolution.DeletedObject;
    }

    public sealed class LinkRecord
    {
        public LinkRecord(int sequence, LinkDirection direction, LinkRecordKind kind, LinkResolution resolution, bool isEchoed,
            string linkModulePath, LinkModuleResolution linkModuleResolution, LinkEndpoint source, LinkEndpoint target)
        {
            Sequence = sequence; Direction = direction; Kind = kind; Resolution = resolution; IsEchoed = isEchoed;
            LinkModulePath = linkModulePath; LinkModuleResolution = linkModuleResolution; Source = source; Target = target;
        }
        public int Sequence { get; }
        public LinkDirection Direction { get; }
        public LinkRecordKind Kind { get; }
        public LinkResolution Resolution { get; }
        public bool IsEchoed { get; }
        public string LinkModulePath { get; }
        public LinkModuleResolution LinkModuleResolution { get; }
        public LinkEndpoint Source { get; }
        public LinkEndpoint Target { get; }
    }

    public sealed class LinkReadSummary
    {
        public LinkReadSummary(Guid correlationId, LinkReadRequest request, RequirementModuleStatus moduleStatus, LinkModuleStatus linkModuleStatus,
            RequirementModuleOwnership ownership, int recordCount, int visitedObjects, int deniedObjects, int deletedExcluded,
            int unresolvedLinks, int sourceReferences, bool truncated, bool enumerationFailed, int openedModules, int cleanupFailures, int surrogateProblems)
            : this(correlationId, request, moduleStatus, linkModuleStatus, ownership, recordCount, visitedObjects, deniedObjects, deletedExcluded,
                unresolvedLinks, sourceReferences, truncated, enumerationFailed, openedModules, cleanupFailures, surrogateProblems, null, null) { }
        public LinkReadSummary(Guid correlationId, LinkReadRequest request, RequirementModuleStatus moduleStatus, LinkModuleStatus linkModuleStatus,
            RequirementModuleOwnership ownership, int recordCount, int visitedObjects, int deniedObjects, int deletedExcluded,
            int unresolvedLinks, int sourceReferences, bool truncated, bool enumerationFailed, int openedModules, int cleanupFailures, int surrogateProblems,
            string qualifiedAnchorModuleId = null, string qualifiedLinkModuleId = null)
        {
            CorrelationId = correlationId; Request = request; ModuleStatus = moduleStatus; LinkModuleStatus = linkModuleStatus;
            Ownership = ownership; RecordCount = recordCount; VisitedObjectCount = visitedObjects; DeniedObjectCount = deniedObjects;
            DeletedExcludedCount = deletedExcluded; UnresolvedLinkCount = unresolvedLinks; SourceReferenceCount = sourceReferences;
            IsTruncated = truncated; EnumerationFailed = enumerationFailed; OpenedModuleCount = openedModules;
            CleanupFailureCount = cleanupFailures; SurrogateProblemCount = surrogateProblems;
            QualifiedAnchorModuleId = qualifiedAnchorModuleId; QualifiedLinkModuleId = qualifiedLinkModuleId;
        }
        public Guid CorrelationId { get; }
        // Optional adapter-observed identities. Legacy/native v1 provides paths only; never invent IDs from those paths.
        public string QualifiedAnchorModuleId { get; }
        public string QualifiedLinkModuleId { get; }
        public LinkReadRequest Request { get; }
        public RequirementModuleStatus ModuleStatus { get; }
        public LinkModuleStatus LinkModuleStatus { get; }
        public RequirementModuleOwnership Ownership { get; }
        public int RecordCount { get; }
        public int LinkCount => RecordCount - SourceReferenceCount;
        public int VisitedObjectCount { get; }
        public int DeniedObjectCount { get; }
        public int DeletedExcludedCount { get; }
        public int UnresolvedLinkCount { get; }
        public int SourceReferenceCount { get; }
        public bool IsTruncated { get; }
        public bool EnumerationFailed { get; }
        public int OpenedModuleCount { get; }
        public int CleanupFailureCount { get; }
        public int SurrogateProblemCount { get; }
        public string Visibility => "ReadableLinkScopeOnly";
        public bool IsCompleteForReadableScope => ModuleStatus == RequirementModuleStatus.Available && LinkModuleStatus == LinkModuleStatus.Available &&
            !IsTruncated && !EnumerationFailed && DeniedObjectCount == 0 && UnresolvedLinkCount == 0 && SourceReferenceCount == 0 && CleanupFailureCount == 0 && SurrogateProblemCount == 0;
    }

    public interface ILinkSnapshot : IDisposable
    {
        LinkReadSummary Summary { get; }
        IEnumerable<LinkRecord> ReadLinks();
        Stream OpenRawStream();
    }
    public interface ILinkReader { Task<ILinkSnapshot> ReadAsync(LinkReadRequest request, CancellationToken cancellationToken = default(CancellationToken)); }
    public interface ILinkService
    {
        Task<ILinkSnapshot> ReadAsync(LinkReadRequest request, CancellationToken cancellationToken = default(CancellationToken));
        Task<LinkWritePreview> PreviewWriteAsync(LinkWriteRequest request, CancellationToken cancellationToken = default(CancellationToken));
        Task<LinkWriteResult> ApplyWriteAsync(LinkWritePreview preview, CancellationToken cancellationToken = default(CancellationToken));
    }
    public interface ITraceabilityExporter
    {
        void Export(Stream destination, ILinkSnapshot snapshot, TraceabilityFormat format);
        void ExportFile(string path, ILinkSnapshot snapshot, TraceabilityFormat format, bool overwrite = false);
    }
}
