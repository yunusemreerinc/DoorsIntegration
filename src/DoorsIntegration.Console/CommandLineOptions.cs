using System;
using DoorsIntegration.Core;

namespace DoorsIntegration.ConsoleApp
{
    internal sealed class CommandLineOptions
    {
        public bool Help { get; private set; }
        public bool Probe { get; private set; }
        public bool DiagnoseCom { get; private set; }
        public bool DiagnoseCompatibility { get; private set; }
        public bool Diagnose { get; private set; }
        public bool Ping { get; private set; }
        public bool IsolatedPing { get; private set; }
        public bool Live { get; private set; }
        public DoorsConnectionMode ConnectionMode { get; private set; } = DoorsConnectionMode.Activate;
        public DoorsVersion Target { get; private set; } = DoorsVersion.V97;

        public static CommandLineOptions Parse(string[] args)
        {
            if (args == null) throw new ArgumentNullException(nameof(args));
            var result = new CommandLineOptions();
            bool modeSeen = false, targetSeen = false, connectionSeen = false;
            var start = 0;
            if (args.Length > 0 && (args[0] == "diagnose" || args[0] == "ping"))
            {
                result.Diagnose = args[0] == "diagnose";
                result.Ping = args[0] == "ping";
                result.Live = result.Ping;
                result.ConnectionMode = DoorsConnectionMode.AttachExisting;
                modeSeen = true;
                start = 1;
            }
            for (var i = start; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--dxl-only":
                        if (!result.Ping || result.IsolatedPing) throw InvalidArguments();
                        result.IsolatedPing = true;
                        break;
                    case "--help":
                    case "-h":
                        result.Help = true;
                        break;
                    case "--dry-run":
                    case "--probe":
                    case "--diagnose-com":
                    case "--diagnose-compatibility":
                        if (modeSeen) throw InvalidArguments();
                        modeSeen = true;
                        result.Probe = args[i] == "--probe";
                        result.DiagnoseCom = args[i] == "--diagnose-com";
                        result.DiagnoseCompatibility = args[i] == "--diagnose-compatibility";
                        break;
                    case "--live":
                        if (!result.Diagnose || result.Live) throw InvalidArguments();
                        result.Live = true;
                        break;
                    case "--connection":
                        if (connectionSeen || i + 1 >= args.Length) throw InvalidArguments();
                        connectionSeen = true;
                        var connection = args[++i];
                        if (connection != "activate" && connection != "attach") throw InvalidArguments();
                        result.ConnectionMode = connection == "activate" ? DoorsConnectionMode.Activate : DoorsConnectionMode.AttachExisting;
                        break;
                    case "--target":
                        if (targetSeen || i + 1 >= args.Length) throw InvalidArguments();
                        targetSeen = true;
                        result.Target = DoorsVersions.Parse(args[++i]);
                        break;
                    default: throw InvalidArguments();
                }
            }
            if (connectionSeen && !result.Probe && !result.Live) throw InvalidArguments();
            return result;
        }

        private static IntegrationException InvalidArguments() =>
            new IntegrationException(IntegrationErrorCode.InvalidConfiguration,
                "Geçersiz veya yinelenen seçenek. Kullanım için --help çalıştırın.");
    }
}
