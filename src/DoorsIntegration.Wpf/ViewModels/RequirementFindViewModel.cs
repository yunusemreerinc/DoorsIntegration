using System.Threading.Tasks;
using DoorsIntegration.Wpf.Infrastructure;
using DoorsIntegration.Wpf.Services;

namespace DoorsIntegration.Wpf.ViewModels
{
    public sealed class RequirementFindViewModel : ObservableModel
    {
        private readonly WorkspaceViewModel owner;
        private string text="",status="Aranacak metni girin.";
        private bool matchCase,wholeWord,wrap=true,changed=true;
        public RequirementFindViewModel(WorkspaceViewModel owner)
        {
            this.owner=owner;
            NextCommand=new UiCommand(()=>Run(RequirementFindDirection.Next),()=>CanFind);
            PreviousCommand=new UiCommand(()=>Run(RequirementFindDirection.Previous),()=>CanFind);
        }
        public string Text { get=>text; set { if(IsIdle&&Set(ref text,value)) ResetResult(); } }
        public bool MatchCase { get=>matchCase; set { if(IsIdle&&Set(ref matchCase,value)) ResetResult(); } }
        public bool WholeWord { get=>wholeWord; set { if(IsIdle&&Set(ref wholeWord,value)) ResetResult(); } }
        public bool Wrap { get=>wrap; set { if(IsIdle&&Set(ref wrap,value)) ResetResult(); } }
        public string Status { get=>status; private set=>Set(ref status,value); }
        public bool IsIdle=>owner.IsIdle&&!owner.IsClosing;
        public bool CanFind=>IsIdle&&owner.RequirementSnapshot!=null&&!string.IsNullOrEmpty(Text)&&Text.Length<=256;
        public string Scope=>owner.SnapshotScopeDescription;
        public UiCommand NextCommand { get; }
        public UiCommand PreviousCommand { get; }
        private Task Run(RequirementFindDirection direction)=>owner.FindInSnapshotAsync(new RequirementFindOptions(Text,MatchCase,WholeWord,Wrap),direction,changed);
        private void ResetResult() { changed=true; Status=Text?.Length>256?"En fazla 256 karakter girin.":"Sonraki veya Önceki ile arayın."; Refresh(); }
        internal void ResetSnapshot() { changed=true; Status=owner.RequirementSnapshot==null?"Önce bir modül okuyun.":"Snapshot değişti; yeniden arayın."; Refresh(); }
        internal void Report(string message) { changed=false; Status=message; }
        internal void Refresh() { NextCommand.Refresh(); PreviousCommand.Refresh(); Changed(nameof(IsIdle)); Changed(nameof(CanFind)); Changed(nameof(Scope)); }
    }
}
