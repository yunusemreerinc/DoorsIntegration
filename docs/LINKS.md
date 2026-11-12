# LinkService ve traceability çıktısı

`LinkService` / `ILinkService`, bir formal modülün **tek bir açık tam link module yolu** üzerinden incoming/outgoing Classic bağlantılarını okur. `ILinkReader` adaptörü `DxlLinkReader` ve harici `scripts/dxl/links.dxl` betiğidir. İki hostun DI kayıtlarında `ILinkService`, `ILinkReader`, `LinkScriptOptions` ve `ITraceabilityExporter` bulunur. `TryAdd` host override'larını korur; servis çözümlemek COM bağlantısı kurmaz. Bu özellik C# API'si üzerinden kullanılabilir.

Desktop **Workspace → Links**, bu servisle yön/baseline/endpoint/surrogate seçenekleri, sayfalama, ayrıntılar ve JSON/CSV traceability export sunar. Genel XLSX dahil export ayrı Workspace → Export ekranındadır. [Desktop kullanımı](DESKTOP-WORKSPACE.md).

## Kullanım

Link oluşturma ve kaldırma, ayrı `PreviewWriteAsync` / `ApplyWriteAsync` akışı ve **Workspace → Link editor** üzerinden sunulur. Dry-run ve açık onay zorunludur; Remove ayrıca ikinci onay ister. Mevcut read/export API'si salt okunur kalır. Kapsam, izin/kilit recheck, duplicate koruması ve önce/sonra karşılaştırma: [LINK-WRITING.md](LINK-WRITING.md).

```csharp
var links = provider.GetRequiredService<ILinkService>();
var exporter = provider.GetRequiredService<ITraceabilityExporter>();
var request = new LinkReadRequest(
    modulePath: "/Project/Requirements",
    linkModulePath: "/Project/Satisfies",
    direction: LinkDirection.Both,
    openIfNeeded: true,
    resolveEndpoints: true,
    target: DoorsVersion.V97);

using (var snapshot = await links.ReadAsync(request, cancellationToken))
{
    // Başarısız/kısmi kapsamı ve sayaçlarını raporlayın; sıfır satır tam kapsama kanıt değildir.
    ReportSummary(snapshot.Summary);
    foreach (var link in snapshot.ReadLinks())
        ProcessLink(link);

    exporter.ExportFile(@"C:\Reports\traceability.json", snapshot, TraceabilityFormat.Json);
    exporter.ExportFile(@"C:\Reports\traceability.csv", snapshot, TraceabilityFormat.Csv);
}
```

`provider`, `ReportSummary`, `ProcessLink` ve mevcut hedef klasör host tarafından sağlanır. UI olayında `await` kullanın. `Incoming` ve `Outgoing`, anchor formal modüle göre gözlem yönüdür; `Source` her zaman gerçek kaynak, `Target` gerçek hedeftir. Aynı fiziksel bağlantı anchor içindeyse `Both` kapsamında outgoing ve incoming gözlemleri ayrı satırlar olabilir. `Sequence` yalnız aktarım içindeki sıradır, kalıcı link ID'si değildir; `LinkCount` gözlem sayısıdır.

`baseline: new RequirementBaseline(2, 1, "review")` anchor'ın var olan baseline'ını seçer. Bağlı uçların sürümleri linkin `sourceVersion`/`targetVersion` handle'larından alınır. `VersionKind` Current/Baseline/Unknown ve `Version` baseline etiketi ayrı tutulur. İstenen veya linkteki baseline okunamadığında current'a geri dönüş yapılmaz. `LinkVersionKind.Current` için `Version` boş stringdir; Unknown için de boş olması sürümün current olduğu anlamına gelmez.

## Sonuç ve çözümleme

