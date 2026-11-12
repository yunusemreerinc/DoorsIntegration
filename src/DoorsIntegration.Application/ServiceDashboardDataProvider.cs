using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;
using DoorsIntegration.Dxl;
using DoorsIntegration.Processing;

namespace DoorsIntegration.ConfigurationManagement
{
    // Application aggregation adapter. It calls read ports only; no preflight approval or executor is held.
    public sealed class DashboardReadPorts
    {
        public IProjectService Projects { get; set; }
        public IModuleService Modules { get; set; }
        public IRequirementService Requirements { get; set; }
        public IBaselineMetadataService Metadata { get; set; }
        public ITraceabilityGraphService Graphs { get; set; }
        public IBaselineSetVerificationService Verification { get; set; }
        public IChangeImpactAnalysisService Impact { get; set; }
        public ImpactReviewService Reviews { get; set; }
        public INativeBaselineSetService NativeSets { get; set; }
        public IBaselineSetAdapter SetMetadata { get; set; }
        public IJobHistoryService Jobs { get; set; }
        public INativeReadObservationSource NativeObservation { get; set; }
        public Func<DashboardRequest,CancellationToken,Task<ImpactAnalysisSnapshot>> MockSnapshot { get; set; }
        public Func<long> ComCallAttempts { get; set; }
    }
    public sealed class ServiceDashboardDataProvider : IDashboardDataProvider
    {
        private readonly DashboardReadPorts ports; private readonly IDashboardAnalysisRepository analyses; private readonly IDashboardClock clock; private readonly IIntegrationLogger log;
        public ServiceDashboardDataProvider(DashboardReadPorts ports,IDashboardAnalysisRepository analyses,IDashboardClock clock,IIntegrationLogger log)
        { this.ports=ports??throw new ArgumentNullException(nameof(ports)); this.analyses=analyses; this.clock=clock; this.log=log; }
        public async Task<DashboardEvidence> CollectAsync(DashboardRequest request,DashboardSections sections,bool runNewAnalyses,IProgress<string> progress,CancellationToken token)
        {
            request.Validate(); token.ThrowIfCancellationRequested(); long startCalls=ports.ComCallAttempts?.Invoke()??0;
            var data=new DashboardEvidence { Environment=request.Environment,DatabaseIdentity=request.DatabaseIdentity,IsMock=request.IsMock,CollectedAtUtc=clock.UtcNow,CollectedSections=sections };
            data.Analyses=analyses.Read(request,token).ToList();
            if(analyses is IDashboardAnalysisDiagnostics diagnostics) data.Warnings.AddRange(diagnostics.ReadWarnings);
            if(ports.Reviews!=null) foreach(var record in data.Analyses.Where(a=>a.Kind==DashboardAnalysisKind.Impact)) foreach(var finding in record.Findings)
            {
                token.ThrowIfCancellationRequested();
                if(string.IsNullOrWhiteSpace(finding.ReviewEvidenceFingerprint)) { finding.ReviewStatus="NotReviewed"; continue; }
                var decision=ports.Reviews.FindApplicableDecision(finding.Id,record.SnapshotIdentity,finding.ReviewEvidenceFingerprint); finding.ReviewStatus=decision?.Decision.ToString()??"NotReviewed";
            }
            if(!runNewAnalyses && (sections&(DashboardSections.Overview|DashboardSections.Baseline))==0)
            {
                FromSavedResults(request,data); if((sections&DashboardSections.Audit)!=0) await Activity(request,data,token).ConfigureAwait(false);
                data.ComCallCount=0; data.Warnings.Add("Existing-result refresh read stored analysis evidence; it did not recollect the live requirement inventory or run an analysis."); return data;
            }
            ImpactAnalysisSnapshot mock=null; var qualityArtifacts=new List<QualityArtifact>();
            if(request.IsMock)
            {
                if(ports.MockSnapshot==null) throw new InvalidOperationException("Mock snapshot provider is not configured.");
                mock=await ports.MockSnapshot(request,token).ConfigureAwait(false); data.ServiceReadCalls++;
                if(mock.Links.Context.Source!=BaselineMetadataSource.Mock||mock.Links.Context.EnvironmentId!=request.Environment||mock.Links.Context.RepositoryId!=request.DatabaseIdentity) throw new InvalidOperationException("Mock environment/snapshot scope mismatch.");
                PopulateMock(request,data,mock,qualityArtifacts);
            }
            else
            {
                await ObserveEnvironment(request,token).ConfigureAwait(false); data.ServiceReadCalls++;
                await CollectReal(request,data,qualityArtifacts,progress,token).ConfigureAwait(false);
            }
            if((sections&DashboardSections.Baseline)!=0) await Baselines(request,data,token).ConfigureAwait(false);
            if(runNewAnalyses)
            {
                if((sections&DashboardSections.Traceability)!=0) await Traceability(request,data,mock,progress,token).ConfigureAwait(false);
                if((sections&DashboardSections.Quality)!=0) Quality(request,data,qualityArtifacts,token);
                if((sections&DashboardSections.Impact)!=0) await Impact(request,data,progress,token).ConfigureAwait(false);
            }
            if((sections&DashboardSections.Audit)!=0) await Activity(request,data,token).ConfigureAwait(false);
            data.CollectedAtUtc=clock.UtcNow; data.ComCallCount=ports.ComCallAttempts==null?(request.IsMock?(long?)0:null):Math.Max(0,ports.ComCallAttempts()-startCalls);
            return data;
        }
        private static void FromSavedResults(DashboardRequest request,DashboardEvidence data)
        {
            var candidates=data.Analyses.OrderByDescending(a=>a.TimestampUtc).SelectMany(a=>a.Requirements).Where(r=>request.Scope.Contains(r.Version.FullPath,r.ModuleIdentity));
            if(request.Scope.Modules.Count>0) { var chosen=new HashSet<string>(request.Scope.Modules.Select(v=>v.ModuleBaselineIdentity)); candidates=candidates.Where(r=>chosen.Contains(r.Version.ModuleBaselineIdentity)); }
            else if(request.Scope.Kind!=DashboardScopeKind.SelectedBaselineSet) candidates=candidates.Where(r=>r.Version.IsCurrent);
            var bindings=candidates.GroupBy(r=>r.ModuleIdentity).Select(g=>g.First().Version.ModuleBaselineIdentity).ToArray();
            var requirements=candidates.Where(r=>bindings.Contains(r.Version.ModuleBaselineIdentity)).GroupBy(r=>r.VersionIdentity).Select(g=>g.First()).ToList();
            data.Requirements=requirements; foreach(var group in requirements.GroupBy(r=>r.Version.ModuleBaselineIdentity)) { var v=group.First().Version; data.Modules.Add(new DashboardModuleFact { Health=new DashboardModuleHealth { Version=v,ModuleIdentity=v.ModuleIdentity,ModulePath=v.FullPath,Project=Project(v.FullPath),Access="Readable",Evidence="Stored analysis inventory; current accessibility not rechecked." },InventoryComplete=data.Analyses.Where(a=>a.Modules.Any(m=>m.ModuleBaselineIdentity==v.ModuleBaselineIdentity)).All(a=>a.Completeness==DashboardCompleteness.Complete) }); }
            data.DiscoveryComplete=request.Scope.Kind==DashboardScopeKind.SelectedFormalModules||request.Scope.Kind==DashboardScopeKind.SelectedBaselineSnapshot;
            if(!data.DiscoveryComplete) data.Warnings.Add("Stored results alone do not prove complete project/folder discovery.");
        }
        private static string Project(string path) => "/"+path.TrimStart('/').Split('/')[0];
        private static void PopulateMock(DashboardRequest request,DashboardEvidence data,ImpactAnalysisSnapshot snapshot,List<QualityArtifact> artifacts)
        {
            var rows=snapshot.Requirements.Where(r=>request.Scope.Contains(r.Version.FullPath,r.Version.ModuleIdentity)).Take(request.MaximumRequirements+1).ToArray();
            if(rows.Length>request.MaximumRequirements) { rows=rows.Take(request.MaximumRequirements).ToArray(); data.Warnings.Add("Mock requirement limit reached."); }
            if(snapshot.Links.Context.Set!=null) { data.ObservedSets.Add(ObserveSet(snapshot.Links.Context.Set)); data.BaselineSetCount=1; data.SetInventoryComplete=snapshot.Links.Context.MetadataComplete; }
            foreach(var group in rows.GroupBy(r=>r.Version.ModuleBaselineIdentity).Take(request.MaximumModules))
            {
                var version=group.First().Version;
                var known=snapshot.Links.Context.Modules.Single(m=>m.ModuleIdentity==version.ModuleIdentity);
                var module=new DashboardModuleFact { Health=new DashboardModuleHealth { ModuleIdentity=version.ModuleIdentity,ModulePath=version.FullPath,Project=Project(version.FullPath),Version=version,Access=known.State==BaselineVerificationReadState.AccessDenied?"AccessDenied":known.State==BaselineVerificationReadState.Verified?"Readable":"Unknown",CurrentModuleState="Explicit Mock read snapshot" },InventoryComplete=known.InventoryComplete,HistoryComplete=false };
                data.Modules.Add(module); data.ProjectPaths.Add(module.Health.Project);
                foreach(var req in group) { var fact=DashboardResultMapper.Requirement(req); fact.ProjectPath=module.Health.Project; data.Requirements.Add(fact); artifacts.Add(Artifact(req)); }
            }
            foreach(var known in snapshot.Links.Context.Modules.Where(m=>request.Scope.Contains(m.FullPath,m.ModuleIdentity)&&!data.Modules.Any(f=>f.Health.ModuleIdentity==m.ModuleIdentity)))
                data.Modules.Add(new DashboardModuleFact { Health=new DashboardModuleHealth { ModuleIdentity=known.ModuleIdentity,ModulePath=known.FullPath,Version=known.Version,Project=Project(known.FullPath),Access=known.State==BaselineVerificationReadState.AccessDenied?"AccessDenied":"Unknown",Evidence=known.Reason },InventoryComplete=false });
            data.DiscoveryComplete=!snapshot.Links.Truncated&&data.Modules.All(m=>m.InventoryComplete); data.FolderPaths=data.Modules.Select(m=>m.Health.ModulePath.Substring(0,m.Health.ModulePath.LastIndexOf('/'))).Distinct().ToList(); data.ProjectPaths=data.ProjectPaths.Distinct().ToList();
            data.Warnings.Add("MOCK: captured MockDoorsAdapter scenario, not native COM/DXL validation. Only this fixture's visible configuration is counted.");
        }
        private static QualityArtifact Artifact(ImpactRequirementVersion r) => new QualityArtifact(new ArtifactIdentity(r.Version.RepositoryId,r.Version.ModuleIdentity,r.AbsoluteNumber.ToString(CultureInfo.InvariantCulture),r.Version.IsCurrent?ArtifactVersion.Current:ArtifactVersion.Baseline,r.Version.IsCurrent?"":r.Version.Version),r.Attributes.Values.Select(v=>new QualityField(v.Name,v.PlainStatus==RequirementValueStatus.Readable?QualityAvailability.Available:v.PlainStatus==RequirementValueStatus.AccessDenied?QualityAvailability.Denied:QualityAvailability.NotRequested,v.PlainStatus==RequirementValueStatus.Readable?v.PlainText:null,v.RichStatus==RequirementValueStatus.Readable?v.RichText:null)),r.IsDeleted);
        private async Task ObserveEnvironment(DashboardRequest request,CancellationToken token)
        {
            if(ports.NativeObservation==null) throw new InvalidOperationException("Native read-only session observer is not configured.");
            var session=await ports.NativeObservation.ReadAsync(new NativeReadQuery(request.ClientVersion),token).ConfigureAwait(false);
            if(!session.SessionObserved||session.DatabaseId!=request.Environment||session.RepositoryId!=request.DatabaseIdentity) throw new InvalidOperationException("Observed DOORS environment/database differs from selected dashboard scope.");
        }
        private async Task CollectReal(DashboardRequest request,DashboardEvidence data,List<QualityArtifact> artifacts,IProgress<string> progress,CancellationToken token)
        {
            var versions=new Dictionary<string,BaselineVersionReference>(StringComparer.Ordinal); var candidates=new HashSet<string>(StringComparer.Ordinal); bool discovery=true;
            if(request.Scope.Kind==DashboardScopeKind.SelectedFormalModules||request.Scope.Kind==DashboardScopeKind.SelectedBaselineSnapshot)
                foreach(var v in request.Scope.Modules) { candidates.Add(v.FullPath); versions.Add(v.FullPath,v); }
            else if(request.Scope.Kind==DashboardScopeKind.SelectedBaselineSet)
            {
                if(ports.SetMetadata==null) throw new InvalidOperationException("Native set read adapter unavailable.");
                var set=await ports.SetMetadata.InspectAsync(new BaselineSetRequest(BaselineSetAction.UpdateDefinition,request.Scope.SetFolder,request.Scope.DefinitionIdentity,request.Scope.SetIdentity,target:request.ClientVersion),token).ConfigureAwait(false); data.ServiceReadCalls++;
                if(set.RepositoryId!=request.DatabaseIdentity) throw new InvalidOperationException("Set repository binding mismatch.");
                var selected=set.Sets.SingleOrDefault(s=>s.Id==request.Scope.SetIdentity&&s.DefinitionId==request.Scope.DefinitionIdentity)??throw new InvalidOperationException("Selected baseline set is unavailable; no Current fallback.");
                foreach(var member in selected.Members.Where(m=>m.TypedVersion?.IdentityVerified==true)) { candidates.Add(member.Module.FullPath); versions[member.Module.FullPath]=member.TypedVersion; }
                discovery=set.Complete; data.SetInventoryComplete=set.Complete; data.BaselineSetCount=set.Sets.Count; data.Warnings.Add(set.Reason);
                data.ObservedSets.Add(ObserveSet(selected));
            }
            else
            {
                var roots=request.Scope.Kind==DashboardScopeKind.SelectedFolders?request.Scope.Folders:request.Scope.Projects.Select(p=>new DashboardFolderSelection { FullPath=p,IncludeSubfolders=true });
                foreach(var root in roots)
                {
                    token.ThrowIfCancellationRequested(); progress?.Report("Discovering dashboard scope: "+root.FullPath);
                    var found=await ports.Projects.DiscoverHierarchyAsync(new DiscoveryScope(root.FullPath,root.IncludeSubfolders,maxItems:10000,maxFolders:10000,timeout:TimeSpan.FromSeconds(request.TimeoutSeconds),target:request.ClientVersion),token).ConfigureAwait(false); data.ServiceReadCalls++;
                    discovery&=found.IsCompleteForVisibleScope;
                    data.ProjectPaths.AddRange(found.Items.Where(i=>i.Type==DoorsItemKind.Project).Select(i=>i.FullPath)); data.FolderPaths.AddRange(found.Items.Where(i=>i.Type==DoorsItemKind.Folder).Select(i=>i.FullPath));
                    foreach(var item in found.Items.Where(i=>i.Type==DoorsItemKind.FormalModule)) candidates.Add(item.FullPath);
                    if(found.Issues.Count>0) data.Warnings.Add("Discovery has access/limit issues; totals are lower bounds.");
                }
                data.Warnings.Add("Native hierarchy is visibility filtered; hidden project modules cannot be counted."); discovery=false;
            }
            if(candidates.Count>request.MaximumModules) { discovery=false; data.Warnings.Add("Module collection limit reached."); }
            foreach(string path in candidates.OrderBy(p=>p,StringComparer.Ordinal).Take(request.MaximumModules))
            {
                token.ThrowIfCancellationRequested(); progress?.Report("Counting read-only module: "+path); versions.TryGetValue(path,out var selected);
                var module=new DashboardModuleFact { Health=new DashboardModuleHealth { ModuleIdentity=selected?.ModuleIdentity,ModulePath=path,Project=data.ProjectPaths.Where(p=>DashboardScope.Under(p,path)).OrderByDescending(p=>p.Length).FirstOrDefault()??"Unknown",Version=selected,Access="Unknown" } };
                data.Modules.Add(module);
                try
                {
                    if(selected!=null&&ports.Modules!=null)
                    {
                        var metadata=await ports.Modules.GetFormalModuleAsync(new DiscoveryScope(path,false,timeout:TimeSpan.FromSeconds(request.TimeoutSeconds),target:request.ClientVersion),token).ConfigureAwait(false); data.ServiceReadCalls++;
                        if(!metadata.Items.Any(i=>i.FullPath==path&&i.Type==DoorsItemKind.FormalModule&&i.AccessStatus==DoorsAccessStatus.Readable)) { discovery=false; data.Warnings.Add("Selected formal module metadata is unavailable: "+path); continue; }
                    }
                    int remaining=request.MaximumRequirements-data.Requirements.Count; if(remaining<1) { data.Warnings.Add("Requirement collection limit reached; later modules remain Unknown."); discovery=false; break; }
                    var read=new RequirementReadRequest(path,request.CustomAttributes.Where(a=>a!="Object Heading"&&a!="Object Text"),selected?.Baseline,DeletedObjectMode.IncludeContent,false,true,remaining,maxFieldCharacters:65536,maxObjectCharacters:262144,maxFileBytes:134217728,timeout:TimeSpan.FromSeconds(request.TimeoutSeconds),target:request.ClientVersion);
                    using(var snapshot=await ports.Requirements.ReadAsync(read,token).ConfigureAwait(false))
                    {
                        data.ServiceReadCalls++; var summary=snapshot.Summary;
                        module.Health.Access=summary.ModuleStatus==RequirementModuleStatus.Available?"Readable":summary.ModuleStatus==RequirementModuleStatus.AccessDenied?"AccessDenied":"Unknown";
                        if(summary.ModuleStatus!=RequirementModuleStatus.Available || string.IsNullOrWhiteSpace(summary.QualifiedModuleId)) { discovery=false; if(module.Health.ModuleIdentity==null) data.UnresolvedModulePaths.Add(path); continue; }
                        if(selected!=null&&selected.ModuleIdentity!=summary.QualifiedModuleId) throw new InvalidOperationException("Module was replaced or renamed; stable identity mismatch.");
                        module.Health.ModuleIdentity=summary.QualifiedModuleId; module.Health.Version=selected??new BaselineVersionReference(request.DatabaseIdentity,summary.QualifiedModuleId,path,0,0,isCurrent:true); module.InventoryComplete=summary.IsCompleteForReadableObjects;
                        foreach(var row in snapshot.ReadObjects())
                        {
                            token.ThrowIfCancellationRequested(); var v=new ImpactRequirementVersion { Version=module.Health.Version,AbsoluteNumber=row.AbsoluteNumber,IsDeleted=row.IsDeleted,Access=BaselineVerificationReadState.Verified,Attributes=row.Values.ToDictionary(p=>p.Key,p=>ImpactAttributeValue.From(p.Value),StringComparer.Ordinal) };
                            var fact=DashboardResultMapper.Requirement(v); fact.ProjectPath=module.Health.Project; data.Requirements.Add(fact); artifacts.Add(Artifact(v));
                        }
                    }
                    if(module.Health.Version.IsCurrent)
                    {
                        var schema=await ports.Requirements.GetAttributeSchemaAsync(new RequirementWriteTarget(path,request.ClientVersion,DoorsConnectionMode.AttachExisting,TimeSpan.FromSeconds(request.TimeoutSeconds)),token).ConfigureAwait(false); data.ServiceReadCalls++;
                        if(schema.IsAvailable&&schema.QualifiedModuleId==module.Health.ModuleIdentity) { module.AttributeCatalogComplete=true; module.AttributeCatalog=schema.Attributes.Where(a=>!a.IsSystem).Select(a=>a.Name).Distinct().ToList(); }
                    }
                }
                catch(IntegrationException ex)
                {
                    discovery=false; module.Health.Access=ex.Code==IntegrationErrorCode.ComAccessDenied?"AccessDenied":"Unknown"; data.Warnings.Add("Module observation failed: "+ex.Code+". No automatic retry.");
                    if(module.Health.ModuleIdentity==null) data.UnresolvedModulePaths.Add(path);
                    if(ex.Code==IntegrationErrorCode.DxlTimeout||ex.Code==IntegrationErrorCode.DxlWaitCancelled) throw;
                }
            }
            data.DiscoveryComplete=discovery; data.ProjectPaths=data.ProjectPaths.Distinct().ToList(); data.FolderPaths=data.FolderPaths.Distinct().ToList();
        }
        private async Task Baselines(DashboardRequest request,DashboardEvidence data,CancellationToken token)
        {
            foreach(var module in data.Modules.Where(m=>m.Health.Version!=null))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var result=await ports.Metadata.ReadAsync(new BaselineHistoryRequest(new BaselineModuleReference(request.DatabaseIdentity,module.Health.ModuleIdentity,module.Health.ModulePath),openIfClosed:true,target:request.ClientVersion,timeout:TimeSpan.FromSeconds(request.TimeoutSeconds)),token).ConfigureAwait(false); data.ServiceReadCalls++;
                    var history=result.Snapshot; module.HistoryComplete=history.Status==BaselineHistoryStatus.Available&&history.HistoryComplete; module.BaselineCount=history.History.Count(h=>h.Existence==BaselineExistenceStatus.Exists); module.NeverBaselinedProven=history.HasNoBaselineHistory;
                    var latest=history.LatestExisting.Version;
                    if(latest!=null&&history.LatestExisting.Status==BaselineDesignationStatus.Available&&history.LatestExisting.Existence==BaselineExistenceStatus.Exists)
                    { module.LatestVersion=new BaselineVersionReference(request.DatabaseIdentity,module.Health.ModuleIdentity,module.Health.ModulePath,latest.Major,latest.Minor,latest.Suffix); module.Health.LatestBaseline=module.LatestVersion.Version; module.Health.LatestMajorVersion=latest.Major; module.Health.LatestMinorVersion=latest.Minor; module.Health.BaselineSuffix=latest.Suffix; module.Health.LatestBaselineDateUtc=history.History.FirstOrDefault(h=>h.Major==latest.Major&&h.Minor==latest.Minor&&h.Suffix==latest.Suffix)?.CreationDateUtc; }
                    module.Health.Evidence=history.ApiEvidence;
                }
                catch(IntegrationException ex) { data.Warnings.Add("Baseline metadata unavailable: "+ex.Code); if(ex.Code==IntegrationErrorCode.DxlTimeout||ex.Code==IntegrationErrorCode.DxlWaitCancelled) throw; }
            }
            if(ports.SetMetadata!=null && request.Scope.Kind!=DashboardScopeKind.SelectedBaselineSet)
            {
                bool complete=true; var sets=new HashSet<string>(); var roots=request.Scope.Kind==DashboardScopeKind.SelectedFolders?request.Scope.Folders.Select(f=>f.FullPath):request.Scope.Projects;
                foreach(string path in roots.Distinct())
                { var observed=await ports.SetMetadata.InspectAsync(new BaselineSetRequest(BaselineSetAction.UpdateDefinition,path,target:request.ClientVersion),token).ConfigureAwait(false); data.ServiceReadCalls++; if(observed.RepositoryId!=request.DatabaseIdentity) { complete=false; continue; } foreach(var set in observed.Sets) if(sets.Add(set.DefinitionId+"/"+set.Id)) data.ObservedSets.Add(ObserveSet(set)); complete&=observed.Complete; }
                data.BaselineSetCount=sets.Count; data.SetInventoryComplete=complete;
            }
        }
        private static DashboardSetObservation ObserveSet(NativeBaselineSetDetails set)=>new DashboardSetObservation { Identity=set.Id,DefinitionIdentity=set.DefinitionId,IsOpen=set.IsOpen,ModuleIdentities=set.Members.Select(m=>m.Module.ModuleId).Distinct().ToList() };
        private TraceabilityPolicy Policy(DashboardRequest request)
        {
            if(!string.IsNullOrWhiteSpace(request.TraceabilityPolicyJson)) return BaselineVerificationPolicyProfile.ReadJson(request.TraceabilityPolicyJson).ToPolicy();
            if(request.LinkModulePaths.Count==0) throw new ArgumentException("Select a link module and project traceability policy.");
            string path=request.LinkModulePaths[0]; return new TraceabilityPolicy(request.TraceabilityPolicyVersion,new[]{new TraceabilityLinkRule("SYS-HLR","SYS",new[]{"HLR"},path,versionPolicy:TraceabilityVersionPolicy.AnyExplicitVersion),new TraceabilityLinkRule("HLR-LLR","HLR",new[]{"LLR"},path,versionPolicy:TraceabilityVersionPolicy.AnyExplicitVersion),new TraceabilityLinkRule("LLR-TEST","LLR",new[]{"TEST"},path,versionPolicy:TraceabilityVersionPolicy.AnyExplicitVersion)},orphanRoles:Array.Empty<string>());
        }
        private async Task Traceability(DashboardRequest request,DashboardEvidence data,ImpactAnalysisSnapshot mock,IProgress<string> progress,CancellationToken token)
        {
            var policy=Policy(request);
            if(request.Scope.Kind==DashboardScopeKind.SelectedBaselineSet)
            {
                var verified=await ports.Verification.VerifyAsync(new BaselineSetVerificationRequest("dashboard",request.Scope.SetFolder,request.Scope.DefinitionIdentity,request.Scope.SetIdentity,policy,request.LinkModulePaths,target:request.ClientVersion,maxRequirements:request.MaximumRequirements,maxLinks:request.MaximumLinks,typeAttribute:"Requirement Type",statusAttribute:"Status"),progress,token).ConfigureAwait(false); data.ServiceReadCalls++;
                if(verified.Snapshot==null) { data.Warnings.Add("Baseline verification snapshot unavailable."); return; }
                Save(data,DashboardResultMapper.Verification(request,verified)); return;
            }
            var service=mock==null?ports.Graphs:new TraceabilityGraphService(new RequirementService(ServiceImpactSnapshotProvider.CapturedRequirements(mock)),new LinkService(new DashboardSnapshotLinkReader(mock)),log);
            var modules=data.Modules.Where(m=>m.Health.Version!=null&&m.Health.Access=="Readable").ToArray();
            for(int offset=0;offset<modules.Length;offset+=64)
            {
                token.ThrowIfCancellationRequested(); var group=modules.Skip(offset).Take(64).ToArray();
                var scopes=group.Select(m=>new TraceabilityModuleScope(m.Health.ModulePath,data.Requirements.Where(q=>q.ModuleIdentity==m.Health.ModuleIdentity&&q.Type!=null).Select(q=>q.Type).Distinct().Count()==1?data.Requirements.First(q=>q.ModuleIdentity==m.Health.ModuleIdentity&&q.Type!=null).Type:"Unknown",m.Health.Version.Baseline,m.Health.Version.IsCurrent?null:m.Health.Version.Version)).ToArray();
                var graph=await service.BuildAsync(new TraceabilityGraphRequest(request.DatabaseIdentity,scopes,request.LinkModulePaths,policy,maxNodes:request.MaximumRequirements,maxObservations:request.MaximumLinks,readTimeout:TimeSpan.FromSeconds(request.TimeoutSeconds),target:request.ClientVersion),cancellationToken:token).ConfigureAwait(false); data.ServiceReadCalls++;
                var mapped=DashboardResultMapper.Graph(request,graph,data.Requirements.Where(q=>group.Any(m=>m.Health.ModuleIdentity==q.ModuleIdentity)));
                var mixed=new HashSet<string>(group.Where(m=>data.Requirements.Where(q=>q.ModuleIdentity==m.Health.ModuleIdentity).Select(q=>q.Type).Distinct().Count()>1).Select(m=>m.Health.ModuleIdentity));
                if(mixed.Count>0) { mapped.Completeness=DashboardCompleteness.Partial; mapped.Warnings.Add("Generic module-role graph cannot prove policy eligibility for mixed requirement types; use typed baseline-set verification. These objects remain Unknown."); var ids=new HashSet<string>(mapped.Requirements.Where(q=>mixed.Contains(q.ModuleIdentity)).Select(q=>q.VersionIdentity)); foreach(var c in mapped.Coverage.Where(c=>ids.Contains(c.RequirementVersionIdentity))) { c.EligibilityKnown=false; c.State=TraceabilityState.Unknown; } }
                Save(data,mapped);
            }
        }
        private void Quality(DashboardRequest request,DashboardEvidence data,List<QualityArtifact> artifacts,CancellationToken token)
        {
            var standard=(RuleStandard)Enum.Parse(typeof(RuleStandard),request.QualityStandard,true);
            var profile=string.IsNullOrWhiteSpace(request.QualityProfileJson)?BuiltinRuleProfiles.CreateTemplate(standard):RuleProfile.ParseJson(request.QualityProfileJson);
            if(profile.Standard!=standard) throw new ArgumentException("Selected quality profile standard differs from filter.");
            var record=DashboardResultMapper.Record(request,DashboardAnalysisKind.Quality,Guid.NewGuid().ToString("N"),clock.UtcNow,data.Requirements); record.ProfileFingerprint=profile.Fingerprint; record.ProfileApproved=profile.IsApprovedFor(profile.ProjectId,clock.UtcNow); record.PolicyFingerprint="not-applicable";
            record.ProfileApproved&=data.Modules.Count>0&&data.Modules.All(m=>m.Health.Project==profile.ProjectId);
            var sink=new QualitySink(record,data.Requirements); var summary=new RequirementsQualityRuleEngine().Evaluate(artifacts,profile,new RuleRunContext(profile.ProjectId.Length>0?profile.ProjectId:"dashboard-project",request.Key,data.DiscoveryComplete,clock.UtcNow,RuleExecutionMode.DraftPreview,maxArtifacts:request.MaximumRequirements),sink,token);
            token.ThrowIfCancellationRequested(); record.AnalysisId=summary.RunId; record.TimestampUtc=clock.UtcNow; record.Completeness=summary.FullyEvaluated&&data.DiscoveryComplete?DashboardCompleteness.Complete:DashboardCompleteness.Partial; record.Status=summary.FullyEvaluated?"Completed":"Incomplete";
            record.Warnings.Add("RuleSeverity Error maps to High; Warning to Medium; Information to Informational. Criticality is not an automatic certification rule.");
            foreach(var group in sink.States) record.Evaluations.Add(new DashboardQualityEvaluation { RequirementVersionIdentity=group.Key,FullyEvaluated=group.Value });
            foreach(var f in record.Findings) f.AnalysisIdentity=record.AnalysisId; Save(data,record);
        }
        private sealed class QualitySink : IRuleResultSink
        {
            private readonly DashboardAnalysisRecord record; private readonly Dictionary<string,DashboardRequirementFact> map; internal readonly Dictionary<string,bool> States=new Dictionary<string,bool>();
            internal QualitySink(DashboardAnalysisRecord record,IEnumerable<DashboardRequirementFact> requirements) { this.record=record; map=requirements.ToDictionary(r=>new ArtifactIdentity(r.Version.RepositoryId,r.ModuleIdentity,r.AbsoluteNumber.ToString(CultureInfo.InvariantCulture),r.Version.IsCurrent?ArtifactVersion.Current:ArtifactVersion.Baseline,r.Version.IsCurrent?"":r.Version.Version).Key); }
            public void Write(RuleResult result)
            {
                if(!map.TryGetValue(result.ArtifactKey,out var req)) return; bool complete=result.State==RuleState.Passed||result.State==RuleState.Finding; States[req.VersionIdentity]=(!States.TryGetValue(req.VersionIdentity,out var old)||old)&&complete;
                if(result.State!=RuleState.Finding&&result.State!=RuleState.RuleError) return;
                var f=DashboardResultMapper.Finding(record,result.ResultFingerprint,"Quality",result.Code,result.Severity==RuleSeverity.Error?DashboardSeverity.High:result.Severity==RuleSeverity.Warning?DashboardSeverity.Medium:DashboardSeverity.Informational,req,result.Explanation,!result.EvidenceComplete,DashboardDestination.RequirementQuality,result.RuleId); f.ReviewStatus=result.ReviewerDisposition.ToString(); f.Evidence=string.Join("; ",result.Evidence.Select(e=>e.Code)); record.Findings.Add(f);
            }
            public void Complete(RuleRunSummary summary) { }
        }
        private async Task Impact(DashboardRequest request,DashboardEvidence data,IProgress<string> progress,CancellationToken token)
        {
            if(ports.Impact==null) { data.Warnings.Add("Impact analysis service unavailable."); return; }
            if(request.IsMock)
            {
                bool current=data.Modules.Any(m=>m.Health.Version?.IsCurrent==true); var example=MockChangeImpactSnapshotProvider.DemoRequest(kind:current?ChangeImpactComparisonKind.BaselineToCurrent:ChangeImpactComparisonKind.ModuleBaselines); var compare=new ChangeImpactRequest(example.Source,example.Target,example.Policy,example.Kind,current?ImpactGraphChoice.Target:ImpactGraphChoice.Source);
                var result=await ports.Impact.AnalyzeAsync(compare,progress,token).ConfigureAwait(false); data.ServiceReadCalls++; ports.Reviews?.Apply(result); Save(data,DashboardResultMapper.Impact(request,result,!current)); return;
            }
            foreach(var module in data.Modules.Where(m=>m.Health.Version!=null&&m.LatestVersion!=null))
            {
                token.ThrowIfCancellationRequested(); var source=new ImpactSnapshotSelection { EnvironmentId=request.Environment,RepositoryId=request.DatabaseIdentity,ClientVersion=request.ClientVersion,Label="Latest baseline",Modules={module.LatestVersion},LinkModulePaths=request.LinkModulePaths.ToList() }; var target=new ImpactSnapshotSelection { EnvironmentId=request.Environment,RepositoryId=request.DatabaseIdentity,ClientVersion=request.ClientVersion,Label=module.Health.Version.Version,Modules={module.Health.Version},LinkModulePaths=request.LinkModulePaths.ToList() };
                if(source.Modules[0].ModuleBaselineIdentity==target.Modules[0].ModuleBaselineIdentity) continue;
                var policy=new ImpactAnalysisPolicy { MaximumRequirements=request.MaximumRequirements,MaximumLinks=request.MaximumLinks,TimeoutSeconds=request.TimeoutSeconds };
                var result=await ports.Impact.AnalyzeAsync(new ChangeImpactRequest(source,target,policy,target.Modules[0].IsCurrent?ChangeImpactComparisonKind.BaselineToCurrent:ChangeImpactComparisonKind.ModuleBaselines),progress,token).ConfigureAwait(false); data.ServiceReadCalls++; ports.Reviews?.Apply(result); Save(data,DashboardResultMapper.Impact(request,result,false));
            }
        }
        private void Save(DashboardEvidence evidence,DashboardAnalysisRecord record)
        {
            evidence.Analyses.Add(record);
            try { analyses.Save(record); }
            catch(Exception ex) when(ex is System.IO.IOException||ex is UnauthorizedAccessException||ex is System.Security.Cryptography.CryptographicException) { evidence.Warnings.Add("New analysis is available in this refresh but its local index could not be published; history/drill-down may be unavailable."); }
        }
        private async Task Activity(DashboardRequest request,DashboardEvidence data,CancellationToken token)
        {
            if(ports.Jobs!=null)
            {
                var page=await ports.Jobs.QueryAsync(new BaselineJobHistoryFilter(database:request.DatabaseIdentity,source:request.IsMock?BaselineMetadataSource.Mock:BaselineMetadataSource.NativeDxl,repository:request.DatabaseIdentity,target:request.ClientVersion,limit:1000),token).ConfigureAwait(false);
                foreach(var item in page.Items) { var j=item.Snapshot; data.Activity.Add(new DashboardAuditEntry { JobId=j.JobId.ToString("N"),JobType="BulkBaseline",Operator=item.Operator,StartTimeUtc=j.ExecutionStartedAtUtc??j.CreatedAtUtc,EndTimeUtc=j.ExecutionEndedAtUtc,Status=j.Status.ToString(),AffectedModuleCount=j.Modules.Count,ModuleIdentities=j.Modules.Select(m=>m.ModuleId).ToList(),ErrorSummary=item.Counts }); }
                if(page.ScanLimited) data.Warnings.Add("Job history scan limit reached.");
            }
            if(ports.NativeSets!=null) foreach(var job in ports.NativeSets.ListJobs()) if(job.Before?.DatabaseId==request.DatabaseIdentity) data.Activity.Add(new DashboardAuditEntry { JobId=job.JobId.ToString("N"),JobType="NativeBaselineSet",Operator=job.Before.UserId,StartTimeUtc=job.StartedAtUtc,EndTimeUtc=job.EndedAtUtc,Status=job.Status.ToString(),AffectedModuleCount=job.Request.Modules.Count,ModuleIdentities=job.Request.Modules.Select(m=>m.ModuleId).ToList(),ErrorSummary=job.Reason });
        }
        public async Task ValidateNavigationAsync(DashboardNavigationIntent intent,CancellationToken token)
        {
            if(ports.NativeObservation!=null) await ObserveEnvironment(new DashboardRequest { Environment=intent.Environment,DatabaseIdentity=intent.DatabaseIdentity,ClientVersion=intent.ClientVersion },token).ConfigureAwait(false);
            if(intent.AnalysisIdentity!=null&&!analyses.Read(new DashboardRequest { Environment=intent.Environment,DatabaseIdentity=intent.DatabaseIdentity,IsMock=ports.MockSnapshot!=null },token).Any(a=>a.AnalysisId==intent.AnalysisIdentity)) throw new InvalidOperationException("Selected analysis result is no longer available in this environment.");
            if(intent.JobId!=null&&intent.AnalysisIdentity==null)
            {
                bool found=false;
                if(ports.Jobs!=null&&Guid.TryParse(intent.JobId,out var jobId)) { var job=await ports.Jobs.OpenAsync(jobId,token).ConfigureAwait(false); found=job?.AuditContext?.DatabaseIdentifier==intent.DatabaseIdentity; }
                if(!found&&ports.NativeSets!=null) found=ports.NativeSets.ListJobs().Any(j=>j.JobId.ToString("N")==intent.JobId&&j.Before?.DatabaseId==intent.Environment&&j.Before?.RepositoryId==intent.DatabaseIdentity);
                if(!found) throw new InvalidOperationException("Selected job is no longer available in this environment.");
            }
            if(intent.ModuleIdentity==null) return;
            using(var snapshot=await OpenRequirementAsync(intent,token).ConfigureAwait(false))
            { if(snapshot.Summary.ModuleStatus!=RequirementModuleStatus.Available||snapshot.Summary.QualifiedModuleId!=intent.ModuleIdentity) throw new InvalidOperationException("Source module is unavailable or identity changed; no fallback navigation."); if(intent.AbsoluteNumber.HasValue&&!snapshot.ReadObjects().Any(r=>r.AbsoluteNumber==intent.AbsoluteNumber.Value)) throw new InvalidOperationException("Source object is inaccessible or no longer in the selected version."); }
        }
        public async Task<IRequirementSnapshot> OpenRequirementAsync(DashboardNavigationIntent intent,CancellationToken token)
        {
            var request=new RequirementReadRequest(intent.ModulePath,new[]{"Status","Requirement Type","Criticality"},intent.Version?.Baseline,DeletedObjectMode.IncludeContent,false,true,timeout:TimeSpan.FromMinutes(2),target:intent.ClientVersion);
            if(ports.MockSnapshot==null) return await ports.Requirements.ReadAsync(request,token).ConfigureAwait(false);
            var scope=new DashboardRequest { Environment=intent.Environment,DatabaseIdentity=intent.DatabaseIdentity,IsMock=true,Scope=new DashboardScope { Kind=DashboardScopeKind.SelectedFormalModules,Modules={intent.Version} } };
            var snapshot=await ports.MockSnapshot(scope,token).ConfigureAwait(false); return await ServiceImpactSnapshotProvider.CapturedRequirements(snapshot).ReadAsync(request,token).ConfigureAwait(false);
        }
    }
}
