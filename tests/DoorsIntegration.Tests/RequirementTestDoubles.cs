using System;
using System.IO;
using System.Linq;
using System.Text;
using DoorsIntegration.Core;
using DoorsIntegration.Dxl;

namespace DoorsIntegration.Tests
{
    internal sealed class RequirementTestContext : IDisposable
    {
        public readonly ExecutorTestContext Transport = new ExecutorTestContext();
        public readonly RequirementReadRequest Request;
        public readonly DxlRequirementReader Reader;
        public Func<Guid, string> Reply;
        public byte[] RawReply;
        public string OutputPath;

        public RequirementTestContext(RequirementReadRequest request = null)
        {
            Request = request ?? new RequirementReadRequest("/P/M");
            File.Copy(new RequirementScriptOptions().ScriptPath, Transport.SourcePath, true);
            Reader = new DxlRequirementReader(Transport.Executor, new RequirementScriptOptions(Transport.SourcePath), Transport.Log);
            Reply = id => Batch(id, Request, Object(1, 10));
            Transport.Sessions.Session.FileResult = true;
            Transport.Sessions.Session.OnRun = session =>
            {
                var id = Guid.ParseExact(session.Prefix.Split('|')[2], "N");
                OutputPath = Transport.ResultPath(Transport.Executor.ActiveOperation);
                File.WriteAllBytes(OutputPath, RawReply ?? new UTF8Encoding(false, true).GetBytes(Reply(id)));
            };
        }

        public static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n");
        public static string Value(string name, string plain = "", string rtf = "", string status = "Readable", string richStatus = "Readable", string type = "Text") =>
            "V\t" + Escape(name) + "\t" + Escape(type) + "\t" + status + "\t" + Escape(plain) + "\t" + richStatus + "\t" + Escape(rtf) + "\n";
        public static string Object(int order, int absolute, string text = "body", string rtf = "{\\rtf1 body}",
            int level = 1, int? parent = null, bool deleted = false, string parentStatus = null, string[] values = null) =>
            "O\t" + order + "\t" + absolute + "\tREQ-" + absolute + "\t" + order + "\tReadable\t" + level + "\t" + parent + "\t" +
            (parentStatus ?? (parent.HasValue ? "Resolved" : "Root")) + "\t" + (deleted ? "1" : "0") + "\n" +
            string.Concat(values ?? new[] { Value("Object Heading", "Heading"), Value("Object Text", text, rtf) }) + "E\n";

        public static string Batch(Guid id, RequirementReadRequest request, string objects = "", int count = 1,
            string status = "Available", string ownership = "OwnedReadOnly", string cleanup = "Closed", int? visited = null,
            int denied = 0, int deletedExcluded = 0, int unavailable = 0, bool truncated = false, bool failed = false)
        {
            var baseline = request.Baseline;
            return "DOORS_REQUIREMENTS\t1\t" + id.ToString("N") + "\nM\t" + Escape(request.ModulePath) + "\t" +
                (baseline == null ? "Current\t\t\t\t0" : "Baseline\t" + baseline.Major + "\t" + baseline.Minor + "\t" + Escape(baseline.Suffix) + "\t1") + "\t" + ownership + "\n" +
                objects + "END\t" + status + "\t" + count + "\t" + (visited ?? count + denied + deletedExcluded + unavailable) + "\t" + denied + "\t" + deletedExcluded +
                "\t" + unavailable + "\t" + (truncated ? "1" : "0") + "\t" + (failed ? "1" : "0") + "\t" + cleanup + "\n";
        }
        public void Dispose() => Transport.Dispose();
    }
}
