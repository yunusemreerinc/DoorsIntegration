# Task 26 — Bulk Baseline Reporting and Audit Trail

Kesinti sonrası kod/kanıt kontrolünde bulunan hatalar, düzeltmeler ve gereksinim eşlemesi: [TASK26-REVIEW.md](TASK26-REVIEW.md). History artık UI'de de sayfalanır; reconciliation bitmiş attempt süresini korur ve audit görünümünü günceller.

Bulk Baseline Manager artık onaylanan planı, modül bazındaki gerçekleşen sonuçları ve job geçmişini JSON, CSV veya XLSX olarak raporlar. Başlangıç Mock + read-only olarak kalır. Bu görev gerçek DOORS verisini değiştirmez; mevcut Real preflight, deployment ve iki ayrı kullanıcı onayı engellerini kaldırmaz.

## Kullanım

1. **Bulk Baseline Manager → Open Job History**: Search (JobId, module identity/path veya environment), Operator, Database, Outcome ve yerel başlangıç/bitiş tarihi filtrelerini girip tekrar **Open Job History** seçin. Kaynak, hedef 9.5/9.7 profili ve repository kapsamı otomatik uygulanır. Sonuçlar en yeni tarihten başlayarak 100'lük sayfalarda gösterilir; **Previous Page / Next Page** ile ilerlenir. Filtre değiştiğinde ilk sayfadan yenileyin. Eşleşen toplam, tarama sınırı ve açılamayan/kilitli/bozuk/değişmiş job uyarıları raporlanır. Core API de offset/limit destekler ve tam payload'ı yalnız istenen sayfa için bellekte tutar.
2. **Read / re-report selected (no reconciliation)** yalnız yerel journal'ı okur. **Audit Report** sekmesinde job/operator/database/environment/client sürüm kanıtı, başlangıç/bitiş, grup, stable identity, previous/expected/actual baseline, status, duration ve hata görülür. Bu işlem COM/DXL veya baseline oluşturma çağrısı yapmaz.
3. **Export root** altında oluşturulacak rapor için Json, Csv veya Xlsx seçin; **Export Report / Export Result** kullanın. Her export yeni ve benzersiz bir klasöre atomik yayımlanır; mevcut rapor üzerine yazılmaz. İşlem worker üzerinde çalışır, progress ve mevcut Cancel ile iptal sunar. Başarısız export yalnız hata manifestini yayımlar; kısmi payload kaldırılır.
4. **Open Selected Job** önceki recovery davranışını korur: Core read-only reconciliation yapar ve yeni audit olayları ekler. **Reconcile current job (read-only)** aynı runner'a bağlıdır ve audit ekranını günceller. Bitmiş attempt'in completion zamanı/süresi tekrar hesaplanmaz. Bu iki yol da baseline oluşturmaz. Devam/retry için önceki açık onay ve yeniden doğrulama kuralları geçerlidir; rapor veya history yazma yetkisi değildir.

### Çıktı ve sayımlar

| Grup | Anlam | Batch sayımı |
|---|---|---|
| Successful | Bu revision'da doğrulanmış `Succeeded` | SuccessCount |
| Skipped | Atlanan / dispatch edilmeden iptal edilen | SkippedCount |
| Locked | Başka session lock'u nedeniyle uygun olmayan | SkippedCount içine dahil; ayrıca LockedCount |
| Failed | Bilinen başarısızlık | FailedCount |
| Unverifiable | Unknown, NeedsReconciliation, in-flight veya henüz sonuçlanmamış | UnknownCount |

`SuccessCount + FailedCount + SkippedCount + UnknownCount = module count`. LockedCount ikinci kez toplama eklenmez. Bütün modüller başarılı ve job sonlanmış olmadıkça `IsFullSuccess=false` kalır. Başarılı ve başarısız/atlanan satırlar birlikteyse PartialSuccess; belirsiz veya henüz sonlanmamış durumda Indeterminate üretilir. Export `Completed` olması dosyanın üretildiğini gösterir; batch başarısını job outcome ve sayımları belirler.

