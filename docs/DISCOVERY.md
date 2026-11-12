# Proje, klasör ve formal modül keşfi

`ProjectService` ve `ModuleService`, DOORS Classic hiyerarşi meta verisini DXL üzerinden keşfeder. Modül içeriğini okumaz veya modül açmaz; edit/share/save, veritabanı oluşturma/silme, ACL değiştirme veya current folder ataması yapmaz. DWA/OSLC/HTTP kullanılmaz. Gerçek DOORS'a bağlanılmadı ve veritabanında değişiklik yapılmadı.

## Masaüstünde kullanım

1. Desktop ZIP'ini tamamen çıkarın. EXE/DLL/config ile **`scripts/dxl/discovery.dxl`** dosyasını aynı dizin yapısında tutun.
2. DOORS test istemcisinde oturum açın. Ana penceredeki Target profile ve Connection mode ayarlarını seçin. Attach yalnızca mevcut erişilebilir ROT nesnesini kullanır; Activate istemci başlatabilir. Kütüphane varsayılanı attach'tır; masaüstü ana pencerede seçilen modu kullanır.
3. **Discovery** sekmesinde tüm hiyerarşi, projeler, klasörler, formal modüller veya **Known module path** seçin.
4. **Full path** alanına `/`, `/Project` veya bilinen modülün tam yolunu girin. Alt kapsam için Recursive seçin; bilinen modül sorgusunda recursive uygulanmaz.
5. **Discover** ile başlatın. Açılışta, sekmeye geçişte veya seçim değişikliğinde bağlantı kurulmaz. Salt okunur tablo tam yol, ad, tür ve erişim durumunu gösterir; erişim hücresi tooltip'i durumu açıklar. Alttaki alan kapsam sorunlarını gösterir.
6. **Export JSON...** sonuç ve sorunları yerel UTF-8 JSON'a aktarır. Yeni keşifte önceki sonuç temizlenir; başarısız keşiften sonra eski sonuç export edilmez. Küçük pencerede sekme içini kaydırın; büyük listelerde pencereyi büyütebilirsiniz.

Keşif sırasında diğer DOORS eylemleri engellenir. Timeout sonrası başlayan COM çağrısı sürebilir; gate temizliğe kadar yeni çağrıyı reddeder. Kapatma isteği aktif COM/gate temizliği bitene kadar ertelenir. Zorla COM iptali, thread abort, Quit veya süreç sonlandırma yapılmaz.

CLI diagnose/ping davranışları korunur. Keşif, Discovery sekmesi ve C# servis API'sinden sunulur; serbest script yürüten bir CLI seçeneği eklenmez.

## API ve DI

Core katmanındaki `IProjectService` / `ProjectService`, `IModuleService` / `ModuleService` ve modeller COM'dan bağımsızdır. `IDoorsHierarchyReader` portunu Dxl katmanındaki `DxlHierarchyReader`, mevcut `IDxlExecutor` üzerinden uygular. `DiscoveryJsonExporter`, `IDiscoveryJsonExporter` sözleşmesini uygular. Her iki host TryAdd ile kayıt yapar; kendi reader/exporter veya `DiscoveryScriptOptions` ayarınızı önceden kaydedebilirsiniz.

```csharp
using DoorsIntegration.Core;
using Microsoft.Extensions.DependencyInjection;

var scope = new DiscoveryScope("/MyProject", recursive: true,
    maxItems: 10000, maxDepth: 32, maxFolders: 512,
    timeout: System.TimeSpan.FromSeconds(30),
    connectionMode: DoorsConnectionMode.AttachExisting, target: DoorsVersion.V97);

var projects = provider.GetRequiredService<IProjectService>();
var hierarchy = await projects.DiscoverHierarchyAsync(scope);
var projectList = await projects.DiscoverProjectsAsync(scope);
var folderList = await projects.DiscoverFoldersAsync(scope);
var modules = provider.GetRequiredService<IModuleService>();
var formalModules = await modules.DiscoverFormalModulesAsync(scope);
var knownModule = await modules.GetFormalModuleAsync(
    new DiscoveryScope("/MyProject/Folder/Requirements"));

// Existing destination directory; existing files are preserved by default.
provider.GetRequiredService<IDiscoveryJsonExporter>()
    .ExportFile(@"C:\Exports\doors-hierarchy.json", hierarchy);
```

`provider`, AddDoorsIntegration/AddDoorsDesktop ile oluşturulmuş host provider'ıdır. Çağrılar sıralıdır; aynı COM sunucusuna eşzamanlı keşif/probe göndermeyin. Export(Stream, snapshot) akışı açık bırakır. ExportFile için overwrite varsayılan false; true açıkça seçilirse dosya değiştirilebilir. Desktop standart SaveFileDialog overwrite onayını kullanır.

