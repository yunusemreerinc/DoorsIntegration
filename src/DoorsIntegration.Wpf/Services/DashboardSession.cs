using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.ConfigurationManagement;
using DoorsIntegration.Core;
using DoorsIntegration.Dxl;

namespace DoorsIntegration.Wpf.Services
{
    public sealed class DashboardSession
    {
        public IConfigurationDashboardService Service { get; private set; }
        public DashboardSnapshotService Snapshots { get; private set; }
        public DashboardCacheService Cache { get; private set; }
        public DashboardNavigationService Navigation { get; private set; }
        public ServiceDashboardDataProvider Provider { get; private set; }
        public IDashboardAnalysisRepository Analyses { get; private set; }
        public DashboardRequest DefaultRequest { get; private set; }
        public DashboardReportService Reports { get; private set; }
        public IDashboardClock Clock { get; private set; }
        private DashboardRequest lastRequest;
        public void Bind(DashboardRequest request) { lastRequest=DashboardSerialization.Copy(request); }
        internal static DashboardSession Create(ClientProfile profile,ClientSession client,IIntegrationLogger logger,string storageRoot,IBaselineJournalProtector protector=null,MockDoorsAdapter mock=null)
        {
            protector=protector??new WindowsBaselineJournalProtector();
            string root=Path.Combine(storageRoot??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DoorsIntegration"),"Dashboard",profile.Mode.ToString(),DoorsVersions.Format(profile.Version));
            var session=new DashboardSession { Clock=new DashboardClock() }; session.Snapshots=new DashboardSnapshotService(Path.Combine(root,"cache"),protector); session.Cache=new DashboardCacheService(session.Snapshots); session.Analyses=new DashboardAnalysisRepository(Path.Combine(root,"analyses"),protector);
            var ports=new DashboardReadPorts { Projects=client.Projects,Modules=client.Modules,Requirements=client.Requirements,Metadata=client.BaselineMetadata,Graphs=client.CreateGraphService(logger),Verification=client.BaselineVerification?.Service,Impact=client.ChangeImpact?.Service,Reviews=client.ChangeImpact?.Reviews,NativeSets=client.NativeBaselineSets?.Service,SetMetadata=client.NativeBaselineSets?.ReadAdapter,Jobs=client.BaselineManager?.JobHistory,NativeObservation=client.NativeReads,ComCallAttempts=client.DashboardComCalls };
            if(profile.Mode==ClientMode.Mock)
            {
                mock=mock??new MockDoorsAdapter(); var context=mock.ChangeImpactSnapshots.Before.Links.Context;
                session.DefaultRequest=new DashboardRequest { Environment=context.EnvironmentId,DatabaseIdentity=context.RepositoryId,IsMock=true,ClientVersion=profile.Version,Scope=new DashboardScope { Kind=DashboardScopeKind.SelectedBaselineSnapshot,Projects=new List<string>{"/Traceability"},Modules=context.Modules.Select(m=>m.Version).ToList() },LinkModulePaths=new List<string>{MockBaselineSetVerificationAdapter.DemoLinkPath} };
                var adapter=mock;
                ports.MockSnapshot=async (request,token)=>
                {
                    if(request.Scope.Kind==DashboardScopeKind.SelectedBaselineSet)
                    {
                        var read=await client.BaselineVerification.Snapshots.CaptureAsync(new BaselineSetVerificationRequest("dashboard",request.Scope.SetFolder,request.Scope.DefinitionIdentity,request.Scope.SetIdentity,MockBaselineSetVerificationAdapter.DemoPolicy(),request.LinkModulePaths,target:request.ClientVersion),token:token).ConfigureAwait(false);
                        return MockChangeImpactSnapshotProvider.Convert(read);
                    }
                    var selection=new ImpactSnapshotSelection { Label="Dashboard Mock snapshot",MockSnapshotKey=request.Scope.Kind==DashboardScopeKind.SelectedBaselineSnapshot||request.Scope.Kind==DashboardScopeKind.SelectedBaselineSet?"A":"B",EnvironmentId=request.Environment,RepositoryId=request.DatabaseIdentity,ClientVersion=request.ClientVersion,LinkModulePaths=request.LinkModulePaths.ToList() };
                    selection.Modules=request.Scope.Modules.Count>0?request.Scope.Modules.ToList():request.Scope.Kind==DashboardScopeKind.SelectedBaselineSet?adapter.ChangeImpactSnapshots.Before.Links.Context.Modules.Select(m=>m.Version).ToList():adapter.ChangeImpactSnapshots.Before.Links.Context.Modules.Select(m=>new BaselineVersionReference(m.Version.RepositoryId,m.ModuleIdentity,m.FullPath,0,0,isCurrent:true)).ToList();
                    return await adapter.CaptureAsync(selection,new ImpactAnalysisPolicy(),null,token).ConfigureAwait(false);
                };
                var states=context.Modules.Select(m=>{
                    var module=new BaselineModuleReference(context.RepositoryId,m.ModuleIdentity,m.FullPath); var v=new BaselineVersion(m.Version.Major,m.Version.Minor,m.Version.Suffix);
                    return new MockBaselineModuleState(module,new[]{new BaselineHistoryEntry(new BaselineIdentity(module,v),"Explicit dashboard Mock baseline fixture","mock",DateTime.SpecifyKind(new DateTime(2026,1,1),DateTimeKind.Utc),"",BaselineExistenceStatus.Exists)},v,null,null);
                }).ToArray();
                var history=new MockBaselineHistoryAdapter(states); ports.Metadata=new BaselineMetadataService(history,history,logger); ports.SetMetadata=new MockSetReader(context);
            }
            else session.DefaultRequest=new DashboardRequest { Environment="observe-required",DatabaseIdentity="observe-required",IsMock=false,ClientVersion=profile.Version,Scope=new DashboardScope { Projects=new List<string>{"/"} } };
            session.Provider=new ServiceDashboardDataProvider(ports,session.Analyses,session.Clock,logger); var calculator=new DashboardMetricsCalculator();
            session.Service=new ConfigurationDashboardService(new DashboardRefreshCoordinator(session.Provider,session.Cache,new DashboardAggregationService(session.Clock,calculator),calculator,session.Clock)); session.Navigation=new DashboardNavigationService(session.Provider); session.Reports=new DashboardReportService(client.Export);
            // Existing UI analysis results are indexed only when bound to an explicitly used dashboard context.
            if(client.ChangeImpact!=null)
            {
                var old=client.ChangeImpact;
                client.ChangeImpact=new ChangeImpactSession(new RecordingImpact(old.Service,r=>session.RecordImpact(r),logger),old.Reviews,old.Reports);
            }
            if(client.BaselineVerification!=null)
            {
                var old=client.BaselineVerification;
                client.BaselineVerification=new BaselineVerificationSession(new RecordingVerification(old.Service,r=>session.RecordVerification(r),logger),old.Snapshots,old.Reports,old.Graphs);
            }
            return session;
        }
        private void RecordImpact(ChangeImpactResult result)
        {
            var request=lastRequest;
            if(request==null||result.Source?.Links.Context.EnvironmentId!=request.Environment||result.Source.Links.Context.RepositoryId!=request.DatabaseIdentity) return;
            var record=DashboardResultMapper.Impact(request,result,result.GraphChoice==ImpactGraphChoice.Source); record.Warnings.Add("Existing analysis policy fingerprint retained; source and target contexts remain separate from inventory as-of."); Analyses.Save(record);
        }
        private void RecordVerification(BaselineSetVerificationResult result)
        { var request=lastRequest; if(request==null||result.Snapshot?.Context.EnvironmentId!=request.Environment||result.Snapshot.Context.RepositoryId!=request.DatabaseIdentity) return; Analyses.Save(DashboardResultMapper.Verification(request,result)); }
        private sealed class MockSetReader : IBaselineSetAdapter
        {
            private readonly BaselineVerificationContext context;
            internal MockSetReader(BaselineVerificationContext context) { this.context=context; }
            public BaselineMetadataSource Source=>BaselineMetadataSource.Mock; public bool WritesValidated=>false;
            public Task<BaselineSetInspection> InspectAsync(BaselineSetRequest request,CancellationToken token)
            {
                token.ThrowIfCancellationRequested(); var set=DashboardSerialization.Copy(context.Set);
                if(request.FolderPath!=context.Definition.FolderPath) return Task.FromResult(new BaselineSetInspection { Source=Source,DatabaseId=context.EnvironmentId,RepositoryId=context.RepositoryId,Complete=true,CanRead=true,FolderExists=false,Reason="Folder is outside the explicitly seeded Mock fixture." });
                set.Members=context.Modules.Where(m=>m.InSet&&m.Version!=null).Select(m=>new BaselineSetModuleBaseline { Module=new BaselineSetModuleReference(new BaselineModuleReference(context.RepositoryId,m.ModuleIdentity,m.FullPath)),TypedVersion=m.Version,ModuleBaselineVersion=m.Version.Version,BaselineIdentity=m.Version.ModuleBaselineIdentity,Resolution="Verified" }).ToList();
                return Task.FromResult(new BaselineSetInspection { Source=Source,DatabaseId=context.EnvironmentId,RepositoryId=context.RepositoryId,UserId="Mock operator",Complete=context.MetadataComplete,CanRead=true,FolderExists=true,FolderKind=DoorsItemKind.Project,Definitions={DashboardSerialization.Copy(context.Definition)},Sets={set},Reason="Read-only projection of the existing Mock baseline-set fixture; no set was created." });
            }
            public Task<BaselineSetAdapterResult> ExecuteAsync(BaselineSetAuthorization authorization,CancellationToken token) => Task.FromResult(new BaselineSetAdapterResult(BaselineSetJobStatus.Blocked,"Dashboard Mock projection is read-only."));
        }
        private sealed class RecordingImpact : IChangeImpactAnalysisService
        {
            private readonly IChangeImpactAnalysisService inner; private readonly Action<ChangeImpactResult> record; private readonly IIntegrationLogger log;
            internal RecordingImpact(IChangeImpactAnalysisService inner,Action<ChangeImpactResult> record,IIntegrationLogger log) { this.inner=inner; this.record=record; this.log=log; }
            public async Task<ChangeImpactResult> AnalyzeAsync(ChangeImpactRequest request,IProgress<string> progress=null,CancellationToken token=default(CancellationToken))
            { var result=await inner.AnalyzeAsync(request,progress,token).ConfigureAwait(false); try { record(result); } catch(Exception) { try { log.Write(IntegrationLogLevel.Error,"DashboardIndexUnavailable",result.AnalysisId,"Analysis succeeded; dashboard index could not be written."); } catch { } } return result; }
        }
        private sealed class RecordingVerification : IBaselineSetVerificationService
        {
            private readonly IBaselineSetVerificationService inner; private readonly Action<BaselineSetVerificationResult> record; private readonly IIntegrationLogger log;
            internal RecordingVerification(IBaselineSetVerificationService inner,Action<BaselineSetVerificationResult> record,IIntegrationLogger log) { this.inner=inner; this.record=record; this.log=log; }
            public async Task<BaselineSetVerificationResult> VerifyAsync(BaselineSetVerificationRequest request,IProgress<string> progress=null,CancellationToken token=default(CancellationToken))
            { var result=await inner.VerifyAsync(request,progress,token).ConfigureAwait(false); try { record(result); } catch(Exception) { try { log.Write(IntegrationLogLevel.Error,"DashboardIndexUnavailable",result.VerificationJobId,"Verification completed; dashboard index could not be written."); } catch { } } return result; }
        }
    }
}