ActualBaseline yalnız kaydedilmiş doğrulanmış başarı/reconciliation kanıtından üretilir. Beklenen hedefin var olduğu varsayılmaz. Bir başarı kaydı sonradan çelişkili hale gelirse yeni raporda actual=null olur; önceki başarı olayı append-only geçmişte korunur. Hedefin varlığının doğrulanması, baseline'ın mutlaka bu uygulama tarafından oluşturulduğu anlamına gelmez. Previous sürümü numaralandırma watermark'ı olabilir; silinmiş baseline ile latest-existing ayrımı önceki metadata modelinde korunur. Süre son attempt'in gözlenen başlangıç/bitiş farkıdır; server commit zamanını veya bütün retry sürelerinin toplamını iddia etmez. Job başlangıç/bitişi runner yaşam döngüsüdür; Unknown sonrasında native işlem sürebilir.

## Mimari ve API

- **Core**: `BulkBaselineReport`, gruplar/outcome, `BaselineJobAuditContext`, `BaselineApprovedPlan`, `BaselineJobAuditEntry`, `IBaselineJobAuditStore`, `IBulkBaselineReportService`, `IJobHistoryService` ve filtre/page DTO'ları. Eski runner constructor'ı korunur; geniş constructor `IBaselineJobAuditContextProvider` kabul eder.
- **Export**: `BulkBaselineReportService` geçerli job snapshot'ını ve audit prefix hash/revision'ını yakalar. `BulkBaselineReportSource` satırları lazy üretir; bütün event geçmişini belleğe toplamaz. Export sırasında yakalanan prefix yeniden doğrulanır; kayıp/değişmiş kanıt sessizce kısmi rapora dönüşmez.
- **Com**: `ComBaselineJobAuditContextProvider` mevcut registry/file inspector'ını kullanır. Kayıtlı binary sürümü varsa kaydeder ve `RegisteredBinaryOnly; active session executable version NOT_VERIFIED` kanıtını ekler. Registry verisi yoksa native client sürümü uydurulmaz: RequestedProfileOnly / NOT_COLLECTED. Mock sürüm simülasyon olarak etiketlenir. Host doğrulanmış session sürümü sağlayacak özel context provider kaydedebilir; registered binary aktif COM session sürümü kabul edilmez.
- **DI**: Console/Desktop host'larında yeni context, protector, audit store, history ve report servisleri `TryAdd` ile kayıtlıdır. Host override'ları korunur. Özel journal kullanan host, `IBaselineJobAuditStore` da sağlamalı/kaydetmelidir; varsayılan File journal iki sözleşmeyi birlikte uygular. WPF mevcut session composition üzerinden aynı Core/Export servislerini kullanır.
- **WPF/MVVM**: session Reports/JobHistory portları, history filtreleri ve read/re-report komutu, format seçimi, audit sekmesi ve reconciliation bağlantısı. I/O mevcut workspace worker'ındadır; yeni COM çağrısı veya UI-thread COM erişimi eklenmez. Filtreler module selection veya onaylanmış batch kapsamını değiştirmez; job açma/re-report eski write ticket'ını temizler.

```csharp
var reports = provider.GetRequiredService<IBulkBaselineReportService>();
BulkBaselineReport report = await reports.ReadAsync(jobId, cancellationToken);
var result = await reports.ExportAsync(jobId,
    new ExportRequest(newDestinationDirectory, ExportFormat.Xlsx,
        ExportErrorPolicy.StopOnInvalidRow), progress, cancellationToken);

var history = provider.GetRequiredService<IJobHistoryService>();
var page = await history.QueryAsync(new BaselineJobHistoryFilter(
    actor: "operator", database: "database", outcome: BaselineBatchOutcome.Indeterminate,
    offset: 0, limit: 100), cancellationToken);
```

Export üst klasörü API çağrısından önce mevcut olmalı; hedef klasör yeni olmalıdır. WPF seçilen export root'unu worker üzerinde oluşturur. Yeni schema tabloları: `BulkBaselineJobs`, `BulkBaselineModules`, `BulkBaselineApprovedPlans`, `BulkBaselineAuditEvents`. JSON `data.json`, CSV tablo başına `.csv`, XLSX `data.xlsx`; hepsinde mevcut `report.json` hata/işlem manifesti vardır. Eski sekiz temel export tablosu korunur; yeni tablolar yalnız veri varsa eklenir. Unicode, invariant/UTC tarihler, CSV formula injection koruması, XLSX metin hücreleri, bounded disk spool ve ValueChunks mevcut exporter'dan gelir. Mevcut DocumentFormat.OpenXml kullanılır; yeni NuGet bağımlılığı yoktur.

