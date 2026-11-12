using System.Windows;
using DoorsIntegration.Wpf.Services;
using DoorsIntegration.Wpf.ViewModels;

namespace DoorsIntegration.Wpf
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            var logger = new ClientLog();
            var settings = new ClientSettingsStore(ClientSettingsStore.DefaultPath, logger);
            var watchdog = new DoorsIntegration.Core.DxlWatchdogSettings { Enabled = settings.LoadWatchdogEnabled() };
            WorkspaceViewModel viewModel = null;
            var confirmation = new ClientConfirmation(new WpfReviewDialog(() => MainWindow, () => viewModel != null && !viewModel.IsReadOnly && viewModel.Mode == ClientMode.RealDoors && !viewModel.IsClosing));
            var baselineReview = new WpfReviewDialog(() => MainWindow, () => viewModel != null && !viewModel.IsReadOnly && !viewModel.IsClosing,
                "Bu batch içindeki tüm modülleri, sürümleri ve riskleri inceledim; kesin olarak onaylıyorum.", "Kesin plan onayı");
            viewModel = new WorkspaceViewModel(new ClientSessionFactory(confirmation, watchdog, baselineReview, null), logger, settings, watchdog);
            MainWindow = new MainWindow(viewModel); MainWindow.Show();
        }
    }
}
