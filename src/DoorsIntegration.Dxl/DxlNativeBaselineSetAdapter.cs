using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;

namespace DoorsIntegration.Dxl
{
    public sealed class NativeBaselineSetScriptOptions
    {
        public string ReadPath { get; set; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scripts", "dxl", "native-baseline-set-read.dxl");
        public string WritePath { get; set; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scripts", "dxl", "native-baseline-set-write.dxl");
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);
    }
    // Implement only after validating dirty/cache, lock, permissions and configured-shareable
    // semantics on the exact client build. An edit-mode-only check is insufficient.
    public interface INativeBaselineSetSafetyProvider
    {
        Task<BaselineSetInspection> InspectAsync(BaselineSetRequest request, CancellationToken token);
        bool IsFreshForDispatch(BaselineSetInspection evidence);
    }
    public sealed class UnvalidatedNativeBaselineSetSafetyProvider : INativeBaselineSetSafetyProvider
    {
        public Task<BaselineSetInspection> InspectAsync(BaselineSetRequest request, CancellationToken token) => Task.FromResult(new BaselineSetInspection
        { Source = BaselineMetadataSource.NativeDxl, Reason = "NOT_RUN: native definition cache/dirty, permission, lock and configured-shareable inspection is not validated for this build." });
        public bool IsFreshForDispatch(BaselineSetInspection evidence) => false;
    }
    public interface INativeBaselineSetCompatibility
    {
        bool WritesValidated { get; }
        bool Matches(BaselineSetRequest request, BaselineSetInspection evidence, NativeBaselineSetScriptOptions scripts);
    }
    public sealed class UnvalidatedNativeBaselineSetCompatibility : INativeBaselineSetCompatibility
    {
        public bool WritesValidated => false;
        public bool Matches(BaselineSetRequest request, BaselineSetInspection evidence, NativeBaselineSetScriptOptions scripts) => false;
    }
    // Deployment evidence cannot replace per-operation user approval. There is no UI toggle
    // that manufactures this evidence. Pin exact scripts, client build, database and user.
    public sealed class ReviewedNativeBaselineSetCompatibility : INativeBaselineSetCompatibility
    {
        private readonly DoorsVersion target; private readonly string version, database, user, readHash, writeHash;
        public ReviewedNativeBaselineSetCompatibility(DoorsVersion target, string clientVersion, string database, string user,
            string readSha256, string writeSha256, string releaseManualEvidence, string runtimeEvidence, string linkBehaviorEvidence, string stateInspectionEvidence)
        {
            this.target = target; string family = DoorsVersions.Format(target); version = Required(clientVersion);
            if (version != family && !version.StartsWith(family + ".", StringComparison.Ordinal)) throw new ArgumentException("Client build does not match target.");
            this.database = Required(database); this.user = Required(user); readHash = Required(readSha256).ToUpperInvariant(); writeHash = Required(writeSha256).ToUpperInvariant();
            foreach (var hash in new[] { readHash, writeHash }) if (hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c))) throw new ArgumentException("Exact SHA256 hashes are required.");
            ManualEvidence = Required(releaseManualEvidence); RuntimeEvidence = Required(runtimeEvidence); LinkEvidence = Required(linkBehaviorEvidence); StateEvidence = Required(stateInspectionEvidence);
        }
        public string ManualEvidence { get; } public string RuntimeEvidence { get; } public string LinkEvidence { get; } public string StateEvidence { get; }
        public bool WritesValidated => true;
        public bool Matches(BaselineSetRequest request, BaselineSetInspection evidence, NativeBaselineSetScriptOptions scripts) =>
            request.Target == target && evidence.Source == BaselineMetadataSource.NativeDxl && evidence.ClientVersion == version && evidence.DatabaseId == database && evidence.UserId == user &&
            evidence.Complete && evidence.NativeLinkBehaviorValidated && !evidence.DirtyDefinition && !string.IsNullOrEmpty(evidence.RepositoryId) && !string.IsNullOrEmpty(evidence.FolderIdentity) &&
            int.TryParse(evidence.SessionId, NumberStyles.None, CultureInfo.InvariantCulture, out int epoch) && epoch > 0 && Hash(scripts.ReadPath) == readHash && Hash(scripts.WritePath) == writeHash;
        private static string Required(string s) => !string.IsNullOrWhiteSpace(s) && s.Length <= 4096 ? s : throw new ArgumentException("Validated evidence is required.");
        private static string Hash(string path) { using (var stream = File.OpenRead(path)) using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", ""); }
    }
    public sealed class DxlNativeBaselineSetAdapter : IBaselineSetAdapter
    {
        private readonly IDxlExecutor executor; private readonly NativeBaselineSetScriptOptions scripts;
        private readonly INativeBaselineSetSafetyProvider safety; private readonly INativeBaselineSetCompatibility compatibility;
        public DxlNativeBaselineSetAdapter(IDxlExecutor executor, NativeBaselineSetScriptOptions scripts,
            INativeBaselineSetSafetyProvider safety, INativeBaselineSetCompatibility compatibility)
        { this.executor = executor ?? throw new ArgumentNullException(nameof(executor)); this.scripts = scripts ?? throw new ArgumentNullException(nameof(scripts)); this.safety = safety ?? throw new ArgumentNullException(nameof(safety)); this.compatibility = compatibility ?? throw new ArgumentNullException(nameof(compatibility)); }
        public BaselineMetadataSource Source => BaselineMetadataSource.NativeDxl;
        public bool WritesValidated => compatibility.WritesValidated;
        public async Task<BaselineSetInspection> InspectAsync(BaselineSetRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); var facts = await safety.InspectAsync(request, token).ConfigureAwait(false);
            if (!Valid(request, facts)) { facts.Complete = false; facts.Reason = "NOT_RUN: validated release/API/state/link evidence is required. " + facts.Reason; return facts; }
            var operation = executor.Start(new DxlExecutionRequest(scripts.ReadPath, parameters: Parameters(request, facts), target: request.Target,
                timeout: scripts.Timeout, maxInlineResultChars: 2097152) { DispatchPrecondition = () => Valid(request, facts) });
            var completion = ReadCompletion(operation, request.FolderPath, facts);
            Observe(completion);
            // Completion owns cleanup even when the wait times out; no cancellation of COM.
            await operation.WaitAsync(token).ConfigureAwait(false);
            return await completion.ConfigureAwait(false);
        }
        private async Task<BaselineSetInspection> ReadCompletion(DxlExecutionOperation operation, string folder, BaselineSetInspection facts)
        { using (var result = await operation.Completion.ConfigureAwait(false)) { if (result.IsFile) throw Protocol(); return NativeBaselineSetProtocol.Read(result.Text, operation.CorrelationId, folder, facts); } }
        private bool Valid(BaselineSetRequest r, BaselineSetInspection s)
        { try { return s != null && s.Source == Source && s.Complete && !s.DirtyDefinition && safety.IsFreshForDispatch(s) && compatibility.Matches(r, s, scripts); } catch (Exception) { return false; } }
        public async Task<BaselineSetAdapterResult> ExecuteAsync(BaselineSetAuthorization authorization, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); var p = authorization?.Plan;
            if (p == null || !(executor is DxlExecutor queued) || !queued.UsesStaQueue || p.Inspection.Source != Source || !Valid(p.Request, p.Inspection) || !authorization.TryBeginWrite())
                return new BaselineSetAdapterResult(BaselineSetJobStatus.Blocked, "NOT_RUN: native STA, validated build/state/link evidence and fresh single-use approval required.");
            var values = Parameters(p.Request, p.Inspection); var r = p.Request;
            var definition = p.Inspection.Definitions.SingleOrDefault(d => d.Id == r.DefinitionId); var set = p.Inspection.Sets.SingleOrDefault(s => s.Id == r.SetId);
            values["guard"] = "ValidatedStateAndLinkEvidence"; values["approval"] = authorization.JobId.ToString("N");
            values["expires"] = ((int)(p.ExpiresAtUtc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds).ToString(CultureInfo.InvariantCulture);
            values["action"] = r.Action.ToString(); values["definitionName"] = definition?.Name ?? ""; values["name"] = r.Name; values["description"] = r.Description;
            values["suffix"] = r.Suffix; values["annotation"] = r.Annotation;
            values["setMajor"] = (set?.Version.Major ?? 0).ToString(CultureInfo.InvariantCulture); values["setMinor"] = (set?.Version.Minor ?? 0).ToString(CultureInfo.InvariantCulture); values["setSuffix"] = set?.Version.Suffix ?? "";
            if (r.Modules.Any(m => m.FullPath.IndexOfAny(new[] { '\t', '\r', '\n' }) >= 0 || m.ModuleId.IndexOfAny(new[] { '\t', '\r', '\n' }) >= 0))
                return new BaselineSetAdapterResult(BaselineSetJobStatus.Blocked, "Control characters in module manifest.");
            values["modules"] = string.Concat(r.Modules.Select(m => m.FullPath + "\t" + m.ModuleId + "\n")); values["moduleCount"] = r.Modules.Count.ToString(CultureInfo.InvariantCulture);
            DxlExecutionOperation operation;
            try { operation = executor.Start(new DxlExecutionRequest(scripts.WritePath, parameters: values, target: r.Target, timeout: scripts.Timeout, maxInlineResultChars: 131072)
                { DispatchPrecondition = () => authorization.Fresh && Valid(r, p.Inspection) }); }
            catch (Exception) { return new BaselineSetAdapterResult(BaselineSetJobStatus.Blocked, "Dispatch did not start. No automatic retry."); }
            var completion = WriteCompletion(operation, p); Observe(completion);
            try { await operation.WaitAsync(token).ConfigureAwait(false); return await completion.ConfigureAwait(false); }
            catch (Exception) { return new BaselineSetAdapterResult(BaselineSetJobStatus.Unknown, "COM/protocol outcome uncertain; wait for queue drain, then reconcile. Never automatically retry."); }
        }
        private static async Task<BaselineSetAdapterResult> WriteCompletion(DxlExecutionOperation operation, BaselineSetPreflight plan)
        {
            using (var result = await operation.Completion.ConfigureAwait(false))
            {
                if (result.IsFile || !result.Text.EndsWith("\n", StringComparison.Ordinal)) throw Protocol(); var f = NativeBaselineSetProtocol.Line(result.Text.Substring(0, result.Text.Length - 1), 9);
                if (f[0] != "NATIVE_BASELINE_SET_WRITE" || f[1] != "1" || f[2] != operation.CorrelationId.ToString("N") || !Enum.TryParse(f[3], out BaselineSetJobStatus status) ||
                    (status != BaselineSetJobStatus.Succeeded && status != BaselineSetJobStatus.Blocked && status != BaselineSetJobStatus.Unknown)) throw Protocol();
                string definition = f[5] == "" ? plan.Request.DefinitionId : NativeBaselineSetProtocol.DefinitionId(plan.Inspection.FolderIdentity, f[5]);
                string set = f[6] == "" ? plan.Request.SetId : NativeBaselineSetProtocol.SetId(definition, NativeBaselineSetProtocol.Version(f[6], f[7], f[8]));
                return new BaselineSetAdapterResult(status, BaselineAuditSanitizer.Text(f[4]), definition, set);
            }
        }
        private static Dictionary<string, string> Parameters(BaselineSetRequest r, BaselineSetInspection facts) => new Dictionary<string, string>
        { ["folder"] = r.FolderPath, ["folderId"] = facts.FolderIdentity, ["database"] = facts.DatabaseId, ["user"] = facts.UserId, ["clientVersion"] = facts.ClientVersion, ["session"] = facts.SessionId };
        private static void Observe(Task task) { _ = task.ContinueWith(t => { var ignored = t.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default); }
        private static IntegrationException Protocol() => new IntegrationException(IntegrationErrorCode.BulkBaselineJobFailed, "Native baseline-set protocol is incomplete/invalid; no state was assumed.");
    }
    public static class NativeBaselineSetProtocol
    {
        // Explicit LOGICAL locator: a definition has no verified GUID perm in this implementation.
        // Rename changes locator. Never present this as a native persistent definition identity.
        public static string DefinitionId(string folderIdentity, string name) => "logical-definition:" + Part(folderIdentity) + ":" + Part(name);
        public static string SetId(string definition, NativeBaselineSetVersion version) => definition + ":set:" + version.Major + ":" + version.Minor + ":" + Part(version.Suffix);
        private static string Part(string v) => Convert.ToBase64String(Encoding.UTF8.GetBytes(v));
        internal static NativeBaselineSetVersion Version(string major, string minor, string suffix) => new NativeBaselineSetVersion(int.Parse(major, CultureInfo.InvariantCulture), int.Parse(minor, CultureInfo.InvariantCulture), suffix);
        internal static string[] Line(string line, int count)
        {
            var fields = line.Split('\t'); if (fields.Length != count) throw new FormatException("Native baseline-set field count.");
            return fields.Select(Unescape).ToArray();
        }
        private static string Unescape(string value)
        {
            var result = new StringBuilder(); for (int i = 0; i < value.Length; i++)
            { if (value[i] != '\\') result.Append(value[i]); else { if (++i >= value.Length) throw new FormatException("Truncated escape."); switch (value[i]) { case '\\': result.Append('\\'); break; case 't': result.Append('\t'); break; case 'r': result.Append('\r'); break; case 'n': result.Append('\n'); break; default: throw new FormatException("Unknown escape."); } } }
            return result.ToString();
        }
        public static BaselineSetInspection Read(string text, Guid correlation, string folder, BaselineSetInspection evidence)
        {
            // Do not mutate a safety provider's cached evidence on a malformed protocol response.
            using (var copy = new MemoryStream()) { var serializer = new DataContractJsonSerializer(typeof(BaselineSetInspection)); serializer.WriteObject(copy, evidence); copy.Position = 0; evidence = (BaselineSetInspection)serializer.ReadObject(copy); }
            if (text == null || text.Length > 2097152 || !text.EndsWith("\n", StringComparison.Ordinal)) throw new FormatException("Incomplete native result.");
            var lines = text.Split('\n'); if (lines.Length > 20005 || lines[0] != "NATIVE_BASELINE_SET_READ\t1\t" + correlation.ToString("N") || lines[lines.Length - 2] != "END\tOK") throw new FormatException("Native scope/correlation is not complete.");
            evidence.Definitions.Clear(); evidence.Sets.Clear();
            foreach (var line in lines.Skip(1).Take(lines.Length - 3))
            {
                var fields = line.Split('\t'); string kind = fields[0]; var f = Line(line, kind == "D" ? 3 : kind == "M" ? 4 : kind == "S" ? 9 : kind == "B" ? 8 : 0);
                string id = DefinitionId(evidence.FolderIdentity, f[1]);
                if (kind == "D")
                { if (evidence.Definitions.Any(d => d.Id == id)) throw new FormatException("Duplicate definition."); evidence.Definitions.Add(new BaselineSetDefinitionDetails { Id = id, IdentityEvidence = "Logical folder identity + name; rename requires reconciliation", FolderPath = folder, Name = f[1], Description = f[2] }); continue; }
                var definition = evidence.Definitions.Single(d => d.Id == id);
                if (kind == "M")
                { var m = new BaselineSetModuleReference(new BaselineModuleReference(evidence.RepositoryId, f[2], f[3])); if (definition.Modules.Any(x => x.ModuleId == m.ModuleId)) throw new FormatException("Duplicate module."); definition.Modules.Add(m); continue; }
                var version = Version(f[2], f[3], f[4]); string setId = SetId(id, version);
                if (kind == "S")
                { if (evidence.Sets.Any(s => s.Id == setId)) throw new FormatException("Duplicate set."); evidence.Sets.Add(new NativeBaselineSetDetails { Id = setId, DefinitionId = id, Version = version, IsOpen = bool.Parse(f[5]), Annotation = f[6], Creator = f[7], CreatedAtUtc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(long.Parse(f[8], CultureInfo.InvariantCulture)) }); }
                else
                { var set = evidence.Sets.Single(s => s.Id == setId); var module = definition.Modules.Single(m => m.ModuleId == f[5] && m.FullPath == f[6]); if (set.Members.Any(m => m.Module.ModuleId == module.ModuleId) || string.IsNullOrEmpty(f[7])) throw new FormatException("Duplicate/unversioned member."); set.Members.Add(new BaselineSetModuleBaseline { Module = module, ModuleBaselineVersion = f[7], BaselineIdentity = setId + ":member:" + Part(module.ModuleId), Resolution = "Verified" }); }
            }
            if (evidence.Sets.GroupBy(s => s.DefinitionId).Any(g => g.Count(s => s.IsOpen) > 1)) throw new FormatException("Multiple open sets.");
            // Include actual enumeration in the immutable precondition fingerprint.
            return evidence;
        }
    }
}
