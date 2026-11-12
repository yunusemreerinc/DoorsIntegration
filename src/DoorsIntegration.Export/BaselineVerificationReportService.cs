using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;

namespace DoorsIntegration.Export
{
    public sealed class BaselineVerificationReportService
    {
        public const string Disclosure = "Read-only policy/evidence verification. Mock PASS does not validate COM/DXL. Unknown/Incomplete is not Passed. Link loss is not proven by inaccessible/echoed links. No automatic repair. Not a certification decision or replacement for DOORS history.";
        private readonly IExportService export;
        public BaselineVerificationReportService(IExportService export) { this.export = export ?? throw new ArgumentNullException(nameof(export)); }
        public Task<ExportResult> ExportAsync(BaselineSetVerificationResult result, ExportRequest request, IProgress<ExportProgress> progress = null, CancellationToken token = default(CancellationToken))
        { if (result == null) throw new ArgumentNullException(nameof(result)); return export.ExportAsync(request, new EnumerableExportSource(Rows(result, token)), progress, token); }
        private static IEnumerable<ExportRecord> Rows(BaselineSetVerificationResult r, CancellationToken token)
        {
            var s = r.Snapshot; var c = s?.Context; var id = r.VerificationJobId.ToString("N"); var set = c?.Set; var coverage = r.Coverage;
            yield return Row(ExportTableKind.Scopes, "scopeId", id, "sourceKind", "BaselineSetTraceabilityVerification", "isComplete", c?.MetadataComplete == true && coverage?.Conclusive == true && r.Findings.All(f => !f.Uncertain) && s?.Truncated != true && s?.ContextChanged != true,
                "visibility", "ExplicitPolicyAndRequestedBaselineScope", "details", Disclosure);
            yield return Row(ExportTableKind.BaselineVerificationJobs, "verificationJobId", id, "projectId", r.ProjectId, "source", c?.Source, "environment", c?.EnvironmentId, "operator", c?.Operator, "clientVersion", c?.ClientVersion, "definitionIdentity", c?.Definition?.Id, "definitionName", c?.Definition?.Name,
                "setIdentity", set?.Id, "setMajor", set?.Version.Major, "setMinor", set?.Version.Minor, "setSuffix", set?.Version.Suffix, "isOpen", set?.IsOpen, "moduleCount", c?.Modules.Count, "selectedModuleCount", c?.Modules.Count(m => m.SelectedForVerification), "missingBaselineCount", c?.Modules.Count(m => m.State == BaselineVerificationReadState.Missing),
                "startedAtUtc", r.StartedAtUtc, "completedAtUtc", r.CompletedAtUtc, "overallStatus", r.Status, "policyId", r.PolicyId, "policyFingerprint", r.PolicyFingerprint, "snapshotFingerprint", s?.Fingerprint, "referenceAvailable", r.ReferenceAvailable,
                "limitations", c?.Reason + "\n" + string.Join("\n", s?.Limitations ?? new List<string>()), "disclosure", Disclosure);
            foreach (var m in c?.Modules ?? new List<ModuleBaselineVerificationResult>())
            { token.ThrowIfCancellationRequested(); yield return Row(ExportTableKind.BaselineVerificationModules, "verificationJobId", id, "moduleIdentity", m.ModuleIdentity, "fullPath", m.FullPath, "moduleBaselineIdentity", m.Version?.ModuleBaselineIdentity, "major", m.Version?.Major, "minor", m.Version?.Minor, "suffix", m.Version?.Suffix, "version", m.Version?.Version,
                    "inDefinition", m.InDefinition, "inSet", m.InSet, "selectedForVerification", m.SelectedForVerification, "status", m.State, "reason", m.Reason, "shareableConfiguration", m.ShareableConfiguration, "inventoryComplete", m.InventoryComplete, "linksComplete", m.LinksComplete, "knownObjectCount", m.KnownObjectCount); }
            foreach (var n in s?.Requirements ?? new List<BaselineRequirementInfo>())
            { token.ThrowIfCancellationRequested(); yield return Row(ExportTableKind.BaselineVerificationRequirements, "verificationJobId", id, "nodeIdentity", n.NodeIdentity, "objectIdentity", n.ObjectIdentity, "moduleIdentity", n.Version?.ModuleIdentity, "moduleBaselineIdentity", n.Version?.ModuleBaselineIdentity, "version", n.Version?.Version, "absoluteNumber", n.AbsoluteNumber, "displayIdentifier", n.DisplayIdentifier, "requirementType", n.RequirementType, "requirementStatus", n.RequirementStatus, "access", n.Access, "isDeleted", n.IsDeleted, "heading", n.Heading, "text", n.Text, "richHeading", n.RichHeading, "richTextWithOle", n.RichText); }
            foreach (var v in r.Links)
            { token.ThrowIfCancellationRequested(); var l = v.Link; yield return Row(ExportTableKind.BaselineVerifiedLinks, "verificationJobId", id, "linkIdentity", l?.Identity?.VersionedKey, "observationId", l?.ObservationId, "sourceModuleIdentity", l?.SourceModuleIdentity, "sourceModuleBaselineIdentity", l?.SourceVersion?.ModuleBaselineIdentity, "sourceVersion", l?.SourceVersion?.Version, "sourceObject", l?.SourceObject,
                    "targetModuleIdentity", l?.TargetModuleIdentity, "targetModuleBaselineIdentity", l?.TargetVersion?.ModuleBaselineIdentity, "targetVersion", l?.TargetVersion?.Version, "targetObject", l?.TargetObject, "linkModuleIdentity", l?.LinkModuleIdentity, "linkModulePath", l?.LinkModulePath, "direction", l?.Direction, "echoed", l?.IsEchoed, "status", v.Status, "underlyingStatus", v.UnderlyingStatus, "countsAsValid", v.CountsAsValid, "reason", v.Reason, "evidence", l?.Evidence); }
            if (coverage != null) yield return Row(ExportTableKind.BaselineTraceabilityCoverage, "verificationJobId", id, "totalRequirements", coverage.TotalRequirements, "requirementsRequiringLinks", coverage.RequirementsRequiringLinks, "requirementsWithValidLinks", coverage.RequirementsWithValidLinks, "requirementsMissingLinks", coverage.RequirementsMissingLinks, "unresolvedRequirements", coverage.UnresolvedRequirements, "accessDeniedRequirements", coverage.AccessDeniedRequirements, "unknownPolicyEligibility", coverage.UnknownPolicyEligibility, "unenumeratedModules", coverage.UnenumeratedModules, "brokenChains", coverage.BrokenTraceabilityChains,
                "denominatorComplete", coverage.DenominatorComplete, "coveragePercentage", coverage.TraceabilityCoveragePercentage, "knownScopeLowerBoundPercentage", coverage.KnownScopeLowerBoundPercentage, "knownScopeUpperBoundPercentage", coverage.KnownScopeUpperBoundPercentage, "conclusive", coverage.Conclusive);
            foreach (var f in r.Findings)
            { token.ThrowIfCancellationRequested(); yield return Row(ExportTableKind.BaselineTraceabilityFindings, "verificationJobId", id, "code", f.Code, "severity", f.Severity, "moduleIdentity", f.ModuleIdentity, "nodeIdentity", f.NodeIdentity, "linkIdentity", f.LinkIdentity, "ruleId", f.RuleId, "evidence", f.Evidence, "explanation", f.Explanation, "recommendation", f.Recommendation, "uncertain", f.Uncertain); }
            foreach (var change in r.Changes)
            { token.ThrowIfCancellationRequested(); yield return Row(ExportTableKind.BaselineTraceabilityChanges, "verificationJobId", id, "changeKind", change.Kind, "beforeIdentity", change.BeforeIdentity, "afterIdentity", change.AfterIdentity, "confirmed", change.Confirmed, "suspectedLinkLoss", change.SuspectedLinkLoss, "evidence", change.Evidence); }
        }
        private static ExportRecord Row(ExportTableKind table, params object[] pairs)
        { var fields = new Dictionary<string, object>(StringComparer.Ordinal); for (int i = 0; i < pairs.Length; i += 2) { var value = pairs[i + 1]; if (value != null && !(value is bool) && !(value is int) && !(value is long) && !(value is double) && !(value is DateTime)) value = BaselineAuditSanitizer.Text(value.ToString()); fields.Add((string)pairs[i], value); } return new ExportRecord(table, fields); }
    }
}
