# BaselineComparisonService

`IBaselineComparisonService` / `BaselineComparisonService`, aynı formal modülün iki **seçili baseline** sürümünü salt okunur karşılaştırır. Core içindeki motor yalnız `IRequirementReader` ve `IIntegrationLogger` portlarına bağlıdır; COM/DXL veya Excel bağımlılığı yoktur. Console ve Desktop DI kayıtları servisi çözer. Bu değişiklik servis API'si ve rapor kaynağı sağlar; yeni bir Desktop sayfası veya CLI komutu eklemez.

## Kimlik ve okuma sözleşmesi

Eşleştirme anahtarı **native `qualifiedUniqueID(modül)` + nesnenin `Absolute Number` değeri**dir. Baseline sürümü eşleştirme anahtarının parçası değildir: aynı nesne iki sürümde eşleşmelidir. Olaylar ve raporlar her iki baseline'ın major/minor/suffix bileşenlerini ayrıca taşır. Aynı yol, Object Identifier, Object Number veya satır sırası tek başına kimlik kanıtı değildir.

IBM, Absolute Number'ın oluşturulan nesneye özgü olduğunu ve yeniden kullanılmadığını açıklar. Bu nedenle doğrulanmış aynı modül kapsamında nesne eşleştirmesine uygundur. [IBM: Creating objects](https://www.ibm.com/docs/en/engineering-lifecycle-management-suite/doors/9.7.2?topic=doors-creating-objects). `qualifiedUniqueID` Item/Module overload'ları, veritabanları arasında nitelikli kimlik döndürür. [IBM: DXL Reference Manual, uniqueID / qualifiedUniqueID](https://www.ibm.com/docs/en/SSYQBZ_9.7.2/com.ibm.doors.requirements.doc/topics/dxl_reference_manual.pdf).

`requirements.dxl` çıktısı artık `DOORS_REQUIREMENTS\t2` başlığından sonra M satırını ve `Q\t<qualifiedModuleId>` satırını içerir. Kimlik aynı native okuma içinde Item'dan alınır ve yüklenen Module ile tekrar doğrulanır. İki baseline'ın kimlikleri birebir eşleşmelidir. Eski v1 aktarımı genel RequirementReader tarafından okunabilir, ancak kimlik doğrulaması gereken baseline karşılaştırmasında reddedilir. Harici betik yolunu özelleştirdiyseniz v2 çıktısını da güncelleyin; eski DLL/yeni script karışımı kullanmayın.

Her iki istek de `Baseline != null`, `DeletedObjects=MetadataOnly`, `AllowAttributeDxl=false` kullanır. Var olan read-only `moduleVersion` / `baselineExists` / `load(..., false)` yolu kullanılır; edit/share/save/undelete/baseline oluşturma veya ACL değişikliği yapılmaz. Borrowed read-only modüller kapatılmaz; bu çağrının açtığı read-only modüller mevcut okuyucunun sahiplik kurallarıyla temizlenir. [IBM: Baselines](https://www.ibm.com/docs/en/engineering-lifecycle-management-suite/doors/9.7.1?topic=requirements-baselines).

Güncel sürüm, aynı baseline'ın iki kez seçilmesi, eksik native kimlik, yanlış modül/sürüm, erişilemeyen baseline, nesne erişim reddi, eksik enumeration, truncation, cleanup hatası, duplicate ID ve sayaç uyuşmazlığı tamamlanmış sonuç üretmez. `BaselineComparisonFailed` hata kodu 52'dir; mevcut native timeout/COM/protokol kodları korunur. İkinci okuma başarısızsa ilk snapshot ve yerel çalışma dosyası da serbest bırakılır. Başlamış COM çağrısı timeout/iptal ile zorla kesilmez; mevcut executor completion/gate kuralları geçerlidir. Timeout **her baseline okuması için** uygulanır; toplam işlem süresi iki okuma ve yerel analizi içerebilir.

## Durumlar ve karşılaştırma kapsamı

Varsayılan alanlar `Object Heading` ve `Object Text`tir. `customAttributes` seçimi iki baseline'a aynı şekilde uygulanır (mevcut okuyucunun 64 özel alan sınırı). Modüldeki bütün attribute'ların otomatik keşfedildiği veya attribute tanımı silinmesinin erişim reddinden ayırt edildiği iddia edilmez. `DeniedOrNotFound` değerinden schema deletion çıkarımı yapılmaz; istenen alan adları birebir ve büyük/küçük harfe duyarlı eşleşir.

| Nesne durumu | Anlamı |
| --- | --- |
| Added | Önceki sürümde canlı nesne yok; sonraki sürümde canlı nesne var. Tombstone'dan geri dönüş buna dahildir. |
| Deleted | Önceki sürümde canlı nesne var; sonraki sürümde yok veya tombstone. |
| Modified | En az bir kanıtlanmış plain/rich/type veya parent/level farkı var. |
| Unchanged | Seçili alanlarda ve parent/level'da fark yok; karşılaştırılabilir bilgiler mevcut. İki tarafta da canlı olmayan tombstone kayıtları da bu gruptadır. |
| Indeterminate | Canlı nesneler eşleşti, kanıtlanmış fark yok, ancak alan/parent bilgisi kesin sonuca yetmiyor. |

`IsConclusive` ayrıca nesne, alan ve toplam düzeyinde bulunur. Örneğin Object Text değişmiş, özel alan okunamamışsa nesne **Modified** kalır ama `IsConclusive=false` olur. Added/Deleted kimlik/popülasyon sonucunu, okunamayan içerik değerlerinden ayırın. İki taraftaki tombstone'ların içerikleri karşılaştırılmaz; silinmiş içerik okunmak üzere zorlanmaz. Yok nesne `null`, tombstone `IsDeleted=true` olarak korunur.

Attribute satırları Added/Deleted/Modified/Unchanged/NormalizationOnly/Unavailable durumunu, önce/sonra plain/rich erişim durumlarını, tip adlarını ve ham değerleri taşır. Attribute Added/Deleted, bu nesnenin yaşam döngüsündeki alan görünümünü belirtir; global attribute schema create/delete olayı değildir.

Parent kimliği veya hierarchy level değişirse `HierarchyChanged=true` olur. Object Number, Object Identifier prefix'i ve SourceOrder görüntüleme metadatası olarak önce/sonra saklanır; tek başlarına Modified üretmezler. Aynı parent/level içindeki sibling sıra değişimi bu sürümde semantik taşıma olayı olarak analiz edilmez. Bu kapsam sınırı raporu okuyan etki analizi tarafından dikkate alınmalıdır.

## Rich text ve normalizasyon

- Varsayılan `UnicodeNfcAndLineEndings`, **yalnız plain text** için Unicode NFC ve CRLF/CR → LF uygular. Case, boşluklar, trailing whitespace, NBSP, sayısal veya tarih formatları sadeleştirilmez. Tip değerleri locale tahminiyle dönüştürülmez.
- `Exact` seçeneği plain text'te de ordinal, ham karşılaştırma yapar.
- Ham önce/sonra değerler her iki politikada korunur. Sadece normalizasyonla eşdeğer plain farkı `NormalizationOnly` olarak görünür; nesne başka fark yoksa Unchanged kalır ve impact olayı üretmez.
- `richTextWithOle` **opaque ve birebir** karşılaştırılır. RTF render, OLE aktivasyonu veya kayıplı plain-text dönüşümü yapılmaz. Sadece RTF/OLE değişmişse bile Modified / RichTextChanged oluşur. Bu, görsel/semantik fark garantisi değildir; RTF serializer farklılıkları da konservatif değişiklik sayılır. Plain normalizasyonu opaque RTF'ye uygulanmaz.
- Okunamayan, TooLarge veya DxlEvaluationDisabled alanları boş metin/Unchanged kabul edilmez. Rich karşılaştırması için bir tarafta veri yoksa kesinlik düşer. Attribute DXL hesaplatmak için hiçbir otomatik retry yapılmaz.

## API ve JSON / Excel raporu

```csharp
var service = provider.GetRequiredService<IBaselineComparisonService>();
var request = new BaselineComparisonRequest(
    "/Project/Folder/FormalModule",
    new RequirementBaseline(1, 0, "A"),
    new RequirementBaseline(2, 0, "B"),
    customAttributes: new[] { "Status", "Verification Method" },
    normalization: BaselineTextNormalization.UnicodeNfcAndLineEndings,
    target: DoorsVersion.V97); // veya V95

using (var comparison = await service.CompareAsync(request, cancellationToken))
{
    foreach (var change in comparison.ReadChanges(cancellationToken))
        Console.WriteLine($"{change.Identity.Key}: {change.Status}; conclusive={change.IsConclusive}");

    var exporter = provider.GetRequiredService<IExportService>();
    var source = new BaselineComparisonExportSource(comparison);
    var json = await exporter.ExportAsync(
        new ExportRequest(@"C:\Reports\baseline-json", ExportFormat.Json), source,
        cancellationToken: cancellationToken);
    var excel = await exporter.ExportAsync(
        new ExportRequest(@"C:\Reports\baseline-excel", ExportFormat.Xlsx), source,
        cancellationToken: cancellationToken);
    // ExportStatus, HasPayload, ReportPath ve issue sayısını kontrol edin.

    foreach (var changeEvent in comparison.ReadEvents(cancellationToken))
        impactQueue.Accept(changeEvent); // caller'ın kuyruğu; servis dış sisteme kendisi göndermez
}
```

Gerekli namespace'ler `DoorsIntegration.Core`, `DoorsIntegration.Export`, DI için `Microsoft.Extensions.DependencyInjection`dır. `cancellationToken` ve `impactQueue` host tarafından sağlanır. Sonuç lease'i export/olay enumeration tamamlanana kadar açık tutulmalıdır. Dispose sonrasında enumeration reddedilir.

İki native snapshot sırayla alınır; tüm nesne gövdeleri bellekte biriktirilmez. İçerik yalnız yerel, işlem kimliğine özgü, paylaşılmayan `objects.bin` dosyasına yazılır. Bellekte Absolute Number → dosya offset indeksleri ve sıralı kimlik listesi kalır; karşılaştırma her seferinde en fazla bir nesne çiftini yükler. Varsayılan sınırlar baseline başına 100.000 nesne ve karşılaştırma başına toplam 1 GiB spool'dur; okuyucunun field/object/file sınırları ayrıca geçerlidir. Beklenmedik process sonlandırmasında Windows DeleteOnClose dosyayı temizler; boş geçici klasör kalabilir. Normal Dispose yalnız bu işlemin dosyasını ve boş klasörünü temizler; recursive cleanup yapmaz. Sonuç paylaşılabilir yerel analiz çıktısıdır; veritabanına uygulanacak yazma planı değildir.

Rapor `IExportService` üzerinden üretilir; yeni NuGet bağımlılığı yoktur. Excel için mevcut Open XML SDK 3.5.1 ve streaming writer kullanılır. JSON `data.json`, XLSX `data.xlsx`, her iki formatta `report.json` oluşur. Dolu karşılaştırma tabloları ortak rapora eklenir:

- `BaselineComparisons`: native kimlik, yol, iki baseline'ın major/minor/suffix'i, normalizasyon politikası, UTC zaman ve durum sayaçları.
- `BaselineObjects`: kalıcı kimlik, nesne durumu, kesinlik, önce/sonra görüntüleme/hiyerarşi/silinme metadatası.
- `BaselineAttributes`: seçili alanlarda tip/plain/opaque rich önce-sonra değerleri, erişim durumları ve fark bayrakları.
- `BaselineEvents`: etki analizi olay kimlikleri, sürümler, durum, alan adları, hierarchy ve review bayrakları.

Mevcut exportların sekiz tablosu korunur; yeni tablolar yalnız doluyken eklenir. Uzun rich/plain değerler ortak `ValueChunks` tablosunda ownerTable/ownerRecordId/column/chunkIndex ile kayıpsız taşınır. Excel'in hücre karakter sınırını aşan bir değeri yalnız placeholder hücresinden okumayın; chunk'ları sırasıyla birleştirin. XLSX XML `_xHHHH_` kaçışlarını **tek geçişte** çözmek gerekir. Ham değerler formül/hiperlink olarak çalıştırılmaz. Unicode, UTC zaman, null/boş ayrımı ve kaynak/çıktı kotası mevcut export sözleşmesinden gelir. Her hedef yeni klasör olmalıdır; hata/iptalde tamamlanmamış payload yayımlanmaz. Kesin olmayan sonuçta Scopes.isComplete=false ve export warning bulunur.

## Etki analizi olayları

`ReadEvents()` yalnız Added/Deleted/Modified/Indeterminate nesneler için `BaselineChangeEvent` üretir. Nesne ve attribute delilleri olayın `Change` alanındadır. `RequiresReview=!IsConclusive`, `IsImpactCandidate=true` bir **aday** belirtir; downstream etki, link çözümlemesi veya standart uyumu kanıtı değildir. Normalizasyon-only değişikliği ayrıca incelemek isteyenler `ReadChanges()` kullanmalıdır.

`SchemaVersion=1`. `EventId`, qualified module+Absolute Number, yönlü baseline çifti, normalizasyon politikası, durum ve sıralı ham önce/sonra delillerinden length-prefixed UTF-8 SHA-256 ile hesaplanır. Aynı delillerin yeniden enumeration'ı ve aynı girdilerle yeni karşılaştırma aynı kimliği üretir; ters yön/politika/değişen delil farklıdır. `ComparisonId` her işlemde yenidir ve EventId hash'ine girmez. Transport target 9.5/9.7 bir delil kimliği değildir. Şema/hash algoritması değişirse olay versiyonu artırılmalıdır. Olaylar stream'dir; bus yayını, transaction, durable outbox veya exactly-once teslim garantisi yoktur. Consumer EventId ile kendi kalıcı dedup/outbox politikasını uygulamalıdır. Audit/diagnostic log gereksinim gövdelerini veya RTF'yi otomatik kaydetmez.

## Doğrulama ve manuel kabul

Mock `IRequirementReader` testleri dört ana durum, değişen Object Number/order/prefix, aynı görüntü numarası-farklı kalıcı ID, modül kimliği uyuşmazlığı, v1/v2 protokol, erişim/eksik enumeration/baseline/cleanup, duplicate ID, deletion/restoration, seçili özel alan/tip, Unicode/line endings/whitespace, rich-only/OLE farkı, bilinmeyen alan/parent, olay dedup, iptal/dispose ve kaynak limitlerini kapsar. JSON ve XLSX testleri uzun rich/Unicode içeriğini geri kurar; Open XML şema kontrolü ve aktif formül/hiperlink yokluğu doğrulanır.

Gerçek **9.5 / 9.7 × desteklenen x86 / x64 COM client** matrisi için önceden hazırlanmış, onaylı bir test modülü ve iki mevcut baseline kullanın. Bu kabul sırasında veri/baseline/ACL oluşturmayın veya değiştirmeyin:

1. `doors diagnose` ile client/profil/mimari kaydını doğrulayın. Kurulu sürümün DXL Reference Manual'ındaki qualifiedUniqueID(Module/Item) overload'larını kontrol edin; güncellenen requirements.dxl'i derleyin.
2. İki baseline'ı DOORS UI'da salt okunur açıp identity/Absolute Number, eklenen/silinen/değişmeyen/değişen nesne sayılarını karşılaştırın. Güncel modülün değişmediğini doğrulayın.
3. Önceden hazırlanmış renumber, parent taşıma, tombstone/restoration, farklı suffix, özel attribute ve format/OLE-only farklarını raporda inceleyin.
4. Kısıtlı kullanıcı/baseline unavailable/attribute denied durumlarında false Deleted veya kesin Unchanged üretilmediğini doğrulayın.
5. Önceden read-only açık modül borrowed kalmalı; edit açık veya cleanup başarısızsa başarı uydurulmamalı. Timeout sonrasında native gate ve geç completion kuralları korunmalı.
6. JSON/XLSX chunk geri kurmasını, Excel'de salt metin/formül güvenliğini ve olay kimliklerinin tekrar çalıştırmada kararlılığını kontrol edin.

**Bu geliştirmede native DXL gerçek DOORS 9.5/9.7 yorumlayıcısında çalıştırılmadı; canlı DOORS verisi okunmadı veya değiştirilmedi.** Mock/managed testleri native uyumluluk sertifikası, DO-178C/DO-200B/DO-330 uyumluluğu veya araç kalifikasyonu garantisi değildir. Karşılaştırma seçili alanlarla sınırlıdır; schema/ACL/link/module-metadata farklarını ve bütün downstream etkiyi otomatik kapsamaz.
