using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Xml;

namespace DoorsIntegration.Core
{
    public sealed class ModuleSelectionProfile
    {
        public ModuleSelectionProfile(string name, string repositoryId, System.Collections.Generic.IEnumerable<ModuleSelectionFolder> folders,
            System.Collections.Generic.IEnumerable<BaselineModuleReference> selectedModules, DateTime savedAtUtc)
        {
            Name = BulkBaselineGuard.Text(name, nameof(name), 256); RepositoryId = BulkBaselineGuard.Text(repositoryId, nameof(repositoryId), 1024);
            var request = new ModuleSelectionRequest(repositoryId, folders); Folders = request.Folders;
            SelectedModules = BulkBaselineGuard.List(selectedModules ?? throw new ArgumentNullException(nameof(selectedModules)), 10000);
            if (SelectedModules.Any(m => m.RepositoryId != repositoryId || !Folders.Any(f => f.ContainsModule(m.CanonicalFullPath))) ||
                SelectedModules.Select(m => m.ModuleId).Distinct(StringComparer.Ordinal).Count() != SelectedModules.Count ||
                SelectedModules.Select(m => m.CanonicalFullPath).Distinct(StringComparer.Ordinal).Count() != SelectedModules.Count)
                throw new ArgumentException("Selected stable identities must be unique and belong to this repository/folder scope.");
            SavedAtUtc = BulkBaselineGuard.Utc(savedAtUtc);
        }
        public int SchemaVersion => 1;
        public string Name { get; }
        public string RepositoryId { get; }
        public System.Collections.Generic.IReadOnlyList<ModuleSelectionFolder> Folders { get; }
        public System.Collections.Generic.IReadOnlyList<BaselineModuleReference> SelectedModules { get; }
        public DateTime SavedAtUtc { get; }
    }
    // Data only, no scripts, ACL grants, version assumptions or write authorization are persisted.
    public sealed class JsonModuleSelectionProfileStore : IModuleSelectionProfileStore
    {
        private const int MaxBytes = 8388608;
        public void Save(string path, ModuleSelectionProfile profile)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            var dto = new ProfileDto { SchemaVersion = 1, Name = profile.Name, RepositoryId = profile.RepositoryId,
                SavedAtUtc = profile.SavedAtUtc.ToString("O", CultureInfo.InvariantCulture),
                Folders = profile.Folders.Select(f => new FolderDto { Path = f.CanonicalFullPath, IncludeSubfolders = f.IncludeSubfolders }).ToArray(),
                SelectedModules = profile.SelectedModules.Select(m => new ModuleDto { ModuleId = m.ModuleId, Path = m.CanonicalFullPath }).ToArray() };
            using (var buffer = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(ProfileDto)).WriteObject(buffer, dto);
                if (buffer.Length > MaxBytes) throw new InvalidDataException("Selection profile exceeds 8 MiB.");
                using (var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { buffer.Position = 0; buffer.CopyTo(destination); destination.Flush(true); }
            }
        }
        public ModuleSelectionProfile Load(string path)
        {
            byte[] bytes;
            using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (source.Length == 0 || source.Length > MaxBytes) throw new InvalidDataException("Selection profile must be 1 byte to 8 MiB.");
                bytes = new byte[(int)source.Length]; int offset = 0, read;
                while (offset < bytes.Length && (read = source.Read(bytes, offset, bytes.Length - offset)) > 0) offset += read;
                if (offset != bytes.Length) throw new EndOfStreamException();
            }
            try
            {
                using (var reader = JsonReaderWriterFactory.CreateJsonReader(bytes, new XmlDictionaryReaderQuotas { MaxDepth = 16,
                    MaxStringContentLength = 16384, MaxArrayLength = 10000, MaxBytesPerRead = 65536, MaxNameTableCharCount = 65536 }))
                {
                    var document = new XmlDocument { XmlResolver = null }; document.Load(reader);
                    var root = document.DocumentElement;
                    CheckObject(root, "SchemaVersion|Name|RepositoryId|Folders|SelectedModules|SavedAtUtc");
                    foreach (XmlElement folder in root["Folders"].ChildNodes) CheckObject(folder, "Path|IncludeSubfolders");
                    foreach (XmlElement module in root["SelectedModules"].ChildNodes) CheckObject(module, "ModuleId|Path");
                }
                ProfileDto dto;
                using (var source = new MemoryStream(bytes, false)) dto = (ProfileDto)new DataContractJsonSerializer(typeof(ProfileDto)).ReadObject(source);
                if (dto.SchemaVersion != 1 || dto.Folders == null || dto.SelectedModules == null) throw new InvalidDataException("Unsupported/incomplete selection profile.");
                if (!DateTime.TryParseExact(dto.SavedAtUtc, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var utc)) throw new InvalidDataException("Invalid profile UTC timestamp.");
                return new ModuleSelectionProfile(dto.Name, dto.RepositoryId, dto.Folders.Select(f => new ModuleSelectionFolder(f.Path, f.IncludeSubfolders)),
                    dto.SelectedModules.Select(m => new BaselineModuleReference(dto.RepositoryId, m.ModuleId, m.Path)), utc);
            }
            catch (Exception ex) when (ex is SerializationException || ex is XmlException || ex is ArgumentException || ex is NullReferenceException)
            { throw new InvalidDataException("Invalid selection profile; no selection or authorization was restored.", ex); }
        }
        private static void CheckObject(XmlElement node, string fields)
        {
            if (node == null || node.GetAttribute("type") != "object") throw new InvalidDataException("Expected a JSON object.");
            var names = node.ChildNodes.Cast<XmlElement>().Select(c => c.LocalName).ToArray();
            var required = fields.Split('|');
            if (names.Length != required.Length || names.Distinct(StringComparer.Ordinal).Count() != names.Length || names.Any(n => !required.Contains(n)))
                throw new InvalidDataException("Unknown, duplicate or missing selection profile property.");
            foreach (XmlElement child in node.ChildNodes)
            {
                string type = child.LocalName == "Folders" || child.LocalName == "SelectedModules" ? "array" :
                    child.LocalName == "IncludeSubfolders" ? "boolean" : child.LocalName == "SchemaVersion" ? "number" : "string";
                if (child.GetAttribute("type") != type || (type != "array" && child.ChildNodes.Cast<XmlNode>().Any(n => n is XmlElement)))
                    throw new InvalidDataException("Invalid selection profile property type.");
                if (type == "array" && child.ChildNodes.Count > (child.LocalName == "Folders" ? 256 : 10000)) throw new InvalidDataException("Selection profile collection limit exceeded.");
            }
        }
        [DataContract] private sealed class ProfileDto
        {
            [DataMember(IsRequired = true)] public int SchemaVersion;
            [DataMember(IsRequired = true)] public string Name;
            [DataMember(IsRequired = true)] public string RepositoryId;
            [DataMember(IsRequired = true)] public FolderDto[] Folders;
            [DataMember(IsRequired = true)] public ModuleDto[] SelectedModules;
            [DataMember(IsRequired = true)] public string SavedAtUtc;
        }
        [DataContract] private sealed class FolderDto
        {
            [DataMember(IsRequired = true)] public string Path;
            [DataMember(IsRequired = true)] public bool IncludeSubfolders;
        }
        [DataContract] private sealed class ModuleDto
        {
            [DataMember(IsRequired = true)] public string ModuleId;
            [DataMember(IsRequired = true)] public string Path;
        }
    }
}
