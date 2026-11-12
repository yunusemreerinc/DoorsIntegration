using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
namespace DoorsIntegration.Wpf.Services
{
    public sealed class CapturedScript : IDisposable
    {
        private readonly string directory;
        public CapturedScript(string source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            var encoding = new UTF8Encoding(false, true); byte[] bytes = encoding.GetBytes(source);
            if (bytes.Length > 1048576) throw new InvalidDataException("Script exceeds 1 MiB.");
            directory = Path.Combine(Path.GetTempPath(), "DoorsIntegration-desktop", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory); PathName = Path.Combine(directory, "reviewed.dxl");
            try { File.WriteAllBytes(PathName, bytes); }
            catch { Directory.Delete(directory, false); throw; }
            using (var sha = SHA256.Create()) Sha256 = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            Source = source;
        }
        public string Source { get; }
        public string PathName { get; }
        public string Sha256 { get; }
        public void Dispose()
        {
            if (File.Exists(PathName)) File.Delete(PathName);
            if (Directory.Exists(directory)) Directory.Delete(directory, false);
        }
    }

}
