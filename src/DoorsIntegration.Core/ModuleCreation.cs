using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DoorsIntegration.Core
{
    public enum CreationFolderStatus { Readable, DeniedOrNotFound, NotContainer, Unavailable }
    public enum CreationPermissionStatus { Allowed, Denied, Unknown }
    public enum CreationInputStatus { Valid, Invalid, Unknown }
    public enum CreationNameStatus { NoVisibleConflict, Conflict, Unknown }
    public enum CreationLockStatus { NoBlockingLockObserved, Locked, Unknown }
    public enum ModuleCreationOutcome { Preview, Blocked, ContextChanged, Created, OutcomeUnknown }
    public enum ModuleCreationCleanup { NotRequired, Closed, Failed }

    public sealed class FormalModuleCreateRequest
    {
        public FormalModuleCreateRequest(string targetFolder, string moduleName, string description,
            DoorsVersion target = DoorsVersion.V97, DoorsConnectionMode connectionMode = DoorsConnectionMode.AttachExisting,
            TimeSpan? timeout = null)
        {
            TargetFolder = DoorsItemPath.Normalize(targetFolder);
            if (string.IsNullOrWhiteSpace(moduleName) || moduleName != moduleName.Trim() || moduleName.Length > 255 ||
                moduleName == "." || moduleName == ".." || moduleName.IndexOf('/') >= 0 || moduleName.IndexOf('\\') >= 0)
                throw Invalid("Modül adı tek, boş olmayan bir yol bileşeni olmalıdır (en fazla 255 karakter).");
            ValidateText(moduleName, false);
            if (description == null || description.Length > 65536) throw Invalid("Modül açıklaması null olamaz ve en fazla 65536 karakter olabilir.");
            ValidateText(description, true);
            FullPath = DoorsItemPath.Normalize((TargetFolder == "/" ? "/" : TargetFolder + "/") + moduleName);
            DoorsVersions.Format(target);
            if (connectionMode != DoorsConnectionMode.AttachExisting && connectionMode != DoorsConnectionMode.Activate)
                throw Invalid("Geçersiz bağlantı modu.");
            Timeout = timeout ?? TimeSpan.FromSeconds(30);
            if (Timeout < TimeSpan.FromMilliseconds(1) || Timeout > TimeSpan.FromMinutes(10)) throw Invalid("Oluşturma bekleme süresi 1 ms ile 10 dakika arasında olmalıdır.");
            ModuleName = moduleName;
            Description = description;
            Target = target;
            ConnectionMode = connectionMode;
        }
        public string TargetFolder { get; }
        public string ModuleName { get; }
        public string Description { get; }
        public string FullPath { get; }
        public DoorsVersion Target { get; }
        public DoorsConnectionMode ConnectionMode { get; }
        public TimeSpan Timeout { get; }

        private static void ValidateText(string value, bool multiline)
        {
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if ((char.IsControl(c) && !(multiline && (c == '\n' || c == '\r' || c == '\t'))) || c == '\u2028' || c == '\u2029')
                    throw Invalid("Ad/açıklama desteklenmeyen kontrol karakteri içeriyor.");
                if (char.IsHighSurrogate(c))
                {
                    if (++i >= value.Length || !char.IsLowSurrogate(value[i])) throw Invalid("Geçersiz UTF-16 metni.");
                }
                else if (char.IsLowSurrogate(c)) throw Invalid("Geçersiz UTF-16 metni.");
            }
        }
        private static IntegrationException Invalid(string message) => new IntegrationException(IntegrationErrorCode.InvalidConfiguration, message);
    }

    public sealed class FormalModuleCreationChecks
    {
        public FormalModuleCreationChecks(string databaseId, string userName, string folderId,
            CreationFolderStatus folder, CreationPermissionStatus permission, CreationInputStatus name,
            CreationInputStatus description, CreationNameStatus collision, CreationLockStatus locks)
        {
            DatabaseId = databaseId ?? throw new ArgumentNullException(nameof(databaseId));
            UserName = userName ?? throw new ArgumentNullException(nameof(userName));
            FolderId = folderId ?? throw new ArgumentNullException(nameof(folderId));
            if (!Enum.IsDefined(typeof(CreationFolderStatus), folder) || !Enum.IsDefined(typeof(CreationPermissionStatus), permission) ||
                !Enum.IsDefined(typeof(CreationInputStatus), name) || !Enum.IsDefined(typeof(CreationInputStatus), description) ||
                !Enum.IsDefined(typeof(CreationNameStatus), collision) || !Enum.IsDefined(typeof(CreationLockStatus), locks))
                throw new ArgumentOutOfRangeException(nameof(folder));
            Folder = folder; Permission = permission; Name = name; Description = description; Collision = collision; Locks = locks;
        }
        public string DatabaseId { get; }
        public string UserName { get; }
        public string FolderId { get; }
        public CreationFolderStatus Folder { get; }
        public CreationPermissionStatus Permission { get; }
        public CreationInputStatus Name { get; }
        public CreationInputStatus Description { get; }
        public CreationNameStatus Collision { get; }
        public CreationLockStatus Locks { get; }
        public bool CanCreate => !string.IsNullOrWhiteSpace(DatabaseId) && !string.IsNullOrWhiteSpace(UserName) && !string.IsNullOrWhiteSpace(FolderId) &&
            Folder == CreationFolderStatus.Readable && Permission == CreationPermissionStatus.Allowed && Name == CreationInputStatus.Valid &&
            Description == CreationInputStatus.Valid && Collision == CreationNameStatus.NoVisibleConflict && Locks == CreationLockStatus.NoBlockingLockObserved;
        public string Summary => "Folder=" + Folder + "; Permission=" + Permission + "; Name=" + Name +
            "; Description=" + Description + "; Collision=" + Collision + "; Locks=" + Locks;
    }

    public sealed class FormalModuleCreationPreview
    {
        private int consumed;
        private int commitAuthorization;
        internal FormalModuleCreationPreview(object owner, FormalModuleCreateRequest request, FormalModuleCreationChecks checks)
        {
            Owner = owner;
            Request = request;
            Checks = checks;
            PlanId = Guid.NewGuid();
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(5);
            ApprovalFingerprint = Fingerprint(request, checks);
        }
        internal object Owner { get; }
        internal bool TryConsume() => Interlocked.CompareExchange(ref consumed, 1, 0) == 0;
        internal void AuthorizeCommit() => Interlocked.CompareExchange(ref commitAuthorization, 1, 0);
        internal void RevokeCommitAuthorization() => Interlocked.Exchange(ref commitAuthorization, 2);
        // Infrastructure consumes this capability once. Only ModuleService can authorize it after confirmation.
        public bool TryBeginCommit() => ExpiresAtUtc > DateTime.UtcNow && Interlocked.CompareExchange(ref commitAuthorization, 2, 1) == 1;
        public Guid PlanId { get; }
        public FormalModuleCreateRequest Request { get; }
        public FormalModuleCreationChecks Checks { get; }
        public DateTime ExpiresAtUtc { get; }
        public string ApprovalFingerprint { get; }
        public bool CanCreate => Checks.CanCreate;

        // Length-prefixed UTF-8 fields avoid ambiguous concatenations. This is a review fingerprint, not a credential.
        private static string Fingerprint(FormalModuleCreateRequest request, FormalModuleCreationChecks checks)
        {
            using (var data = new MemoryStream())
            using (var writer = new BinaryWriter(data, new UTF8Encoding(false, true), true))
            using (var sha = SHA256.Create())
            {
                foreach (string field in new[] { "FormalModuleCreate-v1", request.FullPath, request.Description,
                    DoorsVersions.Format(request.Target), request.ConnectionMode.ToString(), request.Timeout.Ticks.ToString(CultureInfo.InvariantCulture),
                    checks.DatabaseId, checks.UserName, checks.FolderId, checks.Summary, "Prefix=; AbsoluteNumber=1; Display=false" }) writer.Write(field);
                writer.Flush();
                return BitConverter.ToString(sha.ComputeHash(data.ToArray())).Replace("-", "").ToLowerInvariant();
            }
        }
    }

    public sealed class FormalModuleCreationResult
    {
        public FormalModuleCreationResult(Guid correlationId, string fullPath, FormalModuleCreationChecks checks,
            ModuleCreationOutcome outcome, string moduleId = "", string qualifiedModuleId = "", ModuleCreationCleanup cleanup = ModuleCreationCleanup.NotRequired)
        {
            if (correlationId == Guid.Empty) throw new ArgumentException("Request ID is required.", nameof(correlationId));
            if (!Enum.IsDefined(typeof(ModuleCreationOutcome), outcome) || !Enum.IsDefined(typeof(ModuleCreationCleanup), cleanup)) throw new ArgumentOutOfRangeException(nameof(outcome));
            CorrelationId = correlationId;
            FullPath = DoorsItemPath.Normalize(fullPath);
            Checks = checks ?? throw new ArgumentNullException(nameof(checks));
            Outcome = outcome;
            ModuleId = moduleId ?? throw new ArgumentNullException(nameof(moduleId));
            QualifiedModuleId = qualifiedModuleId ?? throw new ArgumentNullException(nameof(qualifiedModuleId));
            Cleanup = cleanup;
            if (outcome == ModuleCreationOutcome.Created && (string.IsNullOrWhiteSpace(moduleId) || string.IsNullOrWhiteSpace(qualifiedModuleId)))
                throw new ArgumentException("Created results require verified module IDs.");
        }
        public Guid CorrelationId { get; }
        public string FullPath { get; }
        public FormalModuleCreationChecks Checks { get; }
        public ModuleCreationOutcome Outcome { get; }
        public string ModuleId { get; }
        public string QualifiedModuleId { get; }
        public ModuleCreationCleanup Cleanup { get; }
    }

    public interface IFormalModuleCreationGateway
    {
        Task<FormalModuleCreationChecks> PreviewAsync(FormalModuleCreateRequest request, CancellationToken cancellationToken);
        // Infrastructure port; callers use IModuleService to enforce preview and human confirmation.
        Task<FormalModuleCreationResult> CommitAsync(FormalModuleCreationPreview approvedPreview, CancellationToken cancellationToken);
    }

    public interface IModuleCreationConfirmation
    {
        // The host must present the complete preview to the user and return true only for explicit approval.
        Task<bool> ConfirmAsync(FormalModuleCreationPreview preview, CancellationToken cancellationToken);
    }

    public sealed class RejectModuleCreationConfirmation : IModuleCreationConfirmation
    {
        public Task<bool> ConfirmAsync(FormalModuleCreationPreview preview, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(false);
        }
    }

    public sealed class ModuleCreationUncertainException : IntegrationException
    {
        public ModuleCreationUncertainException(Guid planId, Guid operationId, Task<FormalModuleCreationResult> completion, Exception innerException = null)
            : base(IntegrationErrorCode.ModuleCreationOutcomeUnknown,
                "Oluşturma sonucu kesinleştirilemedi; COM çağrısı sürüyor veya modül oluşturulmuş olabilir. Otomatik tekrar yapılmadı. Completion ve hedef yolu denetlemeden yeniden oluşturmayın.", innerException)
        {
            PlanId = planId; OperationId = operationId; Completion = completion ?? throw new ArgumentNullException(nameof(completion));
        }
        public Guid PlanId { get; }
        public Guid OperationId { get; }
        public Task<FormalModuleCreationResult> Completion { get; }
    }
}
