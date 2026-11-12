using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;

namespace DoorsIntegration.Dxl
{
    public sealed class BaselineLinkEvidenceScriptOptions
    { public string ScriptPath { get; set; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scripts", "dxl", "baseline-link-versions.dxl"); }
    public interface IBaselineLinkEvidenceCompatibility
    { bool Matches(BaselineSetVerificationRequest request, BaselineVerificationContext context, BaselineLinkEvidenceScriptOptions scripts); }
    public sealed class UnvalidatedBaselineLinkEvidenceCompatibility : IBaselineLinkEvidenceCompatibility
    { public bool Matches(BaselineSetVerificationRequest request, BaselineVerificationContext context, BaselineLinkEvidenceScriptOptions scripts) => false; }
    public sealed class ReviewedBaselineLinkEvidenceCompatibility : IBaselineLinkEvidenceCompatibility
    {
        private readonly DoorsVersion target; private readonly string client, database, user, hash;
        public ReviewedBaselineLinkEvidenceCompatibility(DoorsVersion target, string clientBuild, string database, string user, string scriptSha256, string releaseManualEvidence, string runtimeSourceTargetEchoEvidence)
        { this.target = target; client = Required(clientBuild); this.database = Required(database); this.user = Required(user); hash = Required(scriptSha256).ToUpperInvariant(); Required(releaseManualEvidence); Required(runtimeSourceTargetEchoEvidence);
            if (hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c)) || client != DoorsVersions.Format(target) && !client.StartsWith(DoorsVersions.Format(target) + ".", StringComparison.Ordinal)) throw new ArgumentException("Exact client build/script hash required."); }
        private static string Required(string s) => !string.IsNullOrWhiteSpace(s) && s.Length <= 4096 ? s : throw new ArgumentException("Reviewed native evidence is required.");
        public bool Matches(BaselineSetVerificationRequest request, BaselineVerificationContext context, BaselineLinkEvidenceScriptOptions scripts)
        { if (request.Target != target || context.Source != BaselineMetadataSource.NativeDxl || context.ClientVersion != client || context.EnvironmentId != database || context.Operator != user || !context.MetadataComplete) return false;
            using (var input = File.OpenRead(scripts.ScriptPath)) using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "") == hash; }
    }
    public sealed class DxlBaselineNativeLinkEvidenceReader : IBaselineNativeLinkEvidenceReader
    {
        private readonly IDxlExecutor executor; private readonly BaselineLinkEvidenceScriptOptions scripts; private readonly IBaselineLinkEvidenceCompatibility compatibility;
        public DxlBaselineNativeLinkEvidenceReader(IDxlExecutor executor, BaselineLinkEvidenceScriptOptions scripts, IBaselineLinkEvidenceCompatibility compatibility)
        { this.executor = executor ?? throw new ArgumentNullException(nameof(executor)); this.scripts = scripts ?? throw new ArgumentNullException(nameof(scripts)); this.compatibility = compatibility ?? throw new ArgumentNullException(nameof(compatibility)); }
        private bool Valid(BaselineSetVerificationRequest r, BaselineVerificationContext c)
        { try { return executor is DxlExecutor queued && queued.UsesStaQueue && compatibility.Matches(r, c, scripts); } catch (Exception) { return false; } }
        public async Task<BaselineNativeLinkEvidence> ReadAsync(BaselineSetVerificationRequest request, BaselineVerificationContext context, ModuleBaselineVerificationResult module, string linkModulePath, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!Valid(request, context) || module.Version == null || module.Version.IsCurrent && !(compatibility is ObservedReadOnlyLinkEvidenceCompatibility) || !module.Version.IdentityVerified || !context.LinkModuleIdentities.TryGetValue(linkModulePath, out string linkId))
                return new BaselineNativeLinkEvidence { Reason = "NOT_RUN: exact release/build/manual/runtime sourceVersion/targetVersion/echo evidence and native STA queue are required." };
            var values = new Dictionary<string, string> { ["path"] = module.FullPath, ["moduleId"] = module.ModuleIdentity, ["major"] = module.Version.Major.ToString(CultureInfo.InvariantCulture), ["minor"] = module.Version.Minor.ToString(CultureInfo.InvariantCulture), ["suffix"] = module.Version.Suffix,
                ["linkPath"] = linkModulePath, ["linkId"] = linkId, ["database"] = context.EnvironmentId, ["user"] = context.Operator, ["clientVersion"] = context.ClientVersion, ["maxObjects"] = request.MaxRequirements.ToString(CultureInfo.InvariantCulture), ["maxLinks"] = request.MaxLinks.ToString(CultureInfo.InvariantCulture), ["maxBytes"] = "67108864",
                ["current"] = module.Version.IsCurrent ? "true" : "false", ["session"] = context.SessionId ?? "" };
            var operation = executor.Start(new DxlExecutionRequest(scripts.ScriptPath, DxlInvocationMethod.RunFile, DxlResultMode.File, values, request.Timeout, DoorsConnectionMode.AttachExisting, request.Target, maxResultFileBytes: 67108864)
            { DispatchPrecondition = () => !token.IsCancellationRequested && Valid(request, context) });
            var completion = ParseCompletion(operation, context.RepositoryId, request.MaxLinks, linkModulePath, linkId);
            _ = completion.ContinueWith(t => { var observed = t.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            // The completion task owns late file cleanup. Timeout/cancellation cannot abort COM.
            await operation.WaitAsync(token).ConfigureAwait(false); return await completion.ConfigureAwait(false);
        }
        private static async Task<BaselineNativeLinkEvidence> ParseCompletion(DxlExecutionOperation operation, string repository, int limit, string path, string linkId)
        { using (var result = await operation.Completion.ConfigureAwait(false)) { if (!result.IsFile) throw Failure(); try { using (var stream = result.OpenResultStream()) return BaselineNativeLinkEvidenceProtocol.Read(stream, operation.CorrelationId, repository, limit, path, linkId); }
                catch (Exception ex) when (ex is FormatException || ex is System.Text.DecoderFallbackException || ex is IOException) { throw Failure(); } } }
        private static IntegrationException Failure() => new IntegrationException(IntegrationErrorCode.TraceabilityGraphFailed, "Native link version evidence is incomplete/invalid.");
    }
    public static class BaselineNativeLinkEvidenceProtocol
    {
        public static BaselineNativeLinkEvidence Read(Stream stream, Guid correlation, string repository, int maxLinks, string linkPath, string linkId)
        {
            var result = new BaselineNativeLinkEvidence(); var versions = new Dictionary<string, BaselineVersionReference>();
            using (var reader = new StreamReader(stream, new System.Text.UTF8Encoding(false, true), true, 16384, true))
            {
                if (reader.ReadLine() != "BASELINE_LINK_VERSIONS\t1\t" + correlation.ToString("N")) throw new FormatException("Native version evidence correlation mismatch.");
                string line; bool ended = false;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length > 65536) throw new FormatException("Native version row too large.");
                    if (line.StartsWith("END\t", StringComparison.Ordinal))
                    { var end = NativeBaselineSetProtocol.Line(line, 3); if (int.Parse(end[2], CultureInfo.InvariantCulture) != result.Links.Count || reader.ReadLine() != null) throw new FormatException("Native version evidence count/trailer mismatch."); result.Complete = bool.Parse(end[1]); ended = true; break; }
                    var f = NativeBaselineSetProtocol.Line(line, 23);
                    if (f[0] != "L" || f[3] != linkPath || f[4] != linkId || result.Links.Count >= maxLinks || !Enum.TryParse(f[1], out LinkDirection direction) || direction != LinkDirection.Outgoing && direction != LinkDirection.Incoming) throw new FormatException("Native version evidence scope/limit mismatch.");
                    var link = new BaselineLinkInfo { ObservationId = correlation.ToString("N") + ":" + (result.Links.Count + 1), LinkModulePath = linkPath, LinkModuleIdentity = linkId, Direction = direction, IsEchoed = bool.Parse(f[2]),
                        SourcePath = f[5], TargetPath = f[14], SourceObject = Number(f[12]), TargetObject = Number(f[21]), SourceAccess = State(f[13]), TargetAccess = State(f[22]),
                        ObservedSourceModuleIdentity = f[6], ObservedTargetModuleIdentity = f[15],
                        SourceVersion = Version(f, 5, repository, versions), TargetVersion = Version(f, 14, repository, versions), Evidence = direction == LinkDirection.Outgoing ? "Native sourceVersion(Link) + targetVersion(Link)" : "Native sourceVersion(LinkRef) + observed target anchor ModuleVersion; no targetVersion(LinkRef) assumption" };
                    result.Links.Add(link);
                }
                if (!ended) throw new FormatException("Truncated native version evidence.");
                result.Reason = result.Complete ? "Version evidence captured read-only; not physical multiplicity proof." : "Native read/permission/cleanup/limit incomplete; no absence or loss inference.";
                return result;
            }
        }
        private static int? Number(string value) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n > 0 ? (int?)n : null;
        private static BaselineVerificationReadState State(string value) => Enum.TryParse(value, out BaselineVerificationReadState state) && (state == BaselineVerificationReadState.Verified || state == BaselineVerificationReadState.AccessDenied || state == BaselineVerificationReadState.Unknown) ? state : throw new FormatException("Invalid endpoint read state.");
        private static BaselineVersionReference Version(string[] f, int at, string repository, Dictionary<string, BaselineVersionReference> versions)
        {
            if (string.IsNullOrEmpty(f[at + 1]) || f[at + 2] == "Unknown") return null;
            bool current = f[at + 2] == "Current"; if (!current && f[at + 2] != "Baseline") throw new FormatException("Invalid version kind.");
            string key = string.Join("\t", f.Skip(at).Take(7));
            if (!versions.TryGetValue(key, out var version)) versions.Add(key, version = new BaselineVersionReference(repository, f[at + 1], f[at], current ? 0 : int.Parse(f[at + 3], CultureInfo.InvariantCulture), current ? 0 : int.Parse(f[at + 4], CultureInfo.InvariantCulture), current ? "" : f[at + 5], current, State(f[at + 8]) == BaselineVerificationReadState.Verified));
            return version;
        }
    }
}
