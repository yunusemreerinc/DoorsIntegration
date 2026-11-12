# doors diagnose / doors ping — manuel testler

Proje/klasör/formal modül keşfi ve JSON export için ek senaryolar [DISCOVERY.md](DISCOVERY.md) içindedir.

Yeni `doors smoke` için read-only 11-case raporu, fresh Result, runStr/runFile, NOT_RUN, timeout/orijinal completion ve no-retry kabul matrisi [SMOKE-TESTS.md](SMOKE-TESTS.md) içindedir. `smoke` varsayılanı offline'dır; önceki diagnose/ping davranışı korunur.

Baseline karşılaştırmasının iki mevcut baseline ile salt okunur 9.5/9.7 ve desteklenen x86/x64 kabul matrisi [BASELINE-COMPARISON.md](BASELINE-COMPARISON.md) içindedir. Bu testlerde veri/baseline/ACL oluşturmayın veya değiştirmeyin.

Gereksinim attribute kataloğu ve açık onaylı create/update/move için [REQUIREMENT-WRITING.md](REQUIREMENT-WRITING.md) içindeki opt-in testleri ve manuel matrisi kullanın. Normal test koşuları bu canlı testleri açmaz; gerçek yazma ayrı fingerprint, tam hedef ve açık consent gerektirir. Yeni Desktop ekranları ve tam hedef/acknowledgement onay akışının manuel kabul matrisi [DESKTOP-WORKSPACE.md](DESKTOP-WORKSPACE.md) içindedir.

DOORS'un native DXL süre uyarısındaki Double seçiminin otomasyonu için [DXL-WATCHDOG.md](DXL-WATCHDOG.md) içindeki salt okunur senaryoları 9.5/9.7 ve kullanılan x86/x64 uygulamada çalıştırın. Varsayılan kapalı durum, tanınan/tanınmayan modal kontroller, dört seçim sınırı, iptal, ikinci istemci ve yeniden başlatma kontrollerini içerir; veritabanı yazması gerekmez.

## Komut sözleşmesi

Yeni Settings kontrolü için: ilk açılışta watchdog kutusunun açık olduğunu kontrol edin. Kutuyu kapatıp kaydedin; Preview'de `pragma runLim, 0` görünmeli ve native Double seçeneği pasif olmalı. Uygulamayı yeniden açın; seçim kapalı kalmalı. Tekrar açıp kaydedin; Preview `pragma runLim, 1000000` göstermeli. Kaydetmek bağlantı başlatmamalı. Gerçek bir okuma sürerken Settings kontrolü/kaydetme pasif olmalı. Ayar bozuk veya okunamaz olduğunda güvenli başlangıç açık olmalı. Bu denemeler için veritabanı yazması gerekmez.

Build çıktısındaki `doors.cmd`, aynı klasördeki `DoorsIntegration.Console.exe` uygulamasını çalıştırır ve çıkış kodunu aynen iletir. DLL/config dosyaları yanında kalmalıdır. CMD'de bu klasörden `doors diagnose` / `doors ping` kullanılabilir. PowerShell'de aşağıdaki gibi açık yol kullanın; IBM istemcisinin kendi `doors.exe` dosyasıyla PATH çakışmasını önler:

```powershell
$doors = '.\src\DoorsIntegration.Console\bin\x86\Release\net48\doors.cmd'
& $doors --help
& $doors diagnose --target 9.5
& $doors diagnose --target 9.7
# Aşağıdaki komutlar canlı test ortamında elle uygulanır:
& $doors ping --target 9.7
& $doors diagnose --live --target 9.7 --connection attach
& $doors ping --target 9.7 --connection activate
$LASTEXITCODE
```

