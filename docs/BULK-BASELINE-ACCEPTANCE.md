# Task 27 — Bulk Baseline Verification and Acceptance

Paket, mevcut Core selection/preflight/job runner/report servislerini koruyarak otomatik Mock kabul testleri ve sürüme özel manuel native listeleri sağlar. Üretim Core/COM/DXL/WPF kodu veya gerçek yazma kapıları değiştirilmez. Gerçek DOORS yazması bu paket tarafından başlatılmaz.

## Otomatik çalıştırma

Windows, .NET Framework 4.8 Developer Pack ve mevcut SDK 5.0.416/offline package cache gereklidir. Repo kökünden:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test-BulkBaselineAcceptance.ps1 -Architecture x64
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test-BulkBaselineAcceptance.ps1 -Architecture x86
```

ExecutionPolicy parametresi yalnız bu incelenmiş yerel script'in alt sürecine aittir; makine/kullanıcı ilkesi değiştirilmez. Makine politikası engelliyorsa kurumunuzun onaylı script çalıştırma yöntemini kullanın. Script çözümü restore/build eder, yalnız `TestCategory=BulkBaselineAcceptance` kategorisini çalıştırır ve mevcut ortam değişkenlerini sonunda geri yükler. `DOORS_TEST_MODE=disabled` zorunlu tutulur; script'in Real write seçeneği yoktur.

`-OutputDirectory` yeni bir kanıt klasörü seçer; mevcut klasörün üzerine yazılmaz. `-NoBuild` yalnız eşleşen mimaride önceden hazırlanmış güncel binary için kullanılabilir; bu durumda rapor BuildStatus=NOT_RUN gösterir. Varsayılan çıkış `artifacts/BulkBaselineAcceptance/<UTC>-<arch>-<id>/` altındadır.

Çıktılar: `acceptance.json`, `acceptance.csv`, `acceptance.md`, `acceptance.trx`, build/restore/test logları ve `mock-reports/` altında üç formatlı örnek Mock raporları. TRX ekleri de kaydedilir; DataRow attachment gruplaması bütün dosyaları listelemese bile `mock-reports/` kopyaları ve JSON içindeki `MockReportExampleDirectories` kullanılabilir. Her senaryonun PASS/FAIL/NOT_RUN durumu, beklenen/gözlenen vaka sayısı ve scope'u kaydedilir. Eksik test sonucu PASS sayılmaz. Exit 0 yalnız **Mock kabul paketi PASS** anlamındadır; gerçek release acceptance ve NativeComDxlValidated ayrı tutulur.

## Otomatik kapsam

`BulkBaselineAcceptanceTests` 15 senaryo için 24 vaka içerir:

| ID | Kapsam | Vaka |
|---|---|---:|
| A01 | İki projedeki klasörlerden toplam 20 formal modül seçimi | 1 |
| A02 | Overlap klasörlerinde stable identity ile tekilleştirme; link/diğer türler seçilmez | 1 |
| A03 | 1.4 → Major 2.0 / Minor 1.5 Mock next-version teklifleri | 2 |
| A04 | 0.1, 1.4, 2.9, 7.99 gibi farklı previous sürümler için modül bazlı ilerletme | 2 |
| A05 | Hiç baseline bulunmayan modülde explicit Mock Major/Minor teklifi | 2 |
| A06 | Unicode suffix/annotation, modül override, execution ve audit roundtrip | 1 |
| A07 | Fresh target çakışması, açıklayıcı reason ve dispatch engeli | 1 |
| A08 | Create denied + other-session exclusive lock; skip ve unlock yapılmaması | 1 |
| A09 | Partial failure, ContinueOnError / StopOnFirstError ve korunmuş başarı | 2 |
| A10 | Aktif iş gözlenirken Cancel Remaining; sadece başlanmamış kayıtlar durur, resume duplicate üretmez | 1 |
| A11 | Simüle COM timeout sonrası Unknown/NeedsReconciliation; blind retry yok; geç commit read-only doğrulanır | 1 |
| A12 | Ayrı süreçte baseline commit öncesi/sonrası journal checkpoint sınırında zorla kapanma; yeni süreçte recovery | 2 |
| A13 | Aynı JobId tekrar açılır/çalıştırılır; her modül en fazla bir kez create edilir | 1 |
| A14 | 1000 modül seçim + dry-run; üç tekrar, süre/memory/bitness gözlemleri ve yan etkisizlik | 3 |
| A15 | JSON/CSV/XLSX içinde counts, beş grup, ID/path/expected/actual/error/annotation, UTC ve safe-cell doğruluğu | 3 |

A03–A05 fixture'ları açıkça tanımlanmış Mock adapter tekliflerini test eder; DOORS nextMajor/nextMinor algoritmasının native sürümlerde doğrulandığını iddia etmez. A11 gerçek bir COM çağrısı veya güvenli COM abort testi değildir; uygulamanın belirsizlik/reconciliation durum makinesidir.

### Gerçek süreç kapanması, yalnız Mock

Yeni `tests/DoorsIntegration.AcceptanceHost` .NET Framework 4.8 test yardımcı executable'ıdır; normal ürün dağıtımının bağımlılığı değildir. Parent test iki yeni process başlatır. İlk process bir Mock baseline'ı kalıcı simüle server store'a yazar ve yalnız kendisini `Process.Kill` ile sonlandırır. İkinci process aynı JobId/journal/store'u açar, read-only reconciliation ile ilk modülü doğrular, sadece pending modülü tamamlar ve tekrar Run'da duplicate olmadığını kontrol eder. Kapanma hem completion checkpoint'inden önce hem sonra denenir.

Worker yalnız `%TEMP%/DoorsIntegrationJobTests/<guid>` fixture kökünü kabul eder; yeni fixture boş olmalı, recovery marker bulunmalıdır. User journal'ını veya DOORS process'ini hedeflemez. Test root cleanup mevcut sahiplik/yol sınırı kontrolüyle yapılır. Alt process gizli/no-window olarak açılır; süre sınırı aşılırsa yalnız testin başlattığı process sonlandırılır.

Worker'ın bilinen AES/HMAC fixture anahtarı iki ayrı Mock process'in synthetic journal'ı açabilmesi içindir. Bu bir güvenlik/DPAPI testi değildir ve üretim varsayılanını değiştirmez. Gerçek Windows kullanıcı profiliyle DPAPI ve gerçek client crash/reconnect ayrıca doğrulanmalıdır. Üretim varsayılanı DPAPI, native confirmation/deployment kapıları ve mevcut STA kuyruğudur.

### Performans ölçümü

A14 her tekrarda gerçek selection cursor'ını overlap klasörleriyle dolaşır, 1000 okunabilir formal stable ID seçer ve gerçek Core preflight/dry-run katmanını 1000 açık Mock metadata/state fixture'ıyla çalıştırır. Lock/dirty durumları değişmez; executor/write çağrısı yoktur. Varsayılan regresyon bütçesi seçim için 30000 ms, dry-run için 120000 ms'dir. `DOORS_ACCEPTANCE_SELECTION_BUDGET_MS` ve `DOORS_ACCEPTANCE_DRYRUN_BUDGET_MS` 1–600000 aralığında ayarlanabilir; kullanılan bütçe sonuçta kaydedilir. Bunlar native throughput/SLA iddiası değildir.

Her tekrarda selectionMs, dryRunMs, processBits ve process peak working set TRX stdout ve acceptance JSON/MD içinde saklanır. Peak working set bütün test process'ine aittir; bir operasyonun tam bellek tahsisi veya kesin memory benchmark'ı olarak sunulmaz.

## Gerçek DOORS acceptance — ayrı kanıt

- [DOORS Classic 9.5 checklist](../tests/acceptance/DOORS-9.5-ACCEPTANCE.md)
- [DOORS Classic 9.7 checklist](../tests/acceptance/DOORS-9.7-ACCEPTANCE.md)
- [Hazırlık kapısı, adımlar ve kanıt kuralları](../tests/acceptance/REAL-ACCEPTANCE-GUIDE.md)
- [İki sürüm için NOT_RUN sonuç şablonu](../tests/acceptance/native-results.template.json)

Her sürümde 11 ayrı manuel satır metadata, tek Major/Minor, batch, save/dirty, lock, permission, create sonrası handle, exact post-verification ve app/client crash/reconnect/reconciliation kapsar. Release/build, ortam, operator/reviewer ve write senaryoları için explicit/ikinci onay kanıtı olmadan PASS verilmez. Bugünkü standart Real baseline runtime, önceki native state/next-version/deployment doğrulamaları tamamlanmadığında yazmaya kapalıdır. Bu paket engelleri aşmaz. Eksik ortam/onay/capability NOT_RUN; doğrulanmış beklenen davranış PASS; uyuşmazlık FAIL'dir.

**Mock PASS, COM/DXL entegrasyon doğrulaması değildir.** Script native satırlarını otomatik PASS'e dönüştürmez; gerçek ortam yokken her iki sürüm NOT_RUN kalır. Uygulama audit raporları DOORS native history yerine geçmez veya sertifikasyon kararı üretmez.

## Build ve regresyon

Çözüm artık sekiz ürün projesi, mevcut test projesi ve yalnız test amaçlı AcceptanceHost ile on projedir. Yeni NuGet bağımlılığı yoktur; host mevcut WPF Mock store ve Core portlarını kullanır. Normal Release build/test komutları geçerlidir. Tam suite örneği (SDK host klasöründen, matching architecture restore/build sonrası):

```powershell
$env:DOORS_TEST_MODE = 'disabled'
dotnet test ../../tests/DoorsIntegration.Tests/DoorsIntegration.Tests.csproj -c Release -p:Platform=x64 --no-build --no-restore --settings ../../tests.runsettings --logger 'trx;LogFileName=task27-full-x64.trx' --results-directory ../TestResults -- RunConfiguration.TargetPlatform=x64 RunConfiguration.TestSessionTimeout=420000
```

Güncel ölçümler ve sayılar [VALIDATION.md](VALIDATION.md) ve acceptance artifact klasörlerinde yer alır. Mevcut Task 19–26 servisleri/testleri korunmuştur; production DXL/ayar/ikon değişikliği veya canlı veri değişikliği yapılmamıştır.
