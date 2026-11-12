using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DoorsIntegration.Core
{
    public enum BaselineManagerMode { IndependentModuleBaselines, NativeBaselineSet }
    public enum BaselineSetAction { CreateDefinition, UpdateDefinition, AddDefinitionModules, RemoveDefinitionModules, CreateMajorSet, CreateMinorSet, AddModuleBaselines, CloseSet }
    public enum BaselineSetJobStatus { Pending, Running, Succeeded, Partial, Blocked, Declined, Unknown, NeedsReconciliation }
    [DataContract]
    public sealed class NativeBaselineSetVersion : IEquatable<NativeBaselineSetVersion>
    {
        public NativeBaselineSetVersion(int major, int minor, string suffix = "") { var valid = new BaselineVersion(major, minor, suffix); Major = valid.Major; Minor = valid.Minor; Suffix = valid.Suffix; }
        [DataMember] public int Major { get; private set; }
        [DataMember] public int Minor { get; private set; }
        [DataMember] public string Suffix { get; private set; }
        public bool Equals(NativeBaselineSetVersion other) => other != null && Major == other.Major && Minor == other.Minor && Suffix == other.Suffix;
        public override bool Equals(object obj) => Equals(obj as NativeBaselineSetVersion);
        public override int GetHashCode() => Major ^ Minor ^ Suffix.GetHashCode();
        public override string ToString() => Major + "." + Minor + "[" + Suffix + "]";
    }
    [DataContract]
    public sealed class BaselineSetModuleReference
    {
        public BaselineSetModuleReference(BaselineModuleReference module) { RepositoryId = module.RepositoryId; ModuleId = module.ModuleId; FullPath = module.CanonicalFullPath; }
        [DataMember] public string RepositoryId { get; private set; }
        [DataMember] public string ModuleId { get; private set; }
        [DataMember] public string FullPath { get; private set; }
        public BaselineModuleReference Module => new BaselineModuleReference(RepositoryId, ModuleId, FullPath);
    }
    [DataContract]
    public sealed class BaselineSetDefinitionDetails
    {
        [DataMember] public string Id { get; set; }
        [DataMember] public string IdentityEvidence { get; set; }
        [DataMember] public string FolderPath { get; set; }
        [DataMember] public string Name { get; set; }
        [DataMember] public string Description { get; set; }
        [DataMember] public List<BaselineSetModuleReference> Modules { get; set; } = new List<BaselineSetModuleReference>();
    }
    [DataContract]
    public sealed class BaselineSetModuleBaseline
    {
        [DataMember] public BaselineSetModuleReference Module { get; set; }
        // Module version is obtained separately; never coerced from the enclosing set version.
        [DataMember] public string ModuleBaselineVersion { get; set; }
        [DataMember] public string BaselineIdentity { get; set; }
        [DataMember] public string Resolution { get; set; }
        [DataMember] public BaselineVersionReference TypedVersion { get; set; }
    }
    [DataContract]
    public sealed class NativeBaselineSetDetails
    {
        [DataMember] public string Id { get; set; }
        [DataMember] public string DefinitionId { get; set; }
        [DataMember] public NativeBaselineSetVersion Version { get; set; }
        [DataMember] public bool IsOpen { get; set; }
        [DataMember] public string Annotation { get; set; }
        [DataMember] public string Creator { get; set; }
        [DataMember] public DateTime CreatedAtUtc { get; set; }
        [DataMember] public List<BaselineSetModuleBaseline> Members { get; set; } = new List<BaselineSetModuleBaseline>();
    }
    [DataContract]
    public sealed class BaselineSetInspection
    {
        [DataMember] public BaselineMetadataSource Source { get; set; }
        [DataMember] public string DatabaseId { get; set; }
        [DataMember] public string RepositoryId { get; set; }
        [DataMember] public string FolderIdentity { get; set; }
        [DataMember] public string UserId { get; set; }
        [DataMember] public string SessionId { get; set; }
        [DataMember] public string ClientVersion { get; set; }
        [DataMember] public string Revision { get; set; }
        [DataMember] public bool Complete { get; set; }
        [DataMember] public bool ReadOnlyObservation { get; set; }
        [DataMember] public bool SessionObserved { get; set; }
        [DataMember] public bool VisibilityLimited { get; set; }
        [DataMember] public bool DefinitionCacheUnknown { get; set; }
        [DataMember] public List<NativeModuleReadState> ModuleStates { get; set; } = new List<NativeModuleReadState>();
        [DataMember] public Dictionary<string, string> ObservedLinkModuleIds { get; set; } = new Dictionary<string, string>();
        [DataMember] public bool FolderExists { get; set; }
        [DataMember] public DoorsItemKind FolderKind { get; set; }
        [DataMember] public bool CanRead { get; set; }
        [DataMember] public bool CanCreate { get; set; }
        [DataMember] public bool CanModify { get; set; }
        [DataMember] public bool LockedByOther { get; set; }
        [DataMember] public bool DirtyDefinition { get; set; }
        [DataMember] public bool NativeLinkBehaviorValidated { get; set; }
        [DataMember] public List<string> ModuleProblems { get; set; } = new List<string>();
        [DataMember] public List<BaselineSetDefinitionDetails> Definitions { get; set; } = new List<BaselineSetDefinitionDetails>();
        [DataMember] public List<NativeBaselineSetDetails> Sets { get; set; } = new List<NativeBaselineSetDetails>();
        [DataMember] public string Reason { get; set; }
        public string Fingerprint => BaselineSetSerialization.Hash(this);
    }
    [DataContract]
    public sealed class BaselineSetLinkEdge
    {
        [DataMember] public string SourceModuleId { get; set; }
        [DataMember] public int SourceObjectId { get; set; }
        [DataMember] public string TargetModuleId { get; set; }
        [DataMember] public int TargetObjectId { get; set; }
        [DataMember] public string LinkModuleId { get; set; }
        [DataMember] public string SourceVersion { get; set; }
        [DataMember] public string TargetVersion { get; set; }
        public string Key => string.Join("|", new[] { SourceModuleId, SourceObjectId.ToString(System.Globalization.CultureInfo.InvariantCulture), TargetModuleId, TargetObjectId.ToString(System.Globalization.CultureInfo.InvariantCulture), LinkModuleId }.Select(x => (x ?? "").Length + ":" + (x ?? "")));
    }
    [DataContract]
    public sealed class BaselineSetLinkSnapshot
    {
        [DataMember] public bool Complete { get; set; }
        [DataMember] public string Context { get; set; }
        [DataMember] public string Reason { get; set; }
        [DataMember] public List<BaselineSetLinkEdge> Edges { get; set; } = new List<BaselineSetLinkEdge>();
    }
    [DataContract]
    public sealed class BaselineSetLinkVerification
    {
        [DataMember] public bool Verified { get; set; }
        [DataMember] public List<string> Missing { get; set; } = new List<string>();
        [DataMember] public List<string> Unexpected { get; set; } = new List<string>();
        [DataMember] public List<string> Problems { get; set; } = new List<string>();
    }
    [DataContract]
    public sealed class BaselineSetRequest
    {
        public BaselineSetRequest(BaselineSetAction action, string folderPath, string definitionId = "", string setId = "", string name = "", string description = "",
            IEnumerable<BaselineModuleReference> modules = null, string suffix = "", string annotation = "", DoorsVersion target = DoorsVersion.V97)
        {
            BulkBaselineGuard.Defined(action); DoorsVersions.Format(target); FolderPath = DoorsItemPath.Normalize(folderPath); Action = action; DefinitionId = definitionId ?? ""; SetId = setId ?? "";
            Name = string.IsNullOrEmpty(name) ? "" : BulkBaselineGuard.Text(name, nameof(name), 256); Description = BulkBaselineGuard.Annotation(description ?? ""); Suffix = BulkBaselineGuard.Suffix(suffix); Annotation = BulkBaselineGuard.Annotation(annotation); Target = target;
            Modules = (modules ?? Array.Empty<BaselineModuleReference>()).Select(m => new BaselineSetModuleReference(m)).ToList();
            if (Modules.Count > 10000 || Modules.Select(m => m.RepositoryId).Distinct().Count() > 1) throw new ArgumentException("Invalid module selection.");
            foreach (var group in Modules.GroupBy(m => m.ModuleId)) if (group.Select(m => m.FullPath).Distinct().Count() > 1) throw new ArgumentException("Conflicting stable identity/path.");
            Modules = Modules.GroupBy(m => m.ModuleId).Select(g => g.First()).OrderBy(m => m.ModuleId, StringComparer.Ordinal).ToList();
        }
        [DataMember] public BaselineSetAction Action { get; private set; }
        [DataMember] public string FolderPath { get; private set; }
        [DataMember] public string DefinitionId { get; private set; }
        [DataMember] public string SetId { get; private set; }
        [DataMember] public string Name { get; private set; }
        [DataMember] public string Description { get; private set; }
        [DataMember] public string Suffix { get; private set; }
        [DataMember] public string Annotation { get; private set; }
        [DataMember] public DoorsVersion Target { get; private set; }
        [DataMember] public List<BaselineSetModuleReference> Modules { get; private set; }
    }
    public sealed class BaselineSetPreflight
    {
        internal BaselineSetPreflight(BaselineSetRequest request, BaselineSetInspection inspection, BaselineSetLinkSnapshot reference, IEnumerable<string> problems)
        { Request = BaselineSetSerialization.Clone(request); Inspection = BaselineSetSerialization.Clone(inspection); ReferenceLinks = reference == null ? null : BaselineSetSerialization.Clone(reference); Problems = problems.ToArray(); CreatedAtUtc = DateTime.UtcNow; Fingerprint = BaselineSetSerialization.Hash(new object[] { Request, Inspection, ReferenceLinks }); }
        public BaselineSetRequest Request { get; }
        public BaselineSetInspection Inspection { get; }
        public BaselineSetLinkSnapshot ReferenceLinks { get; }
        public IReadOnlyList<string> Problems { get; }
        public bool Ready => Problems.Count == 0;
        public DateTime CreatedAtUtc { get; }
        public DateTime ExpiresAtUtc => CreatedAtUtc.AddMinutes(2);
        public string Fingerprint { get; }
        internal bool Intact => Fingerprint == BaselineSetSerialization.Hash(new object[] { Request, Inspection, ReferenceLinks });
    }
    public interface IBaselineSetConfirmation { Task<bool> ConfirmAsync(BaselineSetPreflight plan, bool secondRealConfirmation, CancellationToken token); }
    public sealed class RejectBaselineSetConfirmation : IBaselineSetConfirmation
    { public Task<bool> ConfirmAsync(BaselineSetPreflight plan, bool secondRealConfirmation, CancellationToken token) => Task.FromResult(false); }
    public sealed class BaselineSetAuthorization
    {
        private int consumed; private readonly Func<bool> allowed;
        internal BaselineSetAuthorization(BaselineSetPreflight plan, Guid jobId, Func<bool> allowed) { Plan = plan; JobId = jobId; this.allowed = allowed; }
        public BaselineSetPreflight Plan { get; }
        public Guid JobId { get; }
        public bool Fresh => DateTime.UtcNow < Plan.ExpiresAtUtc && Plan.Intact && allowed();
        public bool TryBeginWrite() => Fresh && Interlocked.CompareExchange(ref consumed, 1, 0) == 0;
    }
    public sealed class BaselineSetAdapterResult
    {
        public BaselineSetAdapterResult(BaselineSetJobStatus status, string reason, string definitionId = "", string setId = "") { Status = status; Reason = reason; DefinitionId = definitionId; SetId = setId; }
        public BaselineSetJobStatus Status { get; }
        public string Reason { get; }
        public string DefinitionId { get; }
        public string SetId { get; }
    }
    public interface IBaselineSetAdapter
    {
        BaselineMetadataSource Source { get; }
        bool WritesValidated { get; }
        Task<BaselineSetInspection> InspectAsync(BaselineSetRequest request, CancellationToken token);
        Task<BaselineSetAdapterResult> ExecuteAsync(BaselineSetAuthorization authorization, CancellationToken token);
    }
    public interface IBaselineSetLinkReader
    { Task<BaselineSetLinkSnapshot> CaptureAsync(BaselineSetDefinitionDetails definition, NativeBaselineSetDetails set, CancellationToken token); }
    [DataContract]
    public sealed class BaselineSetJob
    {
        [DataMember] public Guid JobId { get; internal set; }
        [DataMember] public long Revision { get; internal set; }
        [DataMember] public BaselineSetRequest Request { get; internal set; }
        [DataMember] public BaselineMetadataSource Source { get; internal set; }
        [DataMember] public BaselineSetJobStatus Status { get; internal set; }
        [DataMember] public string Reason { get; internal set; }
        [DataMember] public string PlanFingerprint { get; internal set; }
        [DataMember] public string DefinitionId { get; internal set; }
        [DataMember] public string SetId { get; internal set; }
        [DataMember] public DateTime StartedAtUtc { get; internal set; }
        [DataMember] public DateTime? EndedAtUtc { get; internal set; }
        [DataMember] public bool ExplicitApproval { get; internal set; }
        [DataMember] public bool SecondRealApproval { get; internal set; }
        [DataMember] public BaselineSetInspection Before { get; internal set; }
        [DataMember] public BaselineSetInspection After { get; internal set; }
        [DataMember] public BaselineSetLinkSnapshot ReferenceLinks { get; internal set; }
        [DataMember] public BaselineSetLinkVerification LinkVerification { get; internal set; }
        public IReadOnlyList<string> MissingModules => Missing(After, DefinitionId, SetId);
        internal static string[] Missing(BaselineSetInspection snapshot, string definitionId, string setId)
        { var definition = snapshot?.Definitions.SingleOrDefault(d => d.Id == definitionId); var set = snapshot?.Sets.SingleOrDefault(s => s.Id == setId); return definition == null ? Array.Empty<string>() : definition.Modules.Where(m => set == null || !set.Members.Any(x => x.Module.ModuleId == m.ModuleId)).Select(m => m.ModuleId).ToArray(); }
    }
    public interface IBaselineSetJobRepository
    {
        IDisposable Acquire();
        void Save(BaselineSetJob job);
        BaselineSetJob Get(Guid id);
        IReadOnlyList<BaselineSetJob> List();
        IEnumerable<BaselineSetJob> ReadHistory(Guid id);
    }
    public interface INativeBaselineSetService
    {
        Task<IReadOnlyList<BaselineSetDefinitionDetails>> ListBaselineSetDefinitions(string folder, DoorsVersion target, CancellationToken token = default(CancellationToken));
        Task<BaselineSetDefinitionDetails> GetBaselineSetDefinition(string folder, string id, DoorsVersion target, CancellationToken token = default(CancellationToken));
        Task<IReadOnlyList<BaselineSetModuleReference>> ListDefinitionModules(string folder, string id, DoorsVersion target, CancellationToken token = default(CancellationToken));
        Task<IReadOnlyList<NativeBaselineSetDetails>> ListBaselineSets(string folder, string id, DoorsVersion target, CancellationToken token = default(CancellationToken));
        Task<NativeBaselineSetDetails> GetBaselineSetDetails(string folder, string definition, string set, DoorsVersion target, CancellationToken token = default(CancellationToken));
        Task<NativeBaselineSetDetails> GetOpenBaselineSet(string folder, string definition, DoorsVersion target, CancellationToken token = default(CancellationToken));
        Task<BaselineSetPreflight> PreflightAsync(BaselineSetRequest request, CancellationToken token = default(CancellationToken));
        Task<BaselineSetJob> ExecuteAsync(BaselineSetPreflight plan, CancellationToken token = default(CancellationToken));
        Task<BaselineSetJob> CreateBaselineSetDefinition(BaselineSetPreflight plan, CancellationToken token = default(CancellationToken));
        Task<BaselineSetJob> UpdateBaselineSetDefinition(BaselineSetPreflight plan, CancellationToken token = default(CancellationToken));
        Task<BaselineSetJob> AddModulesToDefinition(BaselineSetPreflight plan, CancellationToken token = default(CancellationToken));
        Task<BaselineSetJob> RemoveModulesFromDefinition(BaselineSetPreflight plan, CancellationToken token = default(CancellationToken));
        Task<BaselineSetJob> CreateMajorBaselineSet(BaselineSetPreflight plan, CancellationToken token = default(CancellationToken));
        Task<BaselineSetJob> CreateMinorBaselineSet(BaselineSetPreflight plan, CancellationToken token = default(CancellationToken));
        Task<BaselineSetJob> AddModuleBaselinesToSet(BaselineSetPreflight plan, CancellationToken token = default(CancellationToken));
        Task<BaselineSetJob> CloseBaselineSet(BaselineSetPreflight plan, CancellationToken token = default(CancellationToken));
        Task<BaselineSetJob> ReconcileAsync(Guid jobId, CancellationToken token = default(CancellationToken));
        Task<BaselineSetLinkVerification> VerifyBaselineSet(string folder, string definition, string set, DoorsVersion target, CancellationToken token = default(CancellationToken));
        BaselineSetJob GetJob(Guid id);
        IReadOnlyList<BaselineSetJob> ListJobs();
        bool WritesValidated { get; }
    }
    internal static class BaselineSetSerialization
    {
        private static DataContractJsonSerializer Serializer(Type type) => new DataContractJsonSerializer(type, new DataContractJsonSerializerSettings { MaxItemsInObjectGraph = 2000000, KnownTypes = new[] { typeof(BaselineSetRequest), typeof(BaselineSetInspection), typeof(BaselineSetLinkSnapshot) } });
        internal static byte[] Bytes<T>(T value) { using (var output = new MemoryStream()) { Serializer(typeof(T)).WriteObject(output, value); return output.ToArray(); } }
        internal static T Read<T>(byte[] bytes) { using (var input = new MemoryStream(bytes)) return (T)Serializer(typeof(T)).ReadObject(input); }
        internal static T Clone<T>(T value) => Read<T>(Bytes(value));
        internal static string Hash<T>(T value) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Bytes(value))).Replace("-", ""); }
        internal static string Text(string text) => BaselineAuditSanitizer.Text(text ?? "");
    }
}
