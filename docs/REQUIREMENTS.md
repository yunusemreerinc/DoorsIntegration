# Formal modül gereksinimlerini okuma

`IRequirementService` / `RequirementService`, `IRequirementReader` sözleşmesi üzerinden formal modül nesnelerini okur. `DxlRequirementReader`, harici `scripts/dxl/requirements.dxl` dosyasını `IDxlExecutor` ile çalıştırır. Console `AddDoorsIntegration()` ve Desktop `AddDoorsDesktop()` kayıtları servisi içerir; host kendi reader uygulamasını `TryAdd` öncesinde kaydedebilir veya `Replace` kullanabilir. Servis çözümleme DOORS bağlantısı kurmaz. Özellik C# API'si ve Desktop Workspace → Requirements üzerinden kullanılır; Discovery modül meta verisi keşfini sürdürür.

Desktop **Workspace → Requirements**, current/baseline seçenekleri, özel alanlar, 100 nesnelik sayfalar, raw-rich detail/save ve export için bu API'yi kullanır; [arayüz rehberi](DESKTOP-WORKSPACE.md). Mevcut read API'sine eklenen attribute şeması, create/update/move, dry-run/açık onay ve kısmi sonuç API'leri ayrı [gereksinim yazma rehberindedir](REQUIREMENT-WRITING.md). Bu sayfadaki streaming/baseline/salt okunur okuma davranışı korunur; attribute DXL opt-in seçeneği Desktop'ta ayrıca review ister.

İki seçili baseline'ın aynı native modüle ait olduğunu doğrulayarak karşılaştırmak için [BaselineComparisonService](BASELINE-COMPARISON.md) kullanılır. Okuyucu artık v2 çıktısında `RequirementReadSummary.QualifiedModuleId` döndürür; legacy v1'de bu bilgi null kalır ve kimlik gerektiren karşılaştırma reddedilir.

## Kullanım ve kaynak ömrü

```csharp
var service = provider.GetRequiredService<IRequirementService>();
var request = new RequirementReadRequest(
    "/Project/Folder/Formal Module",
    customAttributes: new[] { "Priority", "Owner", "Verification Status" },
    deletedObjects: DeletedObjectMode.Exclude,
    allowAttributeDxl: false,
    openIfNeeded: true,
    timeout: TimeSpan.FromMinutes(3),
    connectionMode: DoorsConnectionMode.AttachExisting,
    target: DoorsVersion.V97);

using (var snapshot = await service.ReadAsync(request, cancellationToken))
{
    var summary = snapshot.Summary;
    if (summary.ModuleStatus != RequirementModuleStatus.Available)
    {
        // Durumu kullanıcıya raporlayın: erişilemeyen modül, başarılı boş modül değildir.
        ReportModuleStatus(summary);
        return;
    }
    ReportCompleteness(summary);
    foreach (var item in snapshot.ReadObjects())
    {
        // Bir nesneyi işleyip bırakın; büyük modüllerde ToList/ToArray kullanmayın.
        WriteRequirement(item);
    }
}
```

`ReportModuleStatus`, `ReportCompleteness`, `WriteRequirement` ve `provider`, host uygulamanızın sağlayacağı bileşenlerdir. UI olayında `await` kullanın; senkron `.Result` ile arayüzü bekletmeyin. Servis kendi STA executor'ünü kullanır.

Sonuç önce DOORS istemcisinin erişebildiği yerel, işleme özel UTF-8 dosyasına yazılır. COM `Result` alanından yalnız istek kimlikli tamamlanma işareti geçer. COM çağrısı ve temizliği bittikten sonra dosyanın tamamı doğrulanır; snapshot ancak bundan sonra teslim edilir. `ReadObjects()` dosyadan nesne nesne, tekrar okunabilir bir `IEnumerable<RequirementRecord>` döndürür. Bu, COM çalışırken canlı satır akışı değildir. Doğrulama sırasında içerik biriktirilmez; yinelenen kimlikleri saptamak için sınırlı bir `HashSet<int>` tutulur. DOORS'un modülü belleğe yüklemesi ve DXL'in kendi string tablosu bu .NET aktarım sınırlarından bağımsızdır.

