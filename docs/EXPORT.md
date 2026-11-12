# ExportService

Baseline değişiklik raporları için `BaselineComparisonExportSource`, ortak writer'lara `BaselineComparisons`, `BaselineObjects`, `BaselineAttributes`, `BaselineEvents` tablolarını dolu olduklarında ekler. Eski exportların sekiz tablo yapısı korunur. API/JSON/XLSX/chunk ve olay sözleşmesi: [Baseline karşılaştırma rehberi](BASELINE-COMPARISON.md).

`IExportService` / `ExportService`, proje/klasör, modül, gereksinim, attribute ve Classic link verisini ortak şemayla **JSON, CSV veya XLSX** olarak aktarır. Yeni `DoorsIntegration.Export` projesi yalnız Core sözleşmelerine bağlıdır. Veri toplama, DXL yürütme veya DOORS bağlantısı kurma işlemi yapmaz; mevcut snapshot'ları ve kullanıcı tarafından sağlanan lazy kayıt kaynaklarını tüketir. Her iki hostun DI kaydı servisi ve üç writer'ı içerir.

Desktop **Workspace → Export** aynı servisi retained Discovery/Requirement/Link snapshot'ları, format/hata/sınır seçimi ve ilerleme/sonuç raporuyla kullanır. Eksik seçilmiş kaynak atlanmaz; bütün snapshot export edilir, gridin görünen sayfasıyla sınırlı değildir. [Desktop kullanımı](DESKTOP-WORKSPACE.md).

## Kullanım

```csharp
var exporter = provider.GetRequiredService<IExportService>();
var source = new SnapshotExportSource(
    discovery: new[] { discoverySnapshot },
    requirements: new[] { requirementSnapshot },
    links: new[] { linkSnapshot });

var request = new ExportRequest(
    destinationDirectory: @"C:\Reports\doors-export-001",
    format: ExportFormat.Xlsx,
    errorPolicy: ExportErrorPolicy.ContinueInvalidRows);

var progress = new Progress<ExportProgress>(value =>
    ShowProgress(value.Phase, value.RecordsRead, value.RowsWritten, value.IssueCount));

var result = await exporter.ExportAsync(request, source, progress, cancellationToken);
ShowResult(result.Status, result.ReportPath, result.HasPayload);
```

Snapshot'lar önce ilgili ProjectService/ModuleService/RequirementService/LinkService ile edinilmiş olmalıdır. `provider`, snapshot'lar ve `ShowProgress`/`ShowResult`, hostunuzun sağlayacağı bileşenlerdir. Hedefin **üst klasörü mevcut**, hedef klasör ise yeni olmalıdır. Var olan hedefin üzerine yazılmaz. Aynı verinin başka formatı için yeni hedef ve yeniden okunabilir snapshot'larla yeni source oluşturun.

Snapshot sahibi çağırandır; `using` kapsamını `await ExportAsync` tamamlanana kadar açık tutun. Kaynak koleksiyonları tek kez dolaşılır, snapshot'lar exporter tarafından dispose edilmez. Çok sayıda modül için `IEnumerable<IRequirementSnapshot>` / `IEnumerable<ILinkSnapshot>` içinde her snapshot'ın ömrünü `using/yield` ile sırayla yönetebilirsiniz. Asenkron veri edinimini host katmanında yapın; export worker'ından doğrudan COM nesnesi çağırmayın. İptal ve iterator dispose olduğunda lazy kaynağın `finally`/`using` temizliği çalışır.

`SnapshotExportSource` mevcut discovery meta verisini ve gereksinim/link dosyalarını kullanır. DiscoverySnapshot daha önce keşif sınırları içinde belleğe alınmıştır; export bu koleksiyonu ayrıca biriktirmez. Otomatik bütün veritabanı taraması, module-level özel attribute okuması veya kapsam dışı bağlantı sorgusu yapılmaz. Mevcut gereksinim snapshot'larının builtin ve seçilmiş özel object attribute'ları aktarılır.

Özel kayıt sağlayıcıları, module-level attribute'lar veya zaten edinilmiş başka veriler için `IExportSource` ya da `EnumerableExportSource` kullanın:

```csharp
yield return new ExportRecord(ExportTableKind.Attributes,
    new Dictionary<string, object>
    {
        ["modulePath"] = "/Project/Module",
        ["ownerKind"] = "Module",
        ["absoluteNumber"] = null,
        ["name"] = "Reviewed At",
        ["typedDateUtc"] = reviewTimeOffset
    });
```

