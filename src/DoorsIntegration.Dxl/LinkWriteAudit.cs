using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using DoorsIntegration.Core;

namespace DoorsIntegration.Dxl
{
    public sealed class LinkWriteAuditOptions
    {
        public LinkWriteAuditOptions() : this(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DoorsIntegration", "audit", "link-writes.jsonl")) { }
        public LinkWriteAuditOptions(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathRooted(path)) throw new ArgumentException("Absolute audit path required.", nameof(path));
            Path = System.IO.Path.GetFullPath(path);
        }
        public string Path { get; }
    }
    public sealed class FileLinkWriteAuditSink : ILinkWriteAuditSink
    {
        private readonly LinkWriteAuditOptions options;
        private readonly object sync = new object();
        public FileLinkWriteAuditSink(LinkWriteAuditOptions options) { this.options = options ?? throw new ArgumentNullException(nameof(options)); }
        public void Record(LinkWriteAuditEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            var preview = entry.Preview; var request = preview.Request; var inspection = preview.Inspection; var result = entry.Result;
            var data = new Dictionary<string, string>
            {
                ["timestampUtc"] = entry.TimestampUtc.ToString("O", CultureInfo.InvariantCulture), ["phase"] = entry.Phase,
                ["planId"] = preview.PlanId.ToString("N"), ["operationId"] = entry.OperationId.ToString("N"),
                ["fingerprint"] = preview.ApprovalFingerprint, ["action"] = request.Action.ToString(),
                ["sourcePath"] = request.SourceModulePath, ["sourceAbsolute"] = request.SourceAbsoluteNumber.ToString(CultureInfo.InvariantCulture),
                ["targetPath"] = request.TargetModulePath, ["targetAbsolute"] = request.TargetAbsoluteNumber.ToString(CultureInfo.InvariantCulture),
                ["linkModulePath"] = request.LinkModulePath, ["databaseId"] = inspection.DatabaseId, ["actor"] = inspection.UserName,
                ["sourceModuleId"] = inspection.SourceModuleId, ["targetModuleId"] = inspection.TargetModuleId, ["linkModuleId"] = inspection.LinkModuleId,
                ["outcome"] = result?.Outcome.ToString() ?? "", ["persistence"] = result?.Persistence.ToString() ?? "",
                ["cleanup"] = result?.Cleanup.ToString() ?? "", ["afterVerified"] = result?.AfterVerified.ToString() ?? "",
                ["beforeCount"] = (result ?? inspection).Before.Count.ToString(CultureInfo.InvariantCulture),
                ["afterCount"] = result?.After.Count.ToString(CultureInfo.InvariantCulture) ?? "",
                ["added"] = result?.Comparison == null ? "" : string.Join(";", result.Comparison.Added.Select(e => e.Key)),
                ["removed"] = result?.Comparison == null ? "" : string.Join(";", result.Comparison.Removed.Select(e => e.Key)),
                ["issues"] = result == null ? string.Join(";", inspection.Issues) : string.Join(";", result.Issues)
            };
            byte[] bytes;
            using (var memory = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(Dictionary<string, string>), new DataContractJsonSerializerSettings
                { UseSimpleDictionaryFormat = true }).WriteObject(memory, data); bytes = memory.ToArray();
            }
            lock (sync)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(options.Path));
                using (var output = new FileStream(options.Path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
                {
                    if (output.Length != 0)
                    {
                        output.Seek(-1, SeekOrigin.End);
                        if (output.ReadByte() != '\n') throw new InvalidDataException("Incomplete audit record; review before any further write.");
                    }
                    output.Seek(0, SeekOrigin.End); output.Write(bytes, 0, bytes.Length); output.WriteByte((byte)'\n'); output.Flush(true);
                }
            }
        }
    }
}
