using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;

namespace DoorsIntegration.Dxl
{
    // A read dispatch policy, never deployment/write validation. Exact-client runtime tests remain separate.
    public sealed class ObservedReadOnlyLinkEvidenceCompatibility : IBaselineLinkEvidenceCompatibility
    {
        private readonly string script, hash;
        public ObservedReadOnlyLinkEvidenceCompatibility(string script)
        { this.script = Path.GetFullPath(script); hash = Hash(script); }
        public bool Matches(BaselineSetVerificationRequest request, BaselineVerificationContext context, BaselineLinkEvidenceScriptOptions scripts)
        {
            string family = DoorsVersions.Format(request.Target);
            return context.Source == BaselineMetadataSource.NativeDxl && context.SessionObserved && !string.IsNullOrWhiteSpace(context.EnvironmentId) &&
                context.EnvironmentId == context.RepositoryId && !string.IsNullOrWhiteSpace(context.Operator) && int.TryParse(context.SessionId, out int session) && session > 0 &&
                (context.ClientVersion == family || context.ClientVersion?.StartsWith(family + ".", StringComparison.Ordinal) == true) &&
                Path.GetFullPath(scripts.ScriptPath) == script && hash == Hash(script);
        }
        private static string Hash(string path)
        { using (var input = File.OpenRead(path)) using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(input)).Replace("-", ""); }
    }
    public sealed class DxlReadOnlyLinkEvidenceReader : IBaselineNativeLinkEvidenceReader
    {
        private readonly DxlBaselineNativeLinkEvidenceReader reader;
        public DxlReadOnlyLinkEvidenceReader(IDxlExecutor executor)
        {
            var scripts = new BaselineLinkEvidenceScriptOptions { ScriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scripts", "dxl", "readonly-link-versions.dxl") };
            reader = new DxlBaselineNativeLinkEvidenceReader(executor, scripts, new ObservedReadOnlyLinkEvidenceCompatibility(scripts.ScriptPath));
        }
        public async Task<BaselineNativeLinkEvidence> ReadAsync(BaselineSetVerificationRequest request, BaselineVerificationContext context, ModuleBaselineVerificationResult module, string linkModulePath, CancellationToken token)
        {
            var result = await reader.ReadAsync(request, context, module, linkModulePath, token).ConfigureAwait(false);
            foreach (var link in result.Links)
            {
                var anchor = link.Direction == LinkDirection.Outgoing ? link.SourceVersion : link.TargetVersion;
                if (anchor == null || anchor.ModuleBaselineIdentity != module.Version?.ModuleBaselineIdentity || anchor.FullPath != module.FullPath)
                { result.Complete = false; result.Reason = "Native link anchor version/identity differs from selected snapshot; no version substitution."; }
            }
            return result;
        }
    }
}
