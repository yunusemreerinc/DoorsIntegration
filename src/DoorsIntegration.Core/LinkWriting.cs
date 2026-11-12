using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DoorsIntegration.Core
{
    public enum LinkWriteAction { Create, Remove }
    public enum LinkWriteOutcome { Preview, Blocked, ContextChanged, Applied, PartialFailure, OutcomeUnknown }
    public enum LinkWritePersistence { NotAttempted, Saved, Unknown }
    public enum LinkWriteCleanup { NotLoaded, Closed, LeftOpenForReview, Failed }
    public enum LinkWriteIssue
    {
        ModuleUnavailable, NotFormalModule, NotLinkModule, AlreadyOpen, Locked, PermissionDenied,
        BaselineNotWritable, ObjectUnavailable, LinksetMissing, LinksetAmbiguous, DuplicateLink,
        LinkMissing, AmbiguousLink, TraceabilityIncomplete, LimitExceeded, ContextChanged,
        NativeFailure, ReadbackMismatch, CleanupFailed
    }

    public sealed class LinkWriteRequest
    {
        public LinkWriteRequest(LinkWriteAction action, string sourceModulePath, int sourceAbsoluteNumber,
            string targetModulePath, int targetAbsoluteNumber, string linkModulePath,
            DoorsVersion clientVersion = DoorsVersion.V97, DoorsConnectionMode connectionMode = DoorsConnectionMode.AttachExisting,
            TimeSpan? timeout = null, int maxTraceLinks = 2048)
        {
            SourceModulePath = DoorsItemPath.Normalize(sourceModulePath);
            TargetModulePath = DoorsItemPath.Normalize(targetModulePath);
            LinkModulePath = DoorsItemPath.Normalize(linkModulePath);
            Timeout = timeout ?? TimeSpan.FromMinutes(2);
            DoorsVersions.Format(clientVersion);
            if (!Enum.IsDefined(typeof(LinkWriteAction), action) || sourceAbsoluteNumber <= 0 || targetAbsoluteNumber <= 0 ||
                SourceModulePath == "/" || TargetModulePath == "/" || LinkModulePath == "/" ||
                new[] { SourceModulePath, TargetModulePath, LinkModulePath }.Any(p => p.IndexOfAny(new[] { '*', '?' }) >= 0) ||
                LinkModulePath == SourceModulePath || LinkModulePath == TargetModulePath ||
                (connectionMode != DoorsConnectionMode.AttachExisting && connectionMode != DoorsConnectionMode.Activate) ||
                Timeout < TimeSpan.FromMilliseconds(1) || Timeout > TimeSpan.FromMinutes(10) || maxTraceLinks < 1 || maxTraceLinks > 2048)
                throw new IntegrationException(IntegrationErrorCode.InvalidConfiguration, "Link request requires exact module paths, positive absolute numbers and valid limits.");
            Action = action; SourceAbsoluteNumber = sourceAbsoluteNumber; TargetAbsoluteNumber = targetAbsoluteNumber;
            ClientVersion = clientVersion; ConnectionMode = connectionMode; MaxTraceLinks = maxTraceLinks;
        }
        public LinkWriteAction Action { get; }
        public string SourceModulePath { get; }
        public int SourceAbsoluteNumber { get; }
        public string TargetModulePath { get; }
        public int TargetAbsoluteNumber { get; }
        public string LinkModulePath { get; }
        public DoorsVersion ClientVersion { get; }
        public DoorsConnectionMode ConnectionMode { get; }
        public TimeSpan Timeout { get; }
        public int MaxTraceLinks { get; }
        public string DisplayTarget => SourceModulePath + " #" + SourceAbsoluteNumber.ToString(CultureInfo.InvariantCulture) +
            " -> " + TargetModulePath + " #" + TargetAbsoluteNumber.ToString(CultureInfo.InvariantCulture) + " via " + LinkModulePath;
    }

    // One edge in the selected source object's outgoing scope, including baseline targets.
    // Sequence numbers are never treated as persistent link IDs. Duplicate keys retain multiplicity.
    public sealed class LinkTraceEdge
    {
        public LinkTraceEdge(string targetModulePath, string targetModuleId, LinkVersionKind versionKind, string version, int targetAbsoluteNumber)
        {
            TargetModulePath = DoorsItemPath.Normalize(targetModulePath);
            if (TargetModulePath == "/" || string.IsNullOrWhiteSpace(targetModuleId) || targetModuleId.Length > 8192 || targetAbsoluteNumber <= 0 ||
                (versionKind != LinkVersionKind.Current && versionKind != LinkVersionKind.Baseline) ||
                version == null || version.Length > 256 || (versionKind == LinkVersionKind.Current ? version.Length != 0 : version.Length == 0))
                throw new ArgumentException("Invalid trace edge.");
            RequirementBaseline.ValidateUnicode(targetModuleId); RequirementBaseline.ValidateUnicode(version);
            TargetModuleId = targetModuleId; VersionKind = versionKind; Version = version; TargetAbsoluteNumber = targetAbsoluteNumber;
            Key = LinkWriteHash.Fields(TargetModulePath, TargetModuleId, VersionKind.ToString(), Version, TargetAbsoluteNumber.ToString(CultureInfo.InvariantCulture));
        }
        public string TargetModulePath { get; }
        public string TargetModuleId { get; }
        public LinkVersionKind VersionKind { get; }
        public string Version { get; }
        public int TargetAbsoluteNumber { get; }
        public string Key { get; }
    }

    public sealed class LinkTraceComparison
    {
        private LinkTraceComparison(IEnumerable<LinkTraceEdge> added, IEnumerable<LinkTraceEdge> removed)
        { Added = new ReadOnlyCollection<LinkTraceEdge>(added.ToArray()); Removed = new ReadOnlyCollection<LinkTraceEdge>(removed.ToArray()); }
        public IReadOnlyList<LinkTraceEdge> Added { get; }
        public IReadOnlyList<LinkTraceEdge> Removed { get; }
        public static LinkTraceComparison Compare(IEnumerable<LinkTraceEdge> before, IEnumerable<LinkTraceEdge> after)
        {
            if (before == null || after == null) throw new ArgumentNullException();
            var old = before.ToArray(); var added = new List<LinkTraceEdge>();
            var remaining = old.GroupBy(e => e.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            foreach (var edge in after)
            {
                if (remaining.TryGetValue(edge.Key, out int count) && count > 0) remaining[edge.Key] = count - 1;
                else added.Add(edge);
            }
            var removed = new List<LinkTraceEdge>();
            foreach (var edge in old)
            {
                int count = remaining[edge.Key];
                if (count > 0) { removed.Add(edge); remaining[edge.Key] = count - 1; }
            }
            return new LinkTraceComparison(added, removed);
        }
        public bool Matches(LinkWriteRequest request, string targetId)
        {
            var expected = new LinkTraceEdge(request.TargetModulePath, targetId, LinkVersionKind.Current, "", request.TargetAbsoluteNumber);
            return request.Action == LinkWriteAction.Create ? Removed.Count == 0 && Added.Count == 1 && Added[0].Key == expected.Key :
                Added.Count == 0 && Removed.Count == 1 && Removed[0].Key == expected.Key;
        }
    }

    public sealed class LinkWriteResult
    {
        public LinkWriteResult(Guid correlationId, string databaseId, string userName, string sourceModuleId, string targetModuleId,
            string linkModuleId, string sourceIdentifier, string targetIdentifier, string beforeState, LinkWriteOutcome outcome,
            LinkWritePersistence persistence, LinkWriteCleanup cleanup, IEnumerable<LinkWriteIssue> issues,
            IEnumerable<LinkTraceEdge> before, IEnumerable<LinkTraceEdge> after, bool afterVerified = false, bool auditRecorded = true)
        {
            CorrelationId = correlationId; DatabaseId = databaseId ?? ""; UserName = userName ?? "";
            SourceModuleId = sourceModuleId ?? ""; TargetModuleId = targetModuleId ?? ""; LinkModuleId = linkModuleId ?? "";
            SourceIdentifier = sourceIdentifier ?? ""; TargetIdentifier = targetIdentifier ?? ""; BeforeState = beforeState ?? "";
            Outcome = outcome; Persistence = persistence; Cleanup = cleanup;
            Issues = new ReadOnlyCollection<LinkWriteIssue>((issues ?? Enumerable.Empty<LinkWriteIssue>()).ToArray());
            Before = new ReadOnlyCollection<LinkTraceEdge>((before ?? Enumerable.Empty<LinkTraceEdge>()).ToArray());
            After = new ReadOnlyCollection<LinkTraceEdge>((after ?? Enumerable.Empty<LinkTraceEdge>()).ToArray());
            AfterVerified = afterVerified; AuditRecorded = auditRecorded;
        }
        public Guid CorrelationId { get; }
        public string DatabaseId { get; }
        public string UserName { get; }
        public string SourceModuleId { get; }
        public string TargetModuleId { get; }
        public string LinkModuleId { get; }
        public string SourceIdentifier { get; }
        public string TargetIdentifier { get; }
        public string BeforeState { get; }
        public LinkWriteOutcome Outcome { get; }
        public LinkWritePersistence Persistence { get; }
        public LinkWriteCleanup Cleanup { get; }
        public IReadOnlyList<LinkWriteIssue> Issues { get; }
        public IReadOnlyList<LinkTraceEdge> Before { get; }
        public IReadOnlyList<LinkTraceEdge> After { get; }
        public bool AfterVerified { get; }
        public bool AuditRecorded { get; }
        public string TraceabilityScope => "SelectedSourceObjectOutgoingViaExactLinkModule;ReferenceIdentitiesIncludingBaselines";
        public LinkTraceComparison Comparison => AfterVerified ? LinkTraceComparison.Compare(Before, After) : null;
        public bool CanApply => Outcome == LinkWriteOutcome.Preview && Issues.Count == 0 && Cleanup == LinkWriteCleanup.Closed &&
            Persistence == LinkWritePersistence.NotAttempted && BeforeState.Length != 0 && DatabaseId.Length != 0 && UserName.Length != 0 &&
            SourceModuleId.Length != 0 && TargetModuleId.Length != 0 && LinkModuleId.Length != 0 && SourceIdentifier.Length != 0 && TargetIdentifier.Length != 0;
        public bool Succeeded => Outcome == LinkWriteOutcome.Applied && Persistence == LinkWritePersistence.Saved &&
            Cleanup == LinkWriteCleanup.Closed && AfterVerified && Issues.Count == 0 && AuditRecorded;
        public LinkWriteResult WithAuditFailure() => new LinkWriteResult(CorrelationId, DatabaseId, UserName, SourceModuleId, TargetModuleId,
            LinkModuleId, SourceIdentifier, TargetIdentifier, BeforeState, Outcome, Persistence, Cleanup, Issues, Before, After, AfterVerified, false);
    }

    public sealed class LinkWritePreview
    {
        private int consumed, authorized;
        internal LinkWritePreview(object owner, LinkWriteRequest request, LinkWriteResult inspection)
        {
            Owner = owner; Request = request; Inspection = inspection; PlanId = Guid.NewGuid(); ExpiresAtUtc = DateTime.UtcNow.AddMinutes(5);
            ApprovalFingerprint = LinkWriteHash.Fields("LinkWrite-v1", request.Action.ToString(), request.SourceModulePath,
                request.SourceAbsoluteNumber.ToString(CultureInfo.InvariantCulture), request.TargetModulePath,
                request.TargetAbsoluteNumber.ToString(CultureInfo.InvariantCulture), request.LinkModulePath,
                DoorsVersions.Format(request.ClientVersion), request.ConnectionMode.ToString(), request.Timeout.Ticks.ToString(CultureInfo.InvariantCulture),
                request.MaxTraceLinks.ToString(CultureInfo.InvariantCulture), inspection.DatabaseId, inspection.UserName,
                inspection.SourceModuleId, inspection.TargetModuleId, inspection.LinkModuleId, inspection.SourceIdentifier,
                inspection.TargetIdentifier, inspection.BeforeState, string.Join(";", inspection.Before.Select(e => e.Key).OrderBy(k => k, StringComparer.Ordinal)));
        }
        internal object Owner { get; }
        internal bool Consume() => Interlocked.CompareExchange(ref consumed, 1, 0) == 0;
        internal void Authorize() => Interlocked.CompareExchange(ref authorized, 1, 0);
        internal void Revoke() => Interlocked.Exchange(ref authorized, 2);
        public bool TryBeginCommit() => ExpiresAtUtc > DateTime.UtcNow && Interlocked.CompareExchange(ref authorized, 2, 1) == 1;
        public Guid PlanId { get; }
        public DateTime ExpiresAtUtc { get; }
        public string ApprovalFingerprint { get; }
        public LinkWriteRequest Request { get; }
        public LinkWriteResult Inspection { get; }
        public bool CanApply
        {
            get
            {
                if (!Inspection.CanApply || Inspection.Before.Count > Request.MaxTraceLinks) return false;
                int count = Inspection.Before.Count(e => e.TargetModulePath == Request.TargetModulePath && e.TargetModuleId == Inspection.TargetModuleId &&
                    e.VersionKind == LinkVersionKind.Current && e.TargetAbsoluteNumber == Request.TargetAbsoluteNumber);
                return Request.Action == LinkWriteAction.Create ? count == 0 && Inspection.Before.Count < Request.MaxTraceLinks : count == 1;
            }
        }
    }

    public interface ILinkWriteGateway
    {
        Task<LinkWriteResult> PreviewAsync(LinkWriteRequest request, CancellationToken cancellationToken);
        Task<LinkWriteResult> CommitAsync(LinkWritePreview preview, CancellationToken cancellationToken);
    }
    public interface ILinkWriteConfirmation
    {
        Task<bool> ConfirmAsync(LinkWritePreview preview, CancellationToken cancellationToken);
        Task<bool> ConfirmRemovalAsync(LinkWritePreview preview, CancellationToken cancellationToken);
    }
    public sealed class RejectLinkWriteConfirmation : ILinkWriteConfirmation
    {
        public Task<bool> ConfirmAsync(LinkWritePreview preview, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(false); }
        public Task<bool> ConfirmRemovalAsync(LinkWritePreview preview, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(false); }
    }
    public sealed class LinkWriteAuditEntry
    {
        public LinkWriteAuditEntry(string phase, LinkWritePreview preview, LinkWriteResult result = null, Guid? operationId = null)
        {
            Phase = phase; Preview = preview ?? throw new ArgumentNullException(nameof(preview)); Result = result; TimestampUtc = DateTime.UtcNow;
            OperationId = operationId ?? result?.CorrelationId ?? preview.Inspection.CorrelationId;
        }
        public string Phase { get; }
        public LinkWritePreview Preview { get; }
        public LinkWriteResult Result { get; }
        public DateTime TimestampUtc { get; }
        public Guid OperationId { get; }
    }
    public interface ILinkWriteAuditSink { void Record(LinkWriteAuditEntry entry); }
    public sealed class LinkWriteUncertainException : IntegrationException
    {
        public LinkWriteUncertainException(Guid planId, Guid operationId, Task<LinkWriteResult> completion, Exception cause)
            : base(IntegrationErrorCode.LinkWriteOutcomeUnknown, "Link write outcome is uncertain. The COM call may continue; inspect Completion and DOORS before retrying.", cause)
        { PlanId = planId; OperationId = operationId; Completion = completion; }
        public Guid PlanId { get; }
        public Guid OperationId { get; }
        public Task<LinkWriteResult> Completion { get; }
    }
    internal static class LinkWriteHash
    {
        public static string Fields(params string[] values)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), true))
            using (var sha = SHA256.Create())
            {
                foreach (string value in values) writer.Write(value);
                writer.Flush(); return BitConverter.ToString(sha.ComputeHash(stream.ToArray())).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}