## Append-only journal ve güven sınırları

`{JobId:N}.audit/{Revision:D20}.json` authoritative append-only revision dosyalarıdır. Her olay önceki hash'i, public masked projection'ı ve Windows kullanıcısına bağlı DPAPI ile korunan recovery delta'sını içerir. Genesis tam başlangıç snapshot'ı; sonraki olaylar değişen modülleri ve başlıkları taşır. Olaylar create-new + flush + atomic move ile yayımlanır; committed revision hiçbir normal Save/Reconcile tarafından değiştirilmez veya silinmez. Sıra/hash/schema/identity doğrulaması başarısızsa job reset edilmez ve write dispatch edilmez. Varsayılan sınırlar: tek dosya 64 MiB (yapılandırılabilir), job başına 100000 revision ve 4 GiB audit. Sınıra ulaşma sessiz audit kaybı yaratmaz; işlem durur.

`{JobId:N}.json` **değiştirilebilir, DPAPI korumalı materialized recovery cache**'idir; audit journal'ın kendisi değildir. Audit önce, cache sonra commit edilir. Cache eksik veya eskiyse geçerli audit head replay edilir. Cache ileri/çelişkili/bozuksa fail-closed olur. Commit hatasından sonra aynı lease tekrar kullanılamaz; yeniden açma gerekir. Audit kaydı/onay checkpoint'i başarısızsa sonraki write başlatılmaz. Başarı veya in-flight intent sonrasındaki crash, önceki Task 24 reconciliation/idempotency kurallarıyla ele alınır.

Eski schema-1 plaintext snapshot yalnız eski checksum/identity kontrolleriyle okunabilir. Salt okunur raporlama geçmiş uydurmaz. İlk sonraki runner checkpoint'i onu `LegacyImported` genesis olarak içe alır; yeni cache schema-2 korumalıdır. `LegacySnapshotOnly` veya `ImportedFromRevision=N` kapsamı açıkça raporlanır. Legacy dosya/yedekleri export servisi tarafından otomatik temizlenmez; eski kopyalar mevcut korumalarını korur.

WPF `IBaselineApprovalAuditSource` aracılığıyla kullanıcının incelediği **tam batch**, özgün onay UTC'si, fingerprint, module scope ve tek ApprovalId kaydeder. Her modül bu ApprovalId'ye bağlanır. Kabul edilen approval checkpoint'i dispatch'ten önce dayanıklı kaydedilir. Genel Core confirmation portu ayrıntılı audit kaynağı sunmuyorsa yalnız o portun kabul ettiği tek-modüllük kapsam açıkça etiketlenir; hiç verilmemiş tam batch/Real ikinci onayı varsayılmaz. Audit'te saklanan plan bilgisi yeniden kullanılabilir bir yazma yetkisi değildir.

Parola, token ve authentication alanı şemaya eklenmez. Bilinen credential atamaları, Bearer ve URI user-info public audit/rapor metinlerinde maskelenir; exception logları raw payload yerine tip/HResult içerir. Exact business input yalnız korunan operational payload'da recovery için tutulur. Annotation/hata/iş metadatasına sır girilmemelidir; bir metnin kuruma özel hassasiyetini genel regex belirleyemez. Raporlar kontrollü iş metadatası içerir; saklama/paylaşma politikası uygulayan kurumun sorumluluğundadır.

DPAPI için geçerli/yüklü Windows kullanıcı profili gerekir; koruma başarısız olduğunda **plaintext fallback yoktur**. Bu sandbox'ta DPAPI protect başarısız oldu; Windows mesajı yüklenmemiş kullanıcı profili olasılığını belirtiyor, kesin kök neden doğrulanmadı. Offline testler yalnız test assembly'sindeki açıkça enjekte edilmiş, process-key AES/HMAC sağlayıcısını kullanır; uygulama varsayılanı değişmez. Kullanıcı makinelerinde DPAPI roundtrip ve aynı Windows hesabıyla restart ayrıca doğrulanmalıdır; başka hesaba taşımak otomatik desteklenmez.

