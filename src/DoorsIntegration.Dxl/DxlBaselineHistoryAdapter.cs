using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;

namespace DoorsIntegration.Dxl
{
    public sealed class BaselineMetadataScriptOptions
    {
        public string ScriptPath { get; set; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scripts", "dxl", "baseline-metadata.dxl");
    }
    public sealed class DxlBaselineHistoryAdapter : IBaselineHistoryAdapter, IBaselineNextVersionAdapter
    {
        public const string ApiEvidence = "IBM DXL Reference 9.7.2: baseline enumeration/fields, getMostRecentBaseline(false/true), baselineExists(Module,Baseline), intOf(Date); runtime NOT_RUN";
        private readonly IDxlExecutor executor;
        private readonly BaselineMetadataScriptOptions scripts;
        public DxlBaselineHistoryAdapter(IDxlExecutor executor, BaselineMetadataScriptOptions scripts)
        { this.executor = executor ?? throw new ArgumentNullException(nameof(executor)); this.scripts = scripts ?? throw new ArgumentNullException(nameof(scripts)); }
        public Task<BaselineMetadataSnapshot> ReadAsync(BaselineHistoryRequest request, CancellationToken cancellationToken) => ExecuteAsync(request, request?.CollisionTarget, false, cancellationToken);
        public async Task<BaselineCollisionResult> CheckCollisionAsync(BaselineHistoryRequest request, BaselineVersion target, CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (target == null) throw new ArgumentNullException(nameof(target));
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Target != DoorsVersion.V97)
                return new BaselineCollisionResult(target, BaselineExistenceStatus.Unknown, "NOT_RUN: 9.5 baselineExists compatibility has not been verified.");
            return (await ExecuteAsync(request, target, true, cancellationToken).ConfigureAwait(false)).Collision;
        }
        public Task<BaselineVersionProposal> ProposeAsync(BaselineMetadataSnapshot snapshot, BaselineType type, string suffix, CancellationToken cancellationToken)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            cancellationToken.ThrowIfCancellationRequested();
            // The documented nextMajor/nextMinor declarations do not prove safe read-only resolution of
            // symbolic handles (and their current-module dependency) on each target release.
            return Task.FromResult(new BaselineVersionProposal(type, suffix, BaselineProposalStatus.RequiresEnvironmentValidation, null,
                BaselineMetadataSource.NativeDxl, "IBM DXL nextMajor([string suffix]) / nextMinor([string suffix]); target-context handle resolution NOT_RUN",
                "No native version arithmetic or unverified perm was substituted for nextMajor/nextMinor.", snapshot.ObservationId, snapshot.Module));
        }
        private async Task<BaselineMetadataSnapshot> ExecuteAsync(BaselineHistoryRequest request, BaselineVersion collisionTarget, bool collisionOnly, CancellationToken token)
        {
            if (request == null) throw new ArgumentNullException(nameof(request)); token.ThrowIfCancellationRequested();
            // 9.5 declarations could not be verified against that release's manual in this environment.
            // Do not run 9.7-only reviewed code under a 9.5 target label.
            if (request.Target != DoorsVersion.V97)
                return BaselineMetadataSnapshot.Unavailable(request, BaselineHistoryStatus.ApiNotValidated,
                    "NOT_RUN: baseline-history DXL API compatibility for DOORS 9.5 is not verified.", "9.5 release-specific declaration review required");
            var values = new Dictionary<string, string>
            {
                ["path"] = request.Module.CanonicalFullPath, ["moduleId"] = request.Module.ModuleId, ["open"] = request.OpenIfClosed ? "1" : "0",
                ["maxBaselines"] = request.MaxBaselines.ToString(CultureInfo.InvariantCulture), ["maxChars"] = (request.MaxFileBytes / 4).ToString(CultureInfo.InvariantCulture),
                ["collision"] = collisionTarget != null ? "1" : "0", ["targetMajor"] = (collisionTarget?.Major ?? 0).ToString(CultureInfo.InvariantCulture),
                ["targetMinor"] = (collisionTarget?.Minor ?? 0).ToString(CultureInfo.InvariantCulture), ["targetSuffix"] = collisionTarget?.Suffix ?? "", ["onlyCollision"] = collisionOnly ? "1" : "0"
            };
            var operation = executor.Start(new DxlExecutionRequest(scripts.ScriptPath, DxlInvocationMethod.RunString, DxlResultMode.File,
                values, request.Timeout, request.ConnectionMode, request.Target, maxResultFileBytes: request.MaxFileBytes));
            try
            {
                using (var result = await operation.WaitAsync(token).ConfigureAwait(false))
                {
                    if (!result.IsFile) throw Failure();
                    using (var input = result.OpenResultStream())
                        return BaselineMetadataProtocol.Read(input, result.CorrelationId, request, collisionTarget, collisionOnly, token);
                }
            }
            catch (DxlExecutionWaitException)
            {
                _ = operation.Completion.ContinueWith(task =>
                {
                    if (task.Status == TaskStatus.RanToCompletion) { try { task.Result.Dispose(); } catch { } }
                    else { var observed = task.Exception; }
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                throw;
            }
        }
        internal static IntegrationException Failure() => new IntegrationException(IntegrationErrorCode.BaselineMetadataFailed,
            "Baseline history protocol/evidence validation failed; no complete metadata was published.");
    }

    internal static class BaselineMetadataProtocol
    {
        internal static BaselineMetadataSnapshot Read(Stream input, Guid id, BaselineHistoryRequest request, BaselineVersion target,
            bool collisionOnly, CancellationToken token)
        {
            try
            {
                using (var reader = new StreamReader(input, new UTF8Encoding(false, true), true, 65536, true))
                {
                    string Line()
                    {
                        token.ThrowIfCancellationRequested(); var value = new StringBuilder(); int c;
                        while ((c = reader.Read()) >= 0)
                        {
                            if (c == '\n') { if (value.Length > 0 && value[value.Length - 1] == '\r') value.Length--; return value.ToString(); }
                            if (c == 0 || value.Length >= 131072) throw DxlBaselineHistoryAdapter.Failure();
                            value.Append((char)c);
                        }
                        if (value.Length > 0) throw DxlBaselineHistoryAdapter.Failure();
                        return null;
                    }
                    if (Line() != "DOORS_BASELINE_METADATA\t1\t" + id.ToString("N")) throw DxlBaselineHistoryAdapter.Failure();
                    var module = Fields(Line(), 5, "M"); var status = EnumValue<BaselineHistoryStatus>(module[3]);
                    var ownership = EnumValue<RequirementModuleOwnership>(module[4]);
                    if (Decode(module[1]) != request.Module.CanonicalFullPath || (status == BaselineHistoryStatus.Available && Decode(module[2]) != request.Module.ModuleId)) throw DxlBaselineHistoryAdapter.Failure();
                    var history = new List<BaselineHistoryEntry>(); string line = Line();
                    while (line != null && line.StartsWith("B\t", StringComparison.Ordinal))
                    {
                        if (collisionOnly || history.Count >= request.MaxBaselines) throw DxlBaselineHistoryAdapter.Failure();
                        var row = Fields(line, 10, "B"); var version = Version(row, 1); long seconds;
                        if (!long.TryParse(row[6], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out seconds) || seconds < -1 || seconds > int.MaxValue) throw DxlBaselineHistoryAdapter.Failure();
                        DateTime? utc = seconds == -1 ? (DateTime?)null : new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds);
                        var exists = row[8] == "1" ? BaselineExistenceStatus.Exists : row[8] == "0" ? BaselineExistenceStatus.AbsentOrUnreadable : throw DxlBaselineHistoryAdapter.Failure();
                        string nativeIdentity = Decode(row[9]);
                        history.Add(new BaselineHistoryEntry(new BaselineIdentity(request.Module, version, nativeIdentity.Length == 0 ? null : nativeIdentity), Decode(row[4]), Decode(row[5]), utc, Decode(row[7]), exists));
                        line = Line();
                    }
                    var existing = Designation(Fields(line, 7, "L"), "Existing");
                    var numbering = Designation(Fields(Line(), 7, "L"), "Numbering");
                    var collisionRow = Fields(Line(), 5, "C");
                    var collisionStatus = EnumValue<BaselineExistenceStatus>(collisionRow[4]);
                    if (target == null ? collisionStatus != BaselineExistenceStatus.NotRequested || !Version(collisionRow, 1).Equals(new BaselineVersion(0, 0)) :
                        !target.Equals(Version(collisionRow, 1)) || collisionStatus == BaselineExistenceStatus.NotRequested) throw DxlBaselineHistoryAdapter.Failure();
                    var footer = Fields(Line(), 4, "END"); var cleanup = EnumValue<RequirementCleanupStatus>(footer[3]);
                    if (!int.TryParse(footer[1], NumberStyles.None, CultureInfo.InvariantCulture, out int count) || count != history.Count ||
                        (footer[2] != "0" && footer[2] != "1") || Line() != null) throw DxlBaselineHistoryAdapter.Failure();
                    bool complete = footer[2] == "0";
                    if ((collisionOnly && complete) || (status == BaselineHistoryStatus.Available && (ownership == RequirementModuleOwnership.NotLoaded || cleanup == RequirementCleanupStatus.Failed)) ||
                        (ownership == RequirementModuleOwnership.OwnedReadOnly && cleanup != RequirementCleanupStatus.Closed && cleanup != RequirementCleanupStatus.Failed) ||
                        (ownership != RequirementModuleOwnership.OwnedReadOnly && cleanup != RequirementCleanupStatus.NotOwned) ||
                        (status != BaselineHistoryStatus.Available && (history.Count != 0 || complete || existing.Status != BaselineDesignationStatus.Unknown || numbering.Status != BaselineDesignationStatus.Unknown ||
                            (collisionStatus != BaselineExistenceStatus.Unknown && collisionStatus != BaselineExistenceStatus.NotRequested)))) throw DxlBaselineHistoryAdapter.Failure();
                    if (!collisionOnly && status == BaselineHistoryStatus.Available && complete)
                    {
                        if (existing.Status == BaselineDesignationStatus.Available && !history.Exists(e => e.Identity.Version.Equals(existing.Version) && e.Existence == BaselineExistenceStatus.Exists)) throw DxlBaselineHistoryAdapter.Failure();
                        if (existing.Status == BaselineDesignationStatus.None && history.Exists(e => e.Existence == BaselineExistenceStatus.Exists)) throw DxlBaselineHistoryAdapter.Failure();
                        if (numbering.Status == BaselineDesignationStatus.None && (history.Count > 0 || existing.Status != BaselineDesignationStatus.None)) throw DxlBaselineHistoryAdapter.Failure();
                    }
                    return new BaselineMetadataSnapshot(id, request, status, history, existing, numbering, complete, ownership, cleanup, DateTime.UtcNow,
                        BaselineMetadataSource.NativeDxl, DxlBaselineHistoryAdapter.ApiEvidence,
                        new BaselineCollisionResult(target, collisionStatus, collisionStatus == BaselineExistenceStatus.AbsentOrUnreadable ?
                            "baselineExists=false; absence and lack of visibility are not safely distinguished. This does not authorize creation." : "Read-only baselineExists observation; no version reservation."),
                        status != BaselineHistoryStatus.Available ? "Read-only baseline history unavailable: " + status + ". No edit/open-mode conversion or automatic retry was attempted." :
                            !complete ? "History is partial or the requested operation was collision-only." : null);
                }
            }
            catch (Exception ex) when (ex is ArgumentException || ex is FormatException || ex is DecoderFallbackException || ex is OverflowException ||
                (ex is IntegrationException integration && integration.Code != IntegrationErrorCode.BaselineMetadataFailed))
            { throw DxlBaselineHistoryAdapter.Failure(); }
        }
        private static string[] Fields(string line, int count, string role)
        { var fields = line?.Split('\t'); if (fields == null || fields.Length != count || fields[0] != role) throw DxlBaselineHistoryAdapter.Failure(); return fields; }
        private static BaselineVersion Version(string[] fields, int start)
        {
            if (!int.TryParse(fields[start], NumberStyles.None, CultureInfo.InvariantCulture, out int major) || !int.TryParse(fields[start + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int minor)) throw DxlBaselineHistoryAdapter.Failure();
            return new BaselineVersion(major, minor, Decode(fields[start + 2]));
        }
        private static T EnumValue<T>(string token) where T : struct
        { if (!Enum.TryParse(token, false, out T value) || !Enum.IsDefined(typeof(T), value) || value.ToString() != token) throw DxlBaselineHistoryAdapter.Failure(); return value; }
        private static BaselineDesignation Designation(string[] fields, string role)
        {
            if (fields[1] != role) throw DxlBaselineHistoryAdapter.Failure();
            var status = EnumValue<BaselineDesignationStatus>(fields[2]); var version = Version(fields, 3); var exists = EnumValue<BaselineExistenceStatus>(fields[6]);
            if (status != BaselineDesignationStatus.Available && !version.Equals(new BaselineVersion(0, 0))) throw DxlBaselineHistoryAdapter.Failure();
            if (role == "Existing" && status == BaselineDesignationStatus.Available && exists != BaselineExistenceStatus.Exists) throw DxlBaselineHistoryAdapter.Failure();
            return new BaselineDesignation(status, status == BaselineDesignationStatus.Available ? version : null, exists);
        }
        private static string Decode(string field)
        {
            var decoded = new StringBuilder();
            for (int i = 0; i < field.Length; i++)
            {
                char c = field[i];
                if (c == '\r' || c == '\0') throw DxlBaselineHistoryAdapter.Failure();
                if (c != '\\') { decoded.Append(c); continue; }
                if (++i == field.Length) throw DxlBaselineHistoryAdapter.Failure();
                switch (field[i]) { case '\\': decoded.Append('\\'); break; case 'n': decoded.Append('\n'); break; case 'r': decoded.Append('\r'); break; case 't': decoded.Append('\t'); break; default: throw DxlBaselineHistoryAdapter.Failure(); }
            }
            return decoded.ToString();
        }
    }
}