`DiscoveryScriptOptions`, betiği uygulama base directory'sinin `scripts/dxl/discovery.dxl` yolunda bulur; working directory'ye bağlı değildir. Dosya SDK content olarak host/test çıktısına kopyalanır. Özel deployment için DI'ya önceden `new DiscoveryScriptOptions(absolutePath)` kaydedin. Kullanıcı girdisi script yolu olarak kullanılmaz.

## Sonuç ve erişim durumları

| Alan | Anlam |
| --- | --- |
| FullPath / fullPath | Kökten başlayan tam DOORS yolu. Başarısız bilinen yol sorgusunda talep edilen yol korunur. |
| Name | Yolun son bileşeni; kaynak meta verisiyle tutarlılığı denetlenir. |
| Type | Project, Folder, FormalModule; kök DatabaseRoot. Çözümlenemeyen öğe Unknown. Link/Descriptive sonuç filtresine alınmaz. |
| AccessStatus | Aşağıdaki meta veri erişim durumu. |
| MetadataVerified | Kimlik/tür okunabilir meta verisinden geldi mi? False ise kimlik istekten türetilmiştir. Önceden görülen klasör sonradan erişilemez olursa önceki kimlik korunabilir. |
| Issues | Güvenli kod, kapsam yolu ve sabit açıklama; ham DXL hata mesajı içermez. |
| Visibility | ReadableItemsOnly. Kullanıcıya görünmeyen öğeler bilinemez. |
| IsCompleteForVisibleScope | Görülebilen seçili kapsam sorunsuz ve kesilmeden tamamlandı mı? Tüm veritabanının görüldüğü anlamına gelmez. |
| IsTruncated | Öğe/yanıt, derinlik veya klasör sınırı nedeniyle potansiyel kapsam kaldı mı? |
| VisitedFolders | Tek seviyeli klasör sorgusu sayısı; başarısız kapsam sorguları da sayılır. Bilinen modül sorgusunda 0. |

| Erişim durumu | Yorum |
| --- | --- |
| Readable | Hiyerarşi meta verisi/canRead başarılı. Modül açılabilirliği, içerik/nesne/öznitelik yetkisi ve kilit durumu sınanmadı. |
| AccessDenied | Elde edilen handle için canRead izin vermedi. |
| DeniedOrNotFound | item/folder null döndü; bulunamama ile kullanıcıya görünmezlik ayırt edilemiyor. |
| Unavailable | Meta veri/handle çözümleme hatası; silinme/erişim yarışı gibi nedenler olabilir. Ham hata yayınlanmaz. |

DOORS for Item in Folder yalnızca silinmemiş ve kullanıcının okuyabildiği doğrudan çocukları döndürür. Gizli modül listesi üretilemez. Bilinen gizli yol Known module path ile sorgulanabilir: null dönüşü Unknown / DeniedOrNotFound / MetadataVerified=false olur. Kesin "yok" veya kesin "yetkisiz" iddiası yoktur; gizli yollar tahmin edilmez ve farklı yetkiyle okunmaz.

Kapsamın kendisi tür filtresine uyarsa sonuçta yer alır. Recursive dolaşım C# tarafında kuyrukla yapılır. Erişilemeyen alt klasör raporlanır, diğer kuyruk kapsamları devam eder. EnumerationFailed varsa o ana kadar dönen güvenilir satırlar korunur. COM/script/protokol hatasında güvenilir snapshot üretilmez; merkezi hata döner. Keşif çoklu okuma olduğundan atomik/transaction snapshot değildir. Yol karşılaştırmaları ordinal'dir; gerçek/canonical DOORS yol yazımını kullanın. Hedef profil COM kaydını veya aktif istemciyi değiştirmez.

## DXL, sınırlar ve timeout

Harici betik `scripts/dxl/discovery.dxl`, tek klasörü veya bilinen yolu sorgular. item, folder, for Item in Folder, name, fullName, type, canRead ve noError/lastError kullanır. Module read/edit/share, görünüm yükleme, current ataması ve veritabanı yazma API'si yoktur. create/delete yalnızca geçici DXL Buffer belleği içindir.

Path/name/mode, DxlStringLiteral.Encode ile escaped literal olarak geçer; eval/include/kod şablonu değildir. Boş/relative/./../yinelenen yol, kontrol karakteri ve bozuk UTF-16 reddedilir. Tırnak, ters eğik çizgi ve geçerli Unicode korunur. Çıktı COM Result üzerinden gelir; kod sayfalı ara dosya UTF-8 varsayılmaz.

