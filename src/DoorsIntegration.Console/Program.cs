using System;
using System.Threading;
using DoorsIntegration.Core;
using Microsoft.Extensions.DependencyInjection;

namespace DoorsIntegration.ConsoleApp
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                var services = new ServiceCollection().AddDoorsIntegration();
                using (var provider = services.BuildServiceProvider(new ServiceProviderOptions
                {
                    ValidateOnBuild = true,
                    ValidateScopes = true
                }))
                {
                    using (var cancellation = new CancellationTokenSource())
                    {
                        ConsoleCancelEventHandler handler = (sender, e) => { e.Cancel = true; cancellation.Cancel(); };
                        bool smoke = args.Length > 0 && args[0] == "smoke";
                        if (smoke) System.Console.CancelKeyPress += handler;
                        try { return provider.GetRequiredService<ConsoleApplication>().Run(args, System.Console.Out, System.Console.Error, cancellation.Token); }
                        finally { if (smoke) System.Console.CancelKeyPress -= handler; }
                    }
                }
            }
            catch (Exception ex)
            {
                // Includes composition and provider disposal failures.
                using (var logger = new TraceIntegrationLogger())
                {
                    var failure = new IntegrationErrorHandler(logger).Handle(ex, Guid.NewGuid());
                    System.Console.Error.WriteLine("Hata " + failure.Code + ": " + failure.Message);
                    return failure.ExitCode;
                }
            }
        }
    }
}
