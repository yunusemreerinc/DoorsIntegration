using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;

namespace DoorsIntegration.ConfigurationManagement
{
    // In-memory read adapter over an already captured, typed Mock/analysis snapshot.
    // It never replaces native baseline endpoints with Current versions.
    public sealed class DashboardSnapshotLinkReader : ILinkReader
    {
        private readonly ImpactAnalysisSnapshot snapshot;
        public DashboardSnapshotLinkReader(ImpactAnalysisSnapshot snapshot) { this.snapshot=snapshot; }
        public Task<ILinkSnapshot> ReadAsync(LinkReadRequest request,CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); var anchor=snapshot.Links.Context.Modules.Single(m=>m.FullPath==request.ModulePath&&Same(m.Version,request.Baseline));
            var rows=snapshot.Links.Links.Where(l=>l.LinkModulePath==request.LinkModulePath&&(l.SourceVersion?.ModuleBaselineIdentity==anchor.Version.ModuleBaselineIdentity||l.TargetVersion?.ModuleBaselineIdentity==anchor.Version.ModuleBaselineIdentity)).ToArray();
            return Task.FromResult<ILinkSnapshot>(new View(request,anchor,rows,snapshot.Links.Context));
        }
        private static bool Same(BaselineVersionReference v,RequirementBaseline b) => v.IsCurrent?b==null:b!=null&&v.Major==b.Major&&v.Minor==b.Minor&&v.Suffix==b.Suffix;
        private sealed class View : ILinkSnapshot
        {
            private readonly List<LinkRecord> rows=new List<LinkRecord>();
            public View(LinkReadRequest request,ModuleBaselineVerificationResult anchor,BaselineLinkInfo[] links,BaselineVerificationContext context)
            {
                foreach(var link in links)
                {
                    var direction=link.SourceVersion?.ModuleBaselineIdentity==anchor.Version.ModuleBaselineIdentity?LinkDirection.Outgoing:LinkDirection.Incoming;
                    if(request.Direction!=LinkDirection.Both&&direction!=request.Direction) continue;
                    rows.Add(new LinkRecord(rows.Count+1,direction,LinkRecordKind.Link,link.SourceAccess==BaselineVerificationReadState.Verified&&link.TargetAccess==BaselineVerificationReadState.Verified?LinkResolution.Resolved:LinkResolution.EndpointUnresolved,link.IsEchoed,link.LinkModulePath,LinkModuleResolution.Verified,Endpoint(link.SourceVersion,link.SourcePath,link.SourceObject,link.SourceAccess),Endpoint(link.TargetVersion,link.TargetPath,link.TargetObject,link.TargetAccess)));
                }
                context.LinkModuleIdentities.TryGetValue(request.LinkModulePath,out string linkId);
                Summary=new LinkReadSummary(Guid.NewGuid(),request,RequirementModuleStatus.Available,LinkModuleStatus.Available,RequirementModuleOwnership.BorrowedReadOnly,rows.Count,anchor.KnownObjectCount??rows.Count,0,0,rows.Count(l=>l.Resolution!=LinkResolution.Resolved),0,false,false,0,0,0,qualifiedAnchorModuleId:anchor.ModuleIdentity,qualifiedLinkModuleId:linkId);
            }
            private static LinkEndpoint Endpoint(BaselineVersionReference v,string path,int? number,BaselineVerificationReadState state) => new LinkEndpoint(path??v?.FullPath??"/Unknown",v==null?LinkVersionKind.Unknown:v.IsCurrent?LinkVersionKind.Current:LinkVersionKind.Baseline,v?.IsCurrent==false?v.Version:"",number,"",state==BaselineVerificationReadState.Verified?LinkEndpointResolution.Resolved:state==BaselineVerificationReadState.AccessDenied?LinkEndpointResolution.AccessDenied:LinkEndpointResolution.UnresolvedObject);
            public LinkReadSummary Summary { get; }
            public IEnumerable<LinkRecord> ReadLinks() => rows;
            public Stream OpenRawStream() => throw new NotSupportedException();
            public void Dispose() { }
        }
    }
}
