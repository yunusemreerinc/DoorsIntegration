# Birleşik Desktop — 2026-10-27

Ana uygulama `DoorsIntegration.Wpf.exe` olur. WinForms/Console EXE'sine bağımlılığı veya embedded WinForms kontrolü yoktur. Core, Com, Dxl, Export ve bağımsız Processing projeleri paylaşılır. Eski WinForms kaynağı ayrı bakım/geri dönüş seçeneği olarak korunur.

## Başlangıç ve gezinme

ZIP'i tamamen çıkarın; `DoorsIntegration.cmd` veya `DoorsIntegration.Wpf.exe` açın. Mock + salt okunur açılır. Mevcut Workspace, Baseline diff, Traceability graph, Connection diagnostics ve Export sekmelerine **İşlemler & Settings** eklenir. İşlem menüsü solda, girdiler üstte, sonuç/önizleme altta bulunur; uzun formlar ve attribute grid kaydırılır. Dosya alanlarında Seç düğmesi vardır. Save alanlarına yeni dosya, smoke output'a yeni klasör gerekir; var olan çıktı sessizce değiştirilmez.

Profil 9.5/9.7 seçimi çalışan DOORS sürümünü değiştirmez. Real seçimi tek başına COM açmaz. Açılışta yalnız Mock örnekleri okunur. Native kabul kurulmuş DOORS/test verisi gerektirir.

## Ekranlar

| Ekran | İşlev |
|---|---|
| Modül oluştur | Hedef klasör, ad, açıklama; dry-run; onaylı create; sonuç module/qualified ID |
| Gereksinim düzenle | Current modül attribute kataloğu; create/update/move; Absolute Number ve anchor; plain/rich değer; enum listesi; iş kuralı required seçimi |
| Link düzenle | Source/target Absolute Number + exact link module; create/remove; preview; onay; before/after traceability farkı |
| Toplu import / update | CSV/JSONL captured preview; sayfalı önce/istenen değerler; optional XML quality gate; durable journal/idempotency; failure CSV; safe retry preview |
| Kalite kontrolü | Retained requirements + XML review profili; offline bulgular ve JSONL rapor |
| Coverage analizi | Matching retained requirements/links; birden fazla policy; Unknown/incomplete kapsam ayrımı |
| DXL çalıştır | Offline source preview; runStr/runFile, inline/file; güvenli veri parametreleri; captured source review; tam sonuç kaydı |
| Smoke suite | Offline varsayılan; optional Real live; 11 case, JSON/JSONL, süre/ortam/versiyon, settlement |
| Settings | Mevcut watchdog tercihi; explicit tek client binding + isteğe bağlı native Double seçimi (max 4/çağrı) |
| Okuma / keşif ayarları | Discovery türü/recursive/sınırlar; current veya seçili baseline; deleted politikası; link yönü/resolution/surrogate; timeout/boyut sınırları |
| Export ayarları | Hata politikası, kayıt/alan/spool/output/worksheet sınırları ve progress aralığı |
| Değişiklik olayları / kayıt | Baseline olaylarının 100'lük sayfaları; dedicated traceability JSON/CSV; tam raw rich/OLE alanı ve diagnostics metni kaydı |
| Activity | Son 2000 uygulama log kaydı; UTC/seviye/request kimliği; yenileme ve yeni dosyaya kaydetme |

Okuma / keşif ayarları Workspace Oku ve Graph okumasına uygulanır; baseline diff kendi iki baseline girdisini korur. Bu ayarlar değişince retained okuma kanıtları temizlenir. Salt okunur mod kapatılırken/tam profil değişirken yazma planları atılır. Ayrı bir yeni baseline oluşturma, baseline sürümlerini otomatik keşfetme veya standart uyumluluğunu garanti eden API eklenmemiştir.

## Yazma onayları

