using System;
using System.IO;
using System.Text;
using DoorsIntegration.Core;
using DoorsIntegration.Dxl;

namespace DoorsIntegration.Tests
{
    internal sealed class LinkTestContext : IDisposable
    {
        public readonly ExecutorTestContext Transport = new ExecutorTestContext();
        public readonly LinkReadRequest Request;
        public readonly DxlLinkReader Reader;
        public Func<Guid, string> Reply;
        public byte[] RawReply;
        public string OutputPath;
        public LinkTestContext(LinkReadRequest request = null)
        {
            Request = request ?? new LinkReadRequest("/P/M", "/P/Links");
            File.Copy(new LinkScriptOptions().ScriptPath, Transport.SourcePath, true);
            Reader = new DxlLinkReader(Transport.Executor, new LinkScriptOptions(Transport.SourcePath), Transport.Log);
            Reply = id => Batch(id, Request, Row(1));
            Transport.Sessions.Session.FileResult = true;
            Transport.Sessions.Session.OnRun = session =>
            {
                var id = Guid.ParseExact(session.Prefix.Split('|')[2], "N");
                OutputPath = Transport.ResultPath(Transport.Executor.ActiveOperation);
                File.WriteAllBytes(OutputPath, RawReply ?? new UTF8Encoding(false, true).GetBytes(Reply(id)));
            };
        }
        public static string Escape(string value) => RequirementTestContext.Escape(value);
        public static string Endpoint(string path, int? absolute, string identifier, string status = "Resolved", string kind = "Current", string version = "",
            string surrogate = "NotConfigured", string external = "", string reference = "NotRequested") =>
            Escape(path) + "\t" + kind + "\t" + Escape(version) + "\t" + absolute + "\t" + Escape(identifier) + "\t" + status + "\t" + surrogate + "\t" + Escape(external) + "\t" + reference;
        public static string Row(int sequence, string direction = "Outgoing", string kind = "Link", string status = "Resolved", bool echoed = false,
            string verification = "Verified", string source = null, string target = null, string linkPath = "/P/Links") =>
            "L\t" + sequence + "\t" + direction + "\t" + kind + "\t" + status + "\t" + (echoed ? "1" : "0") + "\t" + verification + "\t" + Escape(linkPath) + "\t" +
            (source ?? Endpoint("/P/M", 10, "REQ-10")) + "\t" + (target ?? Endpoint("/P/N", 20, "SYS-20")) + "\n";
        public static string Batch(Guid id, LinkReadRequest request, string rows = "", int count = 1, string moduleStatus = "Available", string linkStatus = "Available",
            string ownership = "OwnedReadOnly", int visited = 1, int denied = 0, int deleted = 0, int unresolved = 0, int gaps = 0,
            bool truncated = false, bool failed = false, int opened = 1, int cleanupFailures = 0)
        {
            var baseline = request.Baseline;
            return "DOORS_LINKS\t1\t" + id.ToString("N") + "\nM\t" + Escape(request.ModulePath) + "\t" + Escape(request.LinkModulePath) + "\t" + request.Direction + "\t" +
                (baseline == null ? "Current\t\t\t\t0" : "Baseline\t" + baseline.Major + "\t" + baseline.Minor + "\t" + Escape(baseline.Suffix) + "\t1") + "\t" + ownership + "\t" + (request.IncludeDeletedObjects ? "1" : "0") + "\n" +
                rows + "END\t" + moduleStatus + "\t" + linkStatus + "\t" + count + "\t" + visited + "\t" + denied + "\t" + deleted + "\t" + unresolved + "\t" + gaps + "\t" +
                (truncated ? "1" : "0") + "\t" + (failed ? "1" : "0") + "\t" + opened + "\t" + cleanupFailures + "\n";
        }
        public void Dispose() => Transport.Dispose();
    }
}
