> Bu belge legacy WinForms istemcisini açıklar. Ana Desktop paketi artık birleşik WPF uygulamasıdır: [birleşik kullanım rehberi](UNIFIED-DESKTOP.md). Legacy paket adı `DoorsIntegration-WinForms-*` olur.

# Desktop Workspace kullanımı

`Workspace` sekmesi mevcut C# servislerini WinForms arayüzüne bağlar. Açılış, sekme seçimi, profile/connection değişimi, yerel kaynak önizlemesi ve export DOORS bağlantısı kurmaz. Sürüm profili çalışan istemciyi değiştirmez. Alttaki ortak connection seçimi yalnız açıkça başlatılan sonraki bağlantılı işlemlerde kullanılır; profil değişince açık sekmede kalınır.

Ana sekmeler pencerenin en üstündedir. Target profile, Connection mode ve native DXL uzatma kontrolleri bütün sekmelerde aynı alt şeritte kalır. Sekme geçişinde başlık/düğme gizleme veya dış boşluk değiştirme yapılmaz; ana içerik ve durum şeritleri yer değiştirmez. Workspace durum mesajı uzadığında içerik aşağı itilmez. Preview/probe düğmeleri DXL preview, Save activity düğmesi Activity sekmesindedir. Alanlar dar pencerede satır kaydırır; uzun formlar dikey, attribute tablosu gerektiğinde yatay kaydırılır. Settings ekranı, kayıtlı watchdog tercihi ve ikon korunur. Uygulama metinleri mevcut İngilizce arayüzle tutarlıdır.

## Requirements

Bu alan artık Read, **Batch import / update**, **Quality** ve **Coverage** alt sekmelerini içerir. Toplu işlemler captured preview, batch intent onayı, her kayıt için ayrı güncel native onay, kalıcı journal ve safe retry kullanır. Quality/Coverage retained snapshot'larla offline çalışır; uygunluk/kalifikasyon garantisi vermez. Input biçimleri, alan eşlemeleri, repository/baseline doğrulaması ve sınırlar: [PROCESSING.md](PROCESSING.md).

1. Tam formal modül yolunu girin. İstenirse `Read a baseline` ile major/minor/suffix seçin; current'a sessiz geri dönüş yoktur.
2. Özel object attribute adlarını ayrı satırlarda yazın. Silinmiş nesne politikasını, read-only açma seçeneğini, timeout ve aktarım sınırlarını seçin.
3. `Read requirements` çalıştırın. Sayılan nesneler, erişim/alan sorunları, kesilme, tamamlanma ve cleanup durumu modül/sürüm/istek kimliğiyle gösterilir.
4. `Page` + `Show page` her seferinde en fazla 100 nesne gösterir. Büyük içerikler grid hücresinde kısaltılır; tam dosya belleğe alınmaz.
5. `Selected object details` kimlik, ebeveyn/seviye ve attribute başına plain/rich erişim durumlarını gösterir. RTF/OLE çalıştırılmaz veya otomatik render edilmez.
6. `Attribute to save` ve `Save raw rich text/OLE` ile seçilen nesnenin okunmuş alanını tam UTF-8 metin olarak kaydedin. DOORS rich text bir RTF parçası olabilir; bağımsız Word/RTF belgesi olduğu varsayılmaz. Tam snapshot için Export kullanın.

`Evaluate attribute DXL` varsayılan kapalıdır. Açılması kurulu kodun çalışmasına izin verir; okuma başlamadan ayrıca hedef yazma + acknowledgement içeren onay istenir. Native okuyucu kendi read-only yüklemesini yönetir; kurulu attribute DXL'in etkileri uygulama tarafından çıkarılamaz veya geri alınamaz.

## Requirement editor

