using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;
using DoorsIntegration.Dxl;
using DoorsIntegration.Wpf.Services;

namespace DoorsIntegration.Wpf.ViewModels
{
    public sealed partial class OperationsViewModel
    {
        public ToolPage ReadOptions { get; private set; }
        public ToolPage ExportOptions { get; private set; }
        public ToolPage Evidence { get; private set; }
        public ToolPage Activity { get; private set; }
        private void InitializeOptions()
        {
            ReadOptions = Page("Okuma / keşif ayarları", "Workspace Oku ve Graph link okumasında kullanılır. Baseline diff iki ayrı baseline alanını kullanır. Ayar değişince retained snapshot'lar temizlenir. Attribute DXL ayrıca yazma modu ve açık onay gerektirir.");
            Choice<DiscoveryKind>(ReadOptions, "kind", "Keşif türü"); Field(ReadOptions, "recursive", "Recursive keşif", "true", boolean: true);
            Field(ReadOptions, "items", "Max keşif öğesi", "10000"); Field(ReadOptions, "depth", "Max derinlik", "32"); Field(ReadOptions, "folders", "Max klasör", "512");
            Field(ReadOptions, "baseline", "Seçili baseline oku", "false", boolean: true); Field(ReadOptions, "major", "Baseline major", "1"); Field(ReadOptions, "minor", "Baseline minor", "0"); Field(ReadOptions, "suffix", "Baseline suffix", "A");
            Choice<DeletedObjectMode>(ReadOptions, "deleted", "Deleted object politikası", "MetadataOnly"); Field(ReadOptions, "open", "Read-only açma gerekirse aç", "true", boolean: true);
            Field(ReadOptions, "dxl", "Attribute DXL değerlendirmesine izin ver", "false", boolean: true);
            Field(ReadOptions, "objects", "Max nesne", "100000"); Field(ReadOptions, "field", "Max alan karakter", "4194304"); Field(ReadOptions, "objectChars", "Max nesne karakter", "16777216");
            Field(ReadOptions, "mib", "Max aktarım MiB", "256"); Timeout(ReadOptions);
            Choice<LinkDirection>(ReadOptions, "direction", "Link yönü", "Both"); Field(ReadOptions, "resolve", "Link endpoint çözümle", "true", boolean: true);
            Field(ReadOptions, "deletedLinks", "Silinmiş nesne linklerini dahil et", "false", boolean: true); Field(ReadOptions, "maxLinks", "Max link", "100000"); Field(ReadOptions, "linked", "Max linked modül", "64");
            Field(ReadOptions, "surrogate", "Explicit surrogate mapping kullan", "false", boolean: true); Field(ReadOptions, "marker", "Surrogate marker attribute"); Field(ReadOptions, "markerValue", "Surrogate marker exact value"); Field(ReadOptions, "reference", "Dış kimlik attribute (isteğe bağlı)");
            ExportOptions = Page("Export ayarları", "Export sekmesinde JSON/CSV/XLSX için kullanılır. Var olan hedef değiştirilmez. Büyük değerler ortak exporter'ın value-chunk temsiliyle korunur.");
            Choice<ExportErrorPolicy>(ExportOptions, "errors", "Hata politikası"); Field(ExportOptions, "records", "Max kayıt", "10000000"); Field(ExportOptions, "issues", "Max hata", "10000"); Field(ExportOptions, "field", "Max alan karakter", "16777216");
            Field(ExportOptions, "spool", "Max spool MiB", "8192"); Field(ExportOptions, "output", "Max çıktı MiB", "8192"); Field(ExportOptions, "rows", "Worksheet başına satır", "1048575"); Field(ExportOptions, "sheets", "Max worksheet", "1024"); Field(ExportOptions, "interval", "Progress aralığı", "1000");
            Evidence = Page("Değişiklik olayları / kayıt", "Baseline değişiklik olaylarını sayfalı inceleyin. Bunlar etki analizi adaylarıdır; otomatik etki yayılımı veya olay sunucusu değildir. Tam değişiklikler Export → BaselineDiff ile kaydedilir.");
            Field(Evidence, "page", "Olay sayfası (100 kayıt)", "1", invalidates: false); Field(Evidence, "save", "Yeni dosya tam yolu", invalidates: false);
            Choice<TraceabilityFormat>(Evidence, "format", "Traceability formatı");
            Action(Evidence, "Değişiklik olaylarını göster", ShowEvents, () => owner.ComparisonSnapshot != null);
            Action(Evidence, "Traceability JSON/CSV kaydet", SaveTraceability, () => owner.LinkSnapshot != null);
            Action(Evidence, "Seçili alanın tam raw rich/OLE değerini kaydet", SaveRaw, () => owner.SelectedRequirement != null && owner.SelectedAttribute != null);
            Action(Evidence, "Diagnostics metnini kaydet", SaveDiagnostics);
            Activity = Page("Activity", "Son 2000 uygulama log kaydı UTC zaman, seviye ve request kimliğiyle tutulur. Smoke ve audit raporları ayrıca kendi dosyalarındadır.");
            Field(Activity, "save", "Yeni activity log dosyası", invalidates: false);
            Action(Activity, "Logları yenile", (token, progress) => Report(Activity, string.Join("\n", (logger as ClientLog)?.ReadActivity() ?? new[] { "Bu host farklı bir log sink kullanıyor." })));
            Action(Activity, "Activity kaydet", (token, progress) => { WriteNew(Activity.Text("save"), string.Join("\n", (logger as ClientLog)?.ReadActivity() ?? new string[0])); return Report(Activity, "Activity kaydedildi: " + Activity.Text("save")); });
        }
        internal DiscoveryScope DiscoveryRequest(string path) => new DiscoveryScope(path, ReadOptions.Flag("recursive"), Number(ReadOptions, "items"), Number(ReadOptions, "depth"), Number(ReadOptions, "folders"), Wait(ReadOptions), owner.Connection, owner.Version);
        internal RequirementBaseline SelectedBaseline => ReadOptions.Flag("baseline") ? new RequirementBaseline(Number(ReadOptions, "major"), Number(ReadOptions, "minor"), ReadOptions.Text("suffix")) : null;
        internal RequirementReadRequest RequirementRequest(string path, string[] attributes) => new RequirementReadRequest(path, attributes, SelectedBaseline, EnumValue<DeletedObjectMode>(ReadOptions, "deleted"),
            ReadOptions.Flag("dxl"), ReadOptions.Flag("open"), Number(ReadOptions, "objects"), Number(ReadOptions, "field"), Number(ReadOptions, "objectChars"),
            checked((long)Number(ReadOptions, "mib") * 1048576), Wait(ReadOptions), owner.Connection, owner.Version);
        internal LinkReadRequest LinkRequest(string path, string link) => new LinkReadRequest(path, link, EnumValue<LinkDirection>(ReadOptions, "direction"), SelectedBaseline, ReadOptions.Flag("open"), ReadOptions.Flag("resolve"),
            ReadOptions.Flag("deletedLinks"), ReadOptions.Flag("surrogate") ? new SurrogateMapping(ReadOptions.Text("marker"), ReadOptions.Text("markerValue"), Null(ReadOptions.Text("reference"))) : null,
            Number(ReadOptions, "objects"), Number(ReadOptions, "maxLinks"), Number(ReadOptions, "linked"), checked((long)Number(ReadOptions, "mib") * 1048576), Wait(ReadOptions), owner.Connection, owner.Version);
        internal ExportRequest ExportRequest(string destination, ExportFormat format) => new ExportRequest(destination, format, EnumValue<ExportErrorPolicy>(ExportOptions, "errors"),
            long.Parse(ExportOptions.Text("records"), CultureInfo.InvariantCulture), Number(ExportOptions, "issues"), Number(ExportOptions, "field"),
            checked((long)Number(ExportOptions, "spool") * 1048576), checked((long)Number(ExportOptions, "output") * 1048576), Number(ExportOptions, "rows"), Number(ExportOptions, "sheets"), Number(ExportOptions, "interval"));
        internal async Task<bool> ApproveAttributeDxl(RequirementReadRequest request, CancellationToken token)
        {
            if (!request.AllowAttributeDxl) return true;
            if (!CanWrite) throw new InvalidOperationException("Attribute DXL için Real DOORS + yazma modu + açık onay gerekir.");
            return await RealTools.Review.ReviewAsync(new WriteReview("Attribute DXL yürütme onayı", request.ModulePath,
                "Kurulu attribute DXL kodu çalıştırılabilir ve veriyi değiştirebilir; etkileri geri alınmaz.\nModule=" + request.ModulePath +
                "\nProfile=" + request.Target + "; Connection=" + request.ConnectionMode + "; Baseline=" + request.Baseline + "; Timeout=" + request.Timeout +
                "\nAttributes=" + string.Join(", ", request.CustomAttributes), DateTime.UtcNow.AddMinutes(5), true), token).ConfigureAwait(false);
        }
        private Task<Action> ShowEvents(CancellationToken token, IProgress<string> progress)
        {
            int page = Number(Evidence, "page"); if (page < 1 || page > 10000) throw new ArgumentOutOfRangeException(nameof(page));
            return Report(Evidence, string.Join("\n\n", owner.ComparisonSnapshot.ReadEvents(token).Skip((page - 1) * 100).Take(100).Select(e =>
                "Event=" + e.EventId + "; ImpactCandidate=" + e.IsImpactCandidate + "; Object=" + e.Change.Identity.AbsoluteNumber + "; Status=" + e.Change.Status +
                "; Conclusive=" + e.Change.IsConclusive + "\n" + string.Join("\n", e.Change.Attributes.Select(a => a.Name + ": " + a.Status)))));
        }
        private Task<Action> SaveTraceability(CancellationToken token, IProgress<string> progress)
        { new TraceabilityExporter().ExportFile(Path.GetFullPath(Evidence.Text("save")), owner.LinkSnapshot, EnumValue<TraceabilityFormat>(Evidence, "format")); return Report(Evidence, "Traceability kaydedildi: " + Evidence.Text("save")); }
        private Task<Action> SaveRaw(CancellationToken token, IProgress<string> progress)
        {
            var record = owner.RequirementSnapshot.ReadObjects().First(o => o.AbsoluteNumber == owner.SelectedRequirement.Id);
            var value = record.Values[owner.SelectedAttribute.Name];
            if (value.RichTextStatus != RequirementValueStatus.Readable) throw new InvalidOperationException("Raw rich/OLE alanı okunabilir değil: " + value.RichTextStatus);
            WriteNew(Evidence.Text("save"), value.RichTextWithOle); return Report(Evidence, "Tam raw rich/OLE kaydedildi: " + Evidence.Text("save"));
        }
        private Task<Action> SaveDiagnostics(CancellationToken token, IProgress<string> progress)
        { WriteNew(Evidence.Text("save"), owner.DiagnosticsText); return Report(Evidence, "Diagnostics kaydedildi: " + Evidence.Text("save")); }
        private static void WriteNew(string path, string text)
        { using (var stream = new FileStream(Path.GetFullPath(path), FileMode.CreateNew, FileAccess.Write)) using (var writer = new StreamWriter(stream, new UTF8Encoding(false, true))) writer.Write(text); }
    }
}
