using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DoorsIntegration.Core
{
    public sealed class BaselineComparisonService : IBaselineComparisonService
    {
        private readonly IRequirementReader reader;
        private readonly IIntegrationLogger logger;
        public BaselineComparisonService(IRequirementReader reader, IIntegrationLogger logger)
        { this.reader = reader ?? throw new ArgumentNullException(nameof(reader)); this.logger = logger ?? throw new ArgumentNullException(nameof(logger)); }

        public Task<IBaselineComparisonResult> CompareAsync(BaselineComparisonRequest request, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return Task.Run(async () =>
            {
                var id = Guid.NewGuid(); BaselineComparisonResult result = null;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Log("BaselineComparisonStarted", id, "ReadOnly=True");
                    result = new BaselineComparisonResult(id, request);
                    using (var snapshot = await reader.ReadAsync(request.BeforeRead, cancellationToken).ConfigureAwait(false))
                        result.Capture(snapshot, request.BeforeRead, true, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    using (var snapshot = await reader.ReadAsync(request.AfterRead, cancellationToken).ConfigureAwait(false))
                        result.Capture(snapshot, request.AfterRead, false, cancellationToken);
                    result.Complete(cancellationToken);
                    Log("BaselineComparisonCompleted", id, "Conclusive=" + result.Summary.IsConclusive);
                    return (IBaselineComparisonResult)result;
                }
                catch (Exception ex)
                {
                    result?.Dispose();
                    Log("BaselineComparisonFailed", id, "ExceptionType=" + ex.GetType().FullName);
                    if (ex is IntegrationException || ex is OperationCanceledException) throw;
                    throw BaselineComparisonGuard.Failure("Baseline comparison failed; no complete comparison was published.", ex);
                }
            });
        }
        private void Log(string name, Guid id, string message)
        { try { logger.Write(IntegrationLogLevel.Information, name, id, message); } catch { /* Diagnostics must not disclose content or break read-only analysis. */ } }
    }

    // Payloads on disk, only absolute-ID/offset indices in memory. Each enumeration loads at most one pair.
    internal sealed class BaselineComparisonResult : IBaselineComparisonResult
    {
        private readonly object sync = new object();
        private readonly string root;
        private readonly FileStream spool;
        private readonly Dictionary<int, long> before = new Dictionary<int, long>(), after = new Dictionary<int, long>();
        private int[] identities;
        private bool disposed;
        public BaselineComparisonResult(Guid id, BaselineComparisonRequest request)
        {
            ComparisonId = id; ComparedAtUtc = DateTime.UtcNow; Request = request;
            root = Path.Combine(Path.GetTempPath(), "doors-baseline-" + id.ToString("N"));
            Directory.CreateDirectory(root);
            try { spool = new FileStream(Path.Combine(root, "objects.bin"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.DeleteOnClose); }
            catch { Directory.Delete(root, false); throw; }
        }
        public Guid ComparisonId { get; }
        public DateTime ComparedAtUtc { get; }
        public BaselineComparisonRequest Request { get; }
        public string QualifiedModuleId { get; private set; }
        public BaselineComparisonSummary Summary { get; private set; }

        internal void Capture(IRequirementSnapshot snapshot, RequirementReadRequest expected, bool isBefore, CancellationToken token)
        {
            var summary = snapshot?.Summary;
            if (summary == null || summary.Request == null || summary.Request.ModulePath != expected.ModulePath || summary.Request.Baseline == null ||
                BaselineComparisonRequest.Version(summary.Request.Baseline) != BaselineComparisonRequest.Version(expected.Baseline) ||
                !summary.Request.CustomAttributes.SequenceEqual(expected.CustomAttributes) || summary.Request.AllowAttributeDxl ||
                summary.Request.DeletedObjects != DeletedObjectMode.MetadataOnly || summary.ModuleStatus != RequirementModuleStatus.Available ||
                summary.Ownership == RequirementModuleOwnership.NotLoaded || summary.CleanupStatus == RequirementCleanupStatus.Failed ||
                summary.IsTruncated || summary.EnumerationFailed || summary.DeniedObjectCount != 0 || summary.UnavailableObjectCount != 0 ||
                summary.DeletedExcludedCount != 0 || string.IsNullOrWhiteSpace(summary.QualifiedModuleId))
                throw BaselineComparisonGuard.Failure("Baseline population or native module identity is unavailable/incomplete. Added/Deleted cannot be inferred safely.");
            new BaselineObjectIdentity(summary.QualifiedModuleId, 1);
            if (isBefore) QualifiedModuleId = summary.QualifiedModuleId;
            else if (QualifiedModuleId != summary.QualifiedModuleId)
                throw BaselineComparisonGuard.Failure("The selected baselines do not belong to the same native formal module.");
            var index = isBefore ? before : after;
            var expectedNames = new HashSet<string>(new[] { "Object Heading", "Object Text" }.Concat(expected.CustomAttributes), StringComparer.Ordinal);
            using (var writer = new BinaryWriter(spool, new UTF8Encoding(false, true), true))
            {
                foreach (var record in snapshot.ReadObjects())
                {
                    token.ThrowIfCancellationRequested();
                    ValidateRecord(record, expected, expectedNames);
                    if (index.Count >= expected.MaxObjects || index.ContainsKey(record.AbsoluteNumber))
                        throw BaselineComparisonGuard.Failure("Duplicate persistent object identity or object limit reached.");
                    index.Add(record.AbsoluteNumber, spool.Position);
                    WriteRecord(writer, record); writer.Flush();
                    if (spool.Position > Request.MaxSpoolBytes) throw BaselineComparisonGuard.Failure("Baseline comparison spool byte limit reached.");
                }
            }
            if (index.Count != summary.ObjectCount || summary.VisitedObjectCount != summary.ObjectCount)
                throw BaselineComparisonGuard.Failure("Baseline population accounting mismatch.");
        }

        private static void ValidateRecord(RequirementRecord record, RequirementReadRequest request, HashSet<string> expectedNames)
        {
            if (record == null || record.AbsoluteNumber <= 0 || record.ParentAbsoluteNumber == record.AbsoluteNumber || record.HierarchyLevel < 1 ||
                !Enum.IsDefined(typeof(RequirementParentStatus), record.ParentStatus) ||
                (record.ParentStatus == RequirementParentStatus.Resolved ? !record.ParentAbsoluteNumber.HasValue || record.ParentAbsoluteNumber <= 0 : record.ParentAbsoluteNumber.HasValue) ||
                (record.ParentStatus == RequirementParentStatus.Root && record.HierarchyLevel != 1) ||
                !expectedNames.SetEquals(record.Values.Keys)) throw BaselineComparisonGuard.Failure("Invalid object identity, hierarchy or requested attribute set.");
            long size = (long)(record.ObjectIdentifier?.Length ?? 0) + (record.ObjectNumber?.Length ?? 0);
            if (size > request.MaxFieldCharacters * 2L) throw BaselineComparisonGuard.Failure("Object metadata exceeds the configured bound.");
            foreach (var value in record.Values.Values)
            {
                if (value == null || value.Name == null || value.TypeName?.Length > 4096 ||
                    !Enum.IsDefined(typeof(RequirementValueStatus), value.PlainStatus) || !Enum.IsDefined(typeof(RequirementValueStatus), value.RichTextStatus) ||
                    (value.PlainStatus == RequirementValueStatus.Readable ? value.PlainText == null : !string.IsNullOrEmpty(value.PlainText)) ||
                    (value.RichTextStatus == RequirementValueStatus.Readable ? value.RichTextWithOle == null : !string.IsNullOrEmpty(value.RichTextWithOle)) ||
                    (value.PlainText?.Length ?? 0) > request.MaxFieldCharacters || (value.RichTextWithOle?.Length ?? 0) > request.MaxFieldCharacters)
                    throw BaselineComparisonGuard.Failure("Invalid or oversized attribute value/status.");
                size += (long)(value.PlainText?.Length ?? 0) + (value.RichTextWithOle?.Length ?? 0);
            }
            if (size > request.MaxObjectCharacters) throw BaselineComparisonGuard.Failure("Object payload exceeds the configured bound.");
        }

        internal void Complete(CancellationToken token)
        {
            identities = before.Keys.Union(after.Keys).OrderBy(value => value).ToArray();
            var counts = Enum.GetValues(typeof(BaselineChangeStatus)).Cast<BaselineChangeStatus>().ToDictionary(value => value, value => 0);
            bool conclusive = true; int normalized = 0;
            foreach (var change in ReadChanges(token))
            { counts[change.Status]++; conclusive &= change.IsConclusive; normalized += change.Attributes.Count(value => value.HasNormalizationDifference); }
            Summary = new BaselineComparisonSummary(counts, conclusive, normalized);
        }

        public IEnumerable<BaselineObjectChange> ReadChanges(CancellationToken cancellationToken = default(CancellationToken))
        {
            CheckDisposed();
            foreach (int identity in identities)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RequirementRecord oldValue, newValue;
                lock (sync) { CheckDisposed(); oldValue = ReadRecord(before, identity); newValue = ReadRecord(after, identity); }
                yield return Compare(new BaselineObjectIdentity(QualifiedModuleId, identity), oldValue, newValue);
            }
        }
        public IEnumerable<BaselineChangeEvent> ReadEvents(CancellationToken cancellationToken = default(CancellationToken))
        {
            foreach (var change in ReadChanges(cancellationToken))
            {
                if (change.Status == BaselineChangeStatus.Unchanged) continue;
                using (var sha = SHA256.Create())
                {
                    using (var crypto = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write))
                    using (var writer = new BinaryWriter(crypto, new UTF8Encoding(false, true)))
                    {
                        writer.Write("DoorsBaselineChangeEvent/v1"); writer.Write(change.Identity.Key);
                        writer.Write(BaselineComparisonRequest.Version(Request.Before)); writer.Write(BaselineComparisonRequest.Version(Request.After));
                        writer.Write((int)Request.Normalization); writer.Write((int)change.Status);
                        writer.Write(change.Before != null); if (change.Before != null) WriteRecord(writer, change.Before);
                        writer.Write(change.After != null); if (change.After != null) WriteRecord(writer, change.After);
                    }
                    yield return new BaselineChangeEvent(BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant(), this, change);
                }
            }
        }

        private BaselineObjectChange Compare(BaselineObjectIdentity identity, RequirementRecord oldValue, RequirementRecord newValue)
        {
            bool oldLive = oldValue != null && !oldValue.IsDeleted, newLive = newValue != null && !newValue.IsDeleted;
            var fields = new List<BaselineAttributeChange>();
            foreach (string name in new[] { "Object Heading", "Object Text" }.Concat(Request.BeforeRead.CustomAttributes).OrderBy(value => value, StringComparer.Ordinal))
            {
                RequirementValue left = null, right = null;
                if (oldValue != null) oldValue.Values.TryGetValue(name, out left);
                if (newValue != null) newValue.Values.TryGetValue(name, out right);
                fields.Add(CompareAttribute(name, left, right, oldLive, newLive));
            }
            bool hierarchyKnown = !(oldLive && oldValue.ParentStatus == RequirementParentStatus.Unavailable) && !(newLive && newValue.ParentStatus == RequirementParentStatus.Unavailable);
            bool hierarchy = oldLive && newLive && ((oldValue.HierarchyLevel != newValue.HierarchyLevel) ||
                (hierarchyKnown && (oldValue.ParentAbsoluteNumber != newValue.ParentAbsoluteNumber || oldValue.ParentStatus != newValue.ParentStatus)));
            bool conclusive = hierarchyKnown && fields.All(value => value.IsConclusive);
            bool modified = hierarchy || fields.Any(value => value.PlainTextChanged || value.RichTextChanged || value.TypeChanged);
            var status = !oldLive && newLive ? BaselineChangeStatus.Added : oldLive && !newLive ? BaselineChangeStatus.Deleted :
                !oldLive && !newLive ? BaselineChangeStatus.Unchanged : modified ? BaselineChangeStatus.Modified :
                conclusive ? BaselineChangeStatus.Unchanged : BaselineChangeStatus.Indeterminate;
            return new BaselineObjectChange(identity, oldValue, newValue, status, hierarchy, conclusive, fields);
        }

        private BaselineAttributeChange CompareAttribute(string name, RequirementValue left, RequirementValue right, bool oldLive, bool newLive)
            => BaselineAttributeComparison.Compare(name, left, right, oldLive, newLive, Request.Normalization);

        internal static BaselineAttributeChange CompareSharedAttribute(string name, RequirementValue left, RequirementValue right, bool oldLive, bool newLive, BaselineTextNormalization normalization)
        {
            if (!oldLive || !newLive)
            {
                bool lifecycleKnown = (!oldLive || BaselineComparisonGuard.Available(left)) && (!newLive || BaselineComparisonGuard.Available(right));
                return new BaselineAttributeChange(name, left, right, newLive ? BaselineAttributeStatus.Added : oldLive ? BaselineAttributeStatus.Deleted : BaselineAttributeStatus.Unchanged,
                    false, false, false, false, lifecycleKnown);
            }
            bool plainComparable = left != null && right != null && left.PlainStatus == RequirementValueStatus.Readable && right.PlainStatus == RequirementValueStatus.Readable;
            bool plain = plainComparable && BaselineComparisonGuard.Normalize(left.PlainText, normalization) != BaselineComparisonGuard.Normalize(right.PlainText, normalization);
            bool normalized = plainComparable && !plain && left.PlainText != right.PlainText;
            bool richComparable = left != null && right != null && left.RichTextStatus == RequirementValueStatus.Readable && right.RichTextStatus == RequirementValueStatus.Readable;
            bool rich = richComparable && left.RichTextWithOle != right.RichTextWithOle;
            bool type = !string.IsNullOrEmpty(left?.TypeName) && !string.IsNullOrEmpty(right?.TypeName) && left.TypeName != right.TypeName;
            bool known = BaselineComparisonGuard.Available(left) && BaselineComparisonGuard.Available(right) && left.RichTextStatus == right.RichTextStatus;
            var status = plain || rich || type ? BaselineAttributeStatus.Modified : !known ? BaselineAttributeStatus.Unavailable : normalized ? BaselineAttributeStatus.NormalizationOnly : BaselineAttributeStatus.Unchanged;
            return new BaselineAttributeChange(name, left, right, status, plain, rich, type, normalized, known);
        }

        private RequirementRecord ReadRecord(Dictionary<int, long> index, int id)
        {
            if (!index.TryGetValue(id, out long offset)) return null;
            spool.Position = offset;
            using (var input = new BinaryReader(spool, new UTF8Encoding(false, true), true))
            {
                int order = input.ReadInt32(), absolute = input.ReadInt32(); string identifier = ReadString(input), number = ReadString(input);
                var numberStatus = (RequirementValueStatus)input.ReadInt32(); int level = input.ReadInt32(), parent = input.ReadInt32();
                var parentStatus = (RequirementParentStatus)input.ReadInt32(); bool deleted = input.ReadBoolean(); int count = input.ReadInt32();
                var values = new List<RequirementValue>(count);
                for (int i = 0; i < count; i++) values.Add(new RequirementValue(ReadString(input), ReadString(input), (RequirementValueStatus)input.ReadInt32(),
                    ReadString(input), (RequirementValueStatus)input.ReadInt32(), ReadString(input)));
                return new RequirementRecord(order, absolute, identifier, number, numberStatus, level, parent == 0 ? (int?)null : parent, parentStatus, deleted, values);
            }
        }
        private static void WriteRecord(BinaryWriter output, RequirementRecord record)
        {
            output.Write(record.SourceOrder); output.Write(record.AbsoluteNumber); WriteString(output, record.ObjectIdentifier); WriteString(output, record.ObjectNumber);
            output.Write((int)record.NumberStatus); output.Write(record.HierarchyLevel); output.Write(record.ParentAbsoluteNumber ?? 0); output.Write((int)record.ParentStatus);
            output.Write(record.IsDeleted); output.Write(record.Values.Count);
            foreach (var value in record.Values.Values.OrderBy(value => value.Name, StringComparer.Ordinal))
            { WriteString(output, value.Name); WriteString(output, value.TypeName); output.Write((int)value.PlainStatus); WriteString(output, value.PlainText); output.Write((int)value.RichTextStatus); WriteString(output, value.RichTextWithOle); }
        }
        private static void WriteString(BinaryWriter output, string text) { output.Write(text != null); if (text != null) output.Write(text); }
        private static string ReadString(BinaryReader input) => input.ReadBoolean() ? input.ReadString() : null;
        private void CheckDisposed() { if (disposed) throw new ObjectDisposedException(nameof(IBaselineComparisonResult)); }
        public void Dispose()
        {
            lock (sync)
            {
                if (disposed) return; disposed = true; spool.Dispose();
                // Only this instance's exact file (DeleteOnClose) and empty directory; never recursive cleanup.
                Directory.Delete(root, false);
            }
        }
    }
}
