using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DoorsIntegration.Core
{
    public enum DoorsItemKind { Unknown, DatabaseRoot, Project, Folder, FormalModule, LinkModule, DescriptiveModule }
    public enum DoorsAccessStatus { Readable, AccessDenied, DeniedOrNotFound, Unavailable }
    public enum DiscoveryKind { Hierarchy, Projects, Folders, FormalModules, ModuleByPath }
    public enum DiscoveryIssueCode
    {
        LookupFailed, EnumerationFailed, AccessRestricted, NotFormalModule, ScopeNotContainer,
        MaxItemsReached, MaxDepthReached, MaxFoldersReached
    }

    public static class DoorsItemPath
    {
        public static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || path[0] != '/' || path.StartsWith("//", StringComparison.Ordinal)) throw InvalidPath();
            if (path.Length > 1 && path.EndsWith("/", StringComparison.Ordinal)) path = path.Substring(0, path.Length - 1);
            if (path != "/" && path.Substring(1).Split('/').Any(segment => segment.Length == 0 || segment == "." || segment == ".."))
                throw InvalidPath();
            for (int index = 0; index < path.Length; index++)
            {
                char value = path[index];
                if (char.IsControl(value) || value == '\u2028' || value == '\u2029') throw InvalidPath();
                if (char.IsHighSurrogate(value))
                {
                    if (++index >= path.Length || !char.IsLowSurrogate(path[index])) throw InvalidPath();
                }
                else if (char.IsLowSurrogate(value)) throw InvalidPath();
            }
            return path;
        }

        public static string LeafName(string path) => path == "/" ? "/" : path.Substring(path.LastIndexOf('/') + 1);
        public static bool IsDirectChild(string parent, string path) => path != parent &&
            path.StartsWith(parent == "/" ? "/" : parent + "/", StringComparison.Ordinal) &&
            path.IndexOf('/', parent == "/" ? 1 : parent.Length + 1) < 0;

        private static IntegrationException InvalidPath() => new IntegrationException(IntegrationErrorCode.InvalidConfiguration,
            "DOORS yolu / ile başlayan tam bir yol olmalıdır; boş, ./.. veya yinelenen yol bileşeni ve kontrol karakteri içeremez.");
    }

    public sealed class DiscoveryScope
    {
        public DiscoveryScope(string rootPath = "/", bool recursive = true, int maxItems = 10000,
            int maxDepth = 32, int maxFolders = 512, TimeSpan? timeout = null,
            DoorsConnectionMode connectionMode = DoorsConnectionMode.AttachExisting, DoorsVersion target = DoorsVersion.V97)
        {
            RootPath = DoorsItemPath.Normalize(rootPath);
            DoorsVersions.Format(target);
            if (maxItems < 1 || maxItems > 10000 || maxDepth < 1 || maxDepth > 128 || maxFolders < 1 || maxFolders > 10000)
                throw new IntegrationException(IntegrationErrorCode.InvalidConfiguration, "Keşif sınırları geçersiz: öğe 1–10000, derinlik 1–128, klasör 1–10000 olmalıdır.");
            if (connectionMode != DoorsConnectionMode.AttachExisting && connectionMode != DoorsConnectionMode.Activate)
                throw new IntegrationException(IntegrationErrorCode.InvalidConfiguration, "Geçersiz DOORS bağlantı modu.");
            Timeout = timeout ?? TimeSpan.FromSeconds(30);
            if (Timeout < TimeSpan.FromMilliseconds(1) || Timeout > TimeSpan.FromHours(24))
                throw new IntegrationException(IntegrationErrorCode.InvalidConfiguration, "Keşif bekleme süresi 1 ms ile 24 saat arasında olmalıdır.");
            Recursive = recursive;
            MaxItems = maxItems;
            MaxDepth = maxDepth;
            MaxFolders = maxFolders;
            ConnectionMode = connectionMode;
            Target = target;
        }
        public string RootPath { get; }
        public bool Recursive { get; }
        // All observed items, including types filtered out of the final result, count toward this bound.
        public int MaxItems { get; }
        public int MaxDepth { get; }
        public int MaxFolders { get; }
        public TimeSpan Timeout { get; }
        public DoorsConnectionMode ConnectionMode { get; }
        public DoorsVersion Target { get; }
    }

    public sealed class DoorsDiscoveryItem
    {
        public DoorsDiscoveryItem(string fullPath, string name, DoorsItemKind type, DoorsAccessStatus accessStatus,
            bool metadataVerified = true)
        {
            FullPath = DoorsItemPath.Normalize(fullPath);
            Name = name ?? throw new ArgumentNullException(nameof(name));
            if (!Enum.IsDefined(typeof(DoorsItemKind), type) || !Enum.IsDefined(typeof(DoorsAccessStatus), accessStatus))
                throw new ArgumentOutOfRangeException(nameof(type));
            if (name != DoorsItemPath.LeafName(FullPath)) throw new ArgumentException("Item name does not match its path.", nameof(name));
            Type = type;
            AccessStatus = accessStatus;
            MetadataVerified = metadataVerified;
        }
        public string FullPath { get; }
        public string Name { get; }
        public DoorsItemKind Type { get; }
        public DoorsAccessStatus AccessStatus { get; }
        // False: identity came from the requested path, not from readable DOORS metadata.
        public bool MetadataVerified { get; }
        public string AccessDescription => AccessStatus == DoorsAccessStatus.Readable ?
            "Hiyerarşi meta verisi okunabilir; modül açılmadı, içerik erişimi sınanmadı." :
            AccessStatus == DoorsAccessStatus.AccessDenied ? "DXL erişim kontrolü okuma izni vermedi." :
            AccessStatus == DoorsAccessStatus.DeniedOrNotFound ? "Yol bulunamadı veya kullanıcıya görünmüyor; API bu durumları ayırmıyor." :
            "Öğenin meta verisine erişilemedi; işlem sırasında silinmiş veya erişilemez olmuş olabilir.";
    }

    public sealed class DiscoveryIssue
    {
        public DiscoveryIssue(string fullPath, DiscoveryIssueCode code)
        {
            FullPath = DoorsItemPath.Normalize(fullPath);
            if (!Enum.IsDefined(typeof(DiscoveryIssueCode), code)) throw new ArgumentOutOfRangeException(nameof(code));
            Code = code;
        }
        public string FullPath { get; }
        public DiscoveryIssueCode Code { get; }
        public string Message
        {
            get
            {
                switch (Code)
                {
                    case DiscoveryIssueCode.LookupFailed: return "Yol meta verisi çözümlenemedi; ham DXL hatası rapora eklenmedi.";
                    case DiscoveryIssueCode.EnumerationFailed: return "Klasör dolaşımı tamamlanamadı; daha önce okunan öğeler korundu.";
                    case DiscoveryIssueCode.AccessRestricted: return "Öğe veya alt kapsam okunamıyor; görünmeyen öğeler listelenemez.";
                    case DiscoveryIssueCode.NotFormalModule: return "İstenen yolun türü formal modül değil; modül açılmadı.";
                    case DiscoveryIssueCode.ScopeNotContainer: return "İstenen yol bir proje/klasör değil; alt öğeler taranmadı.";
                    case DiscoveryIssueCode.MaxItemsReached: return "Öğe/yanıt boyutu sınırına ulaşıldı; sonuç kesildi.";
                    case DiscoveryIssueCode.MaxDepthReached: return "Derinlik sınırı nedeniyle alt kapsam taranmadı.";
                    default: return "Klasör sorgu sınırına ulaşıldı; kalan kapsam taranmadı.";
                }
            }
        }
    }

    public sealed class DiscoverySnapshot
    {
        public DiscoverySnapshot(Guid correlationId, DiscoveryKind kind, DiscoveryScope scope,
            IEnumerable<DoorsDiscoveryItem> items, IEnumerable<DiscoveryIssue> issues,
            int visitedFolders, bool isTruncated, DateTime collectedAtUtc)
        {
            if (correlationId == Guid.Empty) throw new ArgumentException("Request ID is required.", nameof(correlationId));
            if (!Enum.IsDefined(typeof(DiscoveryKind), kind)) throw new ArgumentOutOfRangeException(nameof(kind));
            CorrelationId = correlationId;
            Kind = kind;
            Scope = scope ?? throw new ArgumentNullException(nameof(scope));
            Items = new ReadOnlyCollection<DoorsDiscoveryItem>((items ?? throw new ArgumentNullException(nameof(items)))
                .OrderBy(item => item.FullPath, StringComparer.Ordinal).ToArray());
            Issues = new ReadOnlyCollection<DiscoveryIssue>((issues ?? throw new ArgumentNullException(nameof(issues))).ToArray());
            VisitedFolders = visitedFolders;
            IsTruncated = isTruncated;
            CollectedAtUtc = collectedAtUtc.ToUniversalTime();
        }
        public Guid CorrelationId { get; }
        public DiscoveryKind Kind { get; }
        public DiscoveryScope Scope { get; }
        public IReadOnlyList<DoorsDiscoveryItem> Items { get; }
        public IReadOnlyList<DiscoveryIssue> Issues { get; }
        public int VisitedFolders { get; }
        public bool IsTruncated { get; }
        public DateTime CollectedAtUtc { get; }
        public string Visibility => "ReadableItemsOnly";
        public bool IsCompleteForVisibleScope => !IsTruncated && Issues.Count == 0 && Items.All(item => item.AccessStatus == DoorsAccessStatus.Readable);
    }

    public interface IDoorsHierarchyReader
    {
        Task<DiscoverySnapshot> DiscoverAsync(DiscoveryKind kind, DiscoveryScope scope, CancellationToken cancellationToken = default(CancellationToken));
    }
    public interface IProjectService
    {
        Task<DiscoverySnapshot> DiscoverProjectsAsync(DiscoveryScope scope, CancellationToken cancellationToken = default(CancellationToken));
        Task<DiscoverySnapshot> DiscoverFoldersAsync(DiscoveryScope scope, CancellationToken cancellationToken = default(CancellationToken));
        Task<DiscoverySnapshot> DiscoverHierarchyAsync(DiscoveryScope scope, CancellationToken cancellationToken = default(CancellationToken));
    }
    public interface IModuleService
    {
        Task<FormalModuleCreationPreview> PreviewFormalModuleCreationAsync(FormalModuleCreateRequest request, CancellationToken cancellationToken = default(CancellationToken));
        Task<FormalModuleCreationResult> CreateFormalModuleAsync(FormalModuleCreationPreview preview, CancellationToken cancellationToken = default(CancellationToken));
        Task<DiscoverySnapshot> DiscoverFormalModulesAsync(DiscoveryScope scope, CancellationToken cancellationToken = default(CancellationToken));
        Task<DiscoverySnapshot> GetFormalModuleAsync(DiscoveryScope scope, CancellationToken cancellationToken = default(CancellationToken));
    }
    public interface IDiscoveryJsonExporter
    {
        void Export(System.IO.Stream destination, DiscoverySnapshot snapshot);
        void ExportFile(string path, DiscoverySnapshot snapshot, bool overwrite = false);
    }

}