1. Real DOORS, doğru profil ve connection seçin. İlgili native modülleri önce mevcut native servislerin gerektirdiği şekilde kapatın.
2. Salt okunur modu kapatın. Bu eylem hiçbir yazmayı başlatmaz ve sonraki işlemler için onay sayılmaz.
3. Katalog/alanları doldurun ve dry-run çalıştırın. Requirement create için Heading veya Text ve varsa kuruma ait required alanlar doğrulanır; bütün katalog zorunlu varsayılmaz. System/DXL generated alanlar korunur. Tekli enum picker, çoklu enum için satırla ayrılmış exact etiketler kullanılır; native servis tipi/değerleri yeniden doğrular.
4. İncele ve uygula komutu exact planın kimlik/context, önce/istenen değer, fingerprint ve expiry bilgisini gösterir. Hedefi aynen yazın ve acknowledgement seçin. Yanlış hedef/eksik acknowledgement/Cancel/X/expiry onay değildir.
5. Link remove ikinci, ayrı `DELETE <tam hedef>` onayı ister. Batch intent onayı her kayıt için yeni native önizleme ve onayı atlamaz.
6. Outcome, Persistence, Cleanup, Audit ve adım/issue raporunu inceleyin. Applied + saved + verified/cleanup/audit şartları olmadan başarı varsaymayın. Partial/Unknown otomatik tekrar edilmez.

Genel DXL kaynak metni UTF-8 dosyadan yakalanır, SHA256 ile incelenir ve aynı captured dosya yürütülür. Orijinal dosyanın sonradan değişmesi üst kaynağı değiştirmez; include dosyaları/dış bağımlılıklar yakalanmaz. Parametreler veri olarak mevcut executor'a geçer. File script `di_resultPath` + `di_completeFile()`; inline script `oleSetResult()` sözleşmesini kullanır. Bu alan sandbox değildir. Attribute DXL açılması da ayrıca onay ister.

Settings'teki native **Double execution time** otomasyonu varsayılan kapalıdır. Önce aynı Windows session'ında tek DOORS istemcisi process kimliğiyle bağlanır, sonra seçenek açılıp Kaydet kullanılır. Mevcut sınırlı prompt matcher yalnız entegrasyonun dispatch'i çevresinde çalışır; diğer pencerelere genel onay vermez ve en fazla dört uzatma/çağrı yapar. Client binding ROT sahipliği kanıtı değildir. Watchdog kapatma veya profil/mod değişimi otomasyonu kapatır; kendiliğinden geri açılmaz. Native tercih diske kaydedilmez; watchdog tercihi mevcut settings.xml'de saklanır. Eski double-app-wait-time özelliği geri getirilmemiştir. Smoke fixed raw probe'ları bu otomasyonu kullanmaz.

## Batch, analiz ve raporlar

CSV/JSONL alanları ve örnekler [PROCESSING.md](PROCESSING.md) içindedir. Batch'in repository ID + namespace + key/payload bağlamı kalıcı journal ile korunur. Input değişimi preview'ı atar. Apply sonrası aynı plan tekrar gönderilmez. Safe retry yalnız motorun kesin no-mutation/uygun olarak sınıfladığı kayıtlar için fresh preview üretir ve yeniden onay ister. Geçerli kayıtlarla devam et seçeneği atomik transaction sağlamaz. Preview UI sayfa başına 100 kayıt ve yaklaşık 256 KiB + son bounded alan gösterir; tam immutable preview dosyası ayrıca belirtilir. Büyük dosya tamamıyla UI belleğine alınmaz. Failure CSV formül koruması mevcut exporter'dadır.

Optional quality gate XML profili batch preview fingerprint'ine katılır; eksik/NotEvaluated kontroller varsayılan bloke eder. Kalite analizi ayrı offline çalışabilir. İnceleme profilleri DO-178C/DO-200B/DO-330 uyumluluğunu, sertifikasyonu veya araç kalifikasyonunu otomatik garanti etmez.

Coverage policy formatı her satır için `id|exact link module|Incoming veya Outgoing|minimum|SameVersion veya CurrentTarget veya AnyExplicitVersion|counterpart path;path` biçimindedir. Son sütun boş olabilir. Boş policy listesi retained exact link module için Outgoing/minimum 1 kullanır. Direction/link module retained scope ile uyuşmalı. Tam oran ancak bilinen tam scope ve kullanıcının repository/sürüm doğrulamasıyla sunulur; snapshot'lar atomik değildir. Mevcut graph en fazla 150 ilişki gösterir; export tüm retained scope'u taşır.

Analiz raporları `%LOCALAPPDATA%\DoorsIntegration\processing\analysis` altında yeni klasöre yazılır; ekran gerçek yolu gösterir. JSONL bulgular yerel dosyalardır. Batch audit/journal ve native yazma audit'i mevcut Processing/DXL sözleşmelerini kullanır. Baseline olayları deterministik event ID ve conclusive/impact-candidate bilgisiyle gösterilir; otomatik downstream etki yayılımı veya mesaj bus'ı yoktur. BaselineDiff JSON/XLSX export bu olayları da içerir.