1. Hedef current formal modülün istemcide kapalı olduğundan emin olun; tam yolu girip `Load attributes` seçin.
2. Gerçek katalogdaki adlar/tipler, DXL/system işaretleri ve tooltip içinde enum/read/write-hint bilgileri görünür. Hesaplanan/korunan alanlar değişikliğe kapalıdır. Write hint kesin native izin değildir.
3. `Create`, `Update` veya `Move` seçin. Update/Move için modül içindeki **Absolute Number** kullanılır; Object Number kimlik değildir.
4. `Create`: FirstInModule/After/FirstChild/LastChild; `Update`: Keep veya bu son üç konum; `Move`: yalnız son üç konum. Anchor Absolute Number gerektiğinde etkindir. Move alt ağacı birlikte taşır; başka modüle taşıma yoktur.
5. Değişecek alanlarda `Change` seçin. Değer hücresine çift tıklayın: enumda gerçek etiket listesi, tekli/çoklu seçim; metinde çok satırlı giriş ve PlainText/DoorsRichText seçimi vardır. Rich text beklenen plain metni ayrıca gerektirir. Düz replacement mevcut alanın biçimini değiştirir.
6. Kurum kuralına göre zorunlu alanları `Business required` ile açıkça seçin. Standart DXL metadata'sında genel bir mandatory özelliği varsayılmaz; bilinmeyen zorunluluk uyarısı gösterilir. Bütün katalog zorunlu sayılmaz.
7. `Dry-run / preview`: değerleri, identity/context ve hierarchy kontrollerini okur; create/move/save yapmaz. Report eski plain/raw-rich değerleri, istenen değerleri, required listesini, uyarıları, adımları ve fingerprint'i gösterir.
8. `Review and apply...`: aynı planın tam raporunu modal pencerede yeniden gösterir. **Tam hedefi aynen yazmak**, acknowledgement kutusunu işaretlemek ve `Apply to DOORS` seçmek gerekir. Cancel/kapama/expiry/iptal onay sayılmaz.

Bir input, değer, format, required listesi, profile veya connection değişikliği önizlemeyi geçersiz kılar. Yol/profile/connection değişimi kataloğu da temizler. Native servis onay sonrası exclusive edit altında kontrolleri tekrar yapar. Plan tek kullanımlıdır ve beş dakika geçerlidir.

Sonuçta `Outcome`, `Persistence`, `Cleanup`, `Audit`, nesne/modül kimlikleri ve Applied/Failed/Skipped adımları ayrı gösterilir. Partial/Unknown veya audit hatası başarı sayılmamalıdır. `LeftOpenForReview` servisin sahip olduğu dirty modülün inceleme için açık kaldığını bildirir; otomatik discard, partial-save, rollback veya retry yoktur. Audit yolu ve native sınırlar: [REQUIREMENT-WRITING.md](REQUIREMENT-WRITING.md).

## Create module

Klasör, ad, açıklama ve timeout girin. Önce `Dry-run / preview`; sonra uygun planda `Review and create...`. Aynı tam hedef + acknowledgement onayı gerekir. Klasör/yetki/ad/çakışma/gözlemlenebilir kilit ve context kontrolleri servis tarafından yapılır. Sonuç yeni unique/qualified module ID ve cleanup bilgisini gösterir. Giriş/profile/connection değişince yeni preview gerekir. [Native kilit ve test sınırları](MODULE-CREATION.md).

## Links

Anchor formal modül ve **tam link module yolu** girin. Incoming/Outgoing/Both, current/baseline, endpoint resolution, read-only açma, silinmiş nesne ve sınırları seçin. Surrogate şemanız varsa `Use explicit surrogate mapping` ile marker adı/tam değer/isteğe bağlı dış kimlik alanını girin; varsayılan eşleme tahmin edilmez.

`Read links` sonrası 100 kayıtlık sayfalar, resolution ve surrogate durumları gösterilir. `Selected link details` kaynak/hedef sürümü, Absolute Number/Identifier, link module, echo, kaynak-referans ve erişim durumlarını içerir. Unresolved uçlar silinmez veya Resolved olarak gösterilmez. `Traceability JSON...` / `Traceability CSV...` tam retained snapshot'ı ilgili exporter ile kaydeder. Bu çıktı link satırları ve kapsam durumlarıdır; grafik veya pivot tablosu değildir. [Link kapsamı](LINKS.md).

## Export