| Alan | Anlam |
| --- | --- |
| `LinkModulePath` | Tam link module yolu; tek istekte wildcard veya aynı adlı başka modül kullanılmaz |
| `LinkModuleResolution` | `Verified`: gerçek Link'in `module(link)` yolu doğrulandı; `FilterOnly`: yalnız filtre/referans bilgisi var |
| `Source`, `Target` | Modül yolu, sürüm türü/etiketi, nullable Absolute Number, okunabiliyorsa Object Identifier |
| `Endpoint.Resolution` | Resolved, DeniedOrNotFound, AccessDenied, ModuleDeleted, NotLoadedByPolicy, ModuleLimit, AlreadyOpenForWrite, UnresolvedObject, DeletedObject veya ReadFailed |
| `Resolution` | Resolved, EndpointUnresolved, IncomingReferenceOnly, SourceReferenceOnly veya ReadFailed |
| `IsEchoed` | Link kayıtlarında `echo(Link/LinkRef)` ile baseline'daki echoed bağlantı işareti; current bağlantının kanıtı değildir. Yalnız modül referansında uygulanamaz |
| `Kind` | `Link`: nesne düzeyinde bağlantı/referans; `IncomingSourceReference`: yalnız kaynak modül referansı |

Linkte saklanan `sourceAbsNo`/`targetAbsNo`, uç modül yüklenemese de elde edilebiliyorsa korunur. Display Identifier erişim yokken uydurulmaz; null Absolute Number bilinmeyen kimliktir, 0 değildir. Kimliği modül ve sürüm kapsamıyla saklayın. Object Identifier öneki değişebilir; yollar yeniden adlandırılabilir. Bilinen bir Absolute Number, nesnenin halen var veya okunabilir olduğunu kanıtlamaz.

Outgoing linkler kaynak modülden okunur; hedef çözümlenemese de satır korunur. Incoming okumada `LinkRef` / `sourceVersion` ile kaynak sürümü belirlenir ve politika izin veriyorsa salt okunur yüklenir. Kaynak nesnenin outgoing linklerinde aynı link module, **aynı hedef ModuleVersion** ve hedef Absolute Number doğrulanır. Bu doğrulama yoksa incoming referans `IncomingReferenceOnly` kalır; silinmiş/erişilemeyen uç Resolved yapılmaz.

IBM belgeleri incoming `Link` dolaşımının yüklenmiş kaynağa bağlı olduğunu belirtir; bazı metinlerde LinkRef kapsamı da yüklemeye bağlı tanımlanır. Ek `ModName_` / `each` kaynak referansı dolaşımı bu görünür boşlukları raporlar. Yalnız kaynak modül referansı görüldüğünde `IncomingSourceReference` / `SourceReferenceOnly` satırı üretilir; kaynak nesne ve sürümü bilinmeyebilir. Bu satırlar `SourceReferenceCount` içinde sayılır ve `LinkCount` içine girmez. Mevcut olmayan bilgi tahmin edilmez; fallback, bilinmeyen sürüm yerine current yüklemez. Tek modül referansı birden çok gizli linki temsil edebileceğinden bu sayı eksik link sayısı değildir.

`Visibility == ReadableLinkScopeOnly`. Kullanıcının göremediği linksets/modüller/nesneler hiç dolaştırılmayabilir. Tamamlanmış görünür dolaşım bütün veritabanının veya bütün ACL'lerin envanteri değildir. Modüller arası okuma atomik değildir. `IsCompleteForReadableScope` ancak anchor ve link module kullanılabilirken kesilme/dolaşım/erişim/çözümleme/surrogate/cleanup sorunu yoksa true olur. `UnresolvedLinkCount`, `SourceReferenceCount`, `DeniedObjectCount`, `DeletedExcludedCount` ve cleanup sayaçları ayrıca saklanır.

## Surrogate nesneler

Genel bir `isSurrogate` API'si veya kuruma özgü attribute adı varsayılmaz. Eşleme verilmezse `SurrogateStatus.NotConfigured` döner; bu **normal nesne** demek değildir. Mevcut entegrasyon şemanız biliniyorsa isteğe örneğin aşağıdaki eşlemeyi ekleyin:

```csharp
surrogateMapping: new SurrogateMapping(
    markerAttribute: "Artifact Kind",
    markerValue: "Surrogate",
    referenceAttribute: "External Artifact ID")
```

Bu adlar örnektir; gerçek veritabanınıza ait doğrulanmış adları kullanın. İşaret değeri ordinal/tam eşitlikle karşılaştırılır. `Matched`, `NotMatched`, `Unavailable` ayrı durumlardır. Eksik/erişilemeyen, nesne attribute'u olmayan, çok büyük veya DXL ile hesaplanan işaret `Unavailable` olur; false gibi gösterilmez. `Matched` nesnenin dış kimliği/URI'si `ExternalReference`, okuma durumu `ReferenceStatus` ile döner. Dış kimlik alanı seçilmezse `NotRequested` kalır. DXL attribute'lar çalıştırılmaz.