`snapshot.Dispose()` yeni okuyucuları durdurur. Açık stream/enumerator varsa dosya temizliği son okuyucu kapanana kadar ertelenir. `foreach` ve erken `break` enumerator'ü kapatır; elle alınan enumerator ve `OpenRawStream()` sonuçları da `using` ile kapatılmalıdır. Dosyanın kalıcı kopyası gerekiyorsa snapshot açıkken `OpenRawStream().CopyTo(...)` ile hostun açıkça seçtiği bir hedefe kopyalayın. Geçici dosyalar uygulama hesabının temp alanındadır; süreç çökmesi/işletim sistemi kapanması sonunda artakalan dosyaların temizliği garanti edilmez. İçerik loglanmaz.

## Okunan alanlar ve kimlik

| Model alanı | DXL kaynağı ve anlamı |
| --- | --- |
| `AbsoluteNumber` | `o."Absolute Number"`; modül içindeki nesne kimliği |
| `ObjectIdentifier` | `identifier(o)`; DOORS önekiyle birlikte gösterilen kimlik |
| `ObjectNumber`, `NumberStatus` | `number(o)`; hiyerarşik gösterim numarası, okunamazsa açık durum |
| `Heading`, `Text` | Object Heading / Object Text değerlerinin düz ve zengin metin temsilleri |
| `HierarchyLevel` | `level(o)`; kök nesnede 1 |
| `ParentAbsoluteNumber`, `ParentStatus` | `parent(o)` ile doğrudan ebeveyn; `Root`, `Resolved` veya `Unavailable` |
| `SourceOrder` | `entire(module)` dolaşımındaki sıra; erişilemeyen/dışlanan nesneler nedeniyle boşluk olabilir |
| `IsDeleted` | `isDeleted(o)`; istenen silinmiş nesne politikasına göre |
| `Values[name]` | İstenen nesne attribute'u; tip adı, düz metin, rich text ve ayrı erişim/okuma durumları |

Kalıcı nesne eşleştirmede v2 çıktısındaki `Summary.QualifiedModuleId` ile `AbsoluteNumber` birlikte kullanılır; baseline/current seçimi ayrıca sürüm bağlamı olarak saklanır. Absolute Number tek başına veritabanları arası GUID değildir; modül yolu yeniden adlandırılabilir. Legacy v1'de native modül kimliği yoktur ve kimlik doğrulamalı baseline karşılaştırması yapılmaz. Object Identifier öneki ve Object Number değişebilir. `ObjectNumber` görüntüleme bağlamına bağlı olabilir; kimlik anahtarı olarak kullanılmamalıdır. Ebeveyn silinmiş/dışlanmış ya da okunamıyor olabilir; mevcut kayıtlardan tahmini ebeveyn üretilmez. `Resolved` ebeveynin sonuç kümesinde mutlaka bulunduğu anlamına gelmez.

`entire(module)` filtrelenmiş görünümden bağımsız dolaşır; table/row header/cell nesneleri de dahil olabilir. Bu sürüm tablo yerleşimini yeniden kurmaz. Current modül okuması atomik veritabanı snapshot'ı değildir. Tekrarlanabilir bir sürüm gerekiyorsa var olan baseline'ı açıkça seçin.

## Rich text ve özel attribute'lar

Her `RequirementValue`, `PlainText` ve `RichTextWithOle` alanlarını ayrı taşır. `PlainStatus` ve `RichTextStatus` birbirinden bağımsızdır. Text/String tabanlı attribute'larda belgelenmiş `richTextWithOle(o.attributeName)` çıktısı escape/decode dışında değiştirilmez. `RichTextFormat == "DoorsRtfWithOle"`, DOORS'un RTF veya RTF parçasını ve gömülü OLE verisini belirten bir formattır; tam bir bağımsız `.rtf` belgesi olduğu varsayılmaz. Düz metin, özgün zengin metnin yerine geçirilmez; HTML/Markdown dönüşümü yapılmaz, OLE nesneleri etkinleştirilmez.

Sayısal, tarih, kullanıcı ve enumeration değerleri DOORS'un gösterim metni ve attribute tip adıyla saklanır; rich text `NotApplicable` olur. Tarih/ondalık veya enumeration değerlerini kültürden bağımsız bir şema gibi yorumlamayın. Çok değerli enumeration gösterimi aynen korunur; seçenek kimlikleri/tip tanımları ayrıca dışa aktarılmaz.

