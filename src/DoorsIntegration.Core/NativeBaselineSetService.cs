using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DoorsIntegration.Core
{
    public sealed class NativeBaselineSetService : INativeBaselineSetService
    {
        private readonly IBaselineSetAdapter adapter; private readonly IBaselineSetLinkReader links;
        private readonly IBaselineSetJobRepository jobs; private readonly IBaselineSetConfirmation confirmation;
        private readonly Func<bool> allowed; private readonly IIntegrationLogger logger;
        private readonly BaselineOperationCoordinator coordinator = new BaselineOperationCoordinator();
        public NativeBaselineSetService(IBaselineSetAdapter adapter, IBaselineSetLinkReader links, IBaselineSetJobRepository jobs,
            IBaselineSetConfirmation confirmation, IIntegrationLogger logger, Func<bool> writesAllowed = null)
        { this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter)); this.links = links ?? throw new ArgumentNullException(nameof(links)); this.jobs = jobs ?? throw new ArgumentNullException(nameof(jobs)); this.confirmation = confirmation ?? throw new ArgumentNullException(nameof(confirmation)); this.logger = logger ?? throw new ArgumentNullException(nameof(logger)); allowed = writesAllowed ?? (() => false); }
        public bool WritesValidated => adapter.WritesValidated;
        private async Task<BaselineSetInspection> Read(BaselineSetRequest r, CancellationToken t)
        {
            var result = BaselineSetSerialization.Clone(await adapter.InspectAsync(r, t).ConfigureAwait(false));
            if ((!result.Complete && !(result.ReadOnlyObservation && result.SessionObserved)) || !result.CanRead || result.Source != adapter.Source) throw new IntegrationException(IntegrationErrorCode.BulkBaselineJobFailed, "NOT_RUN / read unavailable: " + BaselineSetSerialization.Text(result.Reason));
            return result;
        }
        public async Task<IReadOnlyList<BaselineSetDefinitionDetails>> ListBaselineSetDefinitions(string folder, DoorsVersion target, CancellationToken token = default(CancellationToken)) => (await Read(new BaselineSetRequest(BaselineSetAction.UpdateDefinition, folder, target: target), token).ConfigureAwait(false)).Definitions;
        public async Task<BaselineSetDefinitionDetails> GetBaselineSetDefinition(string folder, string id, DoorsVersion target, CancellationToken token = default(CancellationToken)) => (await ListBaselineSetDefinitions(folder, target, token).ConfigureAwait(false)).SingleOrDefault(d => d.Id == id);
        public async Task<IReadOnlyList<BaselineSetModuleReference>> ListDefinitionModules(string folder, string id, DoorsVersion target, CancellationToken token = default(CancellationToken)) => (await GetBaselineSetDefinition(folder, id, target, token).ConfigureAwait(false))?.Modules ?? new List<BaselineSetModuleReference>();
        public async Task<IReadOnlyList<NativeBaselineSetDetails>> ListBaselineSets(string folder, string id, DoorsVersion target, CancellationToken token = default(CancellationToken)) => (await Read(new BaselineSetRequest(BaselineSetAction.UpdateDefinition, folder, id, target: target), token).ConfigureAwait(false)).Sets.Where(s => s.DefinitionId == id).ToArray();
        public async Task<NativeBaselineSetDetails> GetBaselineSetDetails(string folder, string definition, string set, DoorsVersion target, CancellationToken token = default(CancellationToken)) => (await ListBaselineSets(folder, definition, target, token).ConfigureAwait(false)).SingleOrDefault(s => s.Id == set);
        public async Task<NativeBaselineSetDetails> GetOpenBaselineSet(string folder, string definition, DoorsVersion target, CancellationToken token = default(CancellationToken)) => (await ListBaselineSets(folder, definition, target, token).ConfigureAwait(false)).SingleOrDefault(s => s.IsOpen);
        public BaselineSetJob GetJob(Guid id) => jobs.Get(id);
        public IReadOnlyList<BaselineSetJob> ListJobs() => jobs.List();
        private BaselineSetJob Origin(string setId) => jobs.List().Where(j => j.SetId == setId && (j.Request.Action == BaselineSetAction.CreateMajorSet || j.Request.Action == BaselineSetAction.CreateMinorSet) && j.ReferenceLinks?.Complete == true).OrderBy(j => j.StartedAtUtc).FirstOrDefault();
        public async Task<BaselineSetPreflight> PreflightAsync(BaselineSetRequest request, CancellationToken token = default(CancellationToken))
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            request = new BaselineSetRequest(request.Action, request.FolderPath, request.DefinitionId, request.SetId, request.Name, request.Description, request.Modules.Select(m => m.Module), request.Suffix, request.Annotation, request.Target);
            var s = BaselineSetSerialization.Clone(await adapter.InspectAsync(request, token).ConfigureAwait(false)); var problems = new List<string>();
            if (!s.Complete || s.Source != adapter.Source || !s.CanRead) problems.Add("Read/state/session evidence unavailable: " + s.Reason);
            if (!s.FolderExists || (s.FolderKind != DoorsItemKind.Project && s.FolderKind != DoorsItemKind.Folder)) problems.Add("Target project/folder does not exist or is not a container.");
            if (!s.CanModify || !s.CanCreate) problems.Add("Create/modify permission denied or unknown.");
            if (s.LockedByOther) problems.Add("Definition locked by another session; automatic unlock is prohibited.");
            if (s.DirtyDefinition) problems.Add("Unsaved definition changes; no implicit save/discard/read-refresh.");
            if (string.IsNullOrEmpty(s.DatabaseId) || string.IsNullOrEmpty(s.UserId) || string.IsNullOrEmpty(s.SessionId) || string.IsNullOrEmpty(s.Revision)) problems.Add("Database/user/session/revision evidence missing.");
            if (!adapter.WritesValidated) problems.Add("NOT_RUN: release/build/API/link compatibility is not validated; real writes remain disabled.");
            if (s.Source == BaselineMetadataSource.NativeDxl && !s.NativeLinkBehaviorValidated) problems.Add("Native link behavior has not been validated.");
            problems.AddRange(s.ModuleProblems ?? new List<string>());
            var d = s.Definitions.SingleOrDefault(x => x.Id == request.DefinitionId); var set = s.Sets.SingleOrDefault(x => x.Id == request.SetId);
            if (d?.Modules.Count > 0 && request.Modules.Any(m => d.Modules.Any(x => x.RepositoryId != m.RepositoryId))) problems.Add("Definition membership cannot cross repository/database scope.");
            if (s.Source == BaselineMetadataSource.NativeDxl && (string.IsNullOrEmpty(s.RepositoryId) || request.Modules.Concat(d?.Modules ?? new List<BaselineSetModuleReference>()).Any(m => m.RepositoryId != s.RepositoryId))) problems.Add("Native module repository identity does not match the inspected database scope.");
            bool creating = request.Action == BaselineSetAction.CreateMajorSet || request.Action == BaselineSetAction.CreateMinorSet;
            bool definitionEdit = request.Action == BaselineSetAction.UpdateDefinition || request.Action == BaselineSetAction.AddDefinitionModules || request.Action == BaselineSetAction.RemoveDefinitionModules;
            if (request.Action == BaselineSetAction.CreateDefinition)
            {
                if (string.IsNullOrWhiteSpace(request.Name) || request.Name.IndexOfAny(new[] { '/', '\\' }) >= 0) problems.Add("A valid definition name is required.");
                if (s.Definitions.Any(x => x.Name == request.Name)) problems.Add("Definition name collision.");
            }
            else if (d == null) problems.Add("Definition is missing/inaccessible; identity may have changed.");
            if (d != null)
            {
                if (request.Modules.Any(m => d.Modules.Any(x => x.ModuleId == m.ModuleId && !x.Module.Matches(m.Module)))) problems.Add("Selected stable identity conflicts with definition path/repository; rediscover the module.");
                int open = s.Sets.Count(x => x.DefinitionId == d.Id && x.IsOpen);
                if (open > 1 || (open > 0 && (creating || definitionEdit))) problems.Add("An open set exists; creating a second set or editing its definition is prohibited.");
                if (request.Action == BaselineSetAction.UpdateDefinition && (string.IsNullOrWhiteSpace(request.Name) || request.Name.IndexOfAny(new[] { '/', '\\' }) >= 0 || s.Definitions.Any(x => x.Id != d.Id && x.Name == request.Name))) problems.Add("Updated definition name is invalid or already exists.");
                if (creating && d.Modules.Count == 0) problems.Add("A set requires formal modules in its definition.");
                if (request.Action == BaselineSetAction.AddModuleBaselines)
                {
                    if (set == null || set.DefinitionId != d.Id || !set.IsOpen) problems.Add("An exact open set is required.");
                    if (request.Modules.Count == 0 || request.Modules.Any(m => !d.Modules.Any(x => x.Module.Matches(m.Module)))) problems.Add("Selected modules must belong to this definition with exact identities/paths.");
                    if (set != null && request.Modules.Any(m => set.Members.Any(x => x.Module.ModuleId == m.ModuleId))) problems.Add("A selected module is already baselined in this set; select missing modules only.");
                    if (s.Source == BaselineMetadataSource.NativeDxl && set != null && (set.Members.Count != 0 || request.Modules.Count != d.Modules.Count)) problems.Add("Native population must include all definition modules together; partial population requires manual recovery.");
                }
                if (request.Action == BaselineSetAction.CloseSet && (set == null || set.DefinitionId != d.Id || !set.IsOpen || BaselineSetJob.Missing(s, d.Id, request.SetId).Length != 0)) problems.Add("Missing modules, wrong definition or set not open; partial sets are never closed automatically.");
            }
            if ((request.Action == BaselineSetAction.AddDefinitionModules || request.Action == BaselineSetAction.RemoveDefinitionModules) && request.Modules.Count == 0) problems.Add("Select formal modules first.");
            BaselineSetLinkSnapshot reference = null;
            if (d != null && creating) reference = await links.CaptureAsync(d, null, token).ConfigureAwait(false);
            if (request.Action == BaselineSetAction.AddModuleBaselines || request.Action == BaselineSetAction.CloseSet)
            {
                var origin = Origin(request.SetId);
                reference = origin != null && origin.DefinitionId == request.DefinitionId && origin.Source == s.Source && origin.Before.DatabaseId == s.DatabaseId ? origin.ReferenceLinks : null;
                if (reference == null) problems.Add("A complete recorded pre-set link reference is required; inspect/reconcile the originating job.");
            }
            if ((creating || request.Action == BaselineSetAction.AddModuleBaselines || request.Action == BaselineSetAction.CloseSet) && reference?.Complete != true) problems.Add("Link reference is incomplete or unresolved; no integrity assumption is made.");
            if (request.Action == BaselineSetAction.CloseSet && d != null && set != null && reference != null)
            {
                var verification = BaselineSetLinks.Compare(reference, await links.CaptureAsync(d, set, token).ConfigureAwait(false), d, set);
                if (!verification.Verified) problems.Add("Pre-close traceability verification failed: " + string.Join("; ", verification.Problems.Concat(verification.Missing)));
            }
            if (jobs.List().Any(j => j.DefinitionId == request.DefinitionId && !string.IsNullOrEmpty(j.DefinitionId) && (j.Status == BaselineSetJobStatus.Running || j.Status == BaselineSetJobStatus.Unknown || j.Status == BaselineSetJobStatus.NeedsReconciliation))) problems.Add("Unresolved journal intent exists; reconcile before a new mutation.");
            return new BaselineSetPreflight(request, s, reference, problems.Distinct());
        }
        public Task<BaselineSetJob> ExecuteAsync(BaselineSetPreflight plan, CancellationToken token = default(CancellationToken)) => coordinator.RunAsync(async () =>
        {
            if (plan == null || !plan.Intact || !plan.Ready || DateTime.UtcNow >= plan.ExpiresAtUtc || !allowed()) throw new IntegrationException(IntegrationErrorCode.BulkBaselineJobFailed, "Fresh intact preflight and write mode are required.");
            using (jobs.Acquire())
            {
                var job = new BaselineSetJob { JobId = Guid.NewGuid(), Request = BaselineSetSerialization.Clone(plan.Request), Before = BaselineSetSerialization.Clone(plan.Inspection),
                    Source = adapter.Source, Status = BaselineSetJobStatus.Pending, StartedAtUtc = DateTime.UtcNow, PlanFingerprint = plan.Fingerprint, DefinitionId = plan.Request.DefinitionId, SetId = plan.Request.SetId, ReferenceLinks = plan.ReferenceLinks };
                jobs.Save(job);
                try
                {
                    job.ExplicitApproval = await confirmation.ConfirmAsync(plan, false, token).ConfigureAwait(false);
                    job.SecondRealApproval = adapter.Source == BaselineMetadataSource.NativeDxl && job.ExplicitApproval && await confirmation.ConfirmAsync(plan, true, token).ConfigureAwait(false);
                    if (!job.ExplicitApproval || (adapter.Source == BaselineMetadataSource.NativeDxl && !job.SecondRealApproval)) return Save(job, BaselineSetJobStatus.Declined, "Explicit approval was not granted.");
                    token.ThrowIfCancellationRequested();
                    var fresh = await PreflightAsync(plan.Request, token).ConfigureAwait(false);
                    if (!fresh.Ready || fresh.Inspection.Fingerprint != plan.Inspection.Fingerprint || BaselineSetSerialization.Hash(fresh.ReferenceLinks) != BaselineSetSerialization.Hash(plan.ReferenceLinks)) return Save(job, BaselineSetJobStatus.Blocked, "Preconditions or link reference changed after approval; run preflight again.");
                    var authorization = new BaselineSetAuthorization(plan, job.JobId, () => allowed() && !token.IsCancellationRequested && adapter.WritesValidated);
                    if (!authorization.Fresh) return Save(job, BaselineSetJobStatus.Blocked, "Approval expired or cancellation/read-only gate changed.");
                    Save(job, BaselineSetJobStatus.Running, "Durable intent; automatic retry is prohibited.", false);
                    BaselineSetAdapterResult result;
                    try { result = await adapter.ExecuteAsync(authorization, CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception) { return Save(job, BaselineSetJobStatus.Unknown, "Transport/COM outcome is uncertain. Reconcile exact state; do not retry blindly."); }
                    if (!string.IsNullOrEmpty(result.DefinitionId)) job.DefinitionId = result.DefinitionId;
                    if (!string.IsNullOrEmpty(result.SetId)) job.SetId = result.SetId;
                    if (result.Status == BaselineSetJobStatus.Unknown || result.Status == BaselineSetJobStatus.NeedsReconciliation) return Save(job, BaselineSetJobStatus.Unknown, result.Reason);
                    if (result.Status == BaselineSetJobStatus.Blocked) return Save(job, result.Status, result.Reason);
                    try { job.After = await adapter.InspectAsync(job.Request, CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception) { return Save(job, BaselineSetJobStatus.Unknown, "Post-operation read unavailable; verify the exact desired state."); }
                    if (!Desired(job, false)) return Save(job, job.MissingModules.Count > 0 ? BaselineSetJobStatus.Partial : BaselineSetJobStatus.NeedsReconciliation, "Desired state was not completely verified; retain the set and inspect missing/unknown modules.");
                    await CheckLinks(job, CancellationToken.None).ConfigureAwait(false);
                    return Save(job, job.LinkVerification != null && !job.LinkVerification.Verified ? BaselineSetJobStatus.NeedsReconciliation : result.Status, result.Reason);
                }
                catch (OperationCanceledException) { return Save(job, BaselineSetJobStatus.Blocked, "Cancelled before dispatch; active native calls are never assumed abortable."); }
            }
        }, token);
        private BaselineSetJob Save(BaselineSetJob job, BaselineSetJobStatus status, string reason, bool terminal = true)
        {
            job.Status = status; job.Reason = BaselineSetSerialization.Text(reason); job.Revision++;
            if (terminal) job.EndedAtUtc = DateTime.UtcNow;
            jobs.Save(job);
            try { logger.Write(IntegrationLogLevel.Information, "NativeBaselineSetCheckpoint", job.JobId, "Revision=" + job.Revision + "; Action=" + job.Request.Action + "; Status=" + status + "; AutomaticRetry=False"); } catch (Exception) { }
            return BaselineSetSerialization.Clone(job);
        }
        private async Task CheckLinks(BaselineSetJob job, CancellationToken token)
        {
            if (job.Request.Action != BaselineSetAction.AddModuleBaselines && job.Request.Action != BaselineSetAction.CloseSet) return;
            var d = job.After.Definitions.SingleOrDefault(x => x.Id == job.DefinitionId); var s = job.After.Sets.SingleOrDefault(x => x.Id == job.SetId);
            if (d != null && s != null) job.LinkVerification = BaselineSetLinks.Compare(job.ReferenceLinks, await links.CaptureAsync(d, s, token).ConfigureAwait(false), d, s);
        }
        private static bool Desired(BaselineSetJob job, bool reconciliation)
        {
            var after = job.After; if (after == null || !after.Complete || !after.CanRead || after.Source != job.Source || after.DatabaseId != job.Before.DatabaseId || after.UserId != job.Before.UserId) return false;
            var r = job.Request; var d = after.Definitions.SingleOrDefault(x => x.Id == job.DefinitionId);
            if (r.Action == BaselineSetAction.CreateDefinition && d == null)
            { var found = after.Definitions.Where(x => x.Name == r.Name && x.FolderPath == r.FolderPath && x.Description == r.Description).ToArray(); if (found.Length == 1) { d = found[0]; job.DefinitionId = d.Id; } }
            if (d == null) return false;
            if (r.Action == BaselineSetAction.CreateDefinition || r.Action == BaselineSetAction.UpdateDefinition) return d.Name == r.Name && d.Description == r.Description &&
                (r.Action != BaselineSetAction.CreateDefinition || d.Modules.Count == r.Modules.Count && r.Modules.All(m => d.Modules.Any(x => x.Module.Matches(m.Module))));
            if (r.Action == BaselineSetAction.AddDefinitionModules) return r.Modules.All(m => d.Modules.Any(x => x.Module.Matches(m.Module)));
            if (r.Action == BaselineSetAction.RemoveDefinitionModules) return r.Modules.All(m => d.Modules.All(x => x.ModuleId != m.ModuleId));
            var set = after.Sets.SingleOrDefault(x => x.Id == job.SetId);
            if ((r.Action == BaselineSetAction.CreateMajorSet || r.Action == BaselineSetAction.CreateMinorSet) && set == null)
            {
                var candidates = after.Sets.Where(x => x.DefinitionId == d.Id && !job.Before.Sets.Any(old => old.Id == x.Id) && x.Annotation == r.Annotation && x.Version.Suffix == r.Suffix).ToArray();
                if (candidates.Length == 1) { set = candidates[0]; job.SetId = set.Id; }
            }
            if (set == null || set.DefinitionId != d.Id) return false;
            if (r.Action == BaselineSetAction.CreateMajorSet || r.Action == BaselineSetAction.CreateMinorSet) return set.IsOpen && set.Annotation == r.Annotation && set.Version.Suffix == r.Suffix && !job.Before.Sets.Any(x => x.Id == set.Id);
            if (r.Action == BaselineSetAction.AddModuleBaselines) return r.Modules.All(m => set.Members.Any(x => x.Module.Module.Matches(m.Module) && x.Resolution == "Verified" && !string.IsNullOrEmpty(x.ModuleBaselineVersion)));
            return !set.IsOpen && BaselineSetJob.Missing(after, d.Id, set.Id).Length == 0;
        }
        public Task<BaselineSetJob> ReconcileAsync(Guid jobId, CancellationToken token = default(CancellationToken)) => coordinator.RunAsync(async () =>
        {
            using (jobs.Acquire())
            {
                var job = jobs.Get(jobId) ?? throw new ArgumentException("Unknown set job.");
                if (job.Status == BaselineSetJobStatus.Pending || job.Status == BaselineSetJobStatus.Declined || job.Status == BaselineSetJobStatus.Blocked) return job;
                try { job.After = await adapter.InspectAsync(job.Request, token).ConfigureAwait(false); }
                catch (Exception) { return Save(job, BaselineSetJobStatus.NeedsReconciliation, "Read-only reconciliation unavailable; no retry was issued."); }
                if (!Desired(job, true)) return Save(job, BaselineSetJobStatus.NeedsReconciliation, "Exact state remains incomplete/ambiguous. No create, close, delete or rollback was repeated.");
                await CheckLinks(job, token).ConfigureAwait(false);
                return Save(job, job.LinkVerification != null && !job.LinkVerification.Verified ? BaselineSetJobStatus.NeedsReconciliation : BaselineSetJobStatus.Succeeded, "Desired state observed read-only; this is not causal ownership proof. No write repeated.");
            }
        }, token);
        public async Task<BaselineSetLinkVerification> VerifyBaselineSet(string folder, string definition, string set, DoorsVersion target, CancellationToken token = default(CancellationToken))
        {
            var d = await GetBaselineSetDefinition(folder, definition, target, token).ConfigureAwait(false); var s = await GetBaselineSetDetails(folder, definition, set, target, token).ConfigureAwait(false);
            if (d == null || s == null) return new BaselineSetLinkVerification { Problems = new List<string> { "Definition/set unavailable." } };
            return BaselineSetLinks.Compare(Origin(set)?.ReferenceLinks, await links.CaptureAsync(d, s, token).ConfigureAwait(false), d, s);
        }
        private Task<BaselineSetJob> Named(BaselineSetPreflight p, BaselineSetAction a, CancellationToken t) { if (p.Request.Action != a) throw new ArgumentException("Operation/plan mismatch."); return ExecuteAsync(p, t); }
        public Task<BaselineSetJob> CreateBaselineSetDefinition(BaselineSetPreflight p, CancellationToken t = default(CancellationToken)) => Named(p, BaselineSetAction.CreateDefinition, t);
        public Task<BaselineSetJob> UpdateBaselineSetDefinition(BaselineSetPreflight p, CancellationToken t = default(CancellationToken)) => Named(p, BaselineSetAction.UpdateDefinition, t);
        public Task<BaselineSetJob> AddModulesToDefinition(BaselineSetPreflight p, CancellationToken t = default(CancellationToken)) => Named(p, BaselineSetAction.AddDefinitionModules, t);
        public Task<BaselineSetJob> RemoveModulesFromDefinition(BaselineSetPreflight p, CancellationToken t = default(CancellationToken)) => Named(p, BaselineSetAction.RemoveDefinitionModules, t);
        public Task<BaselineSetJob> CreateMajorBaselineSet(BaselineSetPreflight p, CancellationToken t = default(CancellationToken)) => Named(p, BaselineSetAction.CreateMajorSet, t);
        public Task<BaselineSetJob> CreateMinorBaselineSet(BaselineSetPreflight p, CancellationToken t = default(CancellationToken)) => Named(p, BaselineSetAction.CreateMinorSet, t);
        public Task<BaselineSetJob> AddModuleBaselinesToSet(BaselineSetPreflight p, CancellationToken t = default(CancellationToken)) => Named(p, BaselineSetAction.AddModuleBaselines, t);
        public Task<BaselineSetJob> CloseBaselineSet(BaselineSetPreflight p, CancellationToken t = default(CancellationToken)) => Named(p, BaselineSetAction.CloseSet, t);
    }
    public static class BaselineSetLinks
    {
        public static BaselineSetLinkVerification Compare(BaselineSetLinkSnapshot before, BaselineSetLinkSnapshot after, BaselineSetDefinitionDetails definition, NativeBaselineSetDetails set)
        {
            var result = new BaselineSetLinkVerification();
            if (before?.Complete != true || after?.Complete != true) result.Problems.Add("Link scope is incomplete/unresolved or pre-set reference is missing.");
            if (definition.Modules.Any(m => !set.Members.Any(x => x.Module.ModuleId == m.ModuleId && x.Resolution == "Verified"))) result.Problems.Add("Set membership is incomplete/unverified.");
            var old = (before?.Edges ?? new List<BaselineSetLinkEdge>()).Select(e => e.Key).ToArray(); var current = (after?.Edges ?? new List<BaselineSetLinkEdge>()).Select(e => e.Key).ToArray();
            foreach (string key in old.Concat(current).Distinct())
            { int delta = old.Count(x => x == key) - current.Count(x => x == key); if (delta > 0) result.Missing.Add(key + "; missing=" + delta); if (delta < 0) result.Unexpected.Add(key + "; unexpected=" + -delta); }
            foreach (var e in before?.Edges ?? new List<BaselineSetLinkEdge>())
                if (string.IsNullOrEmpty(e.LinkModuleId) || string.IsNullOrEmpty(e.SourceModuleId) || string.IsNullOrEmpty(e.TargetModuleId) || e.SourceObjectId <= 0 || e.TargetObjectId <= 0 || string.IsNullOrEmpty(e.SourceVersion) || string.IsNullOrEmpty(e.TargetVersion)) result.Problems.Add("Pre-set endpoint identity/version is unresolved.");
            foreach (var e in after?.Edges ?? new List<BaselineSetLinkEdge>())
            {
                if (string.IsNullOrEmpty(e.LinkModuleId) || string.IsNullOrEmpty(e.SourceModuleId) || string.IsNullOrEmpty(e.TargetModuleId) || e.SourceObjectId <= 0 || e.TargetObjectId <= 0) result.Problems.Add("Unresolved endpoint/link-module identity.");
                foreach (var endpoint in new[] { new { Id = e.SourceModuleId, Version = e.SourceVersion }, new { Id = e.TargetModuleId, Version = e.TargetVersion } })
                { var member = set.Members.SingleOrDefault(m => m.Module.ModuleId == endpoint.Id); if (string.IsNullOrEmpty(endpoint.Version) || member != null && endpoint.Version != member.ModuleBaselineVersion) result.Problems.Add("Wrong/unresolved module-baseline version binding: " + endpoint.Id); }
                var reference = before?.Edges.FirstOrDefault(x => x.Key == e.Key);
                if (reference != null && ((!set.Members.Any(m => m.Module.ModuleId == e.SourceModuleId) && reference.SourceVersion != e.SourceVersion) ||
                    (!set.Members.Any(m => m.Module.ModuleId == e.TargetModuleId) && reference.TargetVersion != e.TargetVersion))) result.Problems.Add("External endpoint version context changed: " + e.Key);
            }
            result.Verified = result.Problems.Count == 0 && result.Missing.Count == 0 && result.Unexpected.Count == 0; return result;
        }
    }
}
