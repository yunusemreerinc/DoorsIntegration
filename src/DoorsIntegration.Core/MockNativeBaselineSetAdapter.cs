using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace DoorsIntegration.Core
{
    public sealed class MockBaselineSetModulePolicy
    {
        public bool Exists { get; set; } = true;
        public bool Formal { get; set; } = true;
        public bool CanCreate { get; set; } = true;
        public bool CanModify { get; set; } = true;
        public bool Locked { get; set; }
        public bool Dirty { get; set; }
        public BaselineCheckState ShareableConfigured { get; set; } = BaselineCheckState.Failed;
        public BaselineEditState EditState { get; set; } = BaselineEditState.ReadOnly;
    }
    public sealed class MockNativeBaselineSetAdapter : IBaselineSetAdapter, IBaselineSetLinkReader
    {
        private readonly object sync = new object(); private readonly string path; private readonly IBaselineJournalProtector protector;
        [DataContract] private sealed class Database
        {
            [DataMember] public long Revision;
            [DataMember] public List<BaselineSetDefinitionDetails> Definitions = new List<BaselineSetDefinitionDetails>();
            [DataMember] public List<NativeBaselineSetDetails> Sets = new List<NativeBaselineSetDetails>();
            [DataMember] public Dictionary<string, int> ModuleMinors = new Dictionary<string, int>();
        }
        public MockNativeBaselineSetAdapter(string persistencePath = null, IBaselineJournalProtector protector = null) { path = persistencePath == null ? null : Path.GetFullPath(persistencePath); this.protector = protector ?? new WindowsBaselineJournalProtector(); }
        private Database memory = new Database();
        public BaselineMetadataSource Source => BaselineMetadataSource.Mock;
        public bool WritesValidated => true;
        public bool CanRead { get; set; } = true;
        public bool CanCreate { get; set; } = true;
        public bool CanModify { get; set; } = true;
        public bool LockedByOther { get; set; }
        public bool DirtyDefinition { get; set; }
        public bool FolderExists { get; set; } = true;
        public bool LoseLinkAfterClose { get; set; }
        public bool IncompleteLinks { get; set; }
        public bool ReturnUnknownAfterCommit { get; set; }
        public string FailModuleId { get; set; }
        public int Writes { get; private set; }
        public Dictionary<string, MockBaselineSetModulePolicy> ModulePolicies { get; } = new Dictionary<string, MockBaselineSetModulePolicy>();
        private Database Load() => path != null && File.Exists(path) ? BaselineSetSerialization.Read<Database>(protector.Unprotect(File.ReadAllBytes(path))) : memory;
        private void Persist(Database state)
        {
            if (path == null) { memory = state; return; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)); string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { byte[] bytes = protector.Protect(BaselineSetSerialization.Bytes(state)); using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { output.Write(bytes, 0, bytes.Length); output.Flush(true); } if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path); memory = state; }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        private IDisposable FileLock()
        { if (path == null) return new MemoryLease(); Directory.CreateDirectory(Path.GetDirectoryName(path)); return new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        private sealed class MemoryLease : IDisposable { public void Dispose() { } }
        private BaselineSetInspection Inspect(Database db, BaselineSetRequest r)
        {
            var definitions = db.Definitions.Where(d => d.FolderPath == r.FolderPath).ToList(); var definition = definitions.SingleOrDefault(d => d.Id == r.DefinitionId);
            var inspected = r.Modules.Concat(definition?.Modules ?? new List<BaselineSetModuleReference>()).GroupBy(m => m.ModuleId).Select(g => g.First()); var problems = new List<string>();
            bool baselining = r.Action == BaselineSetAction.CreateMajorSet || r.Action == BaselineSetAction.CreateMinorSet || r.Action == BaselineSetAction.AddModuleBaselines || r.Action == BaselineSetAction.CloseSet;
            foreach (var module in inspected)
            {
                var p = ModulePolicies.TryGetValue(module.ModuleId, out var policy) ? policy : new MockBaselineSetModulePolicy();
                if (!p.Exists || !p.Formal) problems.Add(module.ModuleId + ": missing/non-formal module.");
                if (baselining && (!p.CanCreate || !p.CanModify || p.Locked || p.Dirty)) problems.Add(module.ModuleId + ": permission/lock/unsaved module state blocks baselining.");
                if (baselining && (p.ShareableConfigured != BaselineCheckState.Failed || p.EditState == BaselineEditState.SharedCurrentSession || p.EditState == BaselineEditState.SharedOtherSession)) problems.Add(module.ModuleId + ": shareable configuration/edit mode is risky or unknown (IBM link-loss alert).");
            }
            return BaselineSetSerialization.Clone(new BaselineSetInspection { Source = Source, DatabaseId = "mock-set-database", UserId = "mock-operator", SessionId = "mock-session", ClientVersion = "Mock; requested " + DoorsVersions.Format(r.Target), Revision = db.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture), Complete = true,
                FolderExists = FolderExists, FolderKind = DoorsItemKind.Folder, CanRead = CanRead, CanCreate = CanCreate, CanModify = CanModify, LockedByOther = LockedByOther, DirtyDefinition = DirtyDefinition, NativeLinkBehaviorValidated = false, ModuleProblems = problems,
                Definitions = definitions, Sets = db.Sets.Where(s => definitions.Any(d => d.Id == s.DefinitionId)).ToList(), Reason = "Explicit Mock fixture; native API/link/DPAPI validation NOT_RUN." });
        }
        public Task<BaselineSetInspection> InspectAsync(BaselineSetRequest request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); lock (sync) using (FileLock()) return Task.FromResult(Inspect(Load(), request)); }
        public Task<BaselineSetAdapterResult> ExecuteAsync(BaselineSetAuthorization authorization, CancellationToken token)
        {
            lock (sync) using (FileLock())
            {
                token.ThrowIfCancellationRequested(); var r = authorization.Plan.Request; var db = BaselineSetSerialization.Clone(Load()); var state = Inspect(db, r);
                if (state.Fingerprint != authorization.Plan.Inspection.Fingerprint || !authorization.TryBeginWrite()) return Task.FromResult(new BaselineSetAdapterResult(BaselineSetJobStatus.Blocked, "Mock state/approval changed."));
                var definition = db.Definitions.SingleOrDefault(d => d.Id == r.DefinitionId); var set = db.Sets.SingleOrDefault(s => s.Id == r.SetId);
                if (!CanCreate || !CanModify || LockedByOther || DirtyDefinition || !FolderExists || state.ModuleProblems.Count > 0) return Task.FromResult(new BaselineSetAdapterResult(BaselineSetJobStatus.Blocked, "Mock permission/lock/state rejected."));
                if (r.Action != BaselineSetAction.CreateDefinition && definition == null) return Task.FromResult(new BaselineSetAdapterResult(BaselineSetJobStatus.Blocked, "Mock definition missing."));
                bool open = definition != null && db.Sets.Any(s => s.DefinitionId == definition.Id && s.IsOpen);
                if (open && (r.Action == BaselineSetAction.UpdateDefinition || r.Action == BaselineSetAction.AddDefinitionModules || r.Action == BaselineSetAction.RemoveDefinitionModules || r.Action == BaselineSetAction.CreateMajorSet || r.Action == BaselineSetAction.CreateMinorSet)) return Task.FromResult(new BaselineSetAdapterResult(BaselineSetJobStatus.Blocked, "Open set prevents definition mutation or a second open set."));
                switch (r.Action)
                {
                    case BaselineSetAction.CreateDefinition:
                        if (db.Definitions.Any(d => d.FolderPath == r.FolderPath && d.Name == r.Name)) return Task.FromResult(new BaselineSetAdapterResult(BaselineSetJobStatus.Blocked, "Name collision."));
                        definition = new BaselineSetDefinitionDetails { Id = "mock-definition-" + Guid.NewGuid().ToString("N"), IdentityEvidence = "MockOpaqueIdentity", FolderPath = r.FolderPath, Name = r.Name, Description = r.Description, Modules = BaselineSetSerialization.Clone(r.Modules) }; db.Definitions.Add(definition); break;
                    case BaselineSetAction.UpdateDefinition: definition.Name = r.Name; definition.Description = r.Description; break;
                    case BaselineSetAction.AddDefinitionModules:
                        foreach (var module in r.Modules) if (!definition.Modules.Any(m => m.ModuleId == module.ModuleId)) definition.Modules.Add(module); break;
                    case BaselineSetAction.RemoveDefinitionModules: definition.Modules.RemoveAll(m => r.Modules.Any(x => x.ModuleId == m.ModuleId)); break;
                    case BaselineSetAction.CreateMajorSet:
                    case BaselineSetAction.CreateMinorSet:
                        var latest = db.Sets.Where(s => s.DefinitionId == definition.Id).OrderByDescending(s => s.Version.Major).ThenByDescending(s => s.Version.Minor).FirstOrDefault()?.Version;
                        bool major = r.Action == BaselineSetAction.CreateMajorSet;
                        set = new NativeBaselineSetDetails { Id = "mock-set-" + Guid.NewGuid().ToString("N"), DefinitionId = definition.Id,
                            Version = new NativeBaselineSetVersion(major ? (latest?.Major ?? 0) + 1 : latest?.Major ?? 0, major ? 0 : (latest?.Minor ?? 0) + 1, r.Suffix), IsOpen = true, Annotation = r.Annotation, Creator = "mock-operator", CreatedAtUtc = DateTime.UtcNow }; db.Sets.Add(set); break;
                    case BaselineSetAction.AddModuleBaselines:
                        if (set == null || set.DefinitionId != definition.Id || !set.IsOpen || r.Modules.Any(m => set.Members.Any(x => x.Module.ModuleId == m.ModuleId) || !definition.Modules.Any(x => x.Module.Matches(m.Module)))) return Task.FromResult(new BaselineSetAdapterResult(BaselineSetJobStatus.Blocked, "Invalid membership/duplicate baseline."));
                        foreach (var module in r.Modules)
                        {
                            if (module.ModuleId == FailModuleId) continue; // Explicit non-native fault fixture; not a claim about native addBaselines atomicity.
                            int minor = db.ModuleMinors.TryGetValue(module.ModuleId, out int previous) ? previous + 1 : 5; db.ModuleMinors[module.ModuleId] = minor;
                            set.Members.Add(new BaselineSetModuleBaseline { Module = module, ModuleBaselineVersion = "1." + minor + "[" + r.Suffix + "]", BaselineIdentity = "mock-baseline-" + Guid.NewGuid().ToString("N"), Resolution = "Verified" });
                        }
                        break;
                    case BaselineSetAction.CloseSet:
                        if (set == null || set.DefinitionId != definition.Id || !set.IsOpen || definition.Modules.Any(m => !set.Members.Any(x => x.Module.ModuleId == m.ModuleId))) return Task.FromResult(new BaselineSetAdapterResult(BaselineSetJobStatus.Blocked, "Incomplete/not-open Mock set."));
                        set.IsOpen = false; break;
                    default: throw new ArgumentOutOfRangeException();
                }
                db.Revision++; Persist(db); Writes++;
                return Task.FromResult(new BaselineSetAdapterResult(ReturnUnknownAfterCommit ? BaselineSetJobStatus.Unknown : set != null && r.Action == BaselineSetAction.AddModuleBaselines && definition.Modules.Any(m => !set.Members.Any(x => x.Module.ModuleId == m.ModuleId)) ? BaselineSetJobStatus.Partial : BaselineSetJobStatus.Succeeded,
                    ReturnUnknownAfterCommit ? "Mock transport timeout after persisted commit; reconcile read-only." : "MOCK mutation observed; no real DOORS data was changed.", definition?.Id ?? "", set?.Id ?? ""));
            }
        }
        public Task<BaselineSetLinkSnapshot> CaptureAsync(BaselineSetDefinitionDetails definition, NativeBaselineSetDetails set, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); var result = new BaselineSetLinkSnapshot { Complete = !IncompleteLinks, Context = set?.Id ?? "Current", Reason = "Mock directed standard links; native link behavior NOT_RUN." };
            if (definition.Modules.Count > 1 && !(LoseLinkAfterClose && set != null && !set.IsOpen))
            {
                var a = definition.Modules[0]; var b = definition.Modules[1];
                result.Edges.Add(new BaselineSetLinkEdge { SourceModuleId = a.ModuleId, SourceObjectId = 1, TargetModuleId = b.ModuleId, TargetObjectId = 2, LinkModuleId = "mock-standard-link-module",
                    SourceVersion = set?.Members.SingleOrDefault(m => m.Module.ModuleId == a.ModuleId)?.ModuleBaselineVersion ?? "Current", TargetVersion = set?.Members.SingleOrDefault(m => m.Module.ModuleId == b.ModuleId)?.ModuleBaselineVersion ?? "Current" });
            }
            return Task.FromResult(result);
        }
    }
}
