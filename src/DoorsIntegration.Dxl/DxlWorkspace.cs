using System;
using System.IO;
using System.Text;
using DoorsIntegration.Core;

namespace DoorsIntegration.Dxl
{
    public interface IDxlWorkspace : IDisposable
    {
        string DirectoryPath { get; }
        string ScriptPath { get; }
        string ResultPath { get; }
        void WriteScript(string script);
        Stream OpenResult(long maxBytes);
    }

    public interface IDxlWorkspaceFactory
    {
        IDxlWorkspace Create(Guid correlationId);
    }

    public sealed class DxlWorkspaceFactory : IDxlWorkspaceFactory
    {
        private readonly string root;
        public DxlWorkspaceFactory() : this(Path.Combine(Path.GetTempPath(), "DoorsIntegration", "Dxl")) { }
        public DxlWorkspaceFactory(string root)
        {
            if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("Çalışma dizini boş olamaz.", nameof(root));
            var fullPath = Path.GetFullPath(root);
            this.root = string.Equals(fullPath, Path.GetPathRoot(fullPath), StringComparison.OrdinalIgnoreCase)
                ? fullPath : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        public IDxlWorkspace Create(Guid correlationId)
        {
            if (correlationId == Guid.Empty) throw new ArgumentException("İstek kimliği boş olamaz.", nameof(correlationId));
            try
            {
                var path = Path.Combine(root, correlationId.ToString("N"));
                if (Directory.Exists(path)) throw new IOException("Workspace already exists.");
                Directory.CreateDirectory(path);
                return new FileDxlWorkspace(root, path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                throw FileFailure(ex);
            }
        }

        internal static IntegrationException FileFailure(Exception cause) => new IntegrationException(
            IntegrationErrorCode.DxlResultFileFailed, "DXL çalışma/sonuç dosyası işlemi başarısız. Dosya erişimini ve cleanup durumunu kontrol edin.", cause);
    }

    internal sealed class FileDxlWorkspace : IDxlWorkspace
    {
        private readonly string root;
        private bool disposed;
        public FileDxlWorkspace(string root, string directory)
        {
            this.root = root;
            DirectoryPath = Path.GetFullPath(directory);
            ScriptPath = Path.Combine(DirectoryPath, "prepared.dxl");
            ResultPath = Path.Combine(DirectoryPath, "result.dat");
            EnsureOwnedDirectory();
        }
        public string DirectoryPath { get; }
        public string ScriptPath { get; }
        public string ResultPath { get; }

        public void WriteScript(string script)
        {
            EnsureLive();
            try
            {
                using (var stream = new FileStream(ScriptPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false, true))) writer.Write(script);
            }
            catch (Exception ex) when (IsFileError(ex)) { throw DxlWorkspaceFactory.FileFailure(ex); }
        }

        public Stream OpenResult(long maxBytes)
        {
            EnsureLive();
            Stream stream = null;
            try
            {
                if ((File.GetAttributes(ResultPath) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Linked result files are not supported.");
                // Keep a read lease while the result is owned, preventing concurrent replacement/writes.
                stream = new FileStream(ResultPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length > maxBytes)
                    throw new IntegrationException(IntegrationErrorCode.DxlResultTooLarge, "DXL sonuç dosyası boyut sınırını aşıyor.");
                return stream;
            }
            catch (Exception ex)
            {
                stream?.Dispose();
                if (IsFileError(ex)) throw DxlWorkspaceFactory.FileFailure(ex);
                throw;
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            try
            {
                EnsureOwnedDirectory();
                // Delete only this operation's two known files. Never recursively delete script-created files.
                File.Delete(ScriptPath);
                File.Delete(ResultPath);
                Directory.Delete(DirectoryPath, false);
                disposed = true;
            }
            catch (Exception ex) when (IsFileError(ex)) { throw DxlWorkspaceFactory.FileFailure(ex); }
        }

        private void EnsureLive()
        {
            if (disposed) throw new ObjectDisposedException(nameof(FileDxlWorkspace));
            EnsureOwnedDirectory();
        }

        private void EnsureOwnedDirectory()
        {
            if (!string.Equals(Path.GetDirectoryName(DirectoryPath), root, StringComparison.OrdinalIgnoreCase) ||
                (File.GetAttributes(DirectoryPath) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Invalid workspace ownership or linked directory.");
        }

        private static bool IsFileError(Exception ex) => ex is IOException || ex is UnauthorizedAccessException ||
            ex is System.Security.SecurityException || ex is EncoderFallbackException;
    }
}