Surrogate'un kendi DOORS Absolute Number/Identifier'ı ve dış sistem kimliği ayrı saklanır. Dış URI takip edilmez, entegrasyon senkronizasyonu başlatılmaz, yer tutucu nesne oluşturulmaz veya gerçek hedefe dönüştürülmez. `SurrogateProblemCount`, raporlanan uç gözlemlerindeki bilinmeyen eşleme/okuma sorunlarını sayar; aynı nesne birden çok linkte sayılabilir.

## Salt okunur sahiplik ve sınırlar

Anchor ve gereken formal uçlar `load(ModuleVersion, false)` ile salt okunur açılır. Mevcut salt okunur modüller ödünç alınır ve kapatılmaz. Mevcut edit/share sürümü kullanılmaz. `openIfNeeded: false` yeni formal modül yüklenmesini engeller. `resolveEndpoints: false`, anchor dışındaki uçlarda nesne çözümlemesini/yüklemeyi atlar; saklı yol/sürüm/absolute number bilgisi korunur. `maxLinkedModules` yalnız yeni yüklenen bağlı formal modülleri sınırlar; anchor buna ek olarak en fazla bir modüldür. Zaten açık salt okunur uçlar bu bütçeyi tüketmez.

Sahip olunan formal modüller ters açılış sırasıyla, kaydetmeden kapatılır. Başarısız kapanışlar `CleanupFailureCount` içinde raporlanır. Link dolaşımı sırasında istemcinin dolaylı yüklediği link module/linksets'in sahipliği varsayılmaz ve bunlar zorla kapatılmaz. `includeDeletedObjects: false` anchor'ın silinmiş nesnelerini çıkarır; true metadata/link dolaşımına dahil eder. Bağlı silinmiş uçlar ayrıca DeletedObject olarak raporlanabilir. Link/nesne silme veya undelete yapılmaz.

Varsayılan sınırlar: 1.000.000 ziyaret edilen anchor nesnesi, 100.000 çıktı kaydı (linkler ve kaynak referansları birlikte), 64 yeni bağlı formal modül ve 256 MiB dosya. `MaxObjects` erişimi reddedilen/dışlanan nesneleri de kapsar. Nesne/kayıt/dosya bütçesi dolduğunda `IsTruncated` döner. Dış surrogate kimliği en fazla 8192 DXL string birimi olarak alınır; aşılırsa `TooLarge`, kırpılmış başarı değeri olmaz. DXL string uzunluğu ile .NET UTF-16 karakter sayısı farklı olabilir. Dosya boyutu hem konservatif DXL tahminiyle hem gerçek byte sayısıyla denetlenir. Çevrim denetimi merkezi `DxlWatchdogSettings` ayarından gelir: varsayılan `pragma runLim, 1000000`; Settings ekranında kapatılıp kaydedilirse `pragma runLim, 0`. Kaynaktaki eski sabit sınırsız pragma kaldırıldı. Bu ayar endpoint içindeki yükleme/trigger veya doğrulama çağrısına sert zaman sınırı uygulamaz. [Watchdog ayarı](DXL-WATCHDOG.md).

Sonuçlar COM'dan büyük string olarak geçirilmez. İşleme özel UTF-8 dosyası tamamen doğrulandıktan sonra `ILinkSnapshot` döner; `ReadLinks()` bir satırı bellekte tutarak tekrar okunabilir akış sağlar. `ToList()` tüm matrisi belleğe alır; büyük aktarımda doğrudan foreach/export kullanın. `OpenRawStream()` ve elle alınan enumerator'leri kapatın. Snapshot dispose edilse bile mevcut okuyucu bitene kadar dosya korunur; son okuyucu kapandığında temizlenir. RequirementSnapshot ve LinkSnapshot aynı dosya sahipliği bileşenini kullanır. Süreç çökmesinde temp temizliği garanti edilmez; istemcinin modül/string belleği .NET aktarım sınırlarından bağımsızdır.

