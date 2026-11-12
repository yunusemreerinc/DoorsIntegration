using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;

namespace DoorsIntegration.Dxl
{
    public sealed class NativeReadQuery
    {
        public NativeReadQuery(DoorsVersion target, string folder = "", IEnumerable<string> modules = null, IEnumerable<string> links = null)
        {
            DoorsVersions.Format(target); Target = target;
            Folder = string.IsNullOrEmpty(folder) ? "" : PathValue(folder);
            Modules = Paths(modules, 128); Links = Paths(links, 32);
        }
        private static string PathValue(string path)
        { if (path == null || path.Any(char.IsControl)) throw new ArgumentException("Control characters in native query path."); return DoorsItemPath.Normalize(path); }
        private static string[] Paths(IEnumerable<string> paths, int limit)
        { var result = (paths ?? Array.Empty<string>()).Select(PathValue).Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToArray(); if (result.Length > limit) throw new ArgumentException("Native query scope exceeds limit."); return result; }
        public DoorsVersion Target { get; }
        public string Folder { get; }
        public IReadOnlyList<string> Modules { get; }
        public IReadOnlyList<string> Links { get; }
    }
    public interface INativeReadObservationSource
    { Task<BaselineSetInspection> ReadAsync(NativeReadQuery query, CancellationToken token); }
    public sealed class NativeReadScriptOptions
    {
        public string ScriptPath { get; set; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scripts", "dxl", "native-read-snapshot.dxl");
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(90);
    }
    // Observation only: no retry, write authorization, definition refresh, lock or save.
    public sealed class DxlNativeReadObservationSource : INativeReadObservationSource
    {
        private readonly IDxlExecutor executor; private readonly NativeReadScriptOptions scripts;
        public DxlNativeReadObservationSource(IDxlExecutor executor, NativeReadScriptOptions scripts)
        { this.executor = executor ?? throw new ArgumentNullException(nameof(executor)); this.scripts = scripts ?? throw new ArgumentNullException(nameof(scripts)); }
        public async Task<BaselineSetInspection> ReadAsync(NativeReadQuery query, CancellationToken token)
        {
            if (query == null) throw new ArgumentNullException(nameof(query)); token.ThrowIfCancellationRequested();
            if (!(executor is DxlExecutor queued) || !queued.UsesStaQueue) throw new InvalidOperationException("Native read requires the shared STA execution queue.");
            var values = new Dictionary<string, string> { ["folder"] = query.Folder, ["modules"] = string.Join("\n", query.Modules), ["links"] = string.Join("\n", query.Links) };
            var operation = executor.Start(new DxlExecutionRequest(scripts.ScriptPath, DxlInvocationMethod.RunFile, DxlResultMode.File, values, scripts.Timeout,
                DoorsConnectionMode.AttachExisting, query.Target, maxResultFileBytes: 16777216) { DispatchPrecondition = () => !token.IsCancellationRequested });
            var completion = ParseAsync(operation, query);
            _ = completion.ContinueWith(t => { var observed = t.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            // Completion owns the file even if the caller stops waiting. COM is never aborted.
            await operation.WaitAsync(token).ConfigureAwait(false); return await completion.ConfigureAwait(false);
        }
        private static async Task<BaselineSetInspection> ParseAsync(DxlExecutionOperation operation, NativeReadQuery query)
        {
            using (var result = await operation.Completion.ConfigureAwait(false))
            {
                if (!result.IsFile) throw Failure();
                try { using (var stream = result.OpenResultStream()) return NativeReadObservationProtocol.Read(stream, operation.CorrelationId, query); }
                catch (Exception ex) when (ex is FormatException || ex is ArgumentException || ex is IOException || ex is OverflowException)
                { throw new IntegrationException(IntegrationErrorCode.BaselineComparisonFailed, "Native read response failed identity, scope or protocol validation.", ex); }
            }
        }
        private static IntegrationException Failure() => new IntegrationException(IntegrationErrorCode.BaselineComparisonFailed, "Native read requires a file response.");
    }
    public static class NativeReadObservationProtocol
    {
        private static T Defined<T>(string value) where T : struct
        { if (!Enum.TryParse(value, out T result) || !Enum.IsDefined(typeof(T), result) || result.ToString() != value) throw new FormatException("Invalid native state."); return result; }
        private static DoorsItemKind Kind(string value)
        { switch (value) { case "Formal": return DoorsItemKind.FormalModule; case "Link": return DoorsItemKind.LinkModule; case "Project": return DoorsItemKind.Project; case "Folder": return DoorsItemKind.Folder; case "Descriptive": return DoorsItemKind.DescriptiveModule; default: return DoorsItemKind.Unknown; } }
        public static BaselineSetInspection Read(Stream input, Guid correlation, NativeReadQuery query)
        {
            var result = new BaselineSetInspection { Source = BaselineMetadataSource.NativeDxl, ReadOnlyObservation = true, VisibilityLimited = true, DefinitionCacheUnknown = true,
                Reason = "Read-only observation. Definition cache freshness, hidden membership, lock ownership and reliable content/ACL/lock revisions are unproven. Writes remain blocked; runtime acceptance is NOT_RUN until tested on the exact client." };
            var definitions = new Dictionary<string, BaselineSetDefinitionDetails>(StringComparer.Ordinal);
            var sets = new Dictionary<string, NativeBaselineSetDetails>(StringComparer.Ordinal); var semantic = new StringBuilder(); int count = 0; bool ended = false, folderSeen = false;
            using (var reader = new StreamReader(input, new UTF8Encoding(false, true), true, 16384, true))
            {
                if (reader.ReadLine() != "NATIVE_READ_SNAPSHOT\t1\t" + correlation.ToString("N")) throw new FormatException("Native read correlation mismatch.");
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length > 65536) throw new FormatException("Native row too large.");
                    if (line.StartsWith("END\t", StringComparison.Ordinal))
                    { var trailer = NativeBaselineSetProtocol.Line(line, 3); if (!bool.Parse(trailer[1]) || int.Parse(trailer[2], CultureInfo.InvariantCulture) != count || reader.ReadLine() != null) throw new FormatException("Native read incomplete or count mismatch."); ended = true; break; }
                    if (++count > 10000 || semantic.Length + line.Length > 2000000) throw new FormatException("Native read limit exceeded."); semantic.Append(line).Append('\n');
                    var tag = line.Split('\t')[0];
                    if (tag != "C" && !result.SessionObserved) throw new FormatException("Native context must precede data.");
                    if (tag == "C")
                    {
                        var f = NativeBaselineSetProtocol.Line(line, 5); string family = DoorsVersions.Format(query.Target);
                        if (result.SessionObserved || string.IsNullOrWhiteSpace(f[1]) || string.IsNullOrWhiteSpace(f[2]) || f[3] != family && !f[3].StartsWith(family + ".", StringComparison.Ordinal) || !int.TryParse(f[4], out int session) || session <= 0) throw new FormatException("Native session/client mismatch.");
                        result.DatabaseId = result.RepositoryId = f[1]; result.UserId = f[2]; result.ClientVersion = f[3]; result.SessionId = f[4]; result.SessionObserved = true;
                    }
                    else if (tag == "F")
                    {
                        var f = NativeBaselineSetProtocol.Line(line, 7); if (folderSeen || query.Folder.Length == 0 || f[1] != query.Folder || string.IsNullOrWhiteSpace(f[2])) throw new FormatException("Native folder mismatch.");
                        folderSeen = true; result.FolderIdentity = f[2]; result.FolderKind = Kind(f[3]); result.FolderExists = true;
                        if (result.FolderKind != DoorsItemKind.Project && result.FolderKind != DoorsItemKind.Folder) throw new FormatException("Invalid definition folder.");
                        result.CanRead = bool.Parse(f[4]); result.CanCreate = bool.Parse(f[5]); result.CanModify = bool.Parse(f[6]);
                    }
                    else if (tag == "D")
                    {
                        var f = NativeBaselineSetProtocol.Line(line, 3); if (!folderSeen || !result.CanRead || definitions.ContainsKey(f[1])) throw new FormatException("Unexpected definition.");
                        var d = new BaselineSetDefinitionDetails { Id = NativeBaselineSetProtocol.DefinitionId(result.FolderIdentity, f[1]), IdentityEvidence = "Folder qualified ID plus definition name locator; not a native immutable definition GUID.", FolderPath = query.Folder, Name = f[1], Description = f[2] };
                        definitions.Add(f[1], d); result.Definitions.Add(d);
                    }
                    else if (tag == "M")
                    {
                        var f = NativeBaselineSetProtocol.Line(line, 4); if (!definitions.TryGetValue(f[1], out var d) || d.Modules.Any(m => m.ModuleId == f[2])) throw new FormatException("Duplicate/unknown definition member.");
                        d.Modules.Add(new BaselineSetModuleReference(new BaselineModuleReference(result.RepositoryId, f[2], f[3])));
                    }
                    else if (tag == "S")
                    {
                        var f = NativeBaselineSetProtocol.Line(line, 9); if (!definitions.TryGetValue(f[1], out var d)) throw new FormatException("Unknown set definition.");
                        var version = new NativeBaselineSetVersion(int.Parse(f[2], CultureInfo.InvariantCulture), int.Parse(f[3], CultureInfo.InvariantCulture), f[4]); string id = NativeBaselineSetProtocol.SetId(d.Id, version);
                        if (sets.ContainsKey(id)) throw new FormatException("Duplicate set.");
                        var s = new NativeBaselineSetDetails { Id = id, DefinitionId = d.Id, Version = version, IsOpen = bool.Parse(f[5]), Annotation = f[6], Creator = f[7], CreatedAtUtc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(long.Parse(f[8], CultureInfo.InvariantCulture)) };
                        sets.Add(id, s); result.Sets.Add(s);
                    }
                    else if (tag == "B")
                    {
                        var f = NativeBaselineSetProtocol.Line(line, 12); if (!definitions.TryGetValue(f[1], out var d)) throw new FormatException("Unknown member definition.");
                        string id = NativeBaselineSetProtocol.SetId(d.Id, new NativeBaselineSetVersion(int.Parse(f[2], CultureInfo.InvariantCulture), int.Parse(f[3], CultureInfo.InvariantCulture), f[4]));
                        if (!sets.TryGetValue(id, out var s) || s.Members.Any(m => m.Module.ModuleId == f[5])) throw new FormatException("Duplicate/unknown set member.");
                        var state = Defined<BaselineVerificationReadState>(f[11]); if (state != BaselineVerificationReadState.Verified) throw new FormatException("Unproven native member version.");
                        var v = new BaselineVersionReference(result.RepositoryId, f[5], f[6], int.Parse(f[7], CultureInfo.InvariantCulture), int.Parse(f[8], CultureInfo.InvariantCulture), f[9]);
                        s.Members.Add(new BaselineSetModuleBaseline { Module = new BaselineSetModuleReference(new BaselineModuleReference(result.RepositoryId, f[5], f[6])), TypedVersion = v, ModuleBaselineVersion = f[10], BaselineIdentity = v.ModuleBaselineIdentity, Resolution = state.ToString() });
                    }
                    else if (tag == "P")
                    {
                        var f = NativeBaselineSetProtocol.Line(line, 13); if (!query.Modules.Contains(f[1]) || result.ModuleStates.Any(m => m.FullPath == f[1])) throw new FormatException("Module query scope mismatch.");
                        var state = new NativeModuleReadState { FullPath = f[1], ModuleIdentity = f[2], Kind = Kind(f[3]), Exists = Defined<BaselineCheckState>(f[4]), CanRead = Defined<BaselineCheckState>(f[5]), CanCreate = Defined<BaselineCheckState>(f[6]), CanModify = Defined<BaselineCheckState>(f[7]), EditState = Defined<BaselineEditState>(f[8]), LockState = Defined<BaselineLockState>(f[9]), Unsaved = Defined<BaselineUnsavedState>(f[10]), Shareable = Defined<BaselineShareableConfiguration>(f[11]), Reason = f[12] };
                        if (state.Exists == BaselineCheckState.Passed && string.IsNullOrWhiteSpace(state.ModuleIdentity)) throw new FormatException("Missing observed stable identity."); result.ModuleStates.Add(state);
                    }
                    else if (tag == "L")
                    {
                        var f = NativeBaselineSetProtocol.Line(line, 5); if (!query.Links.Contains(f[1]) || Kind(f[3]) != DoorsItemKind.LinkModule || !bool.Parse(f[4]) || result.ObservedLinkModuleIds.ContainsKey(f[1]) || string.IsNullOrWhiteSpace(f[2])) throw new FormatException("Link module scope mismatch.");
                        result.ObservedLinkModuleIds.Add(f[1], f[2]);
                    }
                    else throw new FormatException("Unknown native row.");
                }
            }
            if (!ended || !result.SessionObserved || query.Modules.Count != result.ModuleStates.Count) throw new FormatException("Truncated native observation.");
            using (var sha = SHA256.Create()) result.Revision = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(semantic.ToString()))).Replace("-", "");
            return result;
        }
    }
}
