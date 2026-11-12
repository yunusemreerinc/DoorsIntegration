using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;
using DoorsIntegration.Wpf.Services;

namespace DoorsIntegration.Wpf.ViewModels
{
    public sealed partial class WorkspaceViewModel
    {
        public RequirementGoToViewModel GoTo { get; }
        public event Action<RequirementListRow> GoToSelectionRequested;
        internal string SnapshotScopeDescription=>requirements==null?"Önce bir modül okuyun.":requirements.Summary.Request.ModulePath+" · "+(requirements.Summary.Request.Baseline==null?"Current":"Baseline "+requirements.Summary.Request.Baseline.Major+"."+requirements.Summary.Request.Baseline.Minor+" ["+requirements.Summary.Request.Baseline.Suffix+"]");
        internal Task GoToInSnapshotAsync(RequirementGoToKind kind,string text,bool clearFilters)
        {
            var snapshot=requirements; string quickFilter=clearFilters?"":Search; var objectFilter=clearFilters?RequirementFilter.All:Filter;
            return RunAsync("Go To: hedef aranıyor…",(token,progress)=>
            {
                var request=new RequirementGoToRequest(kind,text);
                var result=new RequirementGoToService().Locate(snapshot.ReadObjects(),request,r=>InWorkspaceView(r,quickFilter,objectFilter),token,progress);
                if(result.Status!=RequirementGoToStatus.Found)
                {
                    string message=result.Status==RequirementGoToStatus.HiddenByFilter?"Nesne mevcut filtrelerde gizli. ‘Filtreleri temizleyerek git’ seçeneğini kullanabilirsiniz.":
                        result.Status==RequirementGoToStatus.Ambiguous?"Hedef birden fazla nesneyle eşleşiyor; Absolute Number kullanın.":
                        "Hedef okunmuş snapshot içinde bulunamadı. Okunmamış veya erişilemeyen nesnelere gidilmez.";
                    if(result.Status==RequirementGoToStatus.NotFound&&result.NumberingUnavailable) message+=" Bazı Object Number değerleri okunabilir değil.";
                    return Task.FromResult<Action>(()=> { GoTo.Report(message); Status=message; });
                }
                var select=PrepareSnapshotSelection(snapshot,result.Record,result.ViewIndex,null,quickFilter,objectFilter,token,progress);
                return Task.FromResult<Action>(()=>
                {
                    if(clearFilters) { search=""; filter=RequirementFilter.All; Changed(nameof(Search)); Changed(nameof(Filter)); }
                    select(); string message="Gidildi: "+result.Record.ObjectIdentifier+" · Absolute Number "+result.Record.AbsoluteNumber+" · Object Number "+result.Record.ObjectNumber;
                    if(clearFilters) message+=" · Görünüm filtreleri temizlendi.";
                    GoTo.Report(message); Status=message; GoToSelectionRequested?.Invoke(selectedRequirement);
                });
            });
        }
        // Shared Find/Go To page, attributes and retained-link projection. No native read or write.
        private Action PrepareSnapshotSelection(IRequirementSnapshot snapshot,RequirementRecord record,int viewIndex,string field,
            string quickFilter,RequirementFilter objectFilter,CancellationToken token,IProgress<string> progress)
        {
            int targetPage=viewIndex/PageSize; var projection=ReadPage(snapshot,targetPage,quickFilter,objectFilter,token,progress);
            var values=record.Values.Values.Select(v=>new AttributeDetailRow(v)).ToArray(); var relatedRows=new List<LinkDetailRow>(); var retainedLinks=links;
            if(retainedLinks!=null) foreach(var link in retainedLinks.ReadLinks())
            {
                token.ThrowIfCancellationRequested();
                if((link.Source.ModulePath==snapshot.Summary.Request.ModulePath&&link.Source.AbsoluteNumber==record.AbsoluteNumber)||(link.Target.ModulePath==snapshot.Summary.Request.ModulePath&&link.Target.AbsoluteNumber==record.AbsoluteNumber))
                    if(relatedRows.Count<100) relatedRows.Add(new LinkDetailRow(link));
            }
            token.ThrowIfCancellationRequested();
            return ()=> { ApplyPage(projection,targetPage); selectedRequirement=Requirements.Single(r=>r.Id==record.AbsoluteNumber); Changed(nameof(SelectedRequirement)); Attributes=values; SelectedAttribute=values.FirstOrDefault(v=>v.Name==field)??values.FirstOrDefault(); RelatedLinks=relatedRows; };
        }
    }
}
