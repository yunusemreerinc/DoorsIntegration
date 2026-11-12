using System;
using System.Windows;
using System.Windows.Input;
using DoorsIntegration.Wpf.ViewModels;

namespace DoorsIntegration.Wpf
{
    // View lifetime/focus only. Search, paging and selection stay in MVVM/background services.
    public partial class FindWindow : Window
    {
        public FindWindow(RequirementFindViewModel model)
        {
            if(model==null) throw new ArgumentNullException(nameof(model));
            InitializeComponent(); DataContext=model; Loaded+=(s,e)=>FocusQuery();
        }
        public void FocusQuery() { FindText.Focus(); FindText.SelectAll(); }
        private void OnClose(object sender,ExecutedRoutedEventArgs args) { Close(); args.Handled=true; }
    }
}
