using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DoorsIntegration.Core;
using DoorsIntegration.ConfigurationManagement;
using DoorsIntegration.Wpf.Infrastructure;
using DoorsIntegration.Wpf.Services;

namespace DoorsIntegration.Wpf.ViewModels
{
    public sealed class DashboardKpiCard
    {
        public DashboardMetric Metric { get; set; }
        public UiCommand OpenCommand { get; set; }
    }
    public sealed class DashboardFolderChoice : ObservableModel
    {
        private string path; private bool include=true; private readonly Action changed;
        public DashboardFolderChoice(Action changed,string path="/Traceability") { this.changed=changed; this.path=path; }
        public string FullPath { get=>path; set { if(Set(ref path,value)) changed(); } }
        public bool IncludeSubfolders { get=>include; set { if(Set(ref include,value)) changed(); } }
    }
    public sealed class DashboardModuleChoice : ObservableModel
    {
        private bool selected=true,current; private string major="0",minor="0",suffix=""; private readonly Action changed;
        public DashboardModuleChoice(BaselineVersionReference version,Action changed) { this.changed=changed; ModuleIdentity=version.ModuleIdentity; FullPath=version.FullPath; Repository=version.RepositoryId; current=version.IsCurrent; major=version.Major.ToString(CultureInfo.InvariantCulture); minor=version.Minor.ToString(CultureInfo.InvariantCulture); suffix=version.Suffix; }
        public string ModuleIdentity { get; } public string FullPath { get; } public string Repository { get; }
        public bool Selected { get=>selected; set { if(Set(ref selected,value)) changed(); } }
        public bool Current { get=>current; set { if(Set(ref current,value)) changed(); } }
        public string Major { get=>major; set { if(Set(ref major,value)) changed(); } }
        public string Minor { get=>minor; set { if(Set(ref minor,value)) changed(); } }
        public string Suffix { get=>suffix; set { if(Set(ref suffix,value)) changed(); } }
        public BaselineVersionReference Version() => new BaselineVersionReference(Repository,ModuleIdentity,FullPath,int.Parse(Major,CultureInfo.InvariantCulture),int.Parse(Minor,CultureInfo.InvariantCulture),Suffix,Current);
    }
    public sealed class ConfigurationDashboardViewModel : ObservableModel
    {
        private readonly WorkspaceViewModel owner; private readonly List<UiCommand> commands=new List<UiCommand>(); private bool loadingInputs;
        private DashboardSnapshot snapshot; private DashboardRefreshResult refresh; private DashboardFinding selectedFinding; private DashboardModuleHealth selectedModule; private DashboardAuditEntry selectedActivity; private DashboardSnapshotHeader selectedHistory;
        private DashboardScopeKind scopeKind; private DashboardSections refreshSection=DashboardSections.All; private string environment="",database="",projects="",folderInput="/Traceability",linkModules="",setFolder="/Traceability",definition="",setId="",projectFilter="",folderFilter="",typeFilter="",statusFilter="",criticality="",severity="",baselineStatus="",reviewStatus="",from="",to="",profile="default",ttl="300",analysisAge="86400",traceVersion="project-default-v1",qualityVersion="General-v1",qualityStandard="General",traceJson="",qualityJson="",policyFile="",qualityFile="",scoreWeights="{\"Baseline\":25,\"Traceability\":30,\"Quality\":20,\"ChangeReview\":15,\"Verification\":10}",exportRoot=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),"DoorsIntegration","DashboardReports"),details="Select a metric, module, finding or activity.",message="Load the last suitable cached snapshot or explicitly refresh read-only results.";
        private bool score,failedOnly; private int findingPage,modulePage; private ExportFormat format=ExportFormat.Json; private DashboardReportKind reportKind;
        private IReadOnlyList<DashboardKpiCard> kpis=Array.Empty<DashboardKpiCard>(); private IReadOnlyList<DashboardFinding> findings=Array.Empty<DashboardFinding>(); private IReadOnlyList<DashboardModuleHealth> modules=Array.Empty<DashboardModuleHealth>(); private IReadOnlyList<DashboardSnapshotHeader> history=Array.Empty<DashboardSnapshotHeader>();
        public ConfigurationDashboardViewModel(WorkspaceViewModel owner)
        {
            this.owner=owner; Folders=new ObservableCollection<DashboardFolderChoice>(); ModuleChoices=new ObservableCollection<DashboardModuleChoice>();
            LoadCachedCommand=Command(()=>Refresh(DashboardRefreshMode.CacheOnly)); RefreshCommand=Command(()=>Refresh(DashboardRefreshMode.RefreshExistingResults)); RunAnalysisCommand=Command(()=>Refresh(DashboardRefreshMode.RunNewAnalysis));
            InvalidateCommand=Command(InvalidateCache); SaveProfileCommand=Command(SaveProfile); LoadProfileCommand=Command(LoadProfile); LoadHistoryCommand=Command(LoadHistory); OpenHistoryCommand=Command(OpenHistory,()=>SelectedHistory!=null);
            AddFolderCommand=Command(()=>{ Folders.Add(new DashboardFolderChoice(Invalidate,FolderInput)); Invalidate(); return Task.CompletedTask; }); ClearFoldersCommand=Command(()=>{ Folders.Clear(); Invalidate(); return Task.CompletedTask; });
            ObserveEnvironmentCommand=Command(ObserveEnvironment,()=>owner.Mode==ClientMode.RealDoors&&owner.Session.NativeReads!=null); UseWorkspaceModuleCommand=Command(UseWorkspaceModule,()=>owner.Mode==ClientMode.RealDoors&&owner.RequirementSnapshot?.Summary.QualifiedModuleId!=null);
            LoadPolicyCommand=Command(()=>LoadFile(false)); LoadQualityCommand=Command(()=>LoadFile(true));
            ExportCommand=Command(Export,()=>snapshot!=null); OpenFindingCommand=Command(()=>Navigate(SelectedFinding.Destination,finding:SelectedFinding),()=>SelectedFinding!=null); OpenModuleCommand=Command(()=>Navigate(DashboardDestination.ModuleExplorer,SelectedModule.ModuleIdentity),()=>SelectedModule!=null); CompareModuleCommand=Command(()=>Navigate(DashboardDestination.BaselineCompare,SelectedModule.ModuleIdentity),()=>SelectedModule!=null); BulkModuleCommand=Command(()=>Navigate(DashboardDestination.BulkBaseline,SelectedModule.ModuleIdentity),()=>SelectedModule!=null); OpenActivityCommand=Command(()=>Navigate(DashboardDestination.JobHistory,job:SelectedActivity),()=>SelectedActivity!=null);
            NextFindingsCommand=Command(()=>{ findingPage++; Project(); return Task.CompletedTask; },()=>snapshot!=null&&(findingPage+1)*100<AllFindings().Count()); PreviousFindingsCommand=Command(()=>{ findingPage--; Project(); return Task.CompletedTask; },()=>findingPage>0);
            NextModulesCommand=Command(()=>{ modulePage++; Project(); return Task.CompletedTask; },()=>snapshot!=null&&(modulePage+1)*100<AllModules().Count()); PreviousModulesCommand=Command(()=>{ modulePage--; Project(); return Task.CompletedTask; },()=>modulePage>0);
            ResetConnection();
        }
        private DashboardSession Runtime=>owner.Session.Dashboard;
        public bool IsIdle=>owner.IsIdle&&!owner.IsClosing;
        private UiCommand Command(Func<Task> action,Func<bool> enabled=null) { var c=new UiCommand(action,()=>owner.IsIdle&&!owner.IsClosing&&Runtime!=null&&(enabled?.Invoke()??true)); commands.Add(c); return c; }
        private void Input(ref string field,string value,string property) { if(!owner.IsIdle) return; if(property==nameof(EnvironmentId)&&!string.IsNullOrWhiteSpace(value)&&!Environments.Contains(value)) Environments.Add(value); if(Set(ref field,value,property)) Invalidate(); }
        public string EnvironmentId { get=>environment; set=>Input(ref environment,value,nameof(EnvironmentId)); }
        public string DatabaseIdentity { get=>database; set=>Input(ref database,value,nameof(DatabaseIdentity)); }
        public string Projects { get=>projects; set=>Input(ref projects,value,nameof(Projects)); }
        public string FolderInput { get=>folderInput; set=>Set(ref folderInput,value); }
        public string LinkModules { get=>linkModules; set=>Input(ref linkModules,value,nameof(LinkModules)); }
        public string SetFolder { get=>setFolder; set=>Input(ref setFolder,value,nameof(SetFolder)); }
        public string DefinitionIdentity { get=>definition; set=>Input(ref definition,value,nameof(DefinitionIdentity)); }
        public string SetIdentity { get=>setId; set=>Input(ref setId,value,nameof(SetIdentity)); }
        public string ProjectFilter { get=>projectFilter; set=>Input(ref projectFilter,value,nameof(ProjectFilter)); }
        public string FolderFilter { get=>folderFilter; set=>Input(ref folderFilter,value,nameof(FolderFilter)); }
        public string RequirementType { get=>typeFilter; set=>Input(ref typeFilter,value,nameof(RequirementType)); }
        public string RequirementStatus { get=>statusFilter; set=>Input(ref statusFilter,value,nameof(RequirementStatus)); }
        public string Criticality { get=>criticality; set=>Input(ref criticality,value,nameof(Criticality)); }
        public string FindingSeverity { get=>severity; set=>Input(ref severity,value,nameof(FindingSeverity)); }
        public string BaselineStatus { get=>baselineStatus; set=>Input(ref baselineStatus,value,nameof(BaselineStatus)); }
        public string ReviewStatus { get=>reviewStatus; set=>Input(ref reviewStatus,value,nameof(ReviewStatus)); }
        public string AnalysisFrom { get=>from; set=>Input(ref from,value,nameof(AnalysisFrom)); }
        public string AnalysisTo { get=>to; set=>Input(ref to,value,nameof(AnalysisTo)); }
        public string ProfileName { get=>profile; set=>Set(ref profile,value); }
        public string CacheTtlSeconds { get=>ttl; set=>Input(ref ttl,value,nameof(CacheTtlSeconds)); }
        public string AnalysisMaxAgeSeconds { get=>analysisAge; set=>Input(ref analysisAge,value,nameof(AnalysisMaxAgeSeconds)); }
        public string TraceabilityPolicyVersion { get=>traceVersion; set=>Input(ref traceVersion,value,nameof(TraceabilityPolicyVersion)); }
        public string QualityProfileVersion { get=>qualityVersion; set=>Input(ref qualityVersion,value,nameof(QualityProfileVersion)); }
        public string QualityStandard { get=>qualityStandard; set=>Input(ref qualityStandard,value,nameof(QualityStandard)); }
        public string TraceabilityPolicyJson { get=>traceJson; set=>Input(ref traceJson,value,nameof(TraceabilityPolicyJson)); }
        public string QualityProfileJson { get=>qualityJson; set=>Input(ref qualityJson,value,nameof(QualityProfileJson)); }
        public string PolicyFile { get=>policyFile; set=>Set(ref policyFile,value); }
        public string QualityFile { get=>qualityFile; set=>Set(ref qualityFile,value); }
        public string ScoreWeightsJson { get=>scoreWeights; set=>Input(ref scoreWeights,value,nameof(ScoreWeightsJson)); }
        public bool ScoreEnabled { get=>score; set { if(owner.IsIdle&&Set(ref score,value)) Invalidate(); } }
        public bool FailedJobsOnly { get=>failedOnly; set { if(owner.IsIdle&&Set(ref failedOnly,value)) Invalidate(); } }
        public DashboardScopeKind ScopeKind { get=>scopeKind; set { if(owner.IsIdle&&Set(ref scopeKind,value)) Invalidate(); } }
        public DashboardSections RefreshSection { get=>refreshSection; set=>Set(ref refreshSection,value); }
        public DashboardReportKind ReportKind { get=>reportKind; set=>Set(ref reportKind,value); }
        public ExportFormat Format { get=>format; set=>Set(ref format,value); }
        public string ExportRoot { get=>exportRoot; set=>Set(ref exportRoot,value); }
        public string Details { get=>details; private set=>Set(ref details,value); }
        public string Message { get=>message; private set=>Set(ref message,value); }
        public string Banner=>owner.ProfileDescription+" · CONFIGURATION DASHBOARD · ALWAYS READ-ONLY";
        // Keep the collection stable: replacing ItemsSource can clear a two-way SelectedItem during a session change.
        public ObservableCollection<string> Environments { get; }=new ObservableCollection<string>();
        public string AsOf=>snapshot==null?"No snapshot":snapshot.CollectedAtUtc.ToString("O")+" · "+snapshot.DataQuality.Completeness+(snapshot.DataQuality.IsStale?" · STALE":"")+" · "+snapshot.SnapshotId.ToString("N");
        public string Warnings=>snapshot==null?"No data. A zero is not inferred from an unavailable source.":string.Join("\n",snapshot.DataQuality.Warnings.Distinct());
        public string ScoreSummary=>snapshot==null?"Score unavailable":snapshot.Summary.ScoreStatus+" · "+(snapshot.Summary.ConfigurationHealthScore?.ToString("0.##",CultureInfo.InvariantCulture)??"Unavailable")+"\n"+string.Join("\n",snapshot.Summary.ScoreExplanation);
        public string Performance=>snapshot==null?"":string.Format(CultureInfo.InvariantCulture,"Refresh {0:0.0} ms · aggregate {1:0.0} ms · snapshot load {2:0.0} ms · peak process WS {3:N0} bytes · service reads {4} · COM member attempts {5} · cache hit ratio {6:P0}",snapshot.RefreshMilliseconds,snapshot.AggregationMilliseconds,refresh?.SnapshotLoadMilliseconds??0,snapshot.PeakWorkingSetBytes,snapshot.ServiceReadCalls,snapshot.ComCallCount?.ToString(CultureInfo.InvariantCulture)??"Unknown",refresh?.CacheHitRatio??0);
        public DashboardSnapshot CurrentSnapshot=>snapshot;
        public ObservableCollection<DashboardFolderChoice> Folders { get; }
        public ObservableCollection<DashboardModuleChoice> ModuleChoices { get; }
        public IReadOnlyList<DashboardKpiCard> Kpis=>kpis;
        public IReadOnlyList<DashboardFinding> Findings=>findings;
        public IReadOnlyList<DashboardModuleHealth> Modules=>modules;
        public IEnumerable<DashboardMetric> Metrics=>snapshot?.Summary.Metrics??Enumerable.Empty<DashboardMetric>();
        public IEnumerable<DashboardChartPoint> Charts=>snapshot?.Sections.SelectMany(s=>s.Charts)??Enumerable.Empty<DashboardChartPoint>();
        public IEnumerable<DashboardChartPoint> TraceCharts=>Charts.Where(c=>c.Group.StartsWith("Coverage",StringComparison.Ordinal));
        public IEnumerable<DashboardChartPoint> QualityCharts=>Charts.Where(c=>c.Group.StartsWith("Quality",StringComparison.Ordinal));
        public IEnumerable<DashboardChartPoint> ImpactCharts=>Charts.Where(c=>c.Group.StartsWith("Impact",StringComparison.Ordinal));
        public IEnumerable<DashboardChartPoint> BaselineCharts=>Charts.Where(c=>c.Group=="Baseline status");
        public IEnumerable<DashboardAuditEntry> Activity=>snapshot?.Sections.Where(s=>s.Audit!=null).SelectMany(s=>s.Audit.Recent)??Enumerable.Empty<DashboardAuditEntry>();
        public IEnumerable<DashboardTrendPoint> Trends=>snapshot?.Trends??Enumerable.Empty<DashboardTrendPoint>();
        public IReadOnlyList<DashboardSnapshotHeader> History=>history;
        public DashboardFinding SelectedFinding { get=>selectedFinding; set { if(Set(ref selectedFinding,value)) { Details=value==null?"Select a finding.":value.Category+" / "+value.Id+"\nModule="+value.ModuleIdentity+"\nRequirement="+value.RequirementIdentity+"\nVersion="+value.RequirementVersionIdentity+"\nAnalysis="+value.AnalysisIdentity+"\n"+value.Explanation+"\n"+value.Evidence; RefreshCommands(); } } }
        public DashboardModuleHealth SelectedModule { get=>selectedModule; set { if(Set(ref selectedModule,value)) { Details=value==null?"Select a module.":value.ModuleIdentity+"\n"+value.ModulePath+"\n"+value.Version?.Version+"\n"+value.Evidence; RefreshCommands(); } } }
        public DashboardAuditEntry SelectedActivity { get=>selectedActivity; set { if(Set(ref selectedActivity,value)) { Details=value==null?"Select an activity.":Encoding.UTF8.GetString(DashboardSerialization.Bytes(value)); RefreshCommands(); } } }
        public DashboardSnapshotHeader SelectedHistory { get=>selectedHistory; set { Set(ref selectedHistory,value); RefreshCommands(); } }
        public string PageSummary=>"Modules page "+(modulePage+1)+" / 100 per page; findings page "+(findingPage+1)+" / 100 per page. Full aggregate counts remain in KPI/report.";
        public Array ScopeKinds=>Enum.GetValues(typeof(DashboardScopeKind)); public DashboardSections[] RefreshSections=>new[]{DashboardSections.All,DashboardSections.Overview,DashboardSections.Baseline,DashboardSections.Traceability,DashboardSections.Quality,DashboardSections.Impact,DashboardSections.Audit};
        public string[] Severities=>new[]{""}.Concat(Enum.GetNames(typeof(DashboardSeverity))).ToArray(); public string[] BaselineStates=>new[]{""}.Concat(Enum.GetNames(typeof(DashboardBaselineState))).ToArray();
        public string[] ReviewStates=>new[]{"","NotReviewed","Pending","InReview","ConfirmedImpacted","NotImpacted","NeedsInvestigation","AcceptedRisk","Accepted","Rejected","Waived","NeedsClarification","NotApplicable"};
        public string[] QualityStandards=>new[]{"General","DO178C","DO200B","DO330"}; public Array ExportFormats=>Enum.GetValues(typeof(ExportFormat)); public Array ReportKinds=>Enum.GetValues(typeof(DashboardReportKind));
        public UiCommand LoadCachedCommand { get; } public UiCommand RefreshCommand { get; } public UiCommand RunAnalysisCommand { get; } public UiCommand InvalidateCommand { get; } public UiCommand SaveProfileCommand { get; } public UiCommand LoadProfileCommand { get; } public UiCommand LoadHistoryCommand { get; } public UiCommand OpenHistoryCommand { get; } public UiCommand AddFolderCommand { get; } public UiCommand ClearFoldersCommand { get; } public UiCommand ObserveEnvironmentCommand { get; } public UiCommand UseWorkspaceModuleCommand { get; } public UiCommand LoadPolicyCommand { get; } public UiCommand LoadQualityCommand { get; } public UiCommand ExportCommand { get; } public UiCommand OpenFindingCommand { get; } public UiCommand OpenModuleCommand { get; } public UiCommand CompareModuleCommand { get; } public UiCommand BulkModuleCommand { get; } public UiCommand OpenActivityCommand { get; } public UiCommand NextFindingsCommand { get; } public UiCommand PreviousFindingsCommand { get; } public UiCommand NextModulesCommand { get; } public UiCommand PreviousModulesCommand { get; }
        public void RefreshCommands() { foreach(var c in commands) c.Refresh(); foreach(var card in kpis) card.OpenCommand.Refresh(); Changed(nameof(Banner)); Changed(nameof(IsIdle)); }
        public void Invalidate() { if(loadingInputs) return; snapshot=null; refresh=null; findingPage=modulePage=0; selectedFinding=null; selectedModule=null; selectedActivity=null; Message="Inputs changed; all displayed sections cleared. Load a matching snapshot or refresh."; Project(); }
        public void ResetConnection() { if(Runtime!=null) Apply(Runtime.DefaultRequest); else Invalidate(); }
        private void Apply(DashboardRequest r)
        {
            loadingInputs=true;
            try { environment=r.Environment; database=r.DatabaseIdentity; projects=string.Join(";",r.Scope.Projects); scopeKind=r.Scope.Kind; linkModules=string.Join(";",r.LinkModulePaths); setFolder=r.Scope.SetFolder??"/Traceability"; definition=r.Scope.DefinitionIdentity??MockBaselineSetVerificationAdapter.DemoDefinitionId; setId=r.Scope.SetIdentity??MockBaselineSetVerificationAdapter.DemoSetId; ttl=r.CacheTtlSeconds.ToString(CultureInfo.InvariantCulture); analysisAge=r.MaximumAnalysisAgeSeconds.ToString(CultureInfo.InvariantCulture); traceVersion=r.TraceabilityPolicyVersion; qualityVersion=r.QualityProfileVersion; qualityStandard=r.QualityStandard; traceJson=r.TraceabilityPolicyJson; qualityJson=r.QualityProfileJson; score=r.Score.Enabled; scoreWeights=Encoding.UTF8.GetString(DashboardSerialization.Bytes(r.Score.Weights)); projectFilter=r.Filters.Project; folderFilter=r.Filters.Folder; typeFilter=r.Filters.RequirementType; statusFilter=r.Filters.RequirementStatus; criticality=r.Filters.Criticality; severity=r.Filters.FindingSeverity?.ToString()??""; baselineStatus=r.Filters.BaselineStatus?.ToString()??""; reviewStatus=r.Filters.ReviewStatus; from=r.Filters.AnalysisFromUtc?.ToString("O")??""; to=r.Filters.AnalysisToUtc?.ToString("O")??""; failedOnly=r.Filters.FailedJobsOnly;
                Folders.Clear(); foreach(var f in r.Scope.Folders) Folders.Add(new DashboardFolderChoice(Invalidate,f.FullPath){IncludeSubfolders=f.IncludeSubfolders}); ModuleChoices.Clear(); foreach(var v in r.Scope.Modules) ModuleChoices.Add(new DashboardModuleChoice(v,Invalidate));
            } finally { loadingInputs=false; }
            if(!Environments.Contains(environment)) Environments.Add(environment); foreach(var p in GetType().GetProperties().Where(p=>p.PropertyType==typeof(string)||p.Name==nameof(ScopeKind)||p.Name==nameof(ScoreEnabled)||p.Name==nameof(FailedJobsOnly))) Changed(p.Name); Invalidate();
        }
        public DashboardRequest BuildRequest()
        {
            var r=DashboardSerialization.Copy(Runtime.DefaultRequest); r.Environment=EnvironmentId; r.DatabaseIdentity=DatabaseIdentity; r.Scope=new DashboardScope { Kind=ScopeKind,Projects=Split(Projects).ToList(),Folders=Folders.Select(f=>new DashboardFolderSelection { FullPath=f.FullPath,IncludeSubfolders=f.IncludeSubfolders }).ToList(),Modules=ScopeKind==DashboardScopeKind.SelectedFormalModules||ScopeKind==DashboardScopeKind.SelectedBaselineSnapshot?ModuleChoices.Where(m=>m.Selected).Select(m=>m.Version()).ToList():new List<BaselineVersionReference>(),SetFolder=SetFolder,DefinitionIdentity=DefinitionIdentity,SetIdentity=SetIdentity };
            r.LinkModulePaths=Split(LinkModules).ToList(); r.CacheTtlSeconds=int.Parse(CacheTtlSeconds,CultureInfo.InvariantCulture); r.MaximumAnalysisAgeSeconds=int.Parse(AnalysisMaxAgeSeconds,CultureInfo.InvariantCulture); r.TraceabilityPolicyVersion=TraceabilityPolicyVersion; r.QualityProfileVersion=QualityProfileVersion; r.QualityStandard=QualityStandard; r.TraceabilityPolicyJson=TraceabilityPolicyJson; r.QualityProfileJson=QualityProfileJson;
            r.Filters=new DashboardFilters { Project=ProjectFilter,Folder=FolderFilter,RequirementType=RequirementType,RequirementStatus=RequirementStatus,Criticality=Criticality,FindingSeverity=string.IsNullOrEmpty(FindingSeverity)?(DashboardSeverity?)null:(DashboardSeverity)Enum.Parse(typeof(DashboardSeverity),FindingSeverity),BaselineStatus=string.IsNullOrEmpty(BaselineStatus)?(DashboardBaselineState?)null:(DashboardBaselineState)Enum.Parse(typeof(DashboardBaselineState),BaselineStatus),ReviewStatus=ReviewStatus,AnalysisFromUtc=Utc(AnalysisFrom),AnalysisToUtc=Utc(AnalysisTo),FailedJobsOnly=FailedJobsOnly };
            r.Score=new DashboardScorePolicy { Enabled=ScoreEnabled,Weights=DashboardSerialization.Read<Dictionary<string,decimal>>(Encoding.UTF8.GetBytes(ScoreWeightsJson)) }; r.Validate(); return r;
        }
        private static string[] Split(string value)=>(value??"").Split(new[]{';','\r','\n'},StringSplitOptions.RemoveEmptyEntries).Select(s=>s.Trim()).Where(s=>s.Length>0).ToArray();
        private static DateTime? Utc(string value)=>string.IsNullOrWhiteSpace(value)?(DateTime?)null:DateTime.Parse(value,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind).ToUniversalTime();
        private Task Refresh(DashboardRefreshMode mode)
        {
            var runtime=Runtime; var section=RefreshSection; Exception invalid=null; DashboardRequest request=null; try { request=BuildRequest(); } catch(Exception ex) { invalid=ex; }
            return owner.RunToolAsync(mode==DashboardRefreshMode.RunNewAnalysis?"Dashboard: explicit read-only analysis":"Dashboard: snapshot / existing result refresh",async(token,progress)=>
            {
                if(invalid!=null) throw invalid; runtime.Bind(request); var result=await runtime.Service.RefreshAsync(request,section,mode,progress,token).ConfigureAwait(false);
                return (Action)(()=>{ refresh=result; snapshot=result.Snapshot; Message=result.Status+(result.Error==null?"":" · "+result.Error); findingPage=modulePage=0; Project(); });
            });
        }
        private IEnumerable<DashboardFinding> AllFindings()=>snapshot?.Sections.SelectMany(s=>s.Findings)??Enumerable.Empty<DashboardFinding>();
        private IEnumerable<DashboardModuleHealth> AllModules()=>snapshot?.Sections.Where(s=>s.Baseline!=null).SelectMany(s=>s.Baseline.Modules)??Enumerable.Empty<DashboardModuleHealth>();
        private void Project()
        {
            findings=AllFindings().Skip(findingPage*100).Take(100).ToArray(); modules=AllModules().Skip(modulePage*100).Take(100).ToArray();
            var ids=new[]{"FormalModules","Requirements","Baselines","Coverage","OpenFindings","PendingReviews"}; kpis=ids.Select(id=>snapshot?.Summary.Metrics.SingleOrDefault(m=>m.Id==id)).Where(m=>m!=null).Select(m=>new DashboardKpiCard { Metric=m,OpenCommand=new UiCommand(()=>Navigate(m.Destination),()=>owner.IsIdle&&snapshot!=null) }).ToArray();
            foreach(string name in new[]{nameof(CurrentSnapshot),nameof(Kpis),nameof(Findings),nameof(Modules),nameof(Metrics),nameof(Charts),nameof(TraceCharts),nameof(QualityCharts),nameof(ImpactCharts),nameof(BaselineCharts),nameof(Activity),nameof(Trends),nameof(AsOf),nameof(Warnings),nameof(ScoreSummary),nameof(Performance),nameof(PageSummary),nameof(SelectedFinding),nameof(SelectedModule),nameof(SelectedActivity)}) Changed(name); RefreshCommands();
        }
        // Capture dispatcher-owned inputs before RunToolAsync dispatches work. Validation errors still use the central error handler.
        private Task WithRequest(string description,Func<DashboardRequest,System.Threading.CancellationToken,Task<Action>> work)
        {
            DashboardRequest request=null; Exception invalid=null; try { request=BuildRequest(); } catch(Exception ex) { invalid=ex; }
            return owner.RunToolAsync(description,(token,progress)=>{ if(invalid!=null) throw invalid; token.ThrowIfCancellationRequested(); return work(request,token); });
        }
        private Task InvalidateCache()=>WithRequest("Invalidate dashboard cache",(request,token)=>{ Runtime.Cache.Invalidate(request.Key,Runtime.Clock.UtcNow); return Task.FromResult<Action>(()=>{ Invalidate(); Message="Cache invalidated; snapshot history retained."; }); });
        private Task SaveProfile() { string name=ProfileName; return WithRequest("Save dashboard scope profile",(request,token)=>{ Runtime.Snapshots.SaveProfile(name,request); return Task.FromResult<Action>(()=>Message="Scope/filter/policy profile saved locally."); }); }
        private Task LoadProfile() { string name=ProfileName; var mode=owner.Mode; var version=owner.Version; return owner.RunToolAsync("Load dashboard scope profile",(token,progress)=>{ token.ThrowIfCancellationRequested(); var r=Runtime.Snapshots.LoadProfile(name); if(r.IsMock!=(mode==ClientMode.Mock)||r.ClientVersion!=version) throw new ArgumentException("Profile source mode/release differs; choose the corresponding connection first."); return Task.FromResult<Action>(()=>Apply(r)); }); }
        private Task LoadHistory()=>WithRequest("Dashboard snapshot history",(request,token)=>{ var list=Runtime.Snapshots.History(request.Key); return Task.FromResult<Action>(()=>{ history=list; Changed(nameof(History)); }); });
        private Task OpenHistory() { var id=SelectedHistory.SnapshotId; return WithRequest("Open historical dashboard snapshot",(request,token)=>{ var old=Runtime.Snapshots.Load(request.Key,id)??throw new InvalidOperationException("Snapshot unavailable."); new DashboardMetricsCalculator().Finalize(old,Runtime.Clock.UtcNow); return Task.FromResult<Action>(()=>{ snapshot=old; Message="Historical snapshot; not a live observation."; Project(); }); }); }
        private Task ObserveEnvironment()=>owner.RunToolAsync("Observe dashboard DOORS environment",async(token,progress)=>{ var observed=await owner.Session.NativeReads.ReadAsync(new DoorsIntegration.Dxl.NativeReadQuery(owner.Version),token).ConfigureAwait(false); return (Action)(()=>{ EnvironmentId=observed.DatabaseId; DatabaseIdentity=observed.RepositoryId; Message="Observed "+observed.ClientVersion+" / session "+observed.SessionId+". No data changed."; }); });
        private Task UseWorkspaceModule()
        {
            var summary=owner.RequirementSnapshot.Summary; var b=summary.Request.Baseline; var v=new BaselineVersionReference(DatabaseIdentity,summary.QualifiedModuleId,summary.Request.ModulePath,b?.Major??0,b?.Minor??0,b?.Suffix??"",b==null); ModuleChoices.Add(new DashboardModuleChoice(v,Invalidate)); ScopeKind=b==null?DashboardScopeKind.SelectedFormalModules:DashboardScopeKind.SelectedBaselineSnapshot; Invalidate(); return Task.CompletedTask;
        }
        private Task LoadFile(bool quality) { string path=quality?QualityFile:PolicyFile; return owner.RunToolAsync("Load dashboard policy/profile",(token,progress)=>{ token.ThrowIfCancellationRequested(); if(new FileInfo(path).Length>131072) throw new ArgumentException("Policy/profile exceeds 128 KiB."); string text=File.ReadAllText(path,new UTF8Encoding(false,true)); return Task.FromResult<Action>(()=>{ if(quality) QualityProfileJson=text; else TraceabilityPolicyJson=text; }); }); }
        private Task Export()
        { var current=snapshot; string destination=Path.Combine(ExportRoot,"dashboard-"+Guid.NewGuid().ToString("N")); var request=new ExportRequest(destination,Format); var kind=ReportKind; var root=ExportRoot; return owner.RunToolAsync("Export dashboard report",async(token,progress)=>{ Directory.CreateDirectory(root); var result=await Runtime.Reports.ExportAsync(current,kind,request,token:token).ConfigureAwait(false); return (Action)(()=>Message=result.Status+" · "+result.DirectoryPath); }); }
        private Task Navigate(DashboardDestination destination,string module=null,DashboardFinding finding=null,DashboardAuditEntry job=null)
        {
            var current=snapshot; var runtime=Runtime;
            return owner.RunToolAsync("Validate dashboard drill-down source",async(token,progress)=>{ var intent=runtime.Navigation.Create(current,destination,module,finding,job); await runtime.Navigation.ValidateAsync(current,intent,token).ConfigureAwait(false); IRequirementSnapshot read=null; if(intent.ModuleIdentity!=null&&destination==DashboardDestination.ModuleExplorer) read=await runtime.Provider.OpenRequirementAsync(intent,token).ConfigureAwait(false); Tuple<List<RequirementListRow>,int> page=null; try { if(read!=null) page=owner.ProjectDashboardRequirements(read,intent.AbsoluteNumber,token); return (Action)(()=>owner.ApplyDashboardNavigation(intent,read,page)); } catch { read?.Dispose(); throw; } });
        }
        internal Task OpenSourceModule(string moduleIdentity)=>Navigate(DashboardDestination.ModuleExplorer,moduleIdentity);
    }
}