Yerel hash chain erişim kontrollü dosyalarda kazara bozulma/çelişki tespit eder. WORM depolama, dijital imza, güvenilir dış zaman damgası veya yetkili yerel kullanıcıya karşı tamper-proof/rollback-proof kayıt iddiası yoktur. Dosya izinleri ve bütün cache+audit dizininin yedeklenmesi deployment politikasına bağlıdır. **Bu audit raporları DOORS'un native history kayıtlarının yerine geçmez. Batch transactional değildir; kısmi/bilinmeyen sonuçlar tam başarı değildir.**

## Değişen dosyalar ve kabul

Yeni: Core `BulkBaselineAuditModels.cs`, `BulkBaselineReporting.cs`; Com `ComBaselineJobAuditContextProvider.cs`; Export `BulkBaselineReportService.cs`; test `BulkBaselineReportingTests.cs`, `TestBaselineJournalProtector.cs`; bu belge.

Güncellenen: Core job modelleri/runner/File journal/csproj ve ExportModels; ExportSchemas; Console/Desktop DI; WPF ClientSession/BaselineManagerSession/BaselineManagerApproval/VM/XAML; mevcut journal/runner/WPF testleri; README, WPF guide ve VALIDATION. SettingsPanel, DesktopSettingsStore, watchdog ayarları, ikon ve DXL scriptleri bu görev kapsamında değiştirilmez. Önceki release ZIP'leri korunur; güncel çalıştırılabilir dosyalar `src/DoorsIntegration.Wpf/bin/{x86,x64}/Release/net48/` altındadır.

Kabul kontrolleri: beş grup ve sayım invariant'ı; partial/unknown ayrımı; actual değerinde doğrulama şartı; batch onayı ve sonuç bağlantısı; üç format + güvenli hücreler + Unicode/UTC; append-only prefix değişmezliği, chain/corruption/missing-tail, cache lag/missing/restart, legacy import, protection/audit failure öncesi dispatch engeli; history filtre/page/bozuk-job raporu; WPF render/binding/read-only/re-report/reconciliation ve önceki idempotency/cancellation testleri. Gerçek DOORS testleri ortam/onay yoksa NOT_RUN kalır. Güncel build/test sayıları ve kanıt yolları [VALIDATION.md](VALIDATION.md) dosyasındadır.

## Build / test

Repo kökünden PowerShell; Windows, .NET Framework 4.8 Developer Pack ve mevcut SDK 5.0.416 host:

```powershell
$env:DOTNET_CLI_HOME = Join-Path $PWD '.cli'
$env:DOORS_TEST_MODE = 'disabled'
Push-Location artifacts/SmokeBuildHost
foreach ($arch in @('x86', 'x64')) {
    dotnet restore ../../DoorsIntegration.sln -p:Platform=$arch --packages ../../.packages --configfile ../../NuGet.Config --ignore-failed-sources
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed' }
    dotnet build ../../DoorsIntegration.sln -c Release -p:Platform=$arch --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    dotnet test ../../tests/DoorsIntegration.Tests/DoorsIntegration.Tests.csproj -c Release -p:Platform=$arch --no-build --no-restore --settings ../../tests.runsettings --logger "trx;LogFileName=bulk-baseline-reporting-full-$arch.trx" --results-directory ../TestResults -- RunConfiguration.TargetPlatform=$arch RunConfiguration.TestSessionTimeout=420000
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
}
Pop-Location
```

Manuel read-only kabul: Mock'ta iki modülü yeni onayla çalıştırın, history filtreleyip yeniden raporlayın ve üç formatı açın; app restart sonrası aynı JobId'yi reconciliation ile açın ve duplicate oluşmadığını kontrol edin. Unknown/locked fixture raporlarında actual=null ve outcome/count ayrımını inceleyin. Geçerli Windows profilinde cache/audit DPAPI roundtrip ve restart doğrulayın. Real modda yalnız yerel rapor/history kullanın; native create/deployment doğrulaması bu raporlama görevinin test kanıtı sayılmaz.
