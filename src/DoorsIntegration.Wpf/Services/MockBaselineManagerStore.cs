using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;

namespace DoorsIntegration.Wpf.Services
{
    // Coherent Mock server state, separate from the job journal. Never labels fixtures as NativeDxl.
    public sealed class MockBaselineManagerStore : IBaselineMetadataService, IBaselinePreflightStateReader, IBaselineExecutionAdapter, IModuleSelectionMetadataProvider
    {
        private readonly MockModuleSelectionAdapter catalog;
        private readonly MockBaselineHistoryAdapter original;
        private readonly MockBaselinePreflightStateReader states;
        private readonly string root;
        private readonly object sync = new object();
        private readonly List<Entry> entries = new List<Entry>(); private bool loaded;
        private DateTime directoryStamp;
        public MockBaselineManagerStore(MockModuleSelectionAdapter catalog, string root)
        { this.catalog = catalog; original = catalog.CreateBaselineHistoryAdapter(); states = catalog.CreatePreflightStateReader(); this.root = Path.GetFullPath(root); }
        public BaselineMetadataSource Source => BaselineMetadataSource.Mock;
        [DataContract]
        private sealed class Entry
        {
            [DataMember(IsRequired = true)] internal string Repository { get; set; }
            [DataMember(IsRequired = true)] internal string ModuleId { get; set; }
            [DataMember(IsRequired = true)] internal string Path { get; set; }
            [DataMember(IsRequired = true)] internal int Major { get; set; }
            [DataMember(IsRequired = true)] internal int Minor { get; set; }
            [DataMember(IsRequired = true)] internal string Suffix { get; set; }
            [DataMember(IsRequired = true)] internal string Annotation { get; set; }
            [DataMember(IsRequired = true)] internal string User { get; set; }
            [DataMember(IsRequired = true)] internal DateTime CreatedUtc { get; set; }
            internal BaselineModuleReference Module => new BaselineModuleReference(Repository, ModuleId, Path);
            internal BaselineVersion Version => new BaselineVersion(Major, Minor, Suffix);
            internal BaselineHistoryEntry History => new BaselineHistoryEntry(new BaselineIdentity(Module, Version), Annotation, User, CreatedUtc, CreatedUtc.ToString("O"));
        }
        private Entry[] For(BaselineModuleReference module)
        {
            lock (sync)
            {
                DateTime stamp = Directory.Exists(root) ? Directory.GetLastWriteTimeUtc(root) : DateTime.MinValue;
                if (loaded && stamp != directoryStamp) { loaded = false; entries.Clear(); }
                if (!loaded)
                {
                    var discovered = new List<Entry>();
                    if (Directory.Exists(root))
                    {
                        int count = 0;
                        foreach (string path in Directory.EnumerateFiles(root, "*.json"))
                        {
                            if (++count > 10000 || new FileInfo(path).Length > 131072) throw new InvalidDataException("Mock baseline store limit exceeded.");
                            using (var input = File.OpenRead(path))
                            {
                                var e = (Entry)new DataContractJsonSerializer(typeof(Entry)).ReadObject(input); var valid = e.History;
                                if (e.CreatedUtc.Kind != DateTimeKind.Utc || Path.GetFileNameWithoutExtension(path) != Key(e.Module, e.Version)) throw new InvalidDataException("Mock baseline store identity mismatch.");
                                discovered.Add(e);
                            }
                        }
                    }
                    entries.AddRange(discovered); loaded = true; directoryStamp = stamp;
                }
                return entries.Where(e => e.Module.Matches(module)).OrderBy(e => e.Major).ThenBy(e => e.Minor).ToArray();
            }
        }
        private async Task<MockBaselineHistoryAdapter> HistoryAdapter(BaselineHistoryRequest request, CancellationToken token)
        {
            var snapshot = await original.ReadAsync(request, token).ConfigureAwait(false); var added = For(request.Module);
            var major = await original.ProposeAsync(snapshot, BaselineType.Major, "", token).ConfigureAwait(false);
            var minor = await original.ProposeAsync(snapshot, BaselineType.Minor, "", token).ConfigureAwait(false);
            var last = added.LastOrDefault()?.Version;
            // Explicit Mock numbering only; never a native fallback.
            return new MockBaselineHistoryAdapter(new[] { new MockBaselineModuleState(request.Module,
                snapshot.History.Concat(added.Select(e => e.History)), last ?? snapshot.LatestNumberingVersion.Version,
                last == null ? major.ExpectedVersion : new BaselineVersion(last.Major + 1, 0), last == null ? minor.ExpectedVersion : new BaselineVersion(last.Major, last.Minor + 1),
                status: snapshot.Status) });
        }
        public async Task<BaselineMetadataResult> ReadAsync(BaselineHistoryRequest request, CancellationToken token)
        { var history = await HistoryAdapter(request, token).ConfigureAwait(false); return await new BaselineMetadataService(history, history, new ClientLog()).ReadAsync(request, token).ConfigureAwait(false); }
        public async Task<BaselinePreflightObservation> ReadAsync(BaselinePreflightProbe probe, CancellationToken token)
        {
            var observation = await states.ReadAsync(probe, token).ConfigureAwait(false); var f = observation.Facts; var added = For(probe.Module);
            return new BaselinePreflightObservation(probe, new BaselinePreflightFacts(f.Module, f.Exists, f.Type, f.ReadAccess, f.CreateAccess, f.ModifyAccess,
                f.EditState, f.LockState, f.ExclusiveAvailability, f.UnsavedChanges, f.SessionValid, f.DatabaseIdentity, f.CurrentUserId, f.SessionId, f.ConnectionEpoch,
                f.ContentRevision + ":" + added.Length, f.PermissionRevision, f.LockRevision, f.RevisionsReliable, f.SuffixValid, f.AnnotationValid,
                added.Any(e => e.Version.Equals(probe.ExpectedVersion)) ? BaselineTargetAvailability.Exists : BaselineTargetAvailability.Free,
                Source, "Coherent Mock server state; no native access or lock claim.", f.LockOwnerSessionId, f.LockOwnerUserId, f.DirtyOwnerSessionId), DateTime.UtcNow);
        }
        public async Task<ModuleSelectionMetadata> ReadAsync(string repository, string path, DoorsVersion version, DoorsConnectionMode connection, TimeSpan timeout, CancellationToken token)
        {
            var item = await catalog.ReadAsync(repository, path, version, connection, timeout, token).ConfigureAwait(false);
            var last = item.Module == null ? null : For(item.Module).LastOrDefault();
            return last == null ? item : new ModuleSelectionMetadata(path, item.Module, item.Access, SelectionBaselineStatus.Available, last.Version, "Persisted Mock baseline; no real DOORS mutation.");
        }
        public async Task<BaselineModuleExecutionResult> ExecuteBaselineAsync(BaselineExecutionAuthorization auth, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); var start = DateTime.UtcNow;
            if (auth.Item.Precondition.Facts.Source != Source || !auth.TryBeginWrite()) return Result(BaselineWriteOutcome.Blocked, false, "Mock approval expired or source mismatch.");
            await Task.Delay(60).ConfigureAwait(false); // Allow observable progress / Cancel Remaining in the demo.
            var request = new BaselineHistoryRequest(auth.Item.Module, true, auth.Item.Type, auth.Item.Suffix, auth.Item.ExpectedVersion);
            var metadata = await ReadAsync(request, CancellationToken.None).ConfigureAwait(false);
            if (metadata.Collision.Status == BaselineExistenceStatus.Exists || !Equals(metadata.ExpectedVersion, auth.Item.ExpectedVersion))
                return Result(BaselineWriteOutcome.Blocked, false, "Mock target exists or next version changed.");
            var entry = new Entry { Repository = auth.Item.Module.RepositoryId, ModuleId = auth.Item.ModuleId, Path = auth.Item.FullPath,
                Major = auth.Item.ExpectedVersion.Major, Minor = auth.Item.ExpectedVersion.Minor, Suffix = auth.Item.Suffix, Annotation = auth.Item.Annotation,
                User = auth.Item.Precondition.Facts.CurrentUserId, CreatedUtc = DateTime.UtcNow };
            string target = Path.Combine(root, Key(entry.Module, entry.Version) + ".json");
            lock (sync)
            {
                if (entries.Any(e => e.Module.Matches(entry.Module) && e.Version.Equals(entry.Version))) return Result(BaselineWriteOutcome.Blocked, false, "Duplicate Mock target prevented.");
                Directory.CreateDirectory(root); string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    { new DataContractJsonSerializer(typeof(Entry)).WriteObject(output, entry); output.Flush(true); }
                    File.Move(temporary, target); entries.Add(entry); directoryStamp = Directory.GetLastWriteTimeUtc(root);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            return Result(BaselineWriteOutcome.Created, true, "MOCK: simulated baseline persisted; no DOORS data was changed.");
            BaselineModuleExecutionResult Result(BaselineWriteOutcome outcome, bool attempted, string reason) => new BaselineModuleExecutionResult(
                auth.Review.JobId, auth.Item, outcome, Source, attempted, reason, start, DateTime.UtcNow, Guid.NewGuid());
        }
        private static string Key(BaselineModuleReference module, BaselineVersion version)
        { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(module.RepositoryId + "\n" + module.ModuleId + "\n" + version))).Replace("-", ""); }
    }
}