Sürümlü protokol istek kimliği, scope/child kayıtları, escape edilmiş alanlar ve sayı/kesilme footer'ı taşır. Parser istek/kapsam eşleşmesini, yalnız doğrudan çocukları, yol/ad tutarlılığını, tekil yolları, tür/erişim/kod allowlist'lerini ve sayıları denetler. Bozuk/eksik/kapsam dışı yanıt kullanılmaz. Otomatik loglarda keşfedilen ad/yol, script veya ham exception yoktur; sayılar ve parent/DXL istek kimliği eşlemesi yazılır.

Varsayılan sınırlar **10.000 gözlenen öğe**, **32 derinlik**, **512 klasör sorgusu**, **toplam 30 saniye bekleme**. Gözlenen sayı filtre dışı türleri de içerir. Tek DXL yanıtı 10.000 çocuk ve yaklaşık 2 milyon karakterle sınırlıdır. Sınırlarda sonuç kesilme nedeni ile döner. Derinlik/klasör sınırında taranmayan klasör boş olsa bile potansiyel eksiklik belirtilir.

Timeout/iptal çalışan COM'u kesmez. Reader geç sonucu gözlemler/dispose eder ve yeni klasör sorgulamaz. DxlExecutor aynı STA'da cleanup yapıp gate'i bırakır. Çağıran DxlExecutionWaitException.Operation.Completion üzerinden geç tamamlanmayı izleyebilir. UI kapanışı gate boşalana kadar bekler.

## JSON

Framework DataContractJsonSerializer UTF-8 üretir; elle JSON birleştirilmez. schemaVersion 1, istek/tarih/kapsam/hedef profil, visibility/tamamlanma/kesilme, sınırlar, items ve issues taşınır. Enum'lar okunabilir string'dir. Örnek öğe:

```json
{"fullPath":"/Project/Restricted","name":"Restricted","type":"Unknown","accessStatus":"DeniedOrNotFound","metadataVerified":false}
```

Gerçek öğe ayrıca accessDescription içerir. Kısmi durum isCompleteForVisibleScope=false ve issues ile korunur. Export hedef dizinde özel bir geçici dosyaya yazılıp move/replace ile tamamlanır; mevcut dosyaya açık overwrite gerekir. Hedef dizin otomatik oluşturulmaz. Yeni hata kodları: **30 DiscoveryProtocolFailure**, **31 JsonExportFailed**. COM/script/timeout kodları mevcut sınıflandırmayı kullanır.

## DiscoveryProtocolFailure tanılama

`DiscoveryProtocolFailure`, DXL yürütme zarfı kabul edildikten sonra keşif içeriğinin doğrulanamadığını belirtir. `HResult=0x80131500`, uygulamanın `IntegrationException` değeridir; tek başına COM kaydı veya bitlik hatası anlamına gelmez. Bağlantı/probe başarısı, hiyerarşi betiğinin geçerli içerik ürettiğini doğrulamaz.

Discovery altındaki hata alanında `Reason`, 1 tabanlı `Line` ve `ResponseChars` bulunur. Henüz bir kayıt okunmamışsa Line=0'dır. Activity kaydındaki **DiscoveryBatchRejected**, aynı bilgiyi parent Request ve `DxlRequestId` ile içerir. Veritabanı adları, yollar, ham sonuç ve ham DXL hataları bu tanı kaydına yazılmaz.

| Reason | Kontrol / yapılacak işlem |
| --- | --- |
| HeaderMismatch | Başlık, protokol sürümü veya istek kimliği eşleşmedi. ZIP'i yeni bir klasöre tamamen çıkarın; EXE/DLL ve scripts/dxl/discovery.dxl aynı paketten olmalı. Başka bir COM istemcisi aynı Result alanını değiştirebilir; körlemesine otomatik retry yapılmaz. |
| ScopeMismatch | Dönen tam yol istenenden farklı. Database Explorer'daki tam yolun harflerini ve yazımını kullanın. |
| UnknownItemType / InvalidItemMetadata | Tür veya yol/ad tutarlılığı kabul edilmedi. Reason/Line bilgisiyle DOORS sürümünü kaydedin; veriyi düzeltilmiş gibi varsayıp envantere almayın. |
| MissingResponse / MissingFinalNewline / InvalidFooter / CountMismatch | Boş, eksik veya tutarsız keşif sonucu. Önce paket bütünlüğünü ve read-only ping sonucunu kontrol edin. |
| InvalidLineEnding | LF/CRLF dışındaki ham CR karakteri. LF ve Windows CRLF kayıt ayırıcıları desteklenir; alan içindeki kontrol karakterleri hâlâ reddedilir. |