Bu örnek bir `IEnumerable<ExportRecord>` iterator gövdesindedir; `reviewTimeOffset`, kaynağınızın sağladığı DateTimeOffset değeridir. Satır sözlüğü constructor'da kopyalanır; şemada bulunmayan alanlar sessizce düşürülmez. `ExportSchemas.All` kolon ve tür sözleşmesini verir. Writer değiştirmek için `IExportFormatWriter` kaydını değiştirin; her format için tek writer olmalıdır. `IExportWriteContext.OpenFile` dosya sınırlarını ve çıktı sahipliğini uygular. Writer açtığı stream'leri kapatmalı, token'ı ve `RowWritten()` bildirimini kullanmalıdır.

## Paket ve şema

Başarılı her iş yeni bir klasör yayımlar:

- JSON: `data.json` ve `report.json`.
- CSV: sekiz tablo için ayrı UTF-8 `*.csv` dosyaları ve `report.json`.
- XLSX: `data.xlsx` ve `report.json`; sekiz tablo ayrı çalışma sayfalarındadır.

`schemaVersion` şu anda **1**. Tablolar: `Projects`, `Modules`, `Requirements`, `Attributes`, `Links`, `Scopes`, `Errors`, `ValueChunks`. Projects tablosu folder/root meta verisini de type alanıyla taşıyabilir. Boş tabloların şeması/header'ı da yazılır. JSON'da tablolar `tables` altında dizilerdir. Modül/sürüm/Absolute Number kapsamı ve okunabilir Identifier, parent/level/deleted durumları; attribute tipleri, plain/rich status'ları ve RTF/OLE içeriği; linkin gerçek source/target uçları, gözlem yönü, çözümleme/echo/surrogate bilgisi korunur. Link gözlemi sayısı benzersiz fiziksel link sayısı değildir.

`Scopes` kaynak istek kimliğini, sürüm/kapsamı, visibility, erişim/cleanup/kesilme durumunu ve sayaçları tutar. Eksik kaynak scope'u `IncompleteScope` uyarısı üretir; boş veya kısmi okuma tam envanter gibi gösterilmez. Error satırı kaynak `recordId` ile eşleştirilebilir. Görünmeyen DOORS verisi export ile görünür hale gelmez. Kaynak gereksinim/link rehberlerinin baseline ve erişim sınırları geçerlidir.

Her tabloda `recordId`, `chunkedFields`, `nullFields` bulunur. `recordId` yalnız export içindeki kimliktir, DOORS kimliğinin yerine geçmez. `nullFields`, null kolonların adlarını içerir; CSV boş hücresi ile boş ama okunabilir metin böyle ayrılır. Şema kolon adları virgül içermez. JSON null'ları ve XLSX boş hücre/inline string ayrımı ayrıca korunur.

## Uzun metin ve XLSX sınırları

Veri önce özel geçici dizindeki sekiz sınırlı binary spool'a, sonra seçilen formata satır satır yazılır. Kaynağın tamamına ToList/ToArray uygulanmaz; tüm tablo DOM'u veya shared strings tablosu kurulmaz. Bellek bir kaynak kaydı, sınırlı cell/row buffer'ları ve workbook/sheet meta verisiyle sınırlıdır. Kaynak sağlayıcının kendi bellek kullanımı ve DOORS'un native belleği bu sınıra dahil değildir. Spool ve çıktı birlikte geçici disk alanı tüketir.

Üç format aynı yeniden oluşturma sözleşmesini kullanır. **4000 UTF-16 birimini veya 250 line feed'i aşan** metin bir hücrede kırpılmaz. Asıl kolonda `[ValueChunks:recordId:column]` gösterimi, `chunkedFields` içinde kolon adı ve `ValueChunks` tablosunda `ownerTable`, `ownerRecordId`, `column`, 1'den başlayan `chunkIndex`, `value` bulunur. İlgili chunk'ları sırayla birleştirin; delimiter veya newline eklemeyin. Yalnız gösterim metnine bakarak referans çıkarmayın; `chunkedFields` ve owner kolonları belirleyicidir. Surrogate pair bölünmez. RTF/OLE ve kontrol karakterleri metin olarak aynen yeniden kurulabilir; OLE etkinleştirilmez veya HTML'e dönüştürülmez.

XLSX **DocumentFormat.OpenXml 3.5.1** ve `OpenXmlWriter` ile oluşturulur. Excel/Office kurulumu gerekmez. Inline string kullanılır; hücrelere formül veya hyperlink eklenmez. OOXML kontrol karakteri escape'leri ve literal `_xHHHH_` metinleri ayrı korunur. JSON/CSV'de bu SpreadsheetML escape'i uygulanmaz. Sayfa başlığı dondurulur; header koyu mavidir.

