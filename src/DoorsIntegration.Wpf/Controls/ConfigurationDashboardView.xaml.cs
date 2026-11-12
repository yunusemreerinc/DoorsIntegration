using System.Windows.Controls;
using DoorsIntegration.Wpf.ViewModels;
namespace DoorsIntegration.Wpf.Controls
{
    public partial class ConfigurationDashboardView : UserControl
    {
        private object loadedContext;
        public ConfigurationDashboardView()
        {
            InitializeComponent();
            Loaded += async (s,e) => { if(DataContext is ConfigurationDashboardViewModel model && !ReferenceEquals(loadedContext,model) && model.LoadCachedCommand.CanExecute(null)) { loadedContext=model; await model.LoadCachedCommand.ExecuteAsync(); } };
        }
    }
}