Özel attribute adları açık listeyle seçilir; en fazla 64, her ad en fazla 256 karakter ve benzersiz olmalıdır. Kontrol karakterleri, bozuk Unicode ve yerleşik alanların tekrarı reddedilir. İsimler DXL koduna eklenmez; doğrulanmış ve escaped string parametreleridir. Varsayılan `allowAttributeDxl: false`, DXL ile hesaplanan attribute değerlerini değerlendirmez ve `DxlEvaluationDisabled` döndürür. Bu seçenek `true` yapılırsa şirketin attribute DXL kodu çalışabilir.

Boş ama okunabilir değer ile başarısız okuma ayrıdır. `AccessDenied`, `DeniedOrNotFound`, `NotObjectAttribute`, `DxlEvaluationDisabled`, `ReadFailed` ve `TooLarge` durumlarında içerik boş tutulur. Beklenmeyen DXL dolaşım/alan hatasında tamamlanmış önceki nesneler korunur, `EnumerationFailed` ve eksik nesne sayısı raporlanır; tamamlanmamış nesne başarıyla okunmuş gibi sunulmaz. Yakalanamayan DXL hatası veya eksik footer bütün aktarımın reddine yol açar.

## Erişim, silinmiş nesneler ve baseline

Varsayılan `DeletedObjectMode.Exclude`, silinmiş nesneleri çıkarır ve sayısını `DeletedExcludedCount` içinde verir. `MetadataOnly`, silinmiş nesnenin kimlik/hiyerarşisini döndürür; Heading/Text ve özel attribute'lar `NotRequested` olur. `IncludeContent`, izin verildiği ölçüde içeriği de okur. Hiçbir seçenek undelete/delete işlemi yapmaz. Okunamayan nesnenin kimliği ve içeriği aktarılmaz; sayılabilenler `DeniedObjectCount` içinde raporlanır. DXL kullanıcının göremediği nesneleri hiç dolaştırmayabilir; sayaçlar tam ACL envanteri değildir. `Visibility` bu nedenle `ReadableObjectsOnly` değerindedir.

`baseline: null`, current sürümdür. Var olan bir baseline için örneğin `baseline: new RequirementBaseline(2, 1, "review")` kullanın; suffix yoksa boş string bırakın. Betik `moduleVersion(ModName_, Baseline)` ile belirli sürümü bağlar ve `baselineExists(ModuleVersion)` ile erişilebilirliğini kontrol eder. Aynı handle `data`/`load` için kullanılır; baseline/current durumu ayrıca `isBaseline(Module)` ile doğrulanır. Baseline bulunamadığında veya okunamadığında `BaselineUnavailable` döner; current sürüme geri dönülmez. Baseline yaratma/değiştirme yoktur.

Modül zaten salt okunur açıksa `BorrowedReadOnly` olarak kullanılır ve kapatılmaz. İstenen sürüm edit/share modunda açıksa `AlreadyOpenForWrite` döner. Kapalı sürüm, `openIfNeeded: true` ise belgelenmiş salt okunur `load(ModuleVersion, false)` ile yüklenir; yalnız servisin açtığı modül kapatılır. Kapatma başarısızsa `CleanupStatus.Failed` raporlanır. `openIfNeeded: false`, yalnız önceden açılmış salt okunur sürümü kabul eder; aksi halde `NotLoadedByPolicy` döner.

Kod modül/nesne/attribute/baseline/ACL yazmaz, save/edit/share çağırmaz ve mevcut edit oturumunu kullanmaz. İstemcide tanımlı üçüncü taraf module-open trigger'ları salt okunur yüklemede de çalışabilir. Yalnız önceden açılmış sürüm kullanılacaksa `openIfNeeded: false` seçin; gerçek ortam testi onaylı, yazma yetkisi olmayan hesap ve mevcut salt okunur test verileriyle yapılmalıdır.

