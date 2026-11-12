using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DoorsIntegration.Core
{
    public enum ExportFormat { Json, Csv, Xlsx, Html }
    public enum ExportTableKind { Projects, Modules, Requirements, Attributes, Links, Scopes, Errors, ValueChunks, BaselineComparisons, BaselineObjects, BaselineAttributes, BaselineEvents, BulkBaselineJobs, BulkBaselineModules, BulkBaselineApprovedPlans, BulkBaselineAuditEvents, NativeBaselineSetJobs, NativeBaselineSetDefinitions, NativeBaselineSets, NativeBaselineSetMembers, NativeBaselineSetLinks, BaselineVerificationJobs, BaselineVerificationModules, BaselineVerificationRequirements, BaselineVerifiedLinks, BaselineTraceabilityCoverage, BaselineTraceabilityFindings, BaselineTraceabilityChanges, ImpactAnalyses, ImpactComparedModules, ImpactRequirementChanges, ImpactAttributeChanges, ImpactCandidates, ImpactPaths, ImpactLinkChanges, ImpactFindings, ImpactReviews, ImpactLimitations, DashboardMetadata, DashboardMetrics, DashboardModules, DashboardFindings, DashboardCharts, DashboardActivity, DashboardTrends, DashboardScore }
    public enum ExportColumnType { Text, Integer, Number, Boolean, DateTime }
    public enum ExportErrorPolicy { ContinueInvalidRows, StopOnInvalidRow }
    public enum ExportStatus { Completed, CompletedWithIssues, Failed, Cancelled }
    public enum ExportPhase { Preparing, Reading, Writing, Reporting, Completed, Failed, Cancelled }
    public enum ExportIssueCode { IncompleteScope, InvalidRecord, InvalidUnicode, InvalidDate, FieldTooLarge, LimitReached, SourceFailure, WriteFailure, Cancelled }

    public sealed class ExportRequest
    {
        // A new output directory is published in one rename. Existing directories are never replaced.
        public ExportRequest(string destinationDirectory, ExportFormat format, ExportErrorPolicy errorPolicy = ExportErrorPolicy.ContinueInvalidRows,
            long maxRecords = 10000000, int maxIssues = 10000, int maxFieldCharacters = 16777216,
            long maxSpoolBytes = 8589934592, long maxOutputBytes = 8589934592, int rowsPerWorksheet = 1048575,
            int maxWorksheets = 1024, int progressInterval = 1000)
        {
            try { DestinationDirectory = Path.GetFullPath(destinationDirectory ?? "").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { throw Invalid(ex); }
            if (string.IsNullOrWhiteSpace(destinationDirectory) || string.IsNullOrEmpty(Path.GetFileName(DestinationDirectory)) ||
                !Enum.IsDefined(typeof(ExportFormat), format) || !Enum.IsDefined(typeof(ExportErrorPolicy), errorPolicy) ||
                maxRecords < 1 || maxRecords > 1000000000 || maxIssues < 1 || maxIssues > 100000 || maxFieldCharacters < 1 || maxFieldCharacters > 67108864 ||
                maxSpoolBytes < 1024 || maxSpoolBytes > 1099511627776 || maxOutputBytes < 1024 || maxOutputBytes > 1099511627776 ||
                rowsPerWorksheet < 1 || rowsPerWorksheet > 1048575 || maxWorksheets < 8 || maxWorksheets > 4096 || progressInterval < 1 || progressInterval > 1000000) throw Invalid();
            Format = format; ErrorPolicy = errorPolicy; MaxRecords = maxRecords; MaxIssues = maxIssues; MaxFieldCharacters = maxFieldCharacters;
            MaxSpoolBytes = maxSpoolBytes; MaxOutputBytes = maxOutputBytes; RowsPerWorksheet = rowsPerWorksheet;
            MaxWorksheets = maxWorksheets; ProgressInterval = progressInterval;
        }
        public string DestinationDirectory { get; }
        public ExportFormat Format { get; }
        public ExportErrorPolicy ErrorPolicy { get; }
        public long MaxRecords { get; }
        public int MaxIssues { get; }
        public int MaxFieldCharacters { get; }
        public long MaxSpoolBytes { get; }
        public long MaxOutputBytes { get; }
        public int RowsPerWorksheet { get; }
        public int MaxWorksheets { get; }
        public int ProgressInterval { get; }
        internal static IntegrationException Invalid(Exception cause = null) => new IntegrationException(IntegrationErrorCode.InvalidConfiguration,
            "Export hedef klasörü, biçimi veya kaynak sınırları geçersiz.", cause);
    }

    public sealed class ExportColumn
    {
        public ExportColumn(string name, ExportColumnType type, bool required = false) { Name = name; Type = type; Required = required; }
        public string Name { get; }
        public ExportColumnType Type { get; }
        public bool Required { get; }
    }
    public sealed class ExportTableSchema
    {
        public ExportTableSchema(ExportTableKind kind, IEnumerable<ExportColumn> columns)
        { Kind = kind; Columns = new List<ExportColumn>(columns).AsReadOnly(); }
        public ExportTableKind Kind { get; }
        public IReadOnlyList<ExportColumn> Columns { get; }
    }
    public sealed class ExportRecord
    {
        public ExportRecord(ExportTableKind table, IDictionary<string, object> fields)
        {
            Table = table;
            Fields = new ReadOnlyDictionary<string, object>(new Dictionary<string, object>(fields ?? throw new ArgumentNullException(nameof(fields)), StringComparer.Ordinal));
        }
        public ExportTableKind Table { get; }
        public IReadOnlyDictionary<string, object> Fields { get; }
    }
    public interface IExportSource
    {
        long? EstimatedRecordCount { get; }
        IEnumerable<ExportRecord> Read(CancellationToken cancellationToken);
    }
    public sealed class EnumerableExportSource : IExportSource
    {
        private readonly IEnumerable<ExportRecord> records;
        public EnumerableExportSource(IEnumerable<ExportRecord> records, long? estimatedRecordCount = null)
        {
            this.records = records ?? throw new ArgumentNullException(nameof(records));
            if (estimatedRecordCount < 0) throw new ArgumentOutOfRangeException(nameof(estimatedRecordCount));
            EstimatedRecordCount = estimatedRecordCount;
        }
        public long? EstimatedRecordCount { get; }
        public IEnumerable<ExportRecord> Read(CancellationToken cancellationToken)
        {
            foreach (var record in records) { cancellationToken.ThrowIfCancellationRequested(); yield return record; }
        }
    }
    public sealed class ExportIssue
    {
        public ExportIssue(ExportIssueCode code, string severity, long? recordId, string table, string column, int? errorCode = null, string exceptionType = null)
        { Code = code; Severity = severity; RecordId = recordId; Table = table; Column = column; ErrorCode = errorCode; ExceptionType = exceptionType; }
        public ExportIssueCode Code { get; }
        public string Severity { get; }
        public long? RecordId { get; }
        public string Table { get; }
        public string Column { get; }
        public int? ErrorCode { get; }
        public string ExceptionType { get; }
        public string Message => Code == ExportIssueCode.IncompleteScope ? "Kaynak kapsamı eksik veya kısmi; erişim/okuma durumları payload içinde korunur." :
            Code == ExportIssueCode.Cancelled ? "Export iptal edildi; tamamlanmamış veri yayımlanmadı." :
            Code == ExportIssueCode.LimitReached ? "Export kaynak sınırına ulaştı; tamamlanmamış veri yayımlanmadı." :
            Code == ExportIssueCode.SourceFailure ? "Kaynak dolaşımı tamamlanamadı; tamamlanmamış veri yayımlanmadı." :
            Code == ExportIssueCode.WriteFailure ? "Çıktı yazılamadı; tamamlanmamış veri yayımlanmadı." :
            Code == ExportIssueCode.InvalidDate ? "Tarih UTC DateTime veya DateTimeOffset olmalıdır; saat dilimi tahmin edilmedi." :
            Code == ExportIssueCode.InvalidUnicode ? "Kayıt bozuk Unicode içeriyor; değer değiştirilerek aktarılmadı." :
            Code == ExportIssueCode.FieldTooLarge ? "Alan yapılandırılmış karakter sınırını aştı; kayıt aktarılmadı." :
            "Kayıt şema, alan türü veya zorunlu alan doğrulamasından geçmedi.";
    }
    public sealed class ExportProgress
    {
        public ExportProgress(Guid id, ExportPhase phase, long read, long accepted, long writtenRows, long issues, long? estimated)
        { CorrelationId = id; Phase = phase; RecordsRead = read; RecordsAccepted = accepted; RowsWritten = writtenRows; IssueCount = issues; EstimatedRecordCount = estimated; }
        public Guid CorrelationId { get; }
        public ExportPhase Phase { get; }
        public long RecordsRead { get; }
        public long RecordsAccepted { get; }
        public long RowsWritten { get; }
        public long IssueCount { get; }
        public long? EstimatedRecordCount { get; }
    }
    public sealed class ExportResult
    {
        public ExportResult(Guid id, ExportStatus status, string directory, long read, long accepted, long skipped, long issues,
            long csvProtectedCells, IDictionary<ExportTableKind, long> rowCounts)
        {
            CorrelationId = id; Status = status; DirectoryPath = directory; RecordsRead = read; RecordsAccepted = accepted; RecordsSkipped = skipped;
            IssueCount = issues; CsvProtectedCellCount = csvProtectedCells;
            RowCounts = new ReadOnlyDictionary<ExportTableKind, long>(new Dictionary<ExportTableKind, long>(rowCounts));
        }
        public Guid CorrelationId { get; }
        public ExportStatus Status { get; }
        public string DirectoryPath { get; }
        public string ReportPath => Path.Combine(DirectoryPath, "report.json");
        public bool HasPayload => Status == ExportStatus.Completed || Status == ExportStatus.CompletedWithIssues;
        public long RecordsRead { get; }
        public long RecordsAccepted { get; }
        public long RecordsSkipped { get; }
        public long IssueCount { get; }
        public long CsvProtectedCellCount { get; }
        public IReadOnlyDictionary<ExportTableKind, long> RowCounts { get; }
    }
    public interface IExportService
    {
        Task<ExportResult> ExportAsync(ExportRequest request, IExportSource source, IProgress<ExportProgress> progress = null,
            CancellationToken cancellationToken = default(CancellationToken));
    }
    public interface IExportTableReader
    {
        IReadOnlyList<ExportTableSchema> Schemas { get; }
        long Count(ExportTableKind table);
        IEnumerable<string[]> ReadRows(ExportTableKind table, CancellationToken cancellationToken);
    }
    public interface IExportWriteContext
    {
        IExportTableReader Data { get; }
        int RowsPerWorksheet { get; }
        int MaxWorksheets { get; }
        Stream OpenFile(string fileName);
        void RowWritten();
        void CsvCellProtected();
    }
    public interface IExportFormatWriter
    {
        ExportFormat Format { get; }
        void Write(IExportWriteContext context, CancellationToken cancellationToken);
    }
}