Discovery, Requirements ve Links sekmelerinde en son toplanmış kaynakları kullanır. `Show available sources` yolları/sayıları/tamamlanmayı gösterir. Export edilecek snapshot'ları seçin; seçilmiş eksik kaynak sessizce atlanmaz. Sonuçlar yalnız mevcut snapshot kapsamıdır, otomatik bütün DB taraması değildir.

Yeni çıktı klasörü girin veya `Choose parent...` ile mevcut üst klasörün altında yeni ad oluşturun. JSON/CSV/XLSX, hata politikası, kayıt/alan/spool/output ve worksheet sınırlarını seçip `Export selected snapshots` kullanın. Var olan hedef klasör değiştirilmez. İlerleme durum satırında, son durum ve `report.json` yolu raporda gösterilir. Export orijinal snapshot'ları nesne/link bazında dolaşır; gridin ilk 100 satırıyla sınırlı değildir. CSV injection koruması, Unicode ve rich-text chunk davranışı [EXPORT.md](EXPORT.md) içindeki servisle aynıdır.

Yeni read eskisinin backing dosyasını bırakır. Yazma girişiminden veya onaylı custom DXL dispatch'inden önce eski Discovery/requirement/link snapshot'ları geçersiz kılınır; iptal sonrası da yeniden read/discover gerekebilir. Böylece eski veriler güncelmiş gibi export edilmez. Pencere kapandığında elde tutulan backing dosyalar bırakılır.

## Link editor

Kaynak/hedef tam formal modül yollarını, Absolute Number değerlerini ve mevcut link module yolunu girin; Create/Remove seçin. İlgili modülleri DOORS'ta kapatın ve `Preview / dry-run` çalıştırın. Eksik linkset oluşturulmaz; hata raporlanır. `Review and apply...` tam hedef yazımı ve acknowledgement ister. **Remove ikinci, ayrı açık silme onayı gerektirir:** `DELETE ` ile başlayan tam hedef yeniden yazılır. Her input/profile/connection değişimi planı geçersiz kılar. Önceki retained snapshot'lar apply öncesi temizlenir.

Rapor kimlikler, önizleme hataları, önceki outgoing referanslar ve sonucun Added/Removed farkını gösterir. Karşılaştırma yalnız seçilen kaynak nesne + tam link module kapsamındadır; baseline referansları ayrı korunur. Success için save/reopen sonrası yalnız tek beklenen kenar değişmeli ve cleanup/audit başarılı olmalıdır. Partial/Unknown sonucu tekrar göndermeyin; native completion beklenir. [API, limitler ve manuel kabul](LINK-WRITING.md).

## Run DXL

UTF-8 harici dosyayı seçin; `Load offline preview` yalnız yerel kaynağı okur. Parametre tablosu ad/değer verisidir, DXL kodu olarak birleştirilmez. RunString/RunFile, Inline/File, timeout ve sonuç sınırlarını seçin.

`Review script and run...` kaynağı tekrar yükleyip değişmez özel bir dosyada yakalar. Review, tam yakalanmış kaynak, parametreler, SHA-256, yöntem/transport/profile/connection ve timeout'u gösterir. Tam **orijinal dosya yolunu** yazıp acknowledgement ve Apply ile açıkça onaylayın. İptalde dispatch yoktur. Sonradan orijinal dosyanın değişmesi yakalanmış üst kaynak kodunu değiştirmez. `include` dosyaları veya betiğin dış bağımlılıkları snapshot'a alınmaz; bu özellik sandbox değildir. İlgili güvenilir dosyaları ayrıca gözden geçirin; çalışma dizinine bağlı relative include'lar staging ile taşınabilir sayılmaz.

Inline script `oleSetResult(...)`; File script `di_resultPath` dosyasına sonuç yazıp `di_completeFile()` kullanmalıdır. Kod kendi `Result` protokolünü bozarsa hata alınır. Hazırlık ve örnekler [DXL-EXECUTOR.md](DXL-EXECUTOR.md) içindedir. Genel DXL veriyi değiştirebilir; bu ekran salt okunur sayılmaz.

