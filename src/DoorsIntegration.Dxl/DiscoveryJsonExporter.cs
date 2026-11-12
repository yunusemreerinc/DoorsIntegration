using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using DoorsIntegration.Core;

namespace DoorsIntegration.Dxl
{
    public sealed class DiscoveryJsonExporter : IDiscoveryJsonExporter
    {
        public void Export(Stream destination, DiscoverySnapshot snapshot)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            try
            {
                var document = new JsonDocument
                {
                    SchemaVersion = 1, CorrelationId = snapshot.CorrelationId.ToString("N"),
                    CollectedAtUtc = snapshot.CollectedAtUtc.ToString("O", CultureInfo.InvariantCulture),
                    Kind = snapshot.Kind.ToString(), RootPath = snapshot.Scope.RootPath, Recursive = snapshot.Scope.Recursive,
                    Visibility = snapshot.Visibility, IsCompleteForVisibleScope = snapshot.IsCompleteForVisibleScope,
                    IsTruncated = snapshot.IsTruncated, VisitedFolders = snapshot.VisitedFolders,
                    TargetProfile = DoorsVersions.Format(snapshot.Scope.Target), MaxItems = snapshot.Scope.MaxItems,
                    MaxDepth = snapshot.Scope.MaxDepth, MaxFolders = snapshot.Scope.MaxFolders,
                    Items = snapshot.Items.Select(item => new JsonItem { FullPath = item.FullPath, Name = item.Name,
                        Type = item.Type.ToString(), AccessStatus = item.AccessStatus.ToString(), MetadataVerified = item.MetadataVerified,
                        AccessDescription = item.AccessDescription }).ToArray(),
                    Issues = snapshot.Issues.Select(issue => new JsonIssue { FullPath = issue.FullPath, Code = issue.Code.ToString(), Message = issue.Message }).ToArray()
                };
                new DataContractJsonSerializer(typeof(JsonDocument), new DataContractJsonSerializerSettings { MaxItemsInObjectGraph = int.MaxValue })
                    .WriteObject(destination, document);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is SerializationException ||
                ex is System.Security.SecurityException || ex is ArgumentException || ex is NotSupportedException || ex is ObjectDisposedException)
            {
                throw Failure(ex);
            }
        }

        public void ExportFile(string path, DiscoverySnapshot snapshot, bool overwrite = false)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            string temporary = null;
            try
            {
                if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Destination path is required.", nameof(path));
                var fullPath = Path.GetFullPath(path);
                var directory = Path.GetDirectoryName(fullPath);
                temporary = Path.Combine(directory, ".doors-export-" + Guid.NewGuid().ToString("N") + ".tmp");
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    Export(output, snapshot);
                    output.Flush(true);
                }
                if (overwrite && File.Exists(fullPath)) File.Replace(temporary, fullPath, null);
                else File.Move(temporary, fullPath);
                temporary = null;
            }
            catch (IntegrationException) { throw; }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException ||
                ex is NotSupportedException || ex is System.Security.SecurityException)
            {
                throw Failure(ex);
            }
            finally
            {
                if (temporary != null)
                {
                    try { File.Delete(temporary); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    catch (System.Security.SecurityException) { }
                }
            }
        }

        private static IntegrationException Failure(Exception ex) => new IntegrationException(IntegrationErrorCode.JsonExportFailed,
            "Keşif JSON dosyası yazılamadı. Hedef klasörü, dosya izinlerini ve üzerine yazma seçimini kontrol edin.", ex);

        [DataContract]
        private sealed class JsonDocument
        {
            [DataMember(Name = "schemaVersion", Order = 0)] public int SchemaVersion;
            [DataMember(Name = "correlationId", Order = 1)] public string CorrelationId;
            [DataMember(Name = "collectedAtUtc", Order = 2)] public string CollectedAtUtc;
            [DataMember(Name = "kind", Order = 3)] public string Kind;
            [DataMember(Name = "rootPath", Order = 4)] public string RootPath;
            [DataMember(Name = "recursive", Order = 5)] public bool Recursive;
            [DataMember(Name = "visibility", Order = 6)] public string Visibility;
            [DataMember(Name = "isCompleteForVisibleScope", Order = 7)] public bool IsCompleteForVisibleScope;
            [DataMember(Name = "isTruncated", Order = 8)] public bool IsTruncated;
            [DataMember(Name = "visitedFolders", Order = 9)] public int VisitedFolders;
            [DataMember(Name = "targetProfile", Order = 10)] public string TargetProfile;
            [DataMember(Name = "maxItems", Order = 11)] public int MaxItems;
            [DataMember(Name = "maxDepth", Order = 12)] public int MaxDepth;
            [DataMember(Name = "maxFolders", Order = 13)] public int MaxFolders;
            [DataMember(Name = "items", Order = 14)] public JsonItem[] Items;
            [DataMember(Name = "issues", Order = 15)] public JsonIssue[] Issues;
        }
        [DataContract]
        private sealed class JsonItem
        {
            [DataMember(Name = "fullPath", Order = 0)] public string FullPath;
            [DataMember(Name = "name", Order = 1)] public string Name;
            [DataMember(Name = "type", Order = 2)] public string Type;
            [DataMember(Name = "accessStatus", Order = 3)] public string AccessStatus;
            [DataMember(Name = "metadataVerified", Order = 4)] public bool MetadataVerified;
            [DataMember(Name = "accessDescription", Order = 5)] public string AccessDescription;
        }
        [DataContract]
        private sealed class JsonIssue
        {
            [DataMember(Name = "fullPath", Order = 0)] public string FullPath;
            [DataMember(Name = "code", Order = 1)] public string Code;
            [DataMember(Name = "message", Order = 2)] public string Message;
        }
    }
}
