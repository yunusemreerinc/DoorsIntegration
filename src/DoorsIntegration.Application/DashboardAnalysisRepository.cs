using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using DoorsIntegration.Core;

namespace DoorsIntegration.ConfigurationManagement
{
    public sealed class DashboardAnalysisRepository : IDashboardAnalysisRepository, IDashboardAnalysisDiagnostics
    {
        private readonly string root; private readonly IBaselineJournalProtector protector; private readonly object sync=new object();
        private readonly List<string> readWarnings=new List<string>();
        public IReadOnlyList<string> ReadWarnings { get { lock(sync) return readWarnings.ToArray(); } }
        public DashboardAnalysisRepository(string root,IBaselineJournalProtector protector) { this.root=Path.GetFullPath(root); this.protector=protector; }
        public void Save(DashboardAnalysisRecord record)
        {
            if(record==null||record.TimestampUtc.Kind!=DateTimeKind.Utc||record.Requirements.Count>1000000||record.Links.Count>1000000||record.Findings.Count>2000000) throw new ArgumentException("Invalid dashboard analysis evidence.");
            byte[] bytes=DashboardSerialization.Bytes(record); if(bytes.Length>134217728) throw new InvalidDataException("Analysis evidence size bound reached.");
            lock(sync)
            {
                Directory.CreateDirectory(root); string path=Path.Combine(root,DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffff",CultureInfo.InvariantCulture)+"-"+Guid.NewGuid().ToString("N").Substring(0,12)+".analysis"),temp=Path.Combine(root,"."+Guid.NewGuid().ToString("N").Substring(0,12)+".tmp");
                try { bytes=protector.Protect(bytes); using(var file=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None,16384,FileOptions.WriteThrough)) { file.Write(bytes,0,bytes.Length); file.Flush(true); } File.Move(temp,path); }
                finally { if(File.Exists(temp)) File.Delete(temp); }
            }
        }
        public IEnumerable<DashboardAnalysisRecord> Read(DashboardRequest request,CancellationToken token)
        {
            lock(sync) readWarnings.Clear();
            if(!Directory.Exists(root)) yield break; var ids=new HashSet<string>(); long rows=0;
            foreach(string path in Directory.EnumerateFiles(root,"*.analysis").OrderByDescending(p=>p,StringComparer.Ordinal).Take(256))
            {
                token.ThrowIfCancellationRequested(); DashboardAnalysisRecord record;
                try { if(new FileInfo(path).Length>150994944) throw new InvalidDataException(); record=DashboardSerialization.Read<DashboardAnalysisRecord>(protector.Unprotect(File.ReadAllBytes(path))); }
                catch(Exception ex) when(ex is IOException||ex is System.Runtime.Serialization.SerializationException||ex is System.Security.Cryptography.CryptographicException) { lock(sync) if(readWarnings.Count==0) readWarnings.Add("Stored analysis evidence could not be verified; skipped records may limit totals/history."); continue; }
                if(record.Environment!=request.Environment||record.DatabaseIdentity!=request.DatabaseIdentity||record.IsMock!=request.IsMock||!ids.Add(record.Kind+":"+record.AnalysisId)) continue;
                rows+=record.Requirements.Count+record.Links.Count+record.Findings.Count; if(rows>2000000||ids.Count>32) { lock(sync) readWarnings.Add("Analysis history read bound reached; latest compatible observations only."); yield break; }
                yield return record;
            }
        }
        public bool Contains(string analysisId)
        {
            if(!Directory.Exists(root)) return false;
            foreach(string file in Directory.EnumerateFiles(root,"*.analysis").OrderByDescending(p=>p,StringComparer.Ordinal).Take(256))
                try { if(new FileInfo(file).Length<=150994944 && DashboardSerialization.Read<DashboardAnalysisRecord>(protector.Unprotect(File.ReadAllBytes(file))).AnalysisId==analysisId) return true; } catch(Exception ex) when(ex is IOException||ex is System.Runtime.Serialization.SerializationException||ex is System.Security.Cryptography.CryptographicException) { }
            return false;
        }
    }
}