## Threading ve timeout

Tüm ekranlar aynı Workspace busy/cancellation/close ömrünü kullanır. COM UI thread'de çalışmaz. Tek işlem sürerken profil, form, başka native komut ve kapanış engellenir/ertelenir. UI notification ve sonuç commit'leri dispatcher'da; servis, spool, dosya ve sayfa enumeration worker'dadır.

İptal veya wait timeout native COM'u güvenli biçimde zorla durdurmaz. Original Completion/cleanup beklenir; yeni COM dispatch yapılmaz. Yazma Partial/Unknown olabilir; kör retry yoktur. Smoke başlangıç raporu settlement beklemeden yazılır ve yolu progress alanına gelir; orijinal native çağrı dönene kadar UI busy kalabilir. Late settlement ayrı dosyadır, ilk timeout sonucu değiştirilmez. Native çağrı hiç dönmezse bekleme sürebilir. [Smoke sözleşmesi](SMOKE-TESTS.md).

## Paketleme ve manuel kabul

SDK 5.0.416 ile Release x86/x64 ayrı restore/build edin. `Package-Desktop.ps1` birleşik WPF dağıtımını Desktop adıyla, `Package-Wpf.ps1` Wpf adıyla, `Package-WinForms.ps1` legacy istemciyi ayrı adla paketler. Paketleme live test başlatmaz; kurum betik politikası değiştirilmez. ZIP'te Processing DLL, altı native DXL dosyası ve örnek quality/import dosyaları birlikte bulunmalıdır.

Gerçek ortam kabulünde 9.5/9.7 ve kurulu COM mimarisinde:

- Açılış/profil/sekme geçişi native bağlantı kurmamalı; Mock yazma/live smoke başlatmamalı.
- Yanlış hedef, boş acknowledgement, Cancel, X ve expired plan sıfır mutation üretmeli. Input/profil değişimi yeniden preview gerektirmeli.
- Yalnız açıkça onaylı test DB'de module create, requirement create/update/move ve link create/remove save/reopen doğrulanmalı. Remove ikinci onaydan vazgeçildiğinde yazmamalı.
- Batch'te intent onayı tek başına native kaydı yazmamalı; duplicate key, journal replay ve Unknown durumlarında otomatik dispatch olmamalı.
- Dosya/inline DXL, Unicode/enum/rich alanlar, baseline/deleted/access/surrogate, incomplete scope, büyük export ve hata yolları kontrol edilmeli.
- Yavaş çağrı/iptal/timeout/kapatma yeni dispatch açmamalı; smoke ilk raporu bekleme sırasında incelenebilmeli.
- 1120×760 ve daha büyük pencere, 100–200% DPI, form/attribute grid kaydırma ve klavye gezinmesi sahada kontrol edilmeli.

Geliştirme doğrulamaları mock portlar ve fake COM session ile yapılır. Gerçek DOORS bulunmayan makinede native smoke NOT_RUN'dır; bu geliştirmede gerçek database mutation yapılmamıştır.

Guncel dagitim: artifacts/releases/2026-10-27-unified/. Onceki artifacts kok arsivler eski surum olarak korunur. Package-Desktop/Wpf/WinForms varsayilan olarak tarihli release klasorunu kullanir; -OutputDirectory ile yeni hedef secilebilir.


## JSON requirements rule engine

İşlemler & Settings → Kalite kontrolü artık XML yanında yeni JSON RuleProfile akışını sunar. Project ID, ApprovedProfileOnly/DraftPreview ve General/DO178C/DO200B/DO330 seçilebilir. Örnek JSON dosyaları taslaktır; alan/link module eşlemeleri yapılandırılmadan onaylanamaz. Proje profilini incele ve onayla düğmesi exact fingerprint + acknowledgement ister ve yeni yerel JSON dosyası yazar; Mock/read-only modunu veya native write izinlerini değiştirmez. Analiz retained requirement/link snapshot'larını kullanır; COM çağrısı yapmaz. Kanıt, öneri ve Pending disposition results.jsonl içindedir; API review ledger daha sonra exact matching sonucu insan kararıyla birleştirir. Ayrıntılı kullanım: [REQUIREMENTS-RULE-ENGINE.md](REQUIREMENTS-RULE-ENGINE.md). WinForms XML quality ekranı ve XML batch gate geriye uyumlu kalır.
