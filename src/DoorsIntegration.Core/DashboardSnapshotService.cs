using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DoorsIntegration.Core
{
    public sealed class DashboardSnapshotHeader
    { public Guid SnapshotId { get; set; } public string RequestKey { get; set; } public DateTime CollectedAtUtc { get; set; } public string Status { get; set; } }
    public sealed class DashboardSnapshotService
    {
        [System.Runtime.Serialization.DataContract] private sealed class Invalidation
        { [System.Runtime.Serialization.DataMember] public DateTime AtUtc { get; set; } [System.Runtime.Serialization.DataMember] public List<Guid> Existing { get; set; } = new List<Guid>(); }
        private readonly string root; private readonly IBaselineJournalProtector protector; private readonly object sync=new object();
        public DashboardSnapshotService(string directory,IBaselineJournalProtector protector)
        { root=Path.GetFullPath(directory??throw new ArgumentNullException(nameof(directory))); this.protector=protector??throw new ArgumentNullException(nameof(protector)); }
        private string Folder(string key) { if(key==null||key.Length!=64||key.Any(c=>!Uri.IsHexDigit(c))) throw new ArgumentException("Invalid dashboard cache key."); return Path.Combine(root,key); }
        private void Atomic(string path,byte[] bytes,bool replace)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)); string temp=Path.Combine(Path.GetDirectoryName(path),"."+Guid.NewGuid().ToString("N").Substring(0,16)+".tmp");
            try { using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None,16384,FileOptions.WriteThrough)) { output.Write(bytes,0,bytes.Length); output.Flush(true); }
                if(replace&&File.Exists(path)) File.Replace(temp,path,null); else File.Move(temp,path); }
            finally { if(File.Exists(temp)) File.Delete(temp); }
        }
        public void Save(DashboardSnapshot snapshot)
        {
            if(snapshot?.Request==null||snapshot.RequestKey!=snapshot.Request.Key||snapshot.SchemaVersion!=1||snapshot.SnapshotId==Guid.Empty||snapshot.CollectedAtUtc.Kind!=DateTimeKind.Utc) throw new InvalidDataException("Invalid dashboard snapshot binding.");
            byte[] bytes=DashboardSerialization.Bytes(snapshot); if(bytes.Length>33554432) throw new InvalidDataException("Aggregate snapshot exceeds 32 MiB bound.");
            lock(sync) Atomic(Path.Combine(Folder(snapshot.RequestKey),snapshot.CollectedAtUtc.ToString("yyyyMMddTHHmmssfffffff",CultureInfo.InvariantCulture)+"-"+snapshot.SnapshotId.ToString("N").Substring(0,12)+".snapshot"),protector.Protect(bytes),false);
        }
        private IEnumerable<DashboardSnapshot> Enumerate(string key,List<string> warnings)
        {
            string folder=Folder(key); if(!Directory.Exists(folder)) yield break;
            foreach(string path in Directory.EnumerateFiles(folder,"*.snapshot").OrderByDescending(p=>p,StringComparer.Ordinal).Take(256))
            {
                DashboardSnapshot snapshot=null;
                try { var file=new FileInfo(path); if(file.Length>67108864) throw new InvalidDataException(); snapshot=DashboardSerialization.Read<DashboardSnapshot>(protector.Unprotect(File.ReadAllBytes(path))); if(snapshot.SchemaVersion!=1 || snapshot.RequestKey!=key || snapshot.Request?.Key!=key || snapshot.CollectedAtUtc.Kind!=DateTimeKind.Utc) throw new InvalidDataException(); }
                catch(Exception ex) when(ex is IOException||ex is System.Runtime.Serialization.SerializationException||ex is System.Security.Cryptography.CryptographicException||ex is ArgumentException||ex is InvalidOperationException)
                { snapshot=null; warnings?.Add("A local cached snapshot could not be verified; previous valid history is retained."); }
                if(snapshot!=null) yield return snapshot;
            }
        }
        public DashboardSnapshot LoadLatest(string key,List<string> warnings=null)
        { lock(sync) { var invalidated=InvalidatedAt(key); return Enumerate(key,warnings).FirstOrDefault(s=>s.CollectedAtUtc>=invalidated.AtUtc&&!invalidated.Existing.Contains(s.SnapshotId)); } }
        public DashboardSnapshot Load(string key,Guid id)
        { lock(sync) return Enumerate(key,null).SingleOrDefault(s=>s.SnapshotId==id); }
        public IReadOnlyList<DashboardSnapshotHeader> History(string key)
        { lock(sync) return Enumerate(key,null).Take(64).Select(s=>new DashboardSnapshotHeader { SnapshotId=s.SnapshotId,RequestKey=key,CollectedAtUtc=s.CollectedAtUtc,Status=s.DataQuality.Completeness.ToString() }).ToArray(); }
        private Invalidation InvalidatedAt(string key)
        {
            string path=Path.Combine(Folder(key),"invalidated.cache"); if(!File.Exists(path)) return new Invalidation { AtUtc=DateTime.MinValue };
            try { if(new FileInfo(path).Length>65536) throw new IOException(); return DashboardSerialization.Read<Invalidation>(protector.Unprotect(File.ReadAllBytes(path))); }
            catch { return new Invalidation { AtUtc=DateTime.MaxValue }; } // Corrupt invalidation never resurrects old cache as valid.
        }
        public void Invalidate(string key,DateTime atUtc) { lock(sync) Atomic(Path.Combine(Folder(key),"invalidated.cache"),protector.Protect(DashboardSerialization.Bytes(new Invalidation { AtUtc=atUtc,Existing=Enumerate(key,null).Select(s=>s.SnapshotId).ToList() })),true); }
        private string ProfilePath(string name)
        { if(string.IsNullOrWhiteSpace(name)||name.Length>100||Path.GetFileName(name)!=name||name.Any(c=>!char.IsLetterOrDigit(c)&&c!='-'&&c!='_')) throw new ArgumentException("Use a simple dashboard profile name."); return Path.Combine(root,"profiles",name+".profile"); }
        public void SaveProfile(string name,DashboardRequest request) { request.Validate(); lock(sync) Atomic(ProfilePath(name),protector.Protect(DashboardSerialization.Bytes(request)),true); }
        public DashboardRequest LoadProfile(string name)
        { lock(sync) { string path=ProfilePath(name); if(new FileInfo(path).Length>1048576) throw new InvalidDataException("Scope profile too large."); var request=DashboardSerialization.Read<DashboardRequest>(protector.Unprotect(File.ReadAllBytes(path))); request.Validate(); return request; } }
    }
    public sealed class DashboardCacheService
    {
        private readonly DashboardSnapshotService store; private readonly ConcurrentDictionary<string,DashboardSnapshot> memory=new ConcurrentDictionary<string,DashboardSnapshot>();
        private long requests,hits;
        public DashboardCacheService(DashboardSnapshotService store) { this.store=store; }
        public double HitRatio => Interlocked.Read(ref requests)==0?0:(double)Interlocked.Read(ref hits)/Interlocked.Read(ref requests);
        public DashboardSnapshot Get(string key,List<string> warnings)
        {
            Interlocked.Increment(ref requests);
            if(!memory.TryGetValue(key,out var snapshot)) { snapshot=store.LoadLatest(key,warnings); if(snapshot!=null) PutMemory(key,snapshot); }
            if(snapshot==null) return null; Interlocked.Increment(ref hits); return DashboardSerialization.Copy(snapshot);
        }
        private void PutMemory(string key,DashboardSnapshot snapshot) { if(memory.Count>=8) memory.Clear(); memory[key]=DashboardSerialization.Copy(snapshot); }
        public void Publish(DashboardSnapshot snapshot) { store.Save(snapshot); PutMemory(snapshot.RequestKey,snapshot); }
        public void Invalidate(string key,DateTime now) { store.Invalidate(key,now); memory.TryRemove(key,out var ignored); }
    }
    public sealed class DashboardRefreshCoordinator
    {
        private readonly SemaphoreSlim queue=new SemaphoreSlim(1,1); private readonly IDashboardDataProvider provider; private readonly DashboardCacheService cache;
        private readonly DashboardAggregationService aggregation; private readonly DashboardMetricsCalculator calculator; private readonly IDashboardClock clock;
        public DashboardRefreshCoordinator(IDashboardDataProvider provider,DashboardCacheService cache,DashboardAggregationService aggregation,DashboardMetricsCalculator calculator,IDashboardClock clock)
        { this.provider=provider; this.cache=cache; this.aggregation=aggregation; this.calculator=calculator; this.clock=clock; }
        public async Task<DashboardRefreshResult> RefreshAsync(DashboardRequest input,DashboardSections sections,DashboardRefreshMode mode,IProgress<string> progress,CancellationToken token)
        {
            var request=DashboardSerialization.Copy(input); request.Validate(); if(!Enum.IsDefined(typeof(DashboardRefreshMode),mode)) throw new ArgumentException("Unknown dashboard refresh mode.");
            await queue.WaitAsync(token).ConfigureAwait(false); DashboardSnapshot previous=null; var total=Stopwatch.StartNew();
            try
            {
                var warnings=new List<string>(); var load=Stopwatch.StartNew(); previous=cache.Get(request.Key,warnings); double loadMs=load.Elapsed.TotalMilliseconds;
                if(mode==DashboardRefreshMode.CacheOnly) { if(previous!=null) { calculator.Finalize(previous,clock.UtcNow); previous.DataQuality.Warnings.AddRange(warnings); } return new DashboardRefreshResult { Snapshot=previous,FromCache=previous!=null,Status=previous==null?"Empty":"Cached",SnapshotLoadMilliseconds=loadMs,CacheHitRatio=cache.HitRatio }; }
                token.ThrowIfCancellationRequested(); progress?.Report(mode==DashboardRefreshMode.RunNewAnalysis?"Collecting read-only data and explicitly running selected analyses...":"Refreshing observations and existing analysis results; no implicit analysis...");
                long before=GC.GetTotalMemory(false);
                var evidence=await provider.CollectAsync(request,sections,mode==DashboardRefreshMode.RunNewAnalysis,progress,token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
                var snapshot=aggregation.Aggregate(request,evidence,sections,previous,token); snapshot.RefreshMilliseconds=total.Elapsed.TotalMilliseconds; snapshot.ManagedMemoryDeltaBytes=Math.Max(0,GC.GetTotalMemory(false)-before);
                snapshot.ComCallCount=evidence.ComCallCount;
                using(var process=Process.GetCurrentProcess()) snapshot.PeakWorkingSetBytes=process.PeakWorkingSet64;
                snapshot.DataQuality.Warnings.AddRange(warnings); token.ThrowIfCancellationRequested(); bool failed=false;
                try { cache.Publish(snapshot); } catch(Exception ex) when(ex is IOException||ex is UnauthorizedAccessException||ex is System.Security.Cryptography.CryptographicException) { failed=true; snapshot.DataQuality.Warnings.Add("Fresh snapshot is displayed but local cache publication failed; previous history is unchanged."); }
                return new DashboardRefreshResult { Snapshot=snapshot,Status=failed?"CacheWriteFailed":snapshot.DataQuality.VerifiedComplete?"Complete":"Incomplete",CacheWriteFailed=failed,SnapshotLoadMilliseconds=loadMs,CacheHitRatio=cache.HitRatio };
            }
            catch(OperationCanceledException) { return new DashboardRefreshResult { Snapshot=previous,FromCache=previous!=null,Cancelled=true,Status="Cancelled",Error="Refresh cancelled; previously committed snapshot retained. Active COM calls are not forcibly stopped.",CacheHitRatio=cache.HitRatio }; }
            catch(IntegrationException ex) when(ex.Code==IntegrationErrorCode.DxlWaitCancelled)
            { return new DashboardRefreshResult { Snapshot=previous,FromCache=previous!=null,Cancelled=true,Status="Cancelled",Error="COM wait cancelled; original call may still be running. Previous snapshot retained; no automatic retry.",CacheHitRatio=cache.HitRatio }; }
            catch(Exception ex) when(ex is IntegrationException||ex is IOException||ex is InvalidOperationException)
            { if(previous!=null) { calculator.Finalize(previous,clock.UtcNow); previous.DataQuality.Warnings.Add("Refresh failed; retained snapshot is not a fresh observation."); } return new DashboardRefreshResult { Snapshot=previous,FromCache=previous!=null,Status="Failed",Error="Read-only dashboard refresh failed: "+(ex is IntegrationException integration?integration.Code.ToString():ex.GetType().Name)+". No automatic retry.",CacheHitRatio=cache.HitRatio }; }
            finally { queue.Release(); }
        }
    }
    public sealed class ConfigurationDashboardService : IConfigurationDashboardService
    {
        private readonly DashboardRefreshCoordinator coordinator;
        public ConfigurationDashboardService(DashboardRefreshCoordinator coordinator) { this.coordinator=coordinator; }
        public Task<DashboardRefreshResult> RefreshAsync(DashboardRequest request,DashboardSections sections=DashboardSections.All,DashboardRefreshMode mode=DashboardRefreshMode.RefreshExistingResults,IProgress<string> progress=null,CancellationToken token=default(CancellationToken)) => coordinator.RefreshAsync(request,sections,mode,progress,token);
    }
    public sealed class DashboardNavigationService
    {
        private readonly IDashboardDataProvider provider;
        public DashboardNavigationService(IDashboardDataProvider provider) { this.provider=provider; }
        public DashboardNavigationIntent Create(DashboardSnapshot snapshot,DashboardDestination destination,string moduleIdentity=null,DashboardFinding finding=null,DashboardAuditEntry job=null)
        {
            if(snapshot?.Request==null) throw new ArgumentException("Select a dashboard snapshot first.");
            if(finding!=null && !snapshot.Sections.SelectMany(s=>s.Findings).Any(f=>f.Id==finding.Id)) throw new ArgumentException("Finding is not in this snapshot.");
            moduleIdentity=finding?.ModuleIdentity??moduleIdentity;
            var versions=snapshot.NavigationModules.Where(v=>v.ModuleIdentity==moduleIdentity).ToArray(); BaselineVersionReference version=null;
            if(finding?.AbsoluteNumber>0) version=versions.SingleOrDefault(v=>v.NodeIdentity(finding.AbsoluteNumber.Value)==finding.RequirementVersionIdentity);
            if(finding==null) version=snapshot.ObservedModules.SingleOrDefault(v=>v.ModuleIdentity==moduleIdentity);
            if(version==null && versions.Length==1 && finding?.RequirementVersionIdentity==null) version=versions[0];
            if(moduleIdentity!=null&&version==null) throw new InvalidOperationException("No unambiguous stable version binding for this source; no fallback navigation.");
            if(job!=null&&!snapshot.Sections.Where(s=>s.Audit!=null).SelectMany(s=>s.Audit.Recent).Any(j=>j.JobId==job.JobId&&j.JobType==job.JobType)) throw new ArgumentException("Job is outside the snapshot.");
            return new DashboardNavigationIntent { Destination=destination,ClientVersion=snapshot.Request.ClientVersion,Environment=snapshot.Request.Environment,DatabaseIdentity=snapshot.Request.DatabaseIdentity,OriginSnapshotIdentity=snapshot.Identity,Scope=DashboardSerialization.Copy(snapshot.Request.Scope),ModuleIdentity=version?.ModuleIdentity,ModulePath=version?.FullPath,Version=version,RequirementIdentity=finding?.RequirementIdentity,AbsoluteNumber=finding?.AbsoluteNumber,AnalysisIdentity=finding?.AnalysisIdentity??job?.SourceAnalysisIdentity,AnalysisSnapshot=finding?.AnalysisSnapshotIdentity,FindingIdentity=finding?.Id,JobId=job?.JobId,DefinitionIdentity=snapshot.Request.Scope.DefinitionIdentity,SetIdentity=snapshot.Request.Scope.SetIdentity };
        }
        public async Task ValidateAsync(DashboardSnapshot snapshot,DashboardNavigationIntent intent,CancellationToken token)
        { if(intent==null||intent.OriginSnapshotIdentity!=snapshot.Identity||intent.Environment!=snapshot.Request.Environment||intent.DatabaseIdentity!=snapshot.Request.DatabaseIdentity) throw new InvalidOperationException("Navigation context changed; select the source again."); await provider.ValidateNavigationAsync(intent,token).ConfigureAwait(false); }
    }
}