| Komut | Davranış |
| --- | --- |
| `diagnose` | Offline COM kayıt ve kayıtlı istemci EXE dosya sürümü kontrolü. COM aktivasyonu, ROT sorgusu veya DXL yok. |
| `ping` | Aynı kontroller + COM bağlantısı + sabit `runStr` + `Result`/`oleSetResult` yanıt doğrulaması. Varsayılan bağlantı `attach`. |
| `diagnose --live` | `ping` ile aynı aşamalı canlı kontrol. |
| `--connection attach` | Yalnızca erişilebilir mevcut ROT nesnesi. Başarısızlıkta aktivasyon denenmez. |
| `--connection activate` | Açıkça seçilen COM aktivasyonu; yeni istemci açabilir veya mevcut sunucuyu kullanabilir. Yeni süreç garantisi yok. |
| `--target 9.5` / `9.7` | Beklenen kayıtlı dosya sürümü profili; varsayılan 9.7. Kurulum veya COM kaydı seçilmez/değiştirilmez. |

Önceki `--dry-run`, `--probe`, `--diagnose-com`, `--diagnose-compatibility` seçenekleri korunur. Argümansız çalıştırma offline önizlemedir. Eski `--probe` bağlantı varsayılanı `activate` olarak korunmuştur; yeni canlı komutların varsayılanı `attach`'tır. `--connection` yalnızca canlı işlemde geçerlidir. Serbest script/parametre/dosya çalıştırma seçeneği yoktur.

## Adım sonuçları ve hata ayrımı

Her adım `Passed`, `Failed` veya `Skipped`, açıklama ve hata varsa sayısal/sembolik kod üretir. Başarısız adımlar stderr'e de yazılır. Tüm satırlar aynı raporun istek kimliğiyle ilişkilidir; hassas Result içeriği yazdırılmaz. Çıkış kodu ilk başarısız adımın kodudur. Sonraki cleanup hatası ilk hatayı değiştirmez. Offline başarı yalnızca kayıt/dosya kontrollerinin başarısıdır; atlanan adımlar başarılı sayılmaz.

| Adım | Sınanan davranış | Başarısızlık / sonraki adımlar |
| --- | --- | --- |
| `ComRegistration` | İşlemin x86/x64 registry görünümünde `DOORS.Application`/CLSID kaydı | Eksik/geçersiz: 14; okunamıyor: 20. Sürüm ve tüm canlı adımlar atlanır. |
| `ClientVersion` | Kayıtlı sunucunun EXE dosya sürümü ile hedef profil karşılaştırması | Bilinmiyor: 21; uyuşmuyor: 22. Canlı modda DXL adımları bağımsız olarak yine sınanır; başarılı PONG bu hatayı silmez. |
| `ComConnection` | Aynı STA üzerinde seçilen attach/activate davranışı | ROT yok: 15; erişim: 17; meşgul: 18; süreç içi gate dolu: 25. Çalıştırma/cevap atlanır. |
| `DxlExecution` | Sabit betik için `runStr` çağrısının dönmesi | COM/dispatch hatası uygun koda çevrilir; Result okunmaz, cevap adımı atlanır. |
| `DxlResponse` | `Result` okunması, bu isteğin GUID'si ve tam protokol eşleşmesi | Boş/bozuk/eski/başka isteğe ait yanıt: 12; Result API yok/yanlış tür: 16; kopan oturum: 19. |
| `ComCleanup` | Yalnızca alınan COM referansının aynı STA'da bırakılması | Hata ayrıca raporlanır. `Quit`, süreç sonlandırma veya başka thread'den release yok. |

`runStr` döndüğü halde DXL derleme/çalışma hatası olabilir. Yalnızca `DxlResponse: Passed` istekle eşleşen gidiş dönüşü kanıtlar. Varsayılan sabit betik yalnızca `oleSetResult("DOORS_INTEGRATION|1|<GUID>|OK")` çağırır. Modül/nesne/öznitelik/link/baseline okuma veya yazma, save/delete/create ve veritabanı API çağrısı içermez. Geçici automation Result alanı değişir.