Timeout 23 / iptal 24 yalnız beklemeyi bitirir. Başlamış COM çağrısı öldürülmez; gate gerçek COM/cleanup bitene kadar 25 ile yeni işlemi reddeder. Geç dosya sonucu servis tarafından temizlenir, geç hata gözlemlenir. Token dosya doğrulamasında da kontrol edilir; snapshot tesliminden sonraki yerel tüketim bağımsızdır. Üçüncü taraf module-open trigger'ları salt okunur yüklemede çalışabilir; yalnız önceden açılmış sürüm kullanılacaksa openIfNeeded false seçin. Kod DOORS verisi veya bağlantı yapısını değiştirmez; DWA/OSLC kullanılmaz.

## JSON/CSV traceability sözleşmesi

Her iki çıktı **seyrek traceability matrisi için kaynak–hedef satırlarıdır**. Yoğun bir X/boş hücre tablosu oluşturulmaz: eksik/erişilemeyen link boş hücreyle “bağlantı yok” şeklinde sunulmaz. `recordKind == Link` satırları bağlantı gözlemleri, `IncomingSourceReference` satırları eksik kaynak bilgisi olarak işlenmelidir. `resolution == Resolved` ve sürüm/echo bayrakları değerlendirilmeden doğrulanmış iz ilişkisi saymayın. Tek hücre görünümü için modül+sürüm+Absolute Number uçları ve link module yoluyla gruplayın; gözlem yönünü ve çözümlenmemiş satırları koruyun. Link bulunmayan gereksinimlerin coverage analizi için aynı kapsamın RequirementService çıktısı ile ayrıca join gerekir.

JSON `schemaVersion: 1`, `summary` ve `links` dizisi içerir. Nullable kimlikler JSON null, sayımlar/Absolute Number sayısal, bayraklar boolean'dır. Surrogate dış kimliği dahil stringler kayıpsız escape edilir. CSV UTF-8, virgül ayraçlı, çift tırnak kaçışlı ve CRLF satırlıdır; embedded newline korunur. UTF-8 olarak import edin. CSV her satırda kapsam/sayaç/durumları tekrarlar; sıfır kayıt için tek `recordKind=Summary` satırı üretir. Böylece boş veya başarısız kapsam durumları kaybolmaz.

CSV hücresinde metnin whitespace/kontrol karakterleri sonrası ilk karakteri `=`, `+`, `-`, `@` ise veya hücre bir kontrol karakteriyle başlıyorsa spreadsheet formül değerlendirmesini önlemek için başına `'` eklenir. JSON özgün metni koruyan esas değişim formatıdır; CSV'deki bu koruma dönüşümü kimliğin özgün parçası değildir. Export stream'i/snapshot'ı kapatmaz. Dosya export aynı klasörde geçici dosyaya yazar ve başarıdan sonra taşır; mevcut hedef yalnız `overwrite: true` ile değiştirilir. Hata halinde mevcut dosya korunur.

Hata kodları: **33 LinkProtocolFailure**, **34 TraceabilityExportFailed**; mevcut script yükleme 26, boyut 27, dosya/cleanup 28, kontrollü DXL 29 ve COM hataları korunur. Eksik footer, yanlış istek/yol/yön/baseline, tutarsız sayım, okunamayan uçtan Identifier/içerik sızması ve bozuk UTF-8 reddedilir. Kullanıcıya merkezi `IntegrationErrorHandler` üzerinden raporlanabilir. Loglar içerik, dış URI veya ham DXL içermez.

## Manuel 9.5/9.7 doğrulaması

Gerçek IBM yorumlayıcısında DXL derlenmedi veya çalıştırılmadı; otomatik testlerde fake COM kullanıldı. Her senaryoyu **9.5/9.7 × x86/x64** için mevcut onaylı test verisi ve yazma yetkisi olmayan hesapla çalıştırın. Test için yeni link, nesne, baseline veya ACL oluşturmayın/değiştirmeyin. Önce `doors diagnose` çıktısı ve client fix pack'i kaydedin.

