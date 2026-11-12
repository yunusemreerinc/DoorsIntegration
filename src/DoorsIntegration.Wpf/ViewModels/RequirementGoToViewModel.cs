using System;
using System.Threading.Tasks;
using DoorsIntegration.Wpf.Infrastructure;
using DoorsIntegration.Wpf.Services;

namespace DoorsIntegration.Wpf.ViewModels
{
    public sealed class RequirementGoToViewModel : ObservableModel
    {
        private readonly WorkspaceViewModel owner;
        private RequirementGoToKind kind;
        private string text="",status="Hedefi girip Git düğmesine basın.";
        private bool clearFilters;
        public RequirementGoToViewModel(WorkspaceViewModel owner)
        { this.owner=owner; GoCommand=new UiCommand(Run,()=>CanGo); }
        public RequirementGoToKind Kind { get=>kind; set { if(IsIdle&&Set(ref kind,value)) InputChanged(); } }
        public string Text { get=>text; set { if(IsIdle&&Set(ref text,value)) InputChanged(); } }
        public bool ClearFilters { get=>clearFilters; set { if(IsIdle&&Set(ref clearFilters,value)) InputChanged(); } }
        public Array Kinds=>Enum.GetValues(typeof(RequirementGoToKind));
        public string Hint=>Kind==RequirementGoToKind.AbsoluteNumber?"Örnek: 220 — nesnenin Absolute Number değeri.":Kind==RequirementGoToKind.ObjectIdentifier?"Örnek: FLT-220 — Object Identifier ile tam eşleşme.":"Örnek: 1.2.3 — Object Number ile tam eşleşme; hiyerarşi değişince değişebilir.";
        public string Scope=>owner.SnapshotScopeDescription;
        public bool IsIdle=>owner.IsIdle&&!owner.IsClosing;
        public bool CanGo=>IsIdle&&owner.RequirementSnapshot!=null&&RequirementGoToRequest.Validate(Kind,Text,out _);
        public string Status=>!string.IsNullOrEmpty(Text)&&!RequirementGoToRequest.Validate(Kind,Text,out var reason)?reason:status;
        public UiCommand GoCommand { get; }
        private Task Run()=>owner.GoToInSnapshotAsync(Kind,Text,ClearFilters);
        private void InputChanged() { status="Hedefi girip Git düğmesine basın."; Changed(nameof(Status)); Changed(nameof(Hint)); Refresh(); }
        internal void Report(string message) { status=message; Changed(nameof(Status)); }
        internal void ResetSnapshot() { Report(owner.RequirementSnapshot==null?"Önce bir modül okuyun.":"Snapshot değişti; hedefi yeniden doğrulayın."); Refresh(); }
        internal void Refresh() { GoCommand.Refresh(); Changed(nameof(IsIdle)); Changed(nameof(CanGo)); Changed(nameof(Scope)); }
    }
}