**Sürüm sınırı:** EXE dosya bilgisi çalışan/ROT istemcisinin veya veritabanı sunucusunun sürümü değildir. Çalışan sürümü DOORS **Help > About** ile karşılaştırın. Doğrulanmamış bir `Application.Version` üyesi veya DXL sürüm fonksiyonu çağrılmaz. Yan yana kurulumda hangi EXE'nin kayıtlı olduğunu ayrıca doğrulayın. Registry görünümü ile PE mimarisi ayrı kanıtlardır; çapraz mimari uyumluluk yalnızca canlı ortamda sınanabilir.

Canlı çağrılar senkrondur; bu CLI'da timeout/iptal seçeneği yoktur. Giriş ekranı veya istemci iletişim kutusu çağrıyı bekletebilir. Başlamış COM çağrısını thread abort, süreç öldürme veya başka thread'den release ile kesmeyin. Önce istemci etkileşimini tamamlayın. Kütüphanedeki `DxlExecutor` soft timeout sözleşmesi [DXL-EXECUTOR.md](DXL-EXECUTOR.md) dosyasındadır; bu komutlara zorla iptal özelliği eklemez.

## Ortam matrisi ve kayıt

Bu senaryolar **henüz gerçek DOORS üzerinde uygulanmadı**. 9.5 ve 9.7 istemcilerini ayrı, onaylı test ortamlarında sınayın. Normal yetkili Windows oturumu, .NET Framework 4.8, geçerli DOORS lisansı ve DXL izni kullanın. Veritabanında yazma yetkisi olmayan test hesabı tercih edin. Açık düzenleme oturumları yerine modül açılmamış istemciyle başlayın; bu test için gereksinim verisi oluşturmayın veya değiştirmeyin. COM kaydını, ACL'leri veya kurulu EXE dosyalarını hata üretmek için değiştirmeyin.

| İstemci | Entegrasyon işlemi | Offline | Attach ping | Açık activate | About ile karşılaştırma |
| --- | --- | --- | --- | --- | --- |
| 9.5.x | x86 | Bekliyor | Bekliyor | Bekliyor | Bekliyor |
| 9.5.x | x64 | Bekliyor | Bekliyor | Bekliyor | Bekliyor |
| 9.7.x | x86 | Bekliyor | Bekliyor | Bekliyor | Bekliyor |
| 9.7.x | x64 | Bekliyor | Bekliyor | Bekliyor | Bekliyor |

Her satırda OS/mimari, istemci tam build'i, işlem mimarisi, kayıtlı EXE yolu/dosya sürümü/PE, komut, stdout/stderr, istek kimliği ve çıkış kodunu kaydedin. x64 build için yürütme yolundaki `x86` yerine `x64` kullanın. Örnek çıktı toplama:

```powershell
& $doors diagnose --target 9.7 1> diagnose.stdout.txt 2> diagnose.stderr.txt
$diagnoseExit = $LASTEXITCODE
# Canlı test yalnızca ilgili test ortamında:
& $doors ping --target 9.7 --connection attach 1> ping.stdout.txt 2> ping.stderr.txt
$pingExit = $LASTEXITCODE
```

## Manuel senaryolar

Toplu import/update, restart/idempotency, audit/partial/late outcome ve kalite/coverage kapsam senaryoları [PROCESSING.md](PROCESSING.md) içindedir. Native yazmalar test DB'sinde ayrı batch ve kayıt onaylarıyla yapılır; aşağıdaki diagnose/ping senaryoları salt okunur kalır.

Onaylı Classic link yazma/kaldırma için ayrı [LINK-WRITING.md](LINK-WRITING.md) matrisi ve varsayılan kapalı `LinkWriteIntegrationTests` kullanılır. Genel onaya ek olarak silme ayrıca açık onay ister; buradaki ping/diagnose senaryoları salt okunur kalır.

