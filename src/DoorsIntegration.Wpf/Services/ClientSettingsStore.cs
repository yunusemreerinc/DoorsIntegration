using System;
using System.IO;
using System.Text;
using System.Xml;
using DoorsIntegration.Core;

namespace DoorsIntegration.Wpf.Services
{
    public interface IClientSettingsStore
    {
        bool LoadWatchdogEnabled();
        void SaveWatchdogEnabled(bool enabled);
    }

    public sealed class ClientSettingsStore : IClientSettingsStore
    {
        private readonly string path;
        private readonly IIntegrationLogger logger;
        public ClientSettingsStore(string path, IIntegrationLogger logger)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) throw new ArgumentException("An absolute settings path is required.", nameof(path));
            this.path = Path.GetFullPath(path);
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }
        public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DoorsIntegration", "settings.xml");

        public bool LoadWatchdogEnabled()
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (stream.Length > 4096) throw new InvalidDataException("Settings file exceeds the size limit.");
                    using (var reader = XmlReader.Create(stream, new XmlReaderSettings
                    { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4096 }))
                    {
                        reader.MoveToContent();
                        if (reader.Name != "DoorsIntegrationSettings" || reader.GetAttribute("version") != "1")
                            throw new InvalidDataException("Unsupported settings format.");
                        var value = reader.GetAttribute("dxlWatchdogEnabled");
                        if (value != "true" && value != "false") throw new InvalidDataException("Invalid watchdog setting.");
                        bool enabled = value == "true";
                        reader.ReadStartElement("DoorsIntegrationSettings");
                        if (reader.MoveToContent() != XmlNodeType.None) throw new InvalidDataException("Unexpected settings content.");
                        return enabled;
                    }
                }
            }
            catch (FileNotFoundException) { return true; }
            catch (DirectoryNotFoundException) { return true; }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is XmlException || ex is System.Security.SecurityException)
            {
                try { logger.Write(IntegrationLogLevel.Error, "DesktopSettingsLoadFailed", Guid.NewGuid(),
                    "DefaultWatchdogEnabled=True; ExceptionType=" + ex.GetType().FullName); }
                catch { }
                return true;
            }
        }

        public void SaveWatchdogEnabled(bool enabled)
        {
            var directory = Path.GetDirectoryName(path);
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, ".settings-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var writer = XmlWriter.Create(temporary, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true }))
                {
                    writer.WriteStartElement("DoorsIntegrationSettings");
                    writer.WriteAttributeString("version", "1");
                    writer.WriteAttributeString("dxlWatchdogEnabled", enabled ? "true" : "false");
                    writer.WriteEndElement();
                }
                // Never replace a previously saved preference with a partially written file.
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
