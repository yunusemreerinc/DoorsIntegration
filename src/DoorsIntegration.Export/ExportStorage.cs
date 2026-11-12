using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using DoorsIntegration.Core;

namespace DoorsIntegration.Export
{
    internal sealed class ByteBudget
    {
        private readonly long maximum;
        private long used;
        public ByteBudget(long maximum) { this.maximum = maximum; }
        public void Add(long value)
        {
            if (value < 0 || value > maximum - used) throw Limit();
            used += value;
        }
        internal static IntegrationException Limit() => new IntegrationException(IntegrationErrorCode.ExportLimitReached, "Export kayıt, dosya veya çalışma sayfası sınırına ulaştı.");
    }
    internal sealed class BudgetStream : Stream
    {
        private readonly Stream inner;
        private readonly ByteBudget budget;
        public BudgetStream(Stream inner, ByteBudget budget) { this.inner = inner; this.budget = budget; }
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) { if (value > inner.Length) budget.Add(value - inner.Length); inner.SetLength(value); }
        public override void Write(byte[] buffer, int offset, int count)
        {
            long end = checked(inner.Position + count);
            if (end > inner.Length) budget.Add(end - inner.Length);
            inner.Write(buffer, offset, count);
        }
        public override void Flush() => inner.Flush();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
    internal sealed class ExportWorkspace
    {
        public ExportWorkspace(string destination)
        {
            Destination = Path.GetFullPath(destination);
            var parent = Path.GetDirectoryName(Destination);
            if (!Directory.Exists(parent) || Directory.Exists(Destination) || File.Exists(Destination))
                throw new IntegrationException(IntegrationErrorCode.ExportFailed, "Export üst klasörü mevcut olmalı ve hedef klasör henüz bulunmamalıdır.");
            Root = Path.GetFullPath(Path.Combine(parent, ".doors-export-" + Guid.NewGuid().ToString("N")));
            // Explicitly verify both absolute move targets are the requested destination and its private sibling.
            if (!string.Equals(Path.GetDirectoryName(Root), parent, StringComparison.OrdinalIgnoreCase) || Root == Destination) throw new IOException("Invalid staging path");
            Directory.CreateDirectory(Root);
            SpoolDirectory = Path.Combine(Root, ".spool"); Directory.CreateDirectory(SpoolDirectory);
        }
        public string Destination { get; }
        public string Root { get; }
        public string SpoolDirectory { get; }
        public void DeleteSpools()
        {
            foreach (var schema in ExportSchemas.All) File.Delete(Path.Combine(SpoolDirectory, schema.Kind + ".bin"));
            if (Directory.Exists(SpoolDirectory)) Directory.Delete(SpoolDirectory, false);
        }
        public void Publish()
        {
            if (!string.Equals(Path.GetDirectoryName(Root), Path.GetDirectoryName(Destination), StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid publication path");
            Directory.Move(Root, Destination);
        }
        public void Discard(IEnumerable<string> outputFiles)
        {
            foreach (var file in outputFiles) File.Delete(Path.Combine(Root, file));
            File.Delete(Path.Combine(Root, "report.json")); DeleteSpools();
            if (Directory.Exists(Root)) Directory.Delete(Root, false);
        }
    }
    internal sealed class ExportSpool : IExportTableReader, IDisposable
    {
        private readonly string directory;
        private readonly ByteBudget dataBudget, errorBudget;
        private readonly Dictionary<ExportTableKind, BinaryWriter> writers = new Dictionary<ExportTableKind, BinaryWriter>();
        private readonly Dictionary<ExportTableKind, long> counts = ExportSchemas.All.ToDictionary(schema => schema.Kind, schema => 0L);
        public ExportSpool(string directory, ExportRequest request)
        { this.directory = directory; dataBudget = new ByteBudget(request.MaxSpoolBytes); errorBudget = new ByteBudget((long)(request.MaxIssues + 1) * 4096 + 65536); }
        // Preserve the original eight-table payload for existing exports. Comparison tables are additive only when populated.
        public IReadOnlyList<ExportTableSchema> Schemas => ExportSchemas.All.Where(schema => schema.Kind <= ExportTableKind.ValueChunks || counts[schema.Kind] > 0).ToArray();
        public long Count(ExportTableKind table) => counts[table];
        public IDictionary<ExportTableKind, long> Counts => Schemas.ToDictionary(schema => schema.Kind, schema => counts[schema.Kind]);
        public void Write(ExportTableKind table, string[] values)
        {
            var schema = ExportSchemas.Get(table);
            values[2] = string.Join(",", schema.Columns.Select((column, index) => new { column, index }).Skip(3).Where(item => values[item.index] == null).Select(item => item.column.Name));
            if (!writers.TryGetValue(table, out var writer))
            {
                var path = Path.Combine(directory, table + ".bin");
                var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
                stream.Position = stream.Length;
                writer = new BinaryWriter(new BudgetStream(stream, table == ExportTableKind.Errors ? errorBudget : dataBudget), new UTF8Encoding(false, true));
                writers.Add(table, writer);
            }
            foreach (string value in values) { writer.Write(value != null); if (value != null) writer.Write(value); }
            counts[table]++;
        }
        public IEnumerable<string[]> ReadRows(ExportTableKind table, CancellationToken cancellationToken)
        {
            string path = Path.Combine(directory, table + ".bin");
            if (!File.Exists(path)) yield break;
            using (var reader = new BinaryReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read), new UTF8Encoding(false, true)))
            {
                for (long index = 0; index < counts[table]; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var values = new string[ExportSchemas.Get(table).Columns.Count];
                    for (int column = 0; column < values.Length; column++) values[column] = reader.ReadBoolean() ? reader.ReadString() : null;
                    yield return values;
                }
                if (reader.BaseStream.Position != reader.BaseStream.Length) throw new IOException("Invalid spool length");
            }
        }
        public void Finish()
        {
            Exception failure = null;
            foreach (var writer in writers.Values) { try { writer.Dispose(); } catch (Exception ex) { if (failure == null) failure = ex; } }
            writers.Clear();
            if (failure != null) throw failure;
        }
        public void Dispose() => Finish();
    }
    internal sealed class ExportWriteContext : IExportWriteContext
    {
        private readonly ExportWorkspace workspace;
        private readonly ExportRequest request;
        private readonly ByteBudget budget;
        private readonly Action onRow;
        private readonly HashSet<string> files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public ExportWriteContext(ExportWorkspace workspace, ExportSpool spool, ExportRequest request, Action onRow)
        { this.workspace = workspace; Data = spool; this.request = request; this.onRow = onRow; budget = new ByteBudget(request.MaxOutputBytes); }
        public IExportTableReader Data { get; }
        public int RowsPerWorksheet => request.RowsPerWorksheet;
        public int MaxWorksheets => request.MaxWorksheets;
        public IReadOnlyCollection<string> Files => files;
        public long ProtectedCells { get; private set; }
        public Stream OpenFile(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 100 || fileName.Any(c => !char.IsLetterOrDigit(c) && c != '.' && c != '_' && c != '-') ||
                string.Equals(fileName, "report.json", StringComparison.OrdinalIgnoreCase) || fileName[0] == '.' || !files.Add(fileName)) throw new IOException("Invalid export file name");
            return new BudgetStream(new FileStream(Path.Combine(workspace.Root, fileName), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None), budget);
        }
        public void RowWritten() => onRow();
        public void CsvCellProtected() { ProtectedCells++; }
        public void DeletePayload() { foreach (string file in files) File.Delete(Path.Combine(workspace.Root, file)); }
    }
}