1. **Yardım ve başlatıcı:** DOORS kapalıyken `--help` ve argümansız `doors.cmd` çalıştırın. Kod 0; yardım veya DRY RUN; istemci açılmamalı. Başlatıcının uygulama kodunu ilettiğini sonraki negatif senaryoda da doğrulayın.
2. **Offline kayıt:** `diagnose --target <kurulu sürüm>` çalıştırın. İki registry görünümü, işlem mimarisi/STA, CLSID, Server executable, File version ve PE machine raporlanmalı. Uygun süreç görünümünde kayıt/sürüm eşleşiyorsa ilk iki adım Passed, dört canlı adım Skipped, kod 0. İstemci açılmamalı.
3. **Eksik kayıt:** DOORS kurulu olmayan ayrı test makinesinde `diagnose` çalıştırın. `ComRegistration: Failed (14)`; sonraki adımlar Skipped; açıklamada ProgID ve görünüm olmalı. Kayıt oluşturulmamalı, istemci açılmamalı. Bu durumu oluşturmak için kayıt silmeyin.
4. **Hedef uyuşmazlığı:** 9.7 kayıtlı ortamda `diagnose --target 9.5` (9.5 ortamında tersi) çalıştırın. Kayıt Passed; ClientVersion Failed/22; kayıt değişmemeli. Dosya meta verisi yoksa beklenen kod 21'dir; bunu başarılı sürüm tespiti saymayın.
5. **Mevcut oturum yok:** Kayıtlı istemciyi normal menüsünden kapatıp `ping --target <kurulu sürüm>` çalıştırın. ROT oturumu yoksa bağlantı Failed/15, çalıştırma/cevap Skipped. İstemci açılmamalı; activate fallback olmamalı. Kalıcı automation sunucusu ROT'ta kalmışsa ortamı kaydedin; zorla süreç kapatmayın.
6. **Başarılı attach:** İstemciyi elle açın, test hesabıyla giriş yapın; Help > About sürümünü kaydedin. `ping --target <kurulu sürüm> --connection attach` çalıştırın. Uygun kayıt ve erişilebilir ROT varsa altı adım Passed, DxlResponse mesajında PONG, kod 0. Her yeni çağrının istek kimliği farklı olmalı. ROT sunulmayan ortamda 15 beklenir; bunu başarılı bağlantı saymayın.
7. **Tam canlı tanılama:** Aynı ortamda `diagnose --live --target <kurulu sürüm>` çalıştırın. Ping ile aynı altı aşama ve kod beklenir. Yanlış --target ile tekrarlayın: ClientVersion 22 olarak kalmalı; canlı adımlar Passed olsa bile toplam kod 22 olmalı.
8. **Açık aktivasyon:** İstemciyi normal biçimde kapattıktan sonra `ping --connection activate --target <kurulu sürüm>` çalıştırın. İstemci başlatılabilir/yeniden kullanılabilir. Giriş ekranı açılırsa elle tamamlayın. Kod 0 yalnızca bütün adımlar Passed ise beklenir. Kod otomatik giriş veya Quit çağırmamalı. İstemcinin COM referansı bırakılınca yaşam davranışını kaydedin.
9. **Mimari/sürüm ayrımı:** Aynı ortamda x86 ve x64 çıktılarıyla senaryo 2 ve 6'yı tekrarlayın. İşlem mimarisi doğru değişmeli, her görünüm ayrı listelenmeli. Diğer görünümdeki kayıt yanlışlıkla başarı üretmemeli. Herhangi bir kombinasyonda destek yoksa hata kodunu ve gerçek sonucu matrise işleyin.
10. **Meşgul/izin durumu:** Ortamda zaten var olan bir iletişim kutusu, DXL kısıtı veya erişim reddi varsa ping sonucunu kaydedin. COM çağrısı 17/18 üretirse ilgili adımda görünmeli; otomatik retry yoktur. Çağrı beklerse normal istemci etkileşimini tamamlayın. Sırf hata üretmek için canlı oturum yetkilerini değiştirmeyin veya sonsuz DXL çalıştırmayın.
11. **Geçersiz argüman:** `diagnose --connection activate`, `ping --script write.dxl`, `ping --target 9.6` çalıştırın. Kod 2 ve yardım yönlendirmesi; kayıt sorgusu/COM/DXL başlamamalı.
12. **Veri değişmezliği:** Canlı testleri modül açmadan uygulayın. Salt okunur test hesabı kullanılmasını, betiğin sabit oleSetResult içeriğini ve komutlarda veri yazma işlemi bulunmadığını kaydedin. Kurumun mevcut audit/history görünümünden test hesabına ait veri değişikliği olmadığını doğrulayın; doğrulama için yeni veri/attribute/baseline oluşturmayın. Normal oturum/audit girişleri veri yazma işlemiyle karıştırılmamalı.

