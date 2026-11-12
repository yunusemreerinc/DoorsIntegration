using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;

namespace DoorsIntegration.Export
{
    public sealed class NativeBaselineSetReportService
    {
        public const string Disclosure = "Native baseline-set job evidence; not a replacement for DOORS history. Mock is not COM/DXL validation. Partial/Unknown is not full success. No automatic delete/rollback.";
        private readonly IBaselineSetJobRepository repository; private readonly IExportService export;
        public NativeBaselineSetReportService(IBaselineSetJobRepository repository, IExportService export) { this.repository = repository ?? throw new ArgumentNullException(nameof(repository)); this.export = export ?? throw new ArgumentNullException(nameof(export)); }
        public async Task<ExportResult> ExportAsync(Guid id, ExportRequest request, IProgress<ExportProgress> progress = null, CancellationToken token = default(CancellationToken))
        {
            var latest = repository.Get(id) ?? throw new ArgumentException("Unknown native baseline-set job.");
            // A captured immutable prefix; later reconciliation appends do not change this report.
            return await export.ExportAsync(request, new EnumerableExportSource(Rows(id, latest.Revision, token)), progress, token).ConfigureAwait(false);
        }
        private IEnumerable<ExportRecord> Rows(Guid id, long revision, CancellationToken token)
        {
            foreach (var job in repository.ReadHistory(id).TakeWhile(j => j.Revision <= revision))
            {
                token.ThrowIfCancellationRequested();
                yield return Row(ExportTableKind.NativeBaselineSetJobs, "jobId", id, "revision", job.Revision, "action", job.Request.Action, "source", job.Source, "status", job.Status, "reason", job.Reason,
                    "definitionId", job.DefinitionId, "setId", job.SetId, "operator", job.Before.UserId, "databaseId", job.Before.DatabaseId, "clientVersion", job.Before.ClientVersion,
                    "startedAtUtc", job.StartedAtUtc, "endedAtUtc", job.EndedAtUtc, "planFingerprint", job.PlanFingerprint, "explicitApproval", job.ExplicitApproval, "secondRealApproval", job.SecondRealApproval,
                    "requestedModules", string.Join("\n", job.Request.Modules.Select(m => m.ModuleId + " " + m.FullPath)), "name", job.Request.Name, "description", job.Request.Description, "suffix", job.Request.Suffix, "annotation", job.Request.Annotation, "disclosure", Disclosure);
                foreach (string phase in new[] { "Before", "After" })
                {
                    var snapshot = phase == "Before" ? job.Before : job.After; if (snapshot == null) continue;
                    foreach (var definition in snapshot.Definitions)
                        foreach (var module in definition.Modules.Count == 0 ? new BaselineSetModuleReference[] { null } : definition.Modules.ToArray())
                            yield return Row(ExportTableKind.NativeBaselineSetDefinitions, "jobId", id, "revision", job.Revision, "phase", phase, "definitionId", definition.Id, "identityEvidence", definition.IdentityEvidence, "folderPath", definition.FolderPath, "name", definition.Name, "description", definition.Description, "moduleId", module?.ModuleId, "modulePath", module?.FullPath);
                    foreach (var set in snapshot.Sets)
                    {
                        yield return Row(ExportTableKind.NativeBaselineSets, "jobId", id, "revision", job.Revision, "phase", phase, "setId", set.Id, "definitionId", set.DefinitionId, "setMajor", set.Version.Major, "setMinor", set.Version.Minor, "setSuffix", set.Version.Suffix, "annotation", set.Annotation, "creator", set.Creator, "createdAtUtc", set.CreatedAtUtc, "isOpen", set.IsOpen);
                        var definition = snapshot.Definitions.SingleOrDefault(d => d.Id == set.DefinitionId);
                        foreach (var module in definition?.Modules ?? new List<BaselineSetModuleReference>())
                        {
                            var member = set.Members.SingleOrDefault(m => m.Module.ModuleId == module.ModuleId);
                            yield return Row(ExportTableKind.NativeBaselineSetMembers, "jobId", id, "revision", job.Revision, "phase", phase, "setId", set.Id, "moduleId", module.ModuleId, "modulePath", module.FullPath, "moduleBaselineVersion", member?.ModuleBaselineVersion, "moduleBaselineIdentity", member?.BaselineIdentity, "resolution", member?.Resolution ?? "Missing", "missing", member == null);
                        }
                    }
                }
                foreach (var edge in job.ReferenceLinks?.Edges ?? new List<BaselineSetLinkEdge>())
                    yield return Row(ExportTableKind.NativeBaselineSetLinks, "jobId", id, "revision", job.Revision, "phase", "PreSetReference", "sourceModuleId", edge.SourceModuleId, "sourceObjectId", edge.SourceObjectId, "targetModuleId", edge.TargetModuleId, "targetObjectId", edge.TargetObjectId, "linkModuleId", edge.LinkModuleId, "sourceVersion", edge.SourceVersion, "targetVersion", edge.TargetVersion, "referenceComplete", job.ReferenceLinks.Complete);
                if (job.LinkVerification != null)
                    foreach (var problem in job.LinkVerification.Problems.Concat(job.LinkVerification.Missing).Concat(job.LinkVerification.Unexpected).DefaultIfEmpty("No mismatch in validated scope"))
                        yield return Row(ExportTableKind.NativeBaselineSetLinks, "jobId", id, "revision", job.Revision, "phase", "Verification", "verified", job.LinkVerification.Verified, "problem", problem);
            }
        }
        private static ExportRecord Row(ExportTableKind table, params object[] pairs)
        { var fields = new Dictionary<string, object>(StringComparer.Ordinal); for (int i = 0; i < pairs.Length; i += 2) { object value = pairs[i + 1]; if (value != null && !(value is DateTime) && !(value is bool) && !(value is int) && !(value is long)) value = BaselineAuditSanitizer.Text(value.ToString()); fields.Add((string)pairs[i], value); } return new ExportRecord(table, fields); }
    }
}