`ModuleStatus` diğer durumları: `DeniedOrNotFound`, `AccessDenied`, `NotFormalModule`, `ModuleDeleted`, `ReadFailed`. `IsCompleteForReadableObjects`, modül kullanılabilirken kesilme/dolaşım hatası, sayılan erişim hatası, alan sorunu veya kapatma hatası yoksa true olur. Seçilen politika gereği dışlanan silinmiş nesneler eksiklik sayılmaz. Bu değer görünmeyen veriye erişimin kanıtı değildir.

## Sınırlar, hatalar ve timeout

Varsayılan sınırlar: 1.000.000 ziyaret edilen nesne, alan başına 4.194.304 karakter, nesne başına düz+rich toplam 16.777.216 karakter ve 256 MiB sonuç dosyasıdır. İstek ayrıca daha düşük sınırlar seçebilir. Nesne sınırı ziyaret edilenleri kapsar; erişimi reddedilen ve dışlanan silinmiş nesneler de buna dahildir. Alan büyükse kırpılmaz; `TooLarge` olur. Nesne bütçesine sığmayan attribute'un iki temsili de `TooLarge` olur. Kimlik/hiyerarşi alanları ve protokol satırları da doğrulanır.

Betik her nesneyi ayrı buffer ile tamamlayıp dosyaya yazar. Dosya sınırına yaklaşırken bir sonraki nesneye başlamış olsa da dosyaya eksik kayıt eklemez; `IsTruncated` döner. UTF-8 için konservatif tahmin kullanıldığından sınırın altında durabilir. Executor ayrıca gerçek byte boyutunu denetler. Büyük bir tek attribute önce DOORS API tarafından malzemeleşir; .NET sınırları DOORS'un kendi belleğini sınırlamaz. Çevrim denetimi merkezi `DxlWatchdogSettings` ayarından gelir: varsayılan `pragma runLim, 1000000`; Settings ekranında kapatılıp kaydedilirse `pragma runLim, 0`. Kaynaktaki eski sabit sınırsız pragma kaldırıldı. Nesne/dosya sınırları dolaşımı sınırlar, attribute DXL/trigger/COM içindeki beklemeleri kesmez. [Watchdog ayarı](DXL-WATCHDOG.md).

Dosya istek kimliği, yol, sürüm seçimi, baseline işareti, nesne sırası/kimlikleri, alan adları, durumlar, sayımlar ve tamamlanma footer'ı açısından doğrulanır. Bozuk/eksik veya başka isteğe ait dosya `RequirementProtocolFailure` (**32**) üretir. Mevcut executor hataları: script yükleme **26**, gerçek dosya boyutu **27**, dosya/cleanup **28**, kontrollü DXL hatası **29**, COM hata kodları **10–22**. Hatalar hostun `IntegrationErrorHandler` bileşenine aktarılabilir; loglarda içerik yerine istek kimliği, durum ve sayaçlar bulunur.

Timeout **23** veya bekleme iptali **24**, başlamış COM çağrısını güvenli biçimde iptal edemez. Worker, modül kapatma girişimi ve COM temizliği bitene kadar yaşar; ortak gate **25** ile yeni işlemi reddeder. Servis geç gelen dosya sonucunu otomatik dispose eder, geç hatayı gözlemler. Zorla thread/process sonlandırma yapılmaz. Süre sınırı COM beklemesini kapsar; son dosya doğrulaması da cancellation token'ı satır/alan sınırlarında kontrol eder. Snapshot tesliminden sonraki yerel `ReadObjects()` tüketimi bu token'a bağlı değildir.

## Manuel doğrulama

Bu adımlar mevcut onaylı test verilerini kullanır; veri, ACL veya baseline oluşturmayın/değiştirmeyin. Her senaryoyu **9.5 / 9.7 × x86 / x64** matrisiyle, istemcinin kayıt ve mimari durumunu `doors diagnose` ile kaydederek yürütün. Desteklenmeyen COM mimarisi başarı gibi raporlanmamalıdır. Bu ortamda gerçek istemciye bağlanılmadı; DXL gerçek IBM yorumlayıcısında derlenmedi/çalıştırılmadı.