Excel'in 32.767 karakter ve 253 line feed hücre sınırlarına güvenli pay bırakılır; kontrol escape'lerinin en kötü büyümesi de 4000 birimlik parçalarda hücre sınırına sığar. Varsayılan 1.048.575 data row + 1 header sonrasında yeni sayfa açılır, örneğin Requirements_2. `rowsPerWorksheet` test/host ihtiyaçları için düşürülebilir. Sayfa sayısı ayrıca sınırlıdır. Küçük integer ve boolean hücreleri typed yazılır; 15 haneyi aşan integer'lar ve decimal/real değerleri Excel yuvarlamasını önlemek için invariant text olarak saklanır. Tip bilgisi report şemasındadır.

## Unicode, CSV injection ve tarihler

JSON UTF-8 BOM'suz, CSV UTF-8 BOM'lu ve virgül ayraçlıdır. CSV her hücreyi çift tırnakla sarar, tırnakları çiftler, embedded CR/LF'yi korur ve satır sonunu CRLF yazar. Türkçe Excel'de **Metinden/CSV → UTF-8 → virgül** seçin. CSV text/date alanlarında whitespace/control/format karakterleri sonrası ilk karakter `= + - @` veya bunların full-width karşılığıysa ya da alan kontrol karakteriyle başlıyorsa hücreye başta ek bir tab konur. Koruma sayısı `csvProtectedCellCount` içindedir. CSV yeniden programatik okunacaksa **text/date alanından tam bir baştaki tabı** kaldırın; normalde tabla başlayan orijinal metin iki tablı saklandığından işlem tersinirdir. Kural report içinde de kayıtlıdır. Koruma, CSV'nin spreadsheet içinde yeniden kaydedilmesinden sonraki dönüşümlere genel bir garanti vermez; canonical veri alışverişinde JSON veya typed XLSX'i kullanın.

Bozuk UTF-16 surrogate dizileri `InvalidUnicode` olarak raporlanır; replacement karakteriyle sessiz düzeltme yapılmaz. JSON control karakterlerini escape eder; CSV/XLSX dönüşümleri belgeye uygun şekilde korunur. Numerikler culture invariant'tır; NaN/Infinity geçersizdir. Typed tarih yalnız **UTC DateTime** veya **DateTimeOffset** kabul eder ve UTC ISO 8601 `yyyy-MM-ddTHH:mm:ss.fffffffZ` metni yazar. Local/Unspecified DateTime için saat dilimi tahmin edilmez. XLSX'te tarihler timezone kaybını önlemek için ISO text'tir. DOORS Date attribute'unun `PlainText` gösterimi yerel formatta olabilir; bu metin tarih gibi parse edilmez. Kaynağın güvenilir typed tarihi varsa `typedDateUtc` kullanın.

## Hata raporu, iptal ve ilerleme

`report.json` durum, UTC başlangıç/bitiş, format, payload dosyaları, record/skip/issue sayaçları, staged row sayıları, kolon türleri, chunk sözleşmesi ve issue dizisini içerir. Report/Errors içinde kaynak değerleri veya ham exception mesajı bulunmaz; issue code, table/column, record ID, typed hata kodu ve exception tipi tutulur. Kaynak yollarını/değerlerini rapor üzerinden bulmak için payload scope/record eşlemesini kullanın.

Durumlar `Completed`, `CompletedWithIssues`, `Failed`, `Cancelled`. Varsayılan policy geçersiz satırı Errors'a yazıp sonrakine devam eder; `StopOnInvalidRow` işi durdurur. `FieldTooLarge` alanı kırpmaz; satırı atlar/durdurur. Iterator/source arızasından sonra aynı iterator'e devam edilmez. Kısmi kaynak scope'u veriyle birlikte uyarı olarak korunur. Başarısız/iptal işte tamamlanmamış payload silinir ve **yalnız report.json** yayımlanır; staged row/accepted sayıları committed veri sayısı olarak yorumlanmamalıdır. `HasPayload` kontrol edilmelidir.

Hedef üst klasörü/izin/disk nedeniyle rapor veya publish yapılamazsa typed **35 ExportFailed** exception'ı atılır; mevcut hedef değiştirilmez. Kaynak limitleri **36 ExportLimitReached** olarak raporlanır. Satır/source kaynaklı mevcut typed DOORS hata kodları korunur. Kaynak adı/mesajı loglanmaz; istek kimliği ve sayaçlar loglanır. Senkron logger/Report hataları temizliği engellemez; UI'ya asenkron gönderilmiş progress callback'inin kendi hata yönetimi hosta aittir. Fatal runtime arızası, süreç çökmesi veya cleanup I/O arızası sonrası staging artığı kalabilir; crash recovery/kalıcı job kuyruğu uygulanmadı.

