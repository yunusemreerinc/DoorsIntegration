using System;
using System.Windows.Forms;
using DoorsIntegration.Core;
using Microsoft.Extensions.DependencyInjection;

namespace DoorsIntegration.Desktop
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            try
            {
                using (var provider = new ServiceCollection().AddDoorsDesktop()
                    .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }))
                {
                    var errors = provider.GetRequiredService<IntegrationErrorHandler>();
                    System.Threading.ThreadExceptionEventHandler handler = (sender, args) =>
                    {
                        var failure = errors.Handle(args.Exception, Guid.NewGuid());
                        ShowFailure(failure);
                    };
                    Application.ThreadException += handler;
                    try { Application.Run(provider.GetRequiredService<MainForm>()); }
                    finally { Application.ThreadException -= handler; }
                }
            }
            catch (Exception ex)
            {
                using (var log = new DesktopActivityLog())
                    ShowFailure(new IntegrationErrorHandler(log).Handle(ex, Guid.NewGuid()));
            }
        }

        private static void ShowFailure(IntegrationFailure failure)
        {
            MessageBox.Show(failure.Code + ": " + failure.Message + Environment.NewLine +
                "Request: " + failure.CorrelationId.ToString("N"), "DOORS Integration",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
