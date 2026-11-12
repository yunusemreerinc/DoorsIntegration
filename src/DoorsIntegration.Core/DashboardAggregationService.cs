using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace DoorsIntegration.Core
{
    public sealed class DashboardAggregationService
    {
        private readonly IDashboardClock clock; private readonly DashboardMetricsCalculator calculator;
        public DashboardAggregationService(IDashboardClock clock,DashboardMetricsCalculator calculator) { this.clock=clock; this.calculator=calculator; }
        public DashboardSnapshot Aggregate(DashboardRequest input,DashboardEvidence data,DashboardSections sections,DashboardSnapshot previous=null,CancellationToken token=default(CancellationToken))
        {
            var watch=Stopwatch.StartNew(); var request=DashboardSerialization.Copy(input); request.Validate();
            string requestKey=request.Key;
            if(data.Environment!=request.Environment || data.DatabaseIdentity!=request.DatabaseIdentity || data.IsMock!=request.IsMock) throw new ArgumentException("Dashboard evidence environment/database/mode mismatch.");
            if((sections & ~DashboardSections.All)!=0 || sections==DashboardSections.None || (data.CollectedSections&sections)!=sections) throw new ArgumentException("Dashboard section echo mismatch.");
            if(previous!=null && previous.RequestKey!=request.Key) throw new ArgumentException("Partial refresh cannot combine different scope/filter/policy keys.");
            if(data.Modules.Count>request.MaximumModules || data.Requirements.Count>request.MaximumRequirements) throw new ArgumentException("Provider exceeded dashboard bounds.");
            var modules=new Dictionary<string,DashboardModuleFact>(StringComparer.Ordinal);
            foreach(var m in data.Modules)
            {
                token.ThrowIfCancellationRequested();
                if(m.Health==null || string.IsNullOrWhiteSpace(m.Health.ModuleIdentity) || !request.Scope.Contains(m.Health.ModulePath,m.Health.ModuleIdentity)) continue;
                if(m.Health.Version!=null && (m.Health.Version.ModuleIdentity!=m.Health.ModuleIdentity || m.Health.Version.RepositoryId!=request.DatabaseIdentity || m.Health.Version.FullPath!=m.Health.ModulePath)) throw new ArgumentException("Module stable identity/version mismatch.");
                if((request.Scope.Kind==DashboardScopeKind.SelectedFormalModules||request.Scope.Kind==DashboardScopeKind.SelectedBaselineSnapshot)&&!request.Scope.Modules.Any(v=>v.ModuleBaselineIdentity==m.Health.Version?.ModuleBaselineIdentity)) throw new ArgumentException("Provider returned a different module version from the selected scope.");
                if(modules.TryGetValue(m.Health.ModuleIdentity,out var old)) { if(old.Health.ModulePath!=m.Health.ModulePath || old.Health.Version?.ModuleBaselineIdentity!=m.Health.Version?.ModuleBaselineIdentity) throw new ArgumentException("Conflicting duplicate module identity."); continue; }
                modules.Add(m.Health.ModuleIdentity,DashboardSerialization.Copy(m));
            }
            var compatible=data.Analyses.Where(a=>Compatible(a,request)).ToArray();
            foreach(var module in modules.Values) ApplyBaselineEvidence(module,compatible,request,clock.UtcNow);
            var eligible=modules.Values.Where(m=>ModuleFilter(m,request.Filters)).ToDictionary(m=>m.Health.ModuleIdentity,StringComparer.Ordinal);
            int unknownFilters=0; var requirements=new Dictionary<string,DashboardRequirementFact>(StringComparer.Ordinal);
            foreach(var r in data.Requirements)
            {
                token.ThrowIfCancellationRequested();
                if(r.Version==null || !r.Version.IdentityVerified || r.Version.RepositoryId!=request.DatabaseIdentity || r.AbsoluteNumber<1) throw new ArgumentException("Requirement lacks persistent module/object identity.");
                if(!eligible.TryGetValue(r.ModuleIdentity,out var module) || r.Version.ModuleBaselineIdentity!=module.Health.Version?.ModuleBaselineIdentity) continue;
                bool? match=RequirementFilter(r,request.Filters); if(!match.HasValue) { unknownFilters++; continue; } if(match.Value) requirements[r.VersionIdentity]=r;
            }
            if(request.Filters.FindingSeverity.HasValue || request.Filters.ReviewStatus.Length>0)
            {
                var ids=new HashSet<string>(compatible.SelectMany(a=>a.Findings).Where(f=>FindingFilter(f,request.Filters)).Select(f=>f.RequirementVersionIdentity),StringComparer.Ordinal);
                requirements=requirements.Where(p=>ids.Contains(p.Key)).ToDictionary(p=>p.Key,p=>p.Value,StringComparer.Ordinal);
            }
            bool reqFilter=HasRequirementFilter(request.Filters);
            var matchedModules=new HashSet<string>(requirements.Values.Select(r=>r.ModuleIdentity));
            if(reqFilter) eligible=eligible.Where(p=>matchedModules.Contains(p.Key) || !p.Value.InventoryComplete).ToDictionary(p=>p.Key,p=>p.Value,StringComparer.Ordinal);
            var visible=eligible.Values.OrderBy(m=>m.Health.ModulePath,StringComparer.Ordinal).ToArray();
            var moduleCounts=requirements.Values.GroupBy(r=>r.ModuleIdentity).ToDictionary(g=>g.Key,g=>new { Count=g.LongCount(),Deleted=g.LongCount(r=>r.IsDeleted) });
            foreach(var module in visible) { moduleCounts.TryGetValue(module.Health.ModuleIdentity,out var count); module.Health.Requirements=count?.Count??0; module.Health.DeletedRequirements=count?.Deleted??0; module.Health.Complete=module.InventoryComplete; }
            bool inventoryComplete=data.DiscoveryComplete && data.UnresolvedModulePaths.Count==0 && visible.All(m=>m.InventoryComplete && m.Health.Access=="Readable") && unknownFilters==0;
            var selectedVersions=new HashSet<string>(visible.Where(m=>m.Health.Version!=null).Select(m=>m.Health.Version.ModuleBaselineIdentity),StringComparer.Ordinal);
            var allowedIds=new HashSet<string>(requirements.Keys,StringComparer.Ordinal);
            var snapshot=previous==null ? new DashboardSnapshot() : DashboardSerialization.Copy(previous);
            snapshot.SnapshotId=Guid.NewGuid(); snapshot.Request=request; snapshot.RequestKey=requestKey; snapshot.CollectedAtUtc=clock.UtcNow; snapshot.ServiceReadCalls=data.ServiceReadCalls;
            snapshot.NavigationModules=visible.Where(m=>m.Health.Version!=null).Select(m=>m.Health.Version).Concat(compatible.SelectMany(a=>a.Requirements).Select(r=>r.Version).Where(v=>v!=null&&v.IdentityVerified)).GroupBy(v=>v.ModuleBaselineIdentity).Select(g=>g.First()).Take(10000).ToList();
            snapshot.ObservedModules=visible.Where(m=>m.Health.Version!=null).Select(m=>m.Health.Version).ToList();
            var selectedModuleIds=new HashSet<string>(visible.Select(m=>m.Health.ModuleIdentity));
            snapshot.Trends=compatible.Where(a=>a.Modules.Any(v=>selectedVersions.Contains(v.ModuleBaselineIdentity))).OrderByDescending(a=>a.TimestampUtc).Take(256).Select(a=>new DashboardTrendPoint { TimestampUtc=a.TimestampUtc,ComparableScopeKey=DashboardSerialization.Hash(new[]{a.ComparableScopeKey,requestKey}),MetricId=a.Kind==DashboardAnalysisKind.Impact?"ChangedRequirements":a.Kind==DashboardAnalysisKind.Quality?"QualityFindings":"TraceabilityFindings",Value=TrendValue(a,request,selectedModuleIds,allowedIds),SourceAnalysisIdentity=a.AnalysisId }).ToList();
            var warnings=data.Warnings.ToList();
            if(data.UnresolvedModulePaths.Count>0) warnings.Add(data.UnresolvedModulePaths.Count+" module paths have no observed stable identity; they are uncounted and totals are incomplete.");
            if(unknownFilters>0) warnings.Add("Requirement classification filters could not be evaluated for "+unknownFilters+" observed objects; these are not silently counted as excluded.");
            if(data.Analyses.Count!=compatible.Length) warnings.Add("Some saved analyses have a different environment, policy/profile, snapshot schema or date scope; they were not merged.");
            foreach(DashboardSections section in new[]{DashboardSections.Overview,DashboardSections.Baseline,DashboardSections.Traceability,DashboardSections.Quality,DashboardSections.Impact,DashboardSections.Audit})
            {
                if((sections&section)==0) continue; token.ThrowIfCancellationRequested();
                var s=new DashboardSectionSnapshot { ScopeKey=requestKey,Section=section,CollectedAtUtc=data.CollectedAtUtc,Quality=new DashboardDataQuality { Completeness=inventoryComplete ? DashboardCompleteness.Complete : DashboardCompleteness.Partial, Warnings=warnings.ToList(),AccessDeniedModules=visible.Count(m=>m.Health.Access=="AccessDenied"),UnknownModules=data.UnresolvedModulePaths.Distinct().Count()+visible.Count(m=>m.Health.Access!="Readable" && m.Health.Access!="AccessDenied") } };
                if(section==DashboardSections.Overview) Overview(s,request,data,visible,requirements.Values.ToArray(),inventoryComplete);
                if(section==DashboardSections.Baseline) Baseline(s,request,data,visible,compatible);
                if(section==DashboardSections.Traceability) Traceability(s,request,compatible,selectedVersions,allowedIds,requirements,inventoryComplete,unknownFilters,token);
                if(section==DashboardSections.Quality) Quality(s,request,compatible,selectedVersions,allowedIds,requirements,inventoryComplete);
                if(section==DashboardSections.Impact) Impact(s,request,compatible,selectedVersions,allowedIds,requirements,inventoryComplete);
                if(section==DashboardSections.Audit) Audit(s,request,data,eligible,compatible);
                if(s.Findings.Count>request.MaximumFindings) { s.Findings=s.Findings.Take(request.MaximumFindings).ToList(); s.Quality.Completeness=DashboardCompleteness.Partial; s.Quality.Warnings.Add("Finding display/report detail limit reached; full counts are retained, details truncated."); }
                snapshot.Sections.RemoveAll(oldSection=>oldSection.Section==section); snapshot.Sections.Add(s);
            }
            snapshot.Sections=snapshot.Sections.OrderBy(s=>s.Section).ToList(); calculator.Finalize(snapshot,clock.UtcNow);
            snapshot.AggregationMilliseconds=watch.Elapsed.TotalMilliseconds;
            return snapshot;
        }
        private bool Compatible(DashboardAnalysisRecord a,DashboardRequest r)
        {
            if(a.ClientVersion!=r.ClientVersion || a.TimestampUtc.Kind!=DateTimeKind.Utc || a.TimestampUtc>clock.UtcNow || a.Environment!=r.Environment || a.DatabaseIdentity!=r.DatabaseIdentity || a.IsMock!=r.IsMock || a.AnalysisVersion!=r.AnalysisSnapshotVersion || !DateFilter(a.TimestampUtc,r.Filters)) return false;
            if(a.Kind==DashboardAnalysisKind.Quality) return a.RequestedProfileKey==DashboardAnalysisRecord.ProfileKey(r) && a.Standard==r.QualityStandard;
            if(a.Kind==DashboardAnalysisKind.BaselineVerification&&r.Scope.Kind==DashboardScopeKind.SelectedBaselineSet&&a.SetIdentity!=r.Scope.SetIdentity) return false;
            return a.RequestedPolicyKey==DashboardAnalysisRecord.PolicyKey(r);
        }
        private static bool ModuleFilter(DashboardModuleFact m,DashboardFilters f) => (f.Project.Length==0 || m.Health.Project==f.Project) && (f.Folder.Length==0 || DashboardScope.Under(f.Folder,m.Health.ModulePath)) && (!f.BaselineStatus.HasValue || m.Health.BaselineHealth==f.BaselineStatus.Value);
        private static bool? RequirementFilter(DashboardRequirementFact r,DashboardFilters f)
        {
            bool unknown=false;
            foreach(var pair in new[]{new[]{f.RequirementType,r.Type},new[]{f.RequirementStatus,r.Status},new[]{f.Criticality,r.Criticality}})
            { if(pair[0].Length==0) continue; if(pair[1]==null) unknown=true; else if(pair[0]!=pair[1]) return false; }
            return unknown ? (bool?)null : true;
        }
        public static bool DateFilter(DateTime date,DashboardFilters f) => (!f.AnalysisFromUtc.HasValue || date>=f.AnalysisFromUtc) && (!f.AnalysisToUtc.HasValue || date<=f.AnalysisToUtc);
        private static bool FindingFilter(DashboardFinding f,DashboardFilters filter) => (!filter.FindingSeverity.HasValue || f.Severity==filter.FindingSeverity) && (filter.ReviewStatus.Length==0 || f.ReviewStatus==filter.ReviewStatus) && DateFilter(f.TimestampUtc,filter);
        private static bool HasRequirementFilter(DashboardFilters f) => f.RequirementType.Length>0 || f.RequirementStatus.Length>0 || f.Criticality.Length>0 || f.FindingSeverity.HasValue || f.ReviewStatus.Length>0;
        private static decimal? TrendValue(DashboardAnalysisRecord a,DashboardRequest request,HashSet<string> modules,HashSet<string> objects)
        {
            if(a.Completeness!=DashboardCompleteness.Complete) return null;
            if(a.Kind==DashboardAnalysisKind.Impact) { var persistent=new HashSet<string>(a.Requirements.Where(q=>objects.Contains(q.VersionIdentity)).Select(q=>q.Identity)); return a.Changes.Where(c=>c.PrimaryType!=RequirementChangeType.Unchanged&&modules.Contains(c.ModuleIdentity)&&(!HasRequirementFilter(request.Filters)||objects.Contains(c.RequirementVersionIdentity)||persistent.Contains(c.RequirementIdentity))).Select(c=>c.RequirementIdentity).Distinct().Count(); }
            return a.Findings.Where(f=>FindingFilter(f,request.Filters)&&(f.RequirementVersionIdentity!=null?objects.Contains(f.RequirementVersionIdentity):f.ModuleIdentity!=null?modules.Contains(f.ModuleIdentity):!HasRequirementFilter(request.Filters))).Select(f=>f.Id).Distinct().Count();
        }
        private static void Add(DashboardSectionSnapshot s,DashboardRequest r,string id,string name,decimal? value,DashboardDestination destination,DashboardCompleteness? completeness=null,string warning=null,string unit="count")
        { var c=completeness??s.Quality.Completeness; s.Metrics.Add(new DashboardMetric { Id=id,Name=name,Value=value,Scope=s.ScopeKey??r.Key,Source=s.Section.ToString(),TimestampUtc=s.CollectedAtUtc,Completeness=c,Warning=warning,IsLowerBound=value.HasValue&&c!=DashboardCompleteness.Complete,Unit=unit,Destination=destination }); }
        private static void Overview(DashboardSectionSnapshot s,DashboardRequest r,DashboardEvidence data,DashboardModuleFact[] modules,DashboardRequirementFact[] req,bool complete)
        {
            var selectedModules=new HashSet<string>(modules.Select(m=>m.Health.ModuleIdentity));
            var projects=modules.Select(m=>m.Health.Project).Where(p=>!string.IsNullOrWhiteSpace(p)&&p!="Unknown").Distinct().Count();
            Add(s,r,"TotalProjects","Projects",projects,DashboardDestination.ModuleExplorer);
            Add(s,r,"TotalFolders","Folders",data.FolderPaths.Where(p=>(r.Filters.Project.Length==0||DashboardScope.Under(r.Filters.Project,p))&&(r.Filters.Folder.Length==0||DashboardScope.Under(r.Filters.Folder,p))&&(r.Scope.Kind!=DashboardScopeKind.SelectedFolders || r.Scope.Folders.Any(f=>f.IncludeSubfolders?DashboardScope.Under(f.FullPath,p):p==f.FullPath))&&(!HasRequirementFilter(r.Filters)||modules.Any(m=>DashboardScope.Under(p,m.Health.ModulePath)))).Distinct().Count(),DashboardDestination.ModuleExplorer);
            Add(s,r,"FormalModules","Formal modules",selectedModules.Count,DashboardDestination.ModuleExplorer);
            Add(s,r,"Requirements","Requirements",req.Select(q=>q.Identity).Distinct().Count(),DashboardDestination.ModuleExplorer);
            Add(s,r,"ActiveRequirements","Active requirements",req.Where(q=>!q.IsDeleted).Select(q=>q.Identity).Distinct().Count(),DashboardDestination.ModuleExplorer);
            Add(s,r,"DeletedRequirements","Accessible deleted requirements",req.Where(q=>q.IsDeleted).Select(q=>q.Identity).Distinct().Count(),DashboardDestination.ModuleExplorer);
            bool attrs=modules.All(m=>m.AttributeCatalogComplete) && complete;
            var names=modules.SelectMany(m=>m.AttributeCatalog.Select(a=>m.Health.ModuleIdentity+"\n"+a)).Distinct().Count();
            Add(s,r,"CustomAttributes","Custom attribute definitions",attrs?(decimal?)names:null,DashboardDestination.ModuleExplorer,attrs?DashboardCompleteness.Complete:DashboardCompleteness.Unknown,"Full module attribute catalog required; requested/readable attribute names alone do not prove the total.");
            if(!complete) s.Quality.Warnings.Add("Totals are observations/lower bounds within readable scope; inaccessible or hidden modules are not inferred.");
        }
        private static void ApplyBaselineEvidence(DashboardModuleFact module,DashboardAnalysisRecord[] analyses,DashboardRequest r,DateTime now)
        {
            module.Health.BaselineHealth=module.NeverBaselinedProven && module.HistoryComplete ? DashboardBaselineState.NeverBaselined : DashboardBaselineState.Unknown;
            var comparisons=analyses.Where(a=>a.TimestampUtc<=now && now-a.TimestampUtc<=TimeSpan.FromSeconds(r.MaximumAnalysisAgeSeconds) && (a.Kind==DashboardAnalysisKind.Impact || a.Kind==DashboardAnalysisKind.BaselineComparison) && a.ConclusiveComparison && a.ComparedModuleIdentities.Contains(module.Health.ModuleIdentity) && a.Completeness==DashboardCompleteness.Complete && a.Modules.Any(v=>v.ModuleIdentity==module.Health.ModuleIdentity&&v.IsCurrent) && a.SourceVersions.Any(v=>v.ModuleBaselineIdentity==module.LatestVersion?.ModuleBaselineIdentity)).OrderByDescending(a=>a.TimestampUtc).ToArray();
            var comparison=comparisons.FirstOrDefault();
            if(comparison!=null) { var changes=comparison.Changes.Where(c=>c.ModuleIdentity==module.Health.ModuleIdentity).ToArray(); if(changes.All(c=>(c.Flags&RequirementChangeFlags.Unknown)==0)) { module.Health.BaselineHealth=changes.Any(c=>c.PrimaryType!=RequirementChangeType.Unchanged)?DashboardBaselineState.ChangedSinceBaseline:DashboardBaselineState.UpToDate; module.Health.Evidence="Conclusive latest-baseline → current comparison: "+comparison.AnalysisId; } }
            if(analyses.Any(a=>a.TimestampUtc<=now && now-a.TimestampUtc<=TimeSpan.FromSeconds(r.MaximumAnalysisAgeSeconds) && a.Kind==DashboardAnalysisKind.BaselineVerification && a.OpenSet && a.Modules.Any(v=>v.ModuleIdentity==module.Health.ModuleIdentity))) module.Health.BaselineHealth=DashboardBaselineState.OpenBaselineSet;
            if(analyses.Any(a=>a.TimestampUtc<=now && now-a.TimestampUtc<=TimeSpan.FromSeconds(r.MaximumAnalysisAgeSeconds) && a.Kind==DashboardAnalysisKind.BaselineVerification && a.MissingModuleIdentities.Contains(module.Health.ModuleIdentity))) module.Health.BaselineHealth=DashboardBaselineState.MissingExpectedBaseline;
            if(module.Health.BaselineHealth==DashboardBaselineState.Unknown && module.LatestVersion!=null) { module.Health.BaselineHealth=DashboardBaselineState.VerificationRequired; module.Health.Evidence="Metadata is not a content diff; modification timestamps are not change proof."; }
        }
        private static void Baseline(DashboardSectionSnapshot s,DashboardRequest r,DashboardEvidence data,DashboardModuleFact[] modules,DashboardAnalysisRecord[] records)
        {
            foreach(var module in modules) if(data.ObservedSets.Any(set=>set.IsOpen&&set.ModuleIdentities.Contains(module.Health.ModuleIdentity))) module.Health.BaselineHealth=DashboardBaselineState.OpenBaselineSet;
            s.Baseline=new DashboardBaselineHealth { Modules=modules.Select(m=>m.Health).ToList() };
            if(modules.Any(m=>!m.HistoryComplete || m.Health.BaselineHealth==DashboardBaselineState.Unknown || m.Health.BaselineHealth==DashboardBaselineState.VerificationRequired)) s.Quality.Completeness=DashboardCompleteness.Partial;
            Add(s,r,"Baselines","Existing baselines",modules.Sum(m=>m.BaselineCount),DashboardDestination.BaselineCompare,modules.All(m=>m.HistoryComplete)?(DashboardCompleteness?)null:DashboardCompleteness.Partial);
            foreach(DashboardBaselineState state in Enum.GetValues(typeof(DashboardBaselineState))) { long count=modules.Count(m=>m.Health.BaselineHealth==state); Add(s,r,"Baseline."+state,state.ToString(),count,DashboardDestination.BaselineCompare); s.Charts.Add(new DashboardChartPoint { Group="Baseline status",Label=state.ToString(),Value=count,Percentage=modules.Length==0?(decimal?)null:100m*count/modules.Length,Quality=s.Quality.Completeness.ToString() }); }
            Add(s,r,"BaselineSets","Baseline sets",data.BaselineSetCount,DashboardDestination.BulkBaseline,data.SetInventoryComplete?DashboardCompleteness.Complete:data.BaselineSetCount.HasValue?DashboardCompleteness.Partial:DashboardCompleteness.Unknown);
            var selected=new HashSet<string>(modules.Select(m=>m.Health.ModuleIdentity));
            var verification=records.Where(a=>a.Kind==DashboardAnalysisKind.BaselineVerification&&a.Modules.Any(v=>selected.Contains(v.ModuleIdentity))).GroupBy(a=>a.SetIdentity??a.ComparableScopeKey).Select(g=>g.OrderByDescending(a=>a.TimestampUtc).First()).ToArray();
            var observedSets=data.ObservedSets.GroupBy(set=>set.DefinitionIdentity+"/"+set.Identity).Select(g=>g.Last()).ToArray();
            var openIds=new HashSet<string>(observedSets.Where(set=>set.IsOpen).Select(set=>set.DefinitionIdentity+"/"+set.Identity));
            foreach(var record in verification.Where(a=>a.OpenSet&&!observedSets.Any(set=>set.Identity==a.SetIdentity&&set.DefinitionIdentity==a.DefinitionIdentity))) openIds.Add(record.DefinitionIdentity+"/"+record.SetIdentity);
            Add(s,r,"OpenBaselineSets","Open baseline sets",verification.Length==0&&observedSets.Length==0&&!data.SetInventoryComplete?(decimal?)null:openIds.Count,DashboardDestination.BulkBaseline,data.SetInventoryComplete?DashboardCompleteness.Complete:DashboardCompleteness.Partial);
            Add(s,r,"MissingSetMembers","Verified missing baseline members",verification.Length==0?(decimal?)null:verification.Sum(a=>a.MissingSetMembers),DashboardDestination.TraceabilityExplorer,verification.Length==0?DashboardCompleteness.Unknown:DashboardCompleteness.Partial,"Unseen or access-denied membership is not counted as Missing.");
            Add(s,r,"FailedBaselineJobs","Failed baseline jobs",data.Activity.Count(a=>a.JobType.Contains("Baseline")&&a.ModuleIdentities.Any(selected.Contains)&&Failed(a.Status)),DashboardDestination.JobHistory,DashboardCompleteness.Partial);
            Add(s,r,"BaselineVerificationFindings","Baseline verification findings",verification.Length==0?(decimal?)null:verification.Sum(a=>a.Findings.Count),DashboardDestination.TraceabilityExplorer,verification.Length==0?DashboardCompleteness.Unknown:(DashboardCompleteness?)null);
        }
        private static DashboardAnalysisRecord[] SelectAnalyses(DashboardSectionSnapshot section,DashboardRequest request,DashboardAnalysisRecord[] records,DashboardAnalysisKind kind,HashSet<string> versions)
        {
            var candidates=records.Where(a=>a.Kind==kind && a.Modules.Any(v=>versions.Contains(v.ModuleBaselineIdentity))).ToArray();
            var groups=candidates.GroupBy(a=>a.ComparableScopeKey).Select(g=>g.OrderByDescending(a=>a.TimestampUtc).First()).OrderByDescending(a=>a.TimestampUtc).ToArray();
            // Separate partitioned scopes may coexist, but overlapping modules with different comparison/profile contexts cannot be added.
            var chosen=new List<DashboardAnalysisRecord>(); var used=new HashSet<string>();
            foreach(var group in groups) { var keys=group.Modules.Where(v=>versions.Contains(v.ModuleBaselineIdentity)).Select(v=>v.ModuleBaselineIdentity).ToArray(); bool mismatch=chosen.Count>0&&(kind==DashboardAnalysisKind.Impact || kind==DashboardAnalysisKind.Quality&&chosen[0].ProfileFingerprint!=group.ProfileFingerprint || kind!=DashboardAnalysisKind.Quality&&chosen[0].PolicyFingerprint!=group.PolicyFingerprint); if(mismatch||keys.Any(used.Contains)) { section.Quality.Warnings.Add("Different or overlapping analysis context kept separate: "+group.AnalysisId); continue; } foreach(string key in keys) used.Add(key); chosen.Add(group); }
            if(chosen.Count==0) { section.Quality.Completeness=DashboardCompleteness.Unavailable; section.Quality.Warnings.Add("No compatible saved "+kind+" analysis. Refresh does not run an analysis; use Run New Analysis."); }
            else { section.CollectedAtUtc=chosen.Min(a=>a.TimestampUtc); section.AnalysisIds=chosen.Select(a=>a.AnalysisId).ToList(); if(chosen.Any(a=>a.Completeness!=DashboardCompleteness.Complete) || versions.Any(v=>!used.Contains(v))) section.Quality.Completeness=DashboardCompleteness.Partial; section.Quality.Warnings.AddRange(chosen.SelectMany(a=>a.Warnings)); }
            return chosen.ToArray();
        }
        private static void Traceability(DashboardSectionSnapshot s,DashboardRequest r,DashboardAnalysisRecord[] records,HashSet<string> versions,HashSet<string> ids,Dictionary<string,DashboardRequirementFact> requirements,bool complete,int unknownFilters,CancellationToken token)
        {
            var a=SelectAnalyses(s,r,records,r.Scope.Kind==DashboardScopeKind.SelectedBaselineSet?DashboardAnalysisKind.BaselineVerification:DashboardAnalysisKind.Traceability,versions);
            var facts=a.SelectMany(x=>x.Coverage).Where(c=>ids.Contains(c.RequirementVersionIdentity)).GroupBy(c=>c.RequirementVersionIdentity).Select(g=>g.First()).ToArray();
            var coverage=new DashboardTraceabilityHealth { PolicyVersion=r.TraceabilityPolicyVersion,Applicable=facts.LongCount(c=>c.Applicable || !c.EligibilityKnown),VerifiedLinked=facts.LongCount(c=>c.EligibilityKnown&&c.Applicable&&c.State==TraceabilityState.Satisfied),Missing=facts.LongCount(c=>c.EligibilityKnown&&c.Applicable&&c.State==TraceabilityState.Violation),Unknown=facts.LongCount(c=>!c.EligibilityKnown||c.Applicable&&c.State==TraceabilityState.Unknown)+unknownFilters,Excluded=facts.LongCount(c=>c.EligibilityKnown&&!c.Applicable),AccessDenied=facts.LongCount(c=>c.AccessDenied) };
            coverage.Unknown+=Math.Max(0,ids.Count-facts.Length);
            coverage.DenominatorComplete=complete && a.Length>0 && s.Quality.Completeness==DashboardCompleteness.Complete && facts.Length==ids.Count && coverage.Unknown==0;
            coverage.CoveragePercentage=coverage.DenominatorComplete&&coverage.Applicable>0 ? 100m*coverage.VerifiedLinked/coverage.Applicable : (decimal?)null; s.Traceability=coverage;
            if(!coverage.DenominatorComplete && a.Length>0) s.Quality.Completeness=DashboardCompleteness.Partial;
            var links=new Dictionary<string,DashboardLinkFact>();
            foreach(var link in a.SelectMany(x=>x.Links)) { token.ThrowIfCancellationRequested(); if(ids.Contains(link.SourceVersionIdentity)||ids.Contains(link.TargetVersionIdentity)) links[link.Identity]=link; }
            var q=a.Length==0?DashboardCompleteness.Unavailable:s.Quality.Completeness;
            Add(s,r,"ApplicableRequirements","Applicable requirements",a.Length==0?(decimal?)null:coverage.Applicable,DashboardDestination.TraceabilityExplorer,q);
            Add(s,r,"ValidExpectedLinks","Requirements with valid expected links",a.Length==0?(decimal?)null:coverage.VerifiedLinked,DashboardDestination.TraceabilityExplorer,q);
            Add(s,r,"MissingLinks","Requirements missing expected links",a.Length==0?(decimal?)null:coverage.Missing,DashboardDestination.TraceabilityExplorer,q);
            Add(s,r,"UnknownTraceability","Unknown / unresolved requirements",a.Length==0?(decimal?)null:coverage.Unknown,DashboardDestination.TraceabilityExplorer,q);
            Add(s,r,"ExcludedRequirements","Policy excluded requirements",a.Length==0?(decimal?)null:coverage.Excluded,DashboardDestination.TraceabilityExplorer,q);
            Add(s,r,"Coverage","Traceability coverage",coverage.CoveragePercentage,DashboardDestination.TraceabilityExplorer,q,"Percentage unavailable when eligibility, access or denominator is unproven.","percent");
            Add(s,r,"TotalLinks","Unique observed link relationships",a.Length==0?(decimal?)null:links.Count,DashboardDestination.TraceabilityExplorer,q,"Endpoint/version/link-module identities are deduplicated; physical multiplicity is not inferred.");
            foreach(string resolution in new[]{"Unresolved","AccessDenied","UnexpectedVersionTarget"}) Add(s,r,resolution+"Links",resolution+" links",a.Length==0?(decimal?)null:links.Values.Count(l=>l.Resolution==resolution),DashboardDestination.TraceabilityExplorer,q);
            Add(s,r,"VerifiedBaselineLinks","Verified baseline relationships",a.Length==0||a.Any(x=>!x.BaselineLinkVerificationAvailable)?(decimal?)null:links.Values.Count(l=>l.VerifiedBaseline),DashboardDestination.TraceabilityExplorer,q,"Requires typed baseline endpoint verification; a generic graph is insufficient.");
            Add(s,r,"BrokenChains","Broken traceability chains",a.Length==0||a.Any(x=>!x.BrokenChainCount.HasValue)||HasRequirementFilter(r.Filters)?(decimal?)null:a.Sum(x=>x.BrokenChainCount.Value),DashboardDestination.TraceabilityExplorer,q,"Requires chain analysis for the exact policy scope.");
            s.Findings=a.SelectMany(x=>x.Findings).Where(f=>(f.RequirementVersionIdentity==null||ids.Contains(f.RequirementVersionIdentity))&&FindingFilter(f,r.Filters)).GroupBy(f=>f.Id).Select(g=>g.First()).ToList();
            Add(s,r,"TraceabilityOpenFindings","Open traceability findings",a.Length==0?(decimal?)null:s.Findings.Count(f=>Open(f.ReviewStatus)),DashboardDestination.TraceabilityExplorer,q);
            var byId=facts.ToDictionary(c=>c.RequirementVersionIdentity);
            foreach(var grouping in new[]{"Project","Module","Requirement type","Baseline"}) foreach(var group in requirements.Values.GroupBy(req=>grouping=="Project"?req.Project:grouping=="Module"?req.Version.FullPath:grouping=="Baseline"?req.Version.ModuleBaselineIdentity:req.Type??"Unknown"))
            { var rows=group.Where(req=>byId.ContainsKey(req.VersionIdentity)).Select(req=>byId[req.VersionIdentity]).ToArray(); long applicable=rows.Count(c=>c.Applicable||!c.EligibilityKnown),valid=rows.Count(c=>c.Applicable&&c.State==TraceabilityState.Satisfied); bool known=rows.Length==group.Count()&&rows.All(c=>c.EligibilityKnown&&(!c.Applicable||c.State!=TraceabilityState.Unknown))&&complete&&s.Quality.Completeness==DashboardCompleteness.Complete; s.Charts.Add(new DashboardChartPoint { Group="Coverage by "+grouping,Label=group.Key,Value=valid,Percentage=known&&applicable>0?100m*valid/applicable:(decimal?)null,Quality=known?"Complete":"Incomplete" }); }
        }
        private static void Quality(DashboardSectionSnapshot s,DashboardRequest r,DashboardAnalysisRecord[] records,HashSet<string> versions,HashSet<string> ids,Dictionary<string,DashboardRequirementFact> req,bool complete)
        {
            var a=SelectAnalyses(s,r,records,DashboardAnalysisKind.Quality,versions);
            var findings=a.SelectMany(x=>x.Findings).Where(f=>ids.Contains(f.RequirementVersionIdentity)&&FindingFilter(f,r.Filters)).GroupBy(f=>f.Id).Select(g=>g.First()).ToList();
            var evaluated=new HashSet<string>(a.SelectMany(x=>x.Evaluations).Where(e=>e.FullyEvaluated&&ids.Contains(e.RequirementVersionIdentity)).Select(e=>e.RequirementVersionIdentity));
            s.RequirementQuality=new DashboardQualitySummary { EvaluatedRequirements=evaluated.Count,NotEvaluatedRequirements=Math.Max(0,ids.Count-evaluated.Count),Findings=findings.Count,UniqueAffectedRequirements=findings.Select(f=>f.RequirementIdentity).Distinct().Count(),ProfileVersion=r.QualityProfileVersion,ProfileApproved=a.Length>0&&a.All(x=>x.ProfileApproved) };
            if(a.Length>0 && (!complete || evaluated.Count!=ids.Count || !s.RequirementQuality.ProfileApproved)) s.Quality.Completeness=DashboardCompleteness.Partial;
            var q=a.Length==0?DashboardCompleteness.Unavailable:s.Quality.Completeness;
            Add(s,r,"EvaluatedRequirements","Fully evaluated requirements",a.Length==0?(decimal?)null:evaluated.Count,DashboardDestination.RequirementQuality,q);
            Add(s,r,"NotEvaluatedRequirements","Not evaluated requirements",s.RequirementQuality.NotEvaluatedRequirements,DashboardDestination.RequirementQuality,q);
            Add(s,r,"QualityFindings","Quality findings",a.Length==0?(decimal?)null:findings.Count,DashboardDestination.RequirementQuality,q);
            Add(s,r,"UniqueQualityAffected","Unique affected requirements",a.Length==0?(decimal?)null:s.RequirementQuality.UniqueAffectedRequirements,DashboardDestination.RequirementQuality,q);
            foreach(DashboardSeverity severity in Enum.GetValues(typeof(DashboardSeverity))) { int count=findings.Count(f=>f.Severity==severity); Add(s,r,"Quality."+severity,severity+" quality findings",a.Length==0?(decimal?)null:count,DashboardDestination.RequirementQuality,q); s.Charts.Add(new DashboardChartPoint { Group="Quality severity",Label=severity.ToString(),Value=count,Percentage=findings.Count==0?(decimal?)null:100m*count/findings.Count,Quality=q.ToString() }); }
            foreach(string state in new[]{"Open","Reviewed","Accepted"}) { int count=findings.Count(f=>state=="Open"?Open(f.ReviewStatus):state=="Accepted"?new[]{"Accepted","Waived","NotApplicable","AcceptedRisk"}.Contains(f.ReviewStatus):!Open(f.ReviewStatus)); Add(s,r,"Quality"+state,state+" quality findings",a.Length==0?(decimal?)null:count,DashboardDestination.RequirementQuality,q); }
            foreach(var group in findings.GroupBy(f=>f.Rule??"Unknown")) s.Charts.Add(new DashboardChartPoint { Group="Quality by rule",Label=group.Key,Value=group.Count(),Quality=q.ToString() });
            foreach(var group in findings.GroupBy(f=>f.ModuleIdentity??"Unknown")) s.Charts.Add(new DashboardChartPoint { Group="Quality by module",Label=group.Key,Value=group.Count(),Quality=q.ToString() });
            s.Findings=findings; s.Quality.Warnings.Add("Automated rule findings and reviewer dispositions are separate observations; no certification or tool qualification decision.");
        }
        private static void Impact(DashboardSectionSnapshot s,DashboardRequest r,DashboardAnalysisRecord[] records,HashSet<string> versions,HashSet<string> ids,Dictionary<string,DashboardRequirementFact> req,bool complete)
        {
            var a=SelectAnalyses(s,r,records,DashboardAnalysisKind.Impact,versions);
            var persistent=new HashSet<string>(req.Values.Select(v=>v.Identity));
            var historical=new HashSet<string>(a.SelectMany(x=>x.Requirements).Where(v=>r.Scope.Contains(v.Version.FullPath,v.ModuleIdentity)&&RequirementFilter(v,r.Filters)==true).Select(v=>v.VersionIdentity));
            var findings=a.SelectMany(x=>x.Findings).Where(f=>(ids.Contains(f.RequirementVersionIdentity)||persistent.Contains(f.RequirementIdentity)||historical.Contains(f.RequirementVersionIdentity))&&FindingFilter(f,r.Filters)).GroupBy(f=>f.Id).Select(g=>g.First()).ToList();
            var changes=a.SelectMany(x=>x.Changes).Where(c=>ids.Contains(c.RequirementVersionIdentity)||persistent.Contains(c.RequirementIdentity)||historical.Contains(c.RequirementVersionIdentity)).GroupBy(c=>c.RequirementIdentity).Select(g=>g.First()).ToArray();
            var q=a.Length==0?DashboardCompleteness.Unavailable:s.Quality.Completeness;
            s.Impact=new DashboardImpactSummary { ImpactCount=findings.Count,UniqueImpactedRequirements=findings.Where(f=>f.RequirementIdentity!=null).Select(f=>f.RequirementIdentity).Distinct().Count(),PathCount=a.Sum(x=>x.ImpactPathCount),PendingReviews=findings.Count(f=>Open(f.ReviewStatus)) };
            Add(s,r,"ChangedRequirements","Changed requirements",a.Length==0?(decimal?)null:changes.Count(c=>c.PrimaryType!=RequirementChangeType.Unchanged),DashboardDestination.ImpactAnalysis,q);
            foreach(var type in new[]{RequirementChangeType.Added,RequirementChangeType.Deleted,RequirementChangeType.Modified}) Add(s,r,"Changes."+type,type+" requirements",a.Length==0?(decimal?)null:changes.Count(c=>c.PrimaryType==type || type==RequirementChangeType.Modified && (c.Flags&(RequirementChangeFlags.Content|RequirementChangeFlags.Attribute|RequirementChangeFlags.Moved))!=0),DashboardDestination.ImpactAnalysis,q);
            foreach(string kind in new[]{"DirectImpactCandidate","IndirectImpactCandidate","UnresolvedImpact"}) Add(s,r,kind,kind,a.Length==0?(decimal?)null:findings.Where(f=>f.ImpactKind==kind).Select(f=>f.RequirementIdentity??f.Id).Distinct().Count(),DashboardDestination.ImpactAnalysis,q);
            Add(s,r,"ImpactCount","Impact findings",a.Length==0?(decimal?)null:findings.Count,DashboardDestination.ImpactAnalysis,q);
            Add(s,r,"UniqueImpacted","Unique impacted requirements",a.Length==0?(decimal?)null:s.Impact.UniqueImpactedRequirements,DashboardDestination.ImpactAnalysis,q);
            Add(s,r,"ImpactPaths","Recorded impact paths",a.Length==0?(decimal?)null:s.Impact.PathCount,DashboardDestination.ImpactAnalysis,q);
            Add(s,r,"PotentialTests","Potentially affected tests",a.Length==0?(decimal?)null:findings.Where(f=>f.ImpactKind=="TestImpactCandidate").Select(f=>f.RequirementIdentity).Distinct().Count(),DashboardDestination.ImpactAnalysis,q);
            Add(s,r,"PendingReviews","Pending impact reviews",a.Length==0?(decimal?)null:s.Impact.PendingReviews,DashboardDestination.ImpactAnalysis,q);
            Add(s,r,"ConfirmedImpacted","Confirmed impacted findings",a.Length==0?(decimal?)null:findings.Count(f=>f.ReviewStatus=="ConfirmedImpacted"),DashboardDestination.ImpactAnalysis,q);
            foreach(DashboardSeverity severity in Enum.GetValues(typeof(DashboardSeverity))) s.Charts.Add(new DashboardChartPoint { Group="Impact severity",Label=severity.ToString(),Value=findings.Count(f=>f.Severity==severity),Quality=q.ToString() });
            s.Findings=findings; s.Quality.Warnings.Add("Impact candidates are not mandatory changes or test rerun decisions.");
        }
        public static bool Open(string status) => new[]{"NotReviewed","Pending","InReview","NeedsInvestigation","NeedsClarification"}.Contains(status??"NotReviewed");
        public static bool Failed(string status) => status!=null && new[]{"Failed","Cancelled","Incomplete","Unknown","NeedsReconciliation","Partial","Blocked"}.Any(v=>status.IndexOf(v,StringComparison.OrdinalIgnoreCase)>=0);
        private static void Audit(DashboardSectionSnapshot s,DashboardRequest r,DashboardEvidence data,Dictionary<string,DashboardModuleFact> modules,DashboardAnalysisRecord[] records)
        {
            var activity=data.Activity.Concat(records.Select(a=>new DashboardAuditEntry { JobId=a.AnalysisId,SourceAnalysisIdentity=a.AnalysisId,JobType=a.Kind+"Analysis",Operator=a.Operator,StartTimeUtc=a.TimestampUtc,EndTimeUtc=a.TimestampUtc,Status=a.Status??a.Completeness.ToString(),AffectedModuleCount=a.Modules.Count,FindingCount=a.Findings.Count,ErrorSummary=string.Join("; ",a.Warnings.Take(3)),ModuleIdentities=a.Modules.Select(v=>v.ModuleIdentity).ToList() }));
            var rows=activity.Where(a=>a.ModuleIdentities.Any(modules.ContainsKey)&&DateFilter(a.StartTimeUtc,r.Filters)&&(!r.Filters.FailedJobsOnly||Failed(a.Status))).GroupBy(a=>a.JobType+"/"+a.JobId).Select(g=>g.OrderByDescending(a=>a.EndTimeUtc).First()).OrderByDescending(a=>a.StartTimeUtc).Take(1000).ToList();
            s.Audit=new DashboardAuditSummary { Recent=rows }; s.Quality.Completeness=DashboardCompleteness.Partial; s.Quality.Warnings.Add("Available local job/audit sources only; not a replacement for DOORS native history.");
            Add(s,r,"RecentJobs","Recent matching jobs",rows.Count,DashboardDestination.JobHistory);
            Add(s,r,"FailedJobs","Failed / cancelled / incomplete jobs",rows.Count(a=>Failed(a.Status)),DashboardDestination.JobHistory);
        }
    }
    public sealed class DashboardMetricsCalculator
    {
        public void Finalize(DashboardSnapshot snapshot,DateTime now)
        {
            var warnings=new List<string>();
            foreach(var section in snapshot.Sections)
            {
                foreach(var group in section.Charts.Where(c=>!c.Group.StartsWith("Coverage",StringComparison.Ordinal)).GroupBy(c=>c.Group)) { decimal sum=group.Sum(c=>c.Value); if(sum>0) foreach(var point in group.Where(c=>!c.Percentage.HasValue)) point.Percentage=100m*point.Value/sum; }
                section.Quality.IsStale=now-section.CollectedAtUtc>TimeSpan.FromSeconds(section.Section==DashboardSections.Overview||section.Section==DashboardSections.Baseline?snapshot.Request.CacheTtlSeconds:snapshot.Request.MaximumAnalysisAgeSeconds);
                foreach(var metric in section.Metrics) metric.IsStale=section.Quality.IsStale;
                if(section.Quality.IsStale) section.Quality.Warnings.Add("Stale "+section.Section+" evidence as of "+section.CollectedAtUtc.ToString("O")); warnings.AddRange(section.Quality.Warnings);
            }
            snapshot.Summary.Metrics=snapshot.Sections.SelectMany(s=>s.Metrics).ToList();
            var open=snapshot.Summary.Metrics.Where(m=>m.Id=="TraceabilityOpenFindings"||m.Id=="QualityOpen"||m.Id=="PendingReviews").ToArray();
            snapshot.Summary.Metrics.Add(new DashboardMetric { Id="OpenFindings",Name="Open findings",Value=open.Length==3&&open.All(m=>m.Value.HasValue)?open.Sum(m=>m.Value.Value):(decimal?)null,Scope=snapshot.RequestKey,Source="Traceability + quality + impact",TimestampUtc=open.Length==0?snapshot.CollectedAtUtc:open.Min(m=>m.TimestampUtc),Completeness=open.Length==3&&open.All(m=>m.Completeness==DashboardCompleteness.Complete)?DashboardCompleteness.Complete:DashboardCompleteness.Partial,IsStale=open.Any(m=>m.IsStale),Warning="Combined finding count; not a count of unique affected requirements.",Destination=DashboardDestination.RequirementQuality });
            snapshot.DataQuality=new DashboardDataQuality { Completeness=snapshot.Sections.Count<6?DashboardCompleteness.Partial:snapshot.Sections.All(s=>s.Quality.Completeness==DashboardCompleteness.Complete)?DashboardCompleteness.Complete:DashboardCompleteness.Partial,IsStale=snapshot.Sections.Any(s=>s.Quality.IsStale),Warnings=warnings.Distinct().Take(1000).ToList(),AccessDeniedModules=snapshot.Sections.Select(s=>s.Quality.AccessDeniedModules).DefaultIfEmpty().Max(),UnknownModules=snapshot.Sections.Select(s=>s.Quality.UnknownModules).DefaultIfEmpty().Max() };
            if(snapshot.Sections.Select(s=>s.CollectedAtUtc).Distinct().Count()>1) snapshot.DataQuality.Warnings.Add("Sections have different as-of times; live metadata and stored analyses are not an atomic configuration snapshot.");
            Score(snapshot);
        }
        private static void Score(DashboardSnapshot snapshot)
        {
            var summary=snapshot.Summary; summary.ConfigurationHealthScore=null; summary.ScoreStatus="ScoreUnavailable"; summary.ScoreExplanation.Clear();
            summary.ScoreExplanation.Add("Project-specific optional indicator; not a DO standard score or certification decision.");
            if(!snapshot.Request.Score.Enabled) { summary.ScoreExplanation.Add("Disabled by project policy."); return; }
            Func<string,DashboardMetric> get=id=>summary.Metrics.SingleOrDefault(m=>m.Id==id);
            var baseline=snapshot.Sections.SingleOrDefault(s=>s.Section==DashboardSections.Baseline); var trace=snapshot.Sections.SingleOrDefault(s=>s.Section==DashboardSections.Traceability); var quality=snapshot.Sections.SingleOrDefault(s=>s.Section==DashboardSections.Quality); var impact=snapshot.Sections.SingleOrDefault(s=>s.Section==DashboardSections.Impact);
            var values=new Dictionary<string,decimal?>();
            values["Baseline"]=baseline?.Baseline?.Modules.Count>0 && baseline.Quality.VerifiedComplete ? 100m*baseline.Baseline.Modules.Count(m=>m.BaselineHealth==DashboardBaselineState.UpToDate)/baseline.Baseline.Modules.Count:(decimal?)null;
            values["Traceability"]=trace?.Quality.VerifiedComplete==true?trace.Traceability?.CoveragePercentage:null;
            values["Quality"]=quality?.Quality.VerifiedComplete==true&&quality.RequirementQuality?.EvaluatedRequirements>0?Math.Max(0,100m*(quality.RequirementQuality.EvaluatedRequirements-quality.RequirementQuality.UniqueAffectedRequirements)/quality.RequirementQuality.EvaluatedRequirements):(decimal?)null;
            var total=get("ImpactCount"); var pending=get("PendingReviews"); values["ChangeReview"]=impact?.Quality.VerifiedComplete==true&&total?.Value>0&&pending?.Value!=null ? 100m*(total.Value.Value-pending.Value.Value)/total.Value.Value:(decimal?)null;
            var sections=snapshot.Sections.Where(s=>s.Section!=DashboardSections.Audit).ToArray(); values["Verification"]=sections.Length==5&&sections.All(s=>s.Quality.VerifiedComplete)?100m:(decimal?)null;
            foreach(var pair in snapshot.Request.Score.Weights) summary.ScoreExplanation.Add(pair.Key+": "+pair.Value+"% × "+(values[pair.Key]?.ToString("0.##")??"Unavailable"));
            if(snapshot.Request.Score.Weights.Any(p=>p.Value>0&&!values[p.Key].HasValue)) { summary.ScoreStatus="Incomplete"; return; }
            summary.ConfigurationHealthScore=snapshot.Request.Score.Weights.Sum(p=>p.Value==0?0:p.Value*values[p.Key].Value/100m); summary.ScoreStatus="Available";
        }
    }
}