| Senaryo | Beklenen |
| --- | --- |
| Bilinen outgoing ve incoming, source/target salt okunur açık | Gerçek yön, modül/version/Absolute Number/Identifier ve tam link module yolu eşleşir |
| Aynı isimli farklı klasörlerde link module | Yalnız istenen tam yoldaki linkler döner |
| Kapalı incoming kaynak ve outgoing hedef | İzin/politika uygunsa exact version salt okunur yüklenir; owned modül kapanır |
| Kapalı kaynak yüklenemiyor veya yalnız ModName_ görülüyor | LinkRef ya da SourceReferenceOnly korunur; kaynak kimliği/sürümü uydurulmaz |
| Kaynak/hedef read ACL yok | Erişim durumu ve known absolute korunur; Identifier/içerik sızmaz; sonuç kısmi |
| Link module yok/erişilemez/wrong type | Scope durumu açık, başarılı boş matris olarak sunulmaz |
| Var olan dangling link, silinmiş uç/modül | Unresolved/Deleted durum; kayıt kaybolmaz, obje yaratılmaz |
| Var olan baseline'a link ve echoed link | Saklı source/target sürümü ve IsEchoed eşleşir; current'a geri dönüş yok |
| Kaynak veya hedef edit/share açık | AlreadyOpenForWrite; kullanıcı oturumu/modu/değişiklikleri korunur |
| Anchor baseline, iç modül linki, Both | Aynı fiziksel bağın iki gözlem yönü anlaşılır; gerçek source/target ters çevrilmez |
| Var olan surrogate, mapping doğru/yanlış/yok | Matched/NotMatched/NotConfigured; dış kimlik ayrı, URI açılmaz/senkronizasyon yok |
| Surrogate marker veya ID alanı ACL/DXL/boyut nedeniyle okunamıyor | Unavailable veya ReferenceStatus; normal nesne gibi yorumlanmaz |
| Filtreli görünüm, tablolardaki ve silinmiş anchor nesneleri | entire kapsamı; deleted politikası/sayaçlar doğru; görünüm değiştirilmez |
| Nolink modül ve kısmi erişim | Başarılı boş, erişilemeyen ve kısmi kapsamlar ayrılır; CSV Summary satırı korunur |
| Düşük kayıt/nesne/dosya/modül limitleri | IsTruncated/ModuleLimit ve sayımlar; hiçbir yarım kayıt başarı sayılmaz |
| Timeout/iptal ve sonra doğal tamamlanma | COM/gate yaşar, geç result temizlenir; thread/process zorla sonlandırılmaz |
| JSON/CSV, Unicode/virgül/tırnak/newline/formül benzeri dış kimlik | Alanlar doğru import edilir, CSV formülü çalışmaz; JSON asıl değeri korur |
| Okuma sonrası | Linksets, nesne history, baseline, ACL, mevcut edit oturumlarında değişiklik yok; yalnız owned formal modüller kapalı |

Client crash/normal cleanup'ı atlayan DXL hatası salt okunur modülleri açık bırakabilir. Tamamlanma işareti veya footer eksikse dosya reddedilir. Test raporunda derleme/runtime çıktısı, sahiplik ve cleanup sayaçlarını kaydedin; gereksinim veya dış sistem kimliğini loglamayın.

API dayanakları: IBM [9.7.2 DXL Reference Manual](https://www.ibm.com/docs/en/SSYQBZ_9.7.2/com.ibm.doors.requirements.doc/topics/dxl_reference_manual.pdf) ve [9.6 DXL Reference Manual](https://www.ibm.com/docs/en/SSYQBZ_9.6.0/com.ibm.doors.requirements.doc/topics/dxl_reference_manual.pdf), Links / Versioned links / Link management / ModuleVersion / attribute access bölümleri. Eski sürüm karşılaştırması: [IBM 9.2 DXL Reference Manual](https://download.boulder.ibm.com/ibmdl/pub/software/rationalsdp/documentation/product_doc/DOORS/version_9-2/dxl_reference_manual.pdf). Kurulu 9.5 istemcinin sürüme özel Help/DXL manual'ı ve gerçek yorumlayıcıda derleme saha doğrulamasının parçasıdır. Genel surrogate tespiti varsayılmamasının nedeni entegrasyonların farklı alan şemaları kullanmasıdır; IBM'in [DOORS for Rose Interface rehberi](https://public.dhe.ibm.com/software/rationalsdp/documentation/product_doc/DOORS/version_9-1/rose_integration.pdf) bunun ürün örneğini içerir.