Sonuç önizlemesi en fazla 65.536 karakterdir. `Save complete result...` inline metni veya backing dosyasını tam kaydeder. Sonraki onaylı yürütmede veya pencere kapanınca önceki dosya bırakılır.

## Diagnostics ve işlem ömrü

`Diagnose offline`: her iki registry görünümü, COM kaydı, kayıtlı executable yolu/dosya sürümü/PE ve profile eşleşmesini gösterir; COM bağlantısı kurmaz. `Ping / diagnose live`: COM bağlantısı, DXL yürütme, Result cevabı ve cleanup adımlarını aynı STA üzerinde ayrıca raporlar. DB verisi yazılmaz. Kayıtlı binary sürümü çalışan ROT nesnesinin sürümünü kanıtlamaz. `Save report...` ayrıntıları UTF-8 kaydeder.

Tek UI işlemi sırasında profile, connection, diğer Workspace/Discovery/probe ve Settings eylemleri kilitlenir. `Cancel / stop waiting`, destekleyen isteğin token'ını iptal eder; başlamış COM çağrısını zorla kesmez. Timeout/iptal sonrası UI özgün completion ve cleanup'u bekler; istek yeniden gönderilmez. Yazma geç sonucu hâlâ Partial/Unknown olabilir. Senkron health-check API'sinde token/soft timeout yoktur; çalışan native çağrının dönüşü beklenir. Pencere kapanışı aktif iş bitene kadar ertelenir.

Hatalar merkezi handler üzerinden kod ve request ID ile ayrı ayrıntı alanına gelir. Beklenen input/snapshot hataları açıklanır; ham exception/payload otomatik loglanmaz. Requirement içeriği yalnız ilgili review/detail ekranında gösterilir; audit içerik yerine hash tutar.

## Manuel kabul senaryoları

Gerçek 9.5/9.7 ve uygun x86/x64 ortamında, yalnız onaylı test DB/modülünde:

1. Açılış ve bütün sekmeleri gezme bağlantı kurmamalı. Settings tercihi/ikon ve mevcut Discovery/probe davranışı korunmalı.
2. Requirements current/baseline, custom alanlar, silinmiş/erişilemeyen nesneler, üç sayfa ve raw-rich kaydı denenmeli; export tüm retained kaynağı kapsamalı.
3. Katalog gerçek enum/type/ACL ile karşılaştırılmalı. DXL/system alanları korunmalı; yanlış enum, blank required, olmayan alan ve cycle/table konumu engellenmeli.
4. Dry-run/Cancel/yanlış hedef/unchecked acknowledgement/expired plan hiç yazmamalı. Değer/profile/connection değişince Apply kapanmalı.
5. Onaylı create/update/move ve module-create kimlikleri save/yeniden açma ile doğrulanmalı. Yetkisiz/kilitli/context-changed/partial sonuçlar ve audit ayrıca incelenmeli.
6. Link yönü/baseline/surrogate/endpoint limitleri; unresolved incoming referanslar ve JSON/CSV satırları kontrol edilmeli.
7. Export JSON/CSV/XLSX; eksik kaynak, var olan hedef, Unicode/formül gibi CSV hücresi, büyük RTF/OLE, iptal ve hata raporu sınanmalı.
8. Custom DXL önce offline preview, sonra Cancel, sonra yalnız onaylı güvenli script ile RunString/RunFile ve Inline/File denenmeli; frozen kaynak, parametre escaping ve tam dosya kaydı kontrol edilmeli.
9. Yavaş çağrıda timeout/Cancel/kapatma denenmeli: tek COM dispatch, bekleyen UI ve geç cleanup; başka işlem veya otomatik retry olmamalı.
10. Varsayılan/minimum pencere boyutu, 125–200% DPI, form scrolling, enum/multiline editor ve review target alanı deployment ortamında kontrol edilmeli.

Geliştirme testleri fake portlar ve offscreen WinForms message loop kullanır. Gerçek DOORS DXL'i burada derlenmedi/çalıştırılmadı ve veritabanı değiştirilmedi. Ayrıntılı sonuçlar [VALIDATION.md](VALIDATION.md) içindedir.
