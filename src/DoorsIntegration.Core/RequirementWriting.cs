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
    public enum RequirementWriteFormat { PlainText, DoorsRichText }
    public enum RequirementPositionKind { Keep, FirstInModule, After, FirstChild, LastChild }
    public enum RequirementWriteOutcome { Schema, Preview, Blocked, ContextChanged, Applied, PartialFailure, OutcomeUnknown }
    public enum RequirementWritePersistence { NotAttempted, Saved, Unknown }
    public enum RequirementWriteCleanup { NotLoaded, Closed, LeftOpenForReview, Failed }
    public enum RequirementWriteStepKind { Create, Position, Attribute, Save, Verify }
    public enum RequirementWriteStepStatus { Skipped, Applied, Failed }
    public enum RequirementWriteIssueSeverity { Warning, Error }
    public enum RequirementWriteIssueCode
    {
        ModuleUnavailable, NotFormalModule, AlreadyOpen, Locked, PermissionDenied, BaselineNotWritable,
        TargetUnavailable, InvalidHierarchy, AttributeMissing, AttributeNotWritable, UnsupportedAttribute,
        InvalidValue, RequiredValueMissing, RequiredRulesNotDeclared, EditRecheckRequired, StateTooLarge,
        NativeFailure, ContextChanged, ReadbackMismatch, CleanupFailed, AuditFailed
    }

    public sealed class RequirementWriteTarget
    {
        public RequirementWriteTarget(string modulePath, DoorsVersion clientVersion = DoorsVersion.V97,
            DoorsConnectionMode connectionMode = DoorsConnectionMode.AttachExisting, TimeSpan? timeout = null)
        {
            ModulePath = DoorsItemPath.Normalize(modulePath);
            DoorsVersions.Format(clientVersion);
            Timeout = timeout ?? TimeSpan.FromMinutes(2);
            if (ModulePath == "/" || (connectionMode != DoorsConnectionMode.AttachExisting && connectionMode != DoorsConnectionMode.Activate) ||
                Timeout < TimeSpan.FromMilliseconds(1) || Timeout > TimeSpan.FromMinutes(10)) throw Invalid();
            ClientVersion = clientVersion; ConnectionMode = connectionMode;
        }
        public string ModulePath { get; }
        public DoorsVersion ClientVersion { get; }
        public DoorsConnectionMode ConnectionMode { get; }
        public TimeSpan Timeout { get; }
        internal static IntegrationException Invalid() => new IntegrationException(IntegrationErrorCode.InvalidConfiguration, "Gereksinim yazma isteği veya sınırları geçersiz.");
        internal static void Text(string value, int max, bool lines)
        {
            if (value == null || value.Length > max || value.Any(c => char.IsControl(c) && !(lines && (c == '\r' || c == '\n' || c == '\t')))) throw Invalid();
            RequirementBaseline.ValidateUnicode(value);
        }
        internal static void AttributeName(string name)
        {
            Text(name, 256, false);
            if (string.IsNullOrWhiteSpace(name) || name != name.Trim()) throw Invalid();
        }
    }

    public sealed class RequirementAttributeChange
    {
        public RequirementAttributeChange(string name, string value, RequirementWriteFormat format = RequirementWriteFormat.PlainText,
            string expectedPlainText = null)
        {
            RequirementWriteTarget.AttributeName(name);
            RequirementWriteTarget.Text(value, 65536, true);
            if (!Enum.IsDefined(typeof(RequirementWriteFormat), format) ||
                new[] { "Absolute Number", "Object Identifier", "Object Number", "Created On", "Created By", "Last Modified On", "Last Modified By" }.Contains(name))
                throw RequirementWriteTarget.Invalid();
            if (format == RequirementWriteFormat.DoorsRichText)
            {
                RequirementWriteTarget.Text(expectedPlainText, 65536, true);
                if (!value.StartsWith("{", StringComparison.Ordinal) || !value.EndsWith("}", StringComparison.Ordinal)) throw RequirementWriteTarget.Invalid();
            }
            else if (expectedPlainText != null) throw RequirementWriteTarget.Invalid();
            Name = name; Value = value; Format = format; ExpectedPlainText = expectedPlainText;
        }
        public string Name { get; }
        public string Value { get; }
        public RequirementWriteFormat Format { get; }
        public string ExpectedPlainText { get; }
    }

    public sealed class RequirementPosition
    {
        public RequirementPosition(RequirementPositionKind kind, int? anchorAbsoluteNumber = null)
        {
            if (!Enum.IsDefined(typeof(RequirementPositionKind), kind) ||
                ((kind == RequirementPositionKind.Keep || kind == RequirementPositionKind.FirstInModule) ? anchorAbsoluteNumber.HasValue :
                    (!anchorAbsoluteNumber.HasValue || anchorAbsoluteNumber.Value <= 0))) throw RequirementWriteTarget.Invalid();
            Kind = kind; AnchorAbsoluteNumber = anchorAbsoluteNumber;
        }
        public RequirementPositionKind Kind { get; }
        public int? AnchorAbsoluteNumber { get; }
    }

    public sealed class RequirementWriteRequest
    {
        public RequirementWriteRequest(RequirementWriteTarget target, IEnumerable<RequirementAttributeChange> changes,
            int? absoluteNumber = null, RequirementPosition position = null, IEnumerable<string> requiredAttributes = null)
        {
            Target = target ?? throw new ArgumentNullException(nameof(target));
            Position = position ?? new RequirementPosition(absoluteNumber.HasValue ? RequirementPositionKind.Keep : RequirementPositionKind.FirstInModule);
            var values = (changes ?? Enumerable.Empty<RequirementAttributeChange>()).ToArray();
            if (values.Length > 32 || values.Any(v => v == null) || values.Select(v => v.Name).Distinct(StringComparer.Ordinal).Count() != values.Length ||
                absoluteNumber <= 0 || (!absoluteNumber.HasValue && (values.Length == 0 || Position.Kind == RequirementPositionKind.Keep)) ||
                (absoluteNumber.HasValue && Position.Kind == RequirementPositionKind.FirstInModule) ||
                (absoluteNumber.HasValue && values.Length == 0 && Position.Kind == RequirementPositionKind.Keep) ||
                (absoluteNumber.HasValue && Position.AnchorAbsoluteNumber == absoluteNumber)) throw RequirementWriteTarget.Invalid();
            var required = (requiredAttributes ?? Enumerable.Empty<string>()).ToArray();
            if (required.Length > 32 || required.Distinct(StringComparer.Ordinal).Count() != required.Length) throw RequirementWriteTarget.Invalid();
            foreach (var name in required) RequirementWriteTarget.AttributeName(name);
            Changes = new ReadOnlyCollection<RequirementAttributeChange>(values);
            RequiredAttributes = new ReadOnlyCollection<string>(required);
            AbsoluteNumber = absoluteNumber;
            AttributePayload = string.Concat(values.Select(v => Escape(v.Name) + "\t" + v.Format + "\t" + Escape(v.Value) + "\t" + Escape(v.ExpectedPlainText ?? "") + "\n"));
            if (AttributePayload.Length > 65536) throw RequirementWriteTarget.Invalid();
        }
        public RequirementWriteTarget Target { get; }
        public int? AbsoluteNumber { get; }
        public bool IsCreate => !AbsoluteNumber.HasValue;
        public RequirementPosition Position { get; }
        public IReadOnlyList<RequirementAttributeChange> Changes { get; }
        public IReadOnlyList<string> RequiredAttributes { get; }
        public string AttributePayload { get; }
        private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n");
    }

    public sealed class RequirementAttributeSchema
    {
        public RequirementAttributeSchema(string name, string typeName, string baseType, bool objectAttribute, bool generatedByDxl,
            bool system, bool multiValued, bool readable, bool writableHint, IEnumerable<string> enumerationValues)
        {
            Name = name; TypeName = typeName; BaseType = baseType; IsObjectAttribute = objectAttribute; IsGeneratedByDxl = generatedByDxl;
            IsSystem = system; IsMultiValued = multiValued; IsReadable = readable; IsWritableHint = writableHint;
            EnumerationValues = new ReadOnlyCollection<string>((enumerationValues ?? Enumerable.Empty<string>()).ToArray());
        }
        public string Name { get; }
        public string TypeName { get; }
        public string BaseType { get; }
        public bool IsObjectAttribute { get; }
        public bool IsGeneratedByDxl { get; }
        public bool IsSystem { get; }
        public bool IsMultiValued { get; }
        public bool IsReadable { get; }
        public bool IsWritableHint { get; }
        public IReadOnlyList<string> EnumerationValues { get; }
        public string RequiredRule => "NotDeclaredByStandardMetadata";
    }

    public sealed class RequirementWriteIssue
    {
        public RequirementWriteIssue(RequirementWriteIssueSeverity severity, RequirementWriteIssueCode code, string attributeName = "")
        { Severity = severity; Code = code; AttributeName = attributeName; }
        public RequirementWriteIssueSeverity Severity { get; }
        public RequirementWriteIssueCode Code { get; }
        public string AttributeName { get; }
    }

    public sealed class RequirementModuleSchema
    {
        public RequirementModuleSchema(Guid correlationId, string modulePath, string databaseId, string userName, string moduleId,
            string qualifiedModuleId, IEnumerable<RequirementAttributeSchema> attributes, IEnumerable<RequirementWriteIssue> issues)
        {
            CorrelationId = correlationId; ModulePath = modulePath; DatabaseId = databaseId; UserName = userName;
            ModuleId = moduleId; QualifiedModuleId = qualifiedModuleId;
            Attributes = new ReadOnlyCollection<RequirementAttributeSchema>(attributes.ToArray());
            Issues = new ReadOnlyCollection<RequirementWriteIssue>(issues.ToArray());
        }
        public Guid CorrelationId { get; }
        public string ModulePath { get; }
        public string DatabaseId { get; }
        public string UserName { get; }
        public string ModuleId { get; }
        public string QualifiedModuleId { get; }
        public IReadOnlyList<RequirementAttributeSchema> Attributes { get; }
        public IReadOnlyList<RequirementWriteIssue> Issues { get; }
        public bool IsAvailable => ModuleId.Length != 0 && QualifiedModuleId.Length != 0 && !Issues.Any(i => i.Severity == RequirementWriteIssueSeverity.Error);
    }

    public sealed class RequirementWriteStep
    {
        public RequirementWriteStep(RequirementWriteStepKind kind, string attributeName, RequirementWriteStepStatus status, RequirementWriteIssueCode? error = null)
        { Kind = kind; AttributeName = attributeName; Status = status; Error = error; }
        public RequirementWriteStepKind Kind { get; }
        public string AttributeName { get; }
        public RequirementWriteStepStatus Status { get; }
        public RequirementWriteIssueCode? Error { get; }
    }

    public sealed class RequirementAttributeBeforeValue
    {
        public RequirementAttributeBeforeValue(string name, string plainText, string richTextWithOle)
        { Name = name; PlainText = plainText; RichTextWithOle = richTextWithOle; }
        public string Name { get; }
        public string PlainText { get; }
        public string RichTextWithOle { get; }
    }

    public sealed class RequirementWriteResult
    {
        public RequirementWriteResult(RequirementModuleSchema schema, string beforeState, RequirementWriteOutcome outcome,
            RequirementWritePersistence persistence, RequirementWriteCleanup cleanup, int? absoluteNumber, string objectIdentifier,
            int? parentAbsoluteNumber, int hierarchyLevel, IEnumerable<RequirementWriteStep> steps, bool auditRecorded = true,
            IEnumerable<RequirementAttributeBeforeValue> beforeValues = null)
        {
            Schema = schema ?? throw new ArgumentNullException(nameof(schema)); BeforeState = beforeState;
            Outcome = outcome; Persistence = persistence; Cleanup = cleanup; AbsoluteNumber = absoluteNumber;
            ObjectIdentifier = objectIdentifier; ParentAbsoluteNumber = parentAbsoluteNumber; HierarchyLevel = hierarchyLevel;
            Steps = new ReadOnlyCollection<RequirementWriteStep>(steps.ToArray()); AuditRecorded = auditRecorded;
            BeforeValues = new ReadOnlyCollection<RequirementAttributeBeforeValue>((beforeValues ?? Enumerable.Empty<RequirementAttributeBeforeValue>()).ToArray());
        }
        public RequirementModuleSchema Schema { get; }
        public string BeforeState { get; }
        public RequirementWriteOutcome Outcome { get; }
        public RequirementWritePersistence Persistence { get; }
        public RequirementWriteCleanup Cleanup { get; }
        public int? AbsoluteNumber { get; }
        public string ObjectIdentifier { get; }
        public int? ParentAbsoluteNumber { get; }
        public int HierarchyLevel { get; }
        public IReadOnlyList<RequirementWriteStep> Steps { get; }
        public IReadOnlyList<RequirementAttributeBeforeValue> BeforeValues { get; }
        public bool AuditRecorded { get; }
        public bool Succeeded => Outcome == RequirementWriteOutcome.Applied && Persistence == RequirementWritePersistence.Saved && Cleanup == RequirementWriteCleanup.Closed && AuditRecorded;
        public bool CanApply => Outcome == RequirementWriteOutcome.Preview && Schema.IsAvailable &&
            Schema.DatabaseId.Length != 0 && Schema.UserName.Length != 0 && BeforeState.Length != 0 && Cleanup == RequirementWriteCleanup.Closed;
        public RequirementWriteResult WithAuditFailure() => new RequirementWriteResult(Schema, BeforeState, Outcome, Persistence,
            Cleanup, AbsoluteNumber, ObjectIdentifier, ParentAbsoluteNumber, HierarchyLevel, Steps, false, BeforeValues);
    }

    public sealed class RequirementWritePreview
    {
        private int consumed;
        private int authorized;
        internal RequirementWritePreview(object owner, RequirementWriteRequest request, RequirementWriteResult inspection)
        {
            Owner = owner; Request = request; Inspection = inspection; PlanId = Guid.NewGuid(); ExpiresAtUtc = DateTime.UtcNow.AddMinutes(5);
            using (var data = new MemoryStream())
            using (var writer = new BinaryWriter(data, new UTF8Encoding(false, true), true))
            using (var sha = SHA256.Create())
            {
                foreach (string value in new[] { "RequirementWrite-v1", request.Target.ModulePath, DoorsVersions.Format(request.Target.ClientVersion),
                    request.Target.ConnectionMode.ToString(), request.Target.Timeout.Ticks.ToString(CultureInfo.InvariantCulture),
                    request.AbsoluteNumber?.ToString(CultureInfo.InvariantCulture) ?? "", request.Position.Kind.ToString(),
                    request.Position.AnchorAbsoluteNumber?.ToString(CultureInfo.InvariantCulture) ?? "", request.AttributePayload,
                    string.Join("\n", request.RequiredAttributes), inspection.BeforeState, inspection.Schema.DatabaseId,
                    inspection.Schema.UserName, inspection.Schema.QualifiedModuleId }) writer.Write(value);
                writer.Flush(); ApprovalFingerprint = BitConverter.ToString(sha.ComputeHash(data.ToArray())).Replace("-", "").ToLowerInvariant();
            }
        }
        internal object Owner { get; }
        internal bool Consume() => Interlocked.CompareExchange(ref consumed, 1, 0) == 0;
        internal void Authorize() => Interlocked.CompareExchange(ref authorized, 1, 0);
        internal void Revoke() => Interlocked.Exchange(ref authorized, 2);
        public bool TryBeginCommit() => ExpiresAtUtc > DateTime.UtcNow && Interlocked.CompareExchange(ref authorized, 2, 1) == 1;
        public Guid PlanId { get; }
        public DateTime ExpiresAtUtc { get; }
        public string ApprovalFingerprint { get; }
        public RequirementWriteRequest Request { get; }
        public RequirementWriteResult Inspection { get; }
        public bool CanApply => Inspection.CanApply;
    }

    public interface IRequirementWriteGateway
    {
        Task<RequirementModuleSchema> GetAttributeSchemaAsync(RequirementWriteTarget target, CancellationToken cancellationToken);
        Task<RequirementWriteResult> PreviewAsync(RequirementWriteRequest request, CancellationToken cancellationToken);
        Task<RequirementWriteResult> CommitAsync(RequirementWritePreview preview, CancellationToken cancellationToken);
    }
    public interface IRequirementWriteConfirmation
    {
        Task<bool> ConfirmAsync(RequirementWritePreview preview, CancellationToken cancellationToken);
    }
    public sealed class RejectRequirementWriteConfirmation : IRequirementWriteConfirmation
    {
        public Task<bool> ConfirmAsync(RequirementWritePreview preview, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(false); }
    }
    public sealed class RequirementAttributeAudit
    {
        internal RequirementAttributeAudit(RequirementAttributeChange change, RequirementAttributeBeforeValue before)
        {
            Name = change.Name; Format = change.Format; RequestedSha256 = Digest(change.Value);
            BeforePlainSha256 = before == null ? "" : Digest(before.PlainText);
            BeforeRichSha256 = before == null ? "" : Digest(before.RichTextWithOle);
        }
        public string Name { get; }
        public RequirementWriteFormat Format { get; }
        public string RequestedSha256 { get; }
        public string BeforePlainSha256 { get; }
        public string BeforeRichSha256 { get; }
        private static string Digest(string value)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant();
        }
    }

    public sealed class RequirementWriteAuditEntry
    {
        public RequirementWriteAuditEntry(string phase, Guid planId, Guid operationId, string fingerprint,
            string modulePath, string actor = "", string databaseId = "", string moduleId = "", RequirementWriteResult result = null,
            RequirementWriteRequest request = null, RequirementWriteResult inspection = null)
        {
            Phase = phase; PlanId = planId; OperationId = operationId; Fingerprint = fingerprint; ModulePath = modulePath;
            Actor = actor; DatabaseId = databaseId; ModuleId = moduleId; Result = result; TimestampUtc = DateTime.UtcNow;
            Action = request == null ? "" : request.IsCreate ? "Create" : request.Changes.Count == 0 ? "Move" :
                request.Position.Kind == RequirementPositionKind.Keep ? "Update" : "UpdateAndMove";
            RequestedAbsoluteNumber = request?.AbsoluteNumber;
            Position = request?.Position.Kind.ToString() ?? "";
            AnchorAbsoluteNumber = request?.Position.AnchorAbsoluteNumber;
            var before = result?.BeforeValues ?? inspection?.BeforeValues ?? new RequirementAttributeBeforeValue[0];
            AttributeChanges = new ReadOnlyCollection<RequirementAttributeAudit>((request?.Changes ?? new RequirementAttributeChange[0]).Select(c =>
                new RequirementAttributeAudit(c, before.FirstOrDefault(v => v.Name == c.Name))).ToArray());
        }
        public string Phase { get; }
        public Guid PlanId { get; }
        public Guid OperationId { get; }
        public string Fingerprint { get; }
        public string ModulePath { get; }
        public string Actor { get; }
        public string DatabaseId { get; }
        public string ModuleId { get; }
        public RequirementWriteResult Result { get; }
        public DateTime TimestampUtc { get; }
        public IReadOnlyList<RequirementAttributeAudit> AttributeChanges { get; }
        public string Action { get; }
        public int? RequestedAbsoluteNumber { get; }
        public string Position { get; }
        public int? AnchorAbsoluteNumber { get; }
    }
    public interface IRequirementWriteAuditSink { void Record(RequirementWriteAuditEntry entry); }

    public sealed class RequirementWriteUncertainException : IntegrationException
    {
        public RequirementWriteUncertainException(Guid planId, Guid operationId, Task<RequirementWriteResult> completion, Exception cause)
            : base(IntegrationErrorCode.RequirementWriteOutcomeUnknown, "Yazma sonucu belirsiz; çağrı devam edebilir. Completion ve modülü incelemeden işlemi tekrar göndermeyin.", cause)
        { PlanId = planId; OperationId = operationId; Completion = completion; }
        public Guid PlanId { get; }
        public Guid OperationId { get; }
        public Task<RequirementWriteResult> Completion { get; }
    }
}