Doğrulama hatasında tablo/JSON export boş kalır. Bu düzeltme CRLF yanıtlarını kabul eder; bildirilen bir hatanın gerçek nedeni ancak Reason/Line veya gerçek istemci testiyle doğrulanabilir.

Manuel tekrar: yeni paketi tamamen çıkarın, önce `doors ping`, sonra `/` için Recursive kapalı keşif ve bilinen proje tam yolu için keşif çalıştırın. Hata sürerse Discovery hata alanını ve `DiscoveryBatchRejected` satırını, DOORS sürüm/mimari bilgisini paylaşın. Bu adımlar hiyerarşi meta verisi okur; veritabanında değişiklik yapmaz.

## Manuel doğrulama: 9.5/9.7 × x86/x64

Gerçek istemci testi henüz yapılmadı. Her sürüm/mimari kombinasyonunu onaylı ortamda, mevcut hiyerarşi ve yazma yetkisi olmayan hesapla sınayın. Hata üretmek için veri/ACL/registry değiştirmeyin.

1. Açılış/sekme seçimi offline kalmalı, JSON export pasif olmalı. Tam çıkarılan pakette dış betik bulunmalı.
2. Mevcut projede recursive/nonrecursive ve tür filtrelerini Database Explorer ile karşılaştırın. Nested project/folder/formal öğeleri doğrulayın; Link/Descriptive modüller listelenmemeli.
3. Known module path ile okunabilir formal modül sorgulayın. FormalModule/Readable/verified bekleyin; modül penceresi veya module-open trigger'ı açılmamalı.
4. Zaten gizli bir bilinen yol ile olmayan yolu sorgulayın. API null döndürüyorsa ikisi de Unknown/DeniedOrNotFound olmalı. Klasör yolunu modül diye sorgulamak NotFormalModule üretmeli.
5. Mevcut Türkçe/Unicode/tırnak/boşluk/ters eğik çizgili adları varsa JSON'da bire bir karşılaştırın. Yeni veri oluşturmayın. Geçersiz yol kod 2 ile COM öncesinde reddedilmeli.
6. API'den düşük maxItems/maxDepth/maxFolders ile kesilme ve JSON tamamlanma=false alanlarını doğrulayın. Görünmeyen öğelerin normal taramada bulunamayacağını kaydedin.
7. JSON export ve overwrite onayı iptalini sınayın. Kütüphane overwrite=false mevcut dosyayı koruyup kod 31 üretmeli. Önce başarılı sonra başarısız keşifte eski tablo/JSON seçeneği temizlenmeli.
8. Mevcut meşgul/giriş ekranını normal biçimde tamamlayın. Timeout sonrası zorla COM sonlandırılmamalı; kapanış cleanup'a kadar beklemeli. Sonsuz DXL veya kill ile hata üretmeyin.
9. Mevcut audit/history görünümünde veri değişmediğini doğrulayın. Loglarda ad/yol, gereksinim, DXL ve ham hata mesajı olmamalı. İstek kimliklerini kaydedin.

Bozuk zarf/alan/footer, kapsam dışı veri, erişim yarışı, kısmi dolaşım ve timeout/gate senaryoları fake COM testleriyle doğrulanır. Testlerin geçmesi betiğin gerçek IBM yorumlayıcısında derlendiği anlamına gelmez.

## API kaynakları

- [IBM DOORS 9.7.2 DXL Reference Manual](https://www.ibm.com/docs/en/SSYQBZ_9.7.2/com.ibm.doors.requirements.doc/topics/dxl_reference_manual.pdf): Items, Folder handles, Hierarchy information, Item access controls ve Buffer. IBM'in indekslenmiş bölümleri null/okunabilir öğe sözleşmesini doğrular.
- [IBM DOORS 9.6 DXL Reference Manual](https://www.ibm.com/docs/en/SSYQBZ_9.6.0/com.ibm.doors.requirements.doc/topics/dxl_reference_manual.pdf): canRead, noError/lastError.
- [IBM DOORS 9.2 DXL Reference Manual](https://download.boulder.ibm.com/ibmdl/pub/software/rationalsdp/documentation/product_doc/DOORS/version_9-2/dxl_reference_manual.pdf): eski sürüm Item/Folder sözleşmesi. 9.5 için kurulu Help/DXL kılavuzu ve gerçek motor testi ayrıca gerekir; sertifikasyon iddia edilmez.
