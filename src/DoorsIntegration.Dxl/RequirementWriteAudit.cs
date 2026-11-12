using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using DoorsIntegration.Core;

namespace DoorsIntegration.Dxl
{
    public sealed class RequirementWriteAuditOptions
    {
        public RequirementWriteAuditOptions() : this(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DoorsIntegration", "audit", "requirement-writes.jsonl")) { }
        public RequirementWriteAuditOptions(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathRooted(path)) throw new ArgumentException("Absolute audit path required.", nameof(path));
            Path = System.IO.Path.GetFullPath(path);
        }
        public string Path { get; }
    }

    public sealed class FileRequirementWriteAuditSink : IRequirementWriteAuditSink
    {
        private readonly RequirementWriteAuditOptions options;
        private readonly object sync = new object();
        public FileRequirementWriteAuditSink(RequirementWriteAuditOptions options) { this.options = options ?? throw new ArgumentNullException(nameof(options)); }
        public void Record(RequirementWriteAuditEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            // Identity and review fingerprint are auditable; requirement text and prior-state contents are never logged.
            var data = new Dictionary<string, string>
            {
                ["timestampUtc"] = entry.TimestampUtc.ToString("O", CultureInfo.InvariantCulture), ["phase"] = entry.Phase,
                ["planId"] = entry.PlanId.ToString("N"), ["operationId"] = entry.OperationId.ToString("N"),
                ["fingerprint"] = entry.Fingerprint, ["modulePath"] = entry.ModulePath, ["actor"] = entry.Actor,
                ["databaseId"] = entry.DatabaseId, ["qualifiedModuleId"] = entry.ModuleId,
                ["action"] = entry.Action, ["requestedAbsoluteNumber"] = entry.RequestedAbsoluteNumber?.ToString(CultureInfo.InvariantCulture) ?? "",
                ["position"] = entry.Position, ["anchorAbsoluteNumber"] = entry.AnchorAbsoluteNumber?.ToString(CultureInfo.InvariantCulture) ?? "",
                ["outcome"] = entry.Result?.Outcome.ToString() ?? "", ["persistence"] = entry.Result?.Persistence.ToString() ?? "",
                ["cleanup"] = entry.Result?.Cleanup.ToString() ?? "", ["absoluteNumber"] = entry.Result?.AbsoluteNumber?.ToString(CultureInfo.InvariantCulture) ?? "",
                ["steps"] = entry.Result == null ? "" : string.Join(";", entry.Result.Steps.Select((s, index) =>
                    index + ":" + s.Kind + ":" + s.Status + ":" + s.Error))
            };
            for (int i = 0; i < entry.AttributeChanges.Count; i++)
            {
                var change = entry.AttributeChanges[i];
                string prefix = "attribute." + i.ToString(CultureInfo.InvariantCulture) + ".";
                data[prefix + "name"] = change.Name;
                data[prefix + "format"] = change.Format.ToString();
                data[prefix + "requestedSha256"] = change.RequestedSha256;
                data[prefix + "beforePlainSha256"] = change.BeforePlainSha256;
                data[prefix + "beforeRichSha256"] = change.BeforeRichSha256;
            }
            byte[] bytes;
            using (var memory = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(Dictionary<string, string>), new DataContractJsonSerializerSettings
                { UseSimpleDictionaryFormat = true }).WriteObject(memory, data);
                bytes = memory.ToArray();
            }
            lock (sync)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(options.Path));
                using (var output = new FileStream(options.Path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
                {
                    if (output.Length != 0)
                    {
                        output.Seek(-1, SeekOrigin.End);
                        if (output.ReadByte() != '\n') throw new InvalidDataException("Audit contains an incomplete final record; review it before any further write.");
                    }
                    output.Seek(0, SeekOrigin.End);
                    output.Write(bytes, 0, bytes.Length); output.WriteByte((byte)'\n'); output.Flush(true);
                }
            }
        }
    }
}