`ExportAsync` export işini UI dışındaki worker'da yapar. `IProgress<ExportProgress>` fazları Preparing, Reading, Writing, Reporting ve terminal Completed/Failed/Cancelled'dır. RecordsRead, RecordsAccepted, RowsWritten, IssueCount ve varsa kaynak tahmini verilir. Bilinmeyen toplam için yüzde uydurulmaz; yazılan fiziksel satırlar chunk/error satırlarını da içerir. `Progress<T>` UI context'inde oluşturulursa bildirimler UI'ya taşınır; throttling `progressInterval` ile kayıt/satır sayısına göre yapılır. Okuma sırasında kabul edilmiş satırlar, publish bitmeden diskteki tamamlanmış export değildir.

Token okuma, chunk ve çıktı satırı sınırlarında kontrol edilir; önceden iptal edilmiş istek de report-only sonuç üretir. Reporting/publish commit fazında iptal sonucu değiştirmez. Bloklanan kullanıcı kaynak iterator'ü veya XLSX package finalization zorla kesilmez. ExportService COM çağırmaz; daha önce başlamış bir DOORS çağrısının iptali ilgili reader/executor sözleşmesine tabidir.

Varsayılan sınırlar: 10.000.000 kaynak kaydı, 10.000 issue (+ en fazla bir fatal issue), kaynak alanı başına 16.777.216 UTF-16 birimi, **8 GiB spool + 8 GiB payload**, 1024 worksheet. Report/error spool için issue sayısıyla sınırlı ek rezerv kullanılır. Report bytes payload limitinden ayrıdır, böylece payload sınırı aşıldığında hata raporu üretilebilir. `MaxIssues` dolduğunda sessiz issue düşürmek yerine iş report-only Failed olur. Daha büyük limit seçmek disk ve kaynak maliyetini artırır.

## Doğrulama ve build

Mevcut net48 build yönergeleri geçerlidir; solution yedi projedir. `dotnet restore` yeni Open XML paketlerini NuGet'ten alır. Release paketlerinde `DoorsIntegration.Export.dll`, `DocumentFormat.OpenXml.dll` ve `DocumentFormat.OpenXml.Framework.dll` birlikte bulunmalıdır; paketleme betikleri bunları denetler. SDK .NET Framework hedefinde WindowsBase kullanır; Office Interop yoktur. Lisans: [resmî MIT lisansı](OPENXML-LICENSE.txt).

Otomatik testler üç formatta 20.000 satırlık tek geçişli kaynak, tüketilmiş kayıtların okuma sürerken GC ile serbest kalması, şema/Unicode/tarih/decimal/null koruması, surrogate sınırında chunk birleştirme, RTF/OLE, CSV injection, worksheet rollover/limit, OpenXmlValidator, hata/iptal/limit/atomik publish ve mevcut snapshot adaptörlerini kapsar. Bütün testler fake veriyle çalışır. Gerçek DOORS verisine erişilmedi veya veri değiştirilmedi. Excel'de interaktif açılış/usability testi yapılmadı.

Manuel kontrol için her formatta küçük mevcut onaylı snapshot'ı aktarın; scope/kimlik/sayaçları karşılaştırın, Türkçe/emoji/tırnak/newline/tarih ve uzun RTF chunk'larını geri birleştirin. XLSX'i desteklenen Excel/LibreOffice ortamında açın; repair uyarısı, formül veya OLE aktivasyonu olmamalıdır. CSV'yi açıkça UTF-8/virgül ile import edin; formül benzeri fixture metin olmalıdır. Düşük limit ve iptalde yalnız hata raporu, mevcut hedefte ise değişiklik olmaması beklenir. Test için DOORS nesne/link/attribute/baseline/ACL oluşturmayın veya değiştirmeyin.

Kaynaklar: [Open XML SDK NuGet 3.5.1 / net48 uyumu](https://www.nuget.org/packages/DocumentFormat.OpenXml/3.5.1), [Microsoft SAX/OpenXmlPartWriter rehberi](https://learn.microsoft.com/en-us/office/open-xml/spreadsheet/how-to-copy-a-worksheet-with-sax), [Excel sınırları](https://support.microsoft.com/en-us/excel/excel-specifications-and-limits), [OWASP CSV Injection](https://community.owasp.org/attacks/CSV_Injection).
