using System;
using System.Threading.Tasks;
using DoorsIntegration.Wpf.Services;

namespace DoorsIntegration.Wpf.ViewModels
{
    public sealed partial class WorkspaceViewModel
    {
        public RequirementFindViewModel Find { get; }
        public event Action<RequirementListRow> FindSelectionRequested;
        internal Task FindInSnapshotAsync(RequirementFindOptions options,RequirementFindDirection direction,bool includeAnchor)
        {
            var snapshot=requirements; string quickFilter=Search; var objectFilter=Filter; int? anchor=selectedRequirement?.Id;
            return RunAsync("Find: snapshot aranıyor…",(token,progress)=>
            {
                var result=new RequirementFindService().Find(snapshot.ReadObjects(),options,direction,anchor,includeAnchor,r=>InWorkspaceView(r,quickFilter,objectFilter),token,progress);
                if(result.Hit==null) return Task.FromResult<Action>(()=> { string message=result.Matches==0?"Eşleşme bulunamadı.":"Bu yönde başka eşleşme yok. Baştan devam seçeneğini kullanabilirsiniz."; Find.Report(message); Status=message; });
                var hit=result.Hit; var select=PrepareSnapshotSelection(snapshot,hit.Record,hit.ViewIndex,hit.Field,quickFilter,objectFilter,token,progress);
                return Task.FromResult<Action>(()=>
                {
                    select();
                    string message="Eşleşme "+hit.Ordinal+" / "+result.Matches+" · "+hit.Record.ObjectIdentifier+" · "+hit.Field+(result.Wrapped?" · Baştan devam edildi.":"");
                    Find.Report(message); Status=message; FindSelectionRequested?.Invoke(selectedRequirement);
                });
            });
        }
    }
}
