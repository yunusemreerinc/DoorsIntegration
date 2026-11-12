using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DoorsIntegration.Core;
using DoorsIntegration.Wpf.Services;
using DoorsIntegration.Wpf.ViewModels;

namespace DoorsIntegration.Wpf
{
    // Code-behind only adapts view events/lifetime; business actions live in commands/view models.
    public partial class MainWindow : Window
    {
        private readonly WorkspaceViewModel model;
        private bool allowClose, closeRequested;
        private FindWindow findWindow;
        private GoToWindow goToWindow;
        public MainWindow(WorkspaceViewModel model)
        {
            this.model = model ?? throw new ArgumentNullException(nameof(model));
            InitializeComponent(); DataContext = model;
            Graph.EdgeSelected += model.SelectGraphEdge;
            model.BulkSelection.PlanViewRequested += OnPlanViewRequested;
            model.DashboardNavigationRequested += OnDashboardNavigation;
            model.FindSelectionRequested += OnFindSelection;
            model.GoToSelectionRequested += OnFindSelection;
            Loaded += async (sender, args) =>
            {
                if (model.Mode == ClientMode.Mock) { await model.DiscoverCommand.ExecuteAsync(); await model.ReadCommand.ExecuteAsync(); }
            };
            Closing += OnClosing; Closed += (sender, args) => { model.BulkSelection.PlanViewRequested -= OnPlanViewRequested; model.DashboardNavigationRequested -= OnDashboardNavigation; model.FindSelectionRequested -= OnFindSelection; model.GoToSelectionRequested -= OnFindSelection; model.Dispose(); };
        }
        private void OnGoTo(object sender,ExecutedRoutedEventArgs args)
        {
            if(goToWindow==null) { goToWindow=new GoToWindow(model.GoTo) { Owner=this }; goToWindow.Closed+=(s,e)=>goToWindow=null; goToWindow.Show(); }
            else { goToWindow.Activate(); goToWindow.FocusTarget(); }
            args.Handled=true;
        }
        private void CanFind(object sender,CanExecuteRoutedEventArgs args) { args.CanExecute=model!=null&&model.IsIdle&&!model.IsClosing&&model.RequirementSnapshot!=null; args.Handled=true; }
        private void OnFind(object sender,ExecutedRoutedEventArgs args)
        {
            if(findWindow==null) { findWindow=new FindWindow(model.Find) { Owner=this }; findWindow.Closed+=(s,e)=>findWindow=null; findWindow.Show(); }
            else { findWindow.Activate(); findWindow.FocusQuery(); }
            args.Handled=true;
        }
        private void OnFindSelection(RequirementListRow row)
        {
            WorkspaceTabs.SelectedIndex=0;
            _=Dispatcher.BeginInvoke(new Action(()=> { if(!closeRequested&&ReferenceEquals(model.SelectedRequirement,row)) RequirementsGrid.ScrollIntoView(row); }),DispatcherPriority.Loaded);
        }
        private void OnDashboardNavigation(DashboardDestination destination)
        {
            DashboardContextExpander.IsExpanded=true;
            // Stable existing tab order; the dashboard is appended after Task 1–30 tabs.
            WorkspaceTabs.SelectedIndex = destination==DashboardDestination.ModuleExplorer?0:destination==DashboardDestination.BaselineCompare?1:destination==DashboardDestination.TraceabilityExplorer?9:destination==DashboardDestination.ImpactAnalysis?10:destination==DashboardDestination.BulkBaseline||destination==DashboardDestination.JobHistory?8:5;
        }
        private void OnPlanViewRequested() { WorkspaceTabs.SelectedItem = BulkDryRunTab; }
        private async void OnClosing(object sender, CancelEventArgs args)
        {
            if (allowClose) return;
            args.Cancel = true;
            if (closeRequested) return;
            closeRequested = true;
            await model.PrepareCloseAsync(); allowClose = true;
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
        private void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> args)
        {
            var node = args.NewValue as ProjectTreeNode;
            if (model == null || !model.IsIdle || node?.Item.Type != DoorsItemKind.FormalModule) return;
            model.ModulePath = node.Item.FullPath; model.ReadCommand.Execute(null);
        }
        private void OnRequirementSelectionChanged(object sender, SelectionChangedEventArgs args)
        {
            if (model == null || !model.IsIdle) return;
            model.SelectedRequirement = ((DataGrid)sender).SelectedItem as RequirementListRow;
            model.DetailsCommand.Execute(null);
        }
        private void OnDiffSelectionChanged(object sender, SelectionChangedEventArgs args)
        {
            if (model == null || !model.IsIdle) return;
            model.SelectedDiff = ((DataGrid)sender).SelectedItem as BaselineDiffRow;
            model.DiffDetailsCommand.Execute(null);
        }
    }
}
