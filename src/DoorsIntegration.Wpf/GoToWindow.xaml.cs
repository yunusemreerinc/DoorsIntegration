using System;
using System.Windows;
using System.Windows.Input;
using DoorsIntegration.Wpf.ViewModels;
namespace DoorsIntegration.Wpf
{
    public partial class GoToWindow : Window
    {
        public GoToWindow(RequirementGoToViewModel model)
        {
            if(model==null) throw new ArgumentNullException(nameof(model));
            InitializeComponent(); DataContext=model; Loaded+=(s,e)=>FocusTarget();
        }
        public void FocusTarget() { GoToText.Focus(); GoToText.SelectAll(); }
        private void OnClose(object sender,ExecutedRoutedEventArgs args) { Close(); args.Handled=true; }
    }
}