| Senaryo | Beklenen sonuç |
| --- | --- |
| Mevcut current formal modül, salt okunur açık | `BorrowedReadOnly`; okuma bitince pencere/modül açık kalır |
| Kapalı current, open true / false | `OwnedReadOnly` ve `Closed` / `NotLoadedByPolicy`; edit kilidi ve kaydetme yok |
| Mevcut edit/share oturumu | `AlreadyOpenForWrite`; oturum modu, değişiklikler ve pencere korunur |
| Bilinen heading/text/custom değerler | Kimlik, düz metin, tip adı ve boş değer durumları DOORS ile eşleşir |
| Kalın/italik, listeler, Unicode, hyperlink, gömülü mevcut OLE | Ham `RichTextWithOle` özgün API çıktısıyla eşleşir; OLE aktive edilmez; plain ayrı kalır |
| Çok seviyeli nesneler ve mevcut tablolar | Absolute Number, level, parent ve Object Number kontrol edilir; tablo yardımcı nesneleri kaydedilir |
| Filtreli görünüm ve gizli/deleted ebeveyn | Dolaşım kapsamı ve `Unavailable` ebeveyn durumu doğrulanır; ebeveyn uydurulmaz |
| Var olan silinmiş nesne, üç politika | Exclude sayımı / metadata ve NotRequested / izinli içerik; undelete yok |
| Kısıtlı nesne veya attribute | Okunamayan içerik yok; uygun erişim durumu ve kısmi summary |
| Olmayan/erişilemeyen modül veya attribute | Açık `DeniedOrNotFound`/diğer durum; başarılı boş okuma gibi gösterilmez |
| Mevcut DXL attribute, allow false | `DxlEvaluationDisabled`; test için şirket kodunu çalıştırmayı açmayın |
| Var olan 2.1 ve suffix'li baseline | Tam istenen sürümün bilinen kimlik/içeriği; current'a geri dönüş yok |
| Var olmayan veya erişilemeyen baseline | `BaselineUnavailable`, sıfır nesne, current yüklenmez |
| Büyük mevcut modül ve düşük nesne/dosya/alan limitleri | Tek dosya, kademeli .NET tüketimi; Truncated/TooLarge açıkça raporlanır; kırpılmış rich text başarı sayılmaz |
| Bekleyen istemcide kısa timeout | 23; çağrı/gate yaşamaya devam eder; doğal tamamlanmada owned modül ve temp dosya temizlenir |
| Snapshot stream'i açıkken Dispose | Yeni okuyucu reddedilir; son okuyucu kapanınca dosya silinir |
| Tamamlanan/yarım kalan okuma sonrası | History, baseline listesi, nesne sayıları, ACL ve mevcut edit oturumlarında değişiklik yok |

Gerçek istemci çöker veya DXL hatası normal cleanup akışını engellerse modülün salt okunur açık kalması mümkündür; eksik tamamlanma sonucu reddedilir. Saha test raporunda DXL derleme/runtime çıktısı, client fix pack, mimari, sürüm seçimi, sahiplik, cleanup, sayaçlar ve dosya boyutunu kaydedin; gereksinim içeriğini loglara koymayın.

## API kaynakları

Uygulamadaki `moduleVersion`, `baselineExists`, `data`, salt okunur `load`, `entire`, `identifier`, `number`, `level`, `parent`, erişim kontrolleri, attribute tanımları, `richTextWithOle`, Unicode stream ve pragma adları IBM'in [DXL Reference Manual 9.7.2](https://www.ibm.com/docs/en/SSYQBZ_9.7.2/com.ibm.doors.requirements.doc/topics/dxl_reference_manual.pdf) ve [9.6 DXL Reference Manual](https://www.ibm.com/docs/en/SSYQBZ_9.6.0/com.ibm.doors.requirements.doc/topics/dxl_reference_manual.pdf) içindeki ilgili başlıklara göre seçilmiştir. Eski sürüm karşılaştırması: [IBM 9.2 DXL Reference Manual](https://download.boulder.ibm.com/ibmdl/pub/software/rationalsdp/documentation/product_doc/DOORS/version_9-2/dxl_reference_manual.pdf). Kurulu **9.5** istemcinin sürüme özel Help/DXL Reference Manual'ı ve gerçek derleme testi dağıtım doğrulamasının parçasıdır. Kod doküman doğrulamasını gerçek 9.5/9.7 uyumluluk sertifikası olarak sunmaz.