Bozuk/eski Result, eksik automation üyesi, Result getter hatası ve cleanup hatası sabit canlı betiği değiştirerek veya kayıt bozarak üretilmez. Bu hata dallarını DOORS'suz test çiftleriyle çalıştırın; Test Explorer'da `DoorsHealthChecksTests` ve `HealthCommandTests` sınıflarını seçin. Tam suite build/test yönergeleri [README](../README.md) içindedir. Testlerde sırasıyla runStr hatası ile yanıt hatası ayrılır, eski/boş/bozuk yanıt reddedilir, cleanup hatası ilk hatayı gizlemez ve hassas COM/Result metni rapora/loga sızmaz.

Canlı API sözleşmesi daha önce doğrulanan IBM `DOORS.Application`, `runStr`, `Result` ve `oleSetResult` üyelerini kullanır: [IBM DXL Reference Manual](https://www.ibm.com/docs/en/SSYQBZ_9.7.2/com.ibm.doors.requirements.doc/topics/dxl_reference_manual.pdf). 9.5 için kurulu istemcinin Help menüsündeki sürüme özel kılavuz ve bu matris ile saha doğrulaması gerekir.

## Birleşik WPF Desktop — 2026-10-27

Ana Desktop ZIP'i WPF istemcisini açar. Tüm işlem ekranları, exact-target/acknowledgement ve ayrı link deletion onayı, batch intent + per-record approval, watchdog/native Double ayırımı, read-only başlangıç, native drain ve 9.5/9.7 kabul listesi: [UNIFIED-DESKTOP.md](UNIFIED-DESKTOP.md). Mock/fake gateway testleri gerçek native kabul yerine geçmez.


## TraceabilityGraphService — read-only native acceptance

See TRACEABILITY-GRAPH.md for a configured repository/module/version/link-module scope. In an approved 9.5/9.7 read-only environment, compare sampled incoming/outgoing rows with source→target graph endpoints; verify explicit baseline raw-label bindings, denied targets/source references, partial scope Unknown results, role/link-module/version policies and allowed/reported/prohibited cycles. Check multi-hop boundaries/limits and JSON/CSV/GraphML consumers. Current v1 native link snapshots lack qualified link-module IDs; ID-required policy must remain Unknown. Do not create links, requirements or baselines merely to populate the graph. Native waits must retain the original executor/cleanup and must not dispatch retries.

## Requirements Quality Rule Engine — read-only acceptance

Use an approved retained snapshot and a new JSON project profile. Verify EmptyRequirement, ambiguous-term indicators, multiple-obligation candidates, verification method plus acceptance criteria, configured mandatory fields and exact traceability scope. Confirm Denied/NotRequested/unknown baseline or native link-module identity yields NotEvaluated rather than a fabricated missing fact. Unapproved profiles must not run in ApprovedProfileOnly; DraftPreview stays advisory. Cancel/wrong fingerprint/missing acknowledgement must not save an approved project profile. Configuration/project changes must invalidate approval. Reviewer disposition must preserve the automatic finding, require exact result and prior revision, and reject incomplete ledger tails or changed result evidence. Test outputs must state NotAssessed/no compliance or tool-qualification claim. These checks require no DOORS database mutation. See REQUIREMENTS-RULE-ENGINE.md.
