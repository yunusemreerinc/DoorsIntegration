using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;
using DoorsIntegration.Dxl;

namespace DoorsIntegration.Tests
{
    internal sealed class DiscoveryTestContext : IDisposable
    {
        public readonly ExecutorTestContext Transport = new ExecutorTestContext();
        public readonly List<string> Visits = new List<string>();
        public readonly Dictionary<string, Func<Guid, string>> Replies = new Dictionary<string, Func<Guid, string>>(StringComparer.Ordinal);
        public string LastMode;
        public DxlHierarchyReader Reader { get; }

        public DiscoveryTestContext(IDiscoveryDebugStore debugStore = null)
        {
            File.Copy(new DiscoveryScriptOptions().ScriptPath, Transport.SourcePath, true);
            Reader = new DxlHierarchyReader(Transport.Executor, new DiscoveryScriptOptions(Transport.SourcePath), Transport.Log, debugStore ?? new DiscardDiscoveryDebugStore());
            Transport.Sessions.Session.OnRun = session =>
            {
                var path = Parameter(session.Script, "path");
                Visits.Add(path);
                LastMode = Parameter(session.Script, "mode");
                var id = Guid.ParseExact(session.Prefix.Split('|')[2], "N");
                if (!Replies.TryGetValue(path, out var reply)) throw new InvalidOperationException("No fake response for requested path.");
                session.Payload = reply(id);
            };
        }

        public void Reply(string path, string scopeType, params string[] children)
        {
            Replies[path] = id => Batch(id, path, scopeType, "Readable", children);
        }

        public static string Batch(Guid id, string path, string type, string access = "Readable", string[] children = null,
            string warning = null, bool truncated = false)
        {
            var records = children ?? new string[0];
            return "DOORS_DISCOVERY|1|" + id.ToString("N") + "\n" + Row("S", path, type, access) +
                string.Concat(records) + (warning == null ? "" : "W\t" + warning + "\n") +
                "END\t" + records.Length + "\t" + (truncated ? "1" : "0") + "\n";
        }

        public static string Row(string role, string path, string type, string access = "Readable") =>
            role + "\t" + Escape(path) + "\t" + Escape(DoorsItemPath.LeafName(path)) + "\t" + type + "\t" + access + "\n";

        private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n");

        public static string Parameter(string script, string name)
        {
            var match = Regex.Match(script, "string di_arg_" + name + " = (\"(?:\\\\.|[^\"\\\\])*\");");
            if (!match.Success) throw new InvalidDataException("Missing generated parameter.");
            var literal = match.Groups[1].Value;
            var result = new StringBuilder();
            for (int index = 1; index < literal.Length - 1; index++)
            {
                char value = literal[index];
                if (value == '\\')
                {
                    value = literal[++index];
                    if (value == 'n') value = '\n';
                    else if (value == 'r') value = '\r';
                    else if (value == 't') value = '\t';
                }
                result.Append(value);
            }
            return result.ToString();
        }
        public void Dispose() => Transport.Dispose();
    }

    internal sealed class DiscardDiscoveryDebugStore : IDiscoveryDebugStore
    {
        public void Save(DiscoveryDebugRecord record) { }
    }

    internal sealed class RecordingHierarchyReader : IDoorsHierarchyReader
    {
        public int Calls;
        public DiscoveryKind Kind;
        public DiscoveryScope Scope;
        public CancellationToken Token;
        public IntegrationException Failure { get; set; }
        public Task<DiscoverySnapshot> DiscoverAsync(DiscoveryKind kind, DiscoveryScope scope, CancellationToken cancellationToken = default(CancellationToken))
        {
            Calls++;
            Kind = kind;
            Scope = scope;
            Token = cancellationToken;
            if (Failure != null) return Task.FromException<DiscoverySnapshot>(Failure);
            return Task.FromResult(new DiscoverySnapshot(Guid.NewGuid(), kind, scope,
                new[] { new DoorsDiscoveryItem("/P/M", "M", DoorsItemKind.FormalModule, DoorsAccessStatus.Readable) },
                Enumerable.Empty<DiscoveryIssue>(), 1, false, DateTime.UtcNow));
        }
    }
}
