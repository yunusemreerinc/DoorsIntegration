# Task 25 — WPF Bulk Baseline Manager

**Task 26 güncellemesi:** Aşağıdaki Task 25 kaydı tarihsel başlangıçtır. Güncel sürümde JSON/CSV/XLSX mevcut exporter üzerinden üretilir; yeni Audit Report sekmesi ve filtreli history/re-report vardır. Onaylanan plan artık audit verisi olarak saklanır; saklanan kayıt yazma yetkisi değildir. Authoritative append-only audit ve DPAPI korumalı recovery cache ayrımı, yeni dosya adları ve kullanım için [BULK-BASELINE-REPORTING.md](BULK-BASELINE-REPORTING.md) geçerlidir.

Yeni **Bulk Baseline Manager** ana sekmesi, Task 20 selection, Task 22 preflight, Task 23 adapter/onay ve Task 24 durable job runner sonuçlarını birleştirir. İlk sekiz ana sekme korunur; yeni ekran dokuzuncudur. Başlangıç Mock + salt okunur moddur. Theme mevcut koyu mavi kaynakları kullanır.

**Mock uçtan uca çalışır:** baseline simülasyonu job journal'ından ayrı yerel store'a kaydedilir; history/reconcile ve restart aynı simüle baseline'ı görür. **Real baseline yazması hazır kabul edilmez:** native state/next-version ve 9.5/9.7 deployment doğrulamaları beklediğinden standart Real runtime `CanWrite=false` kalır. İkinci onay bu engeli kaldırmaz. Yeni DXL perm, DWA/OSLC/HTTP yolu yoktur; gerçek DOORS verisi değiştirilmedi.

## Mock kullanım

1. Kaynak **Mock** olarak kalsın. Yeni sekmede örnek kök `/Bulk/P01/F01` yazıp **Load Tree** seçin. Sol ağaçta F01 kutusunu işaretleyin. Her seçili klasörün **Include Subfolders** kutusu bağımsızdır; `+` çocukları lazy yükler. Birden fazla proje/klasör işaretlenebilir.
2. **Discover Formal Modules** ilk sayfayı yükler. **Load More** bounded keşfi sürdürür; formal olmayan modüller aday olmaz. Overlap eden klasörlerden gelen modüller stabil kimlik/yol ile tekilleştirilir. Erişim kısıtlı adaylar listelenir ancak seçilemez.
3. Modül checkbox'larını seçin. Örnek 001 ve 002 uygundur; 003 başka session lock, 004 unsaved, 005 create access denied örnekleridir. Search ve **Selected Only** yalnız görünümü değiştirir; gizlenen seçili modüller korunur. **Select All (filtered)** yalnız filtreyle eşleşen yüklü adayları seçer; **Clear Selection** tüm seçimi açıkça temizler.
4. Tek batch için **Major/Minor**, suffix, annotation ve execution policy seçin. Suffix en fazla 128, annotation en fazla 16384 karakterdir; geçersiz Unicode/kontrol karakteri bloklanır. Mock numbering native algoritma doğrulaması değildir.
5. **Preflight / Dry Run** izin/lock/dirty/version/context kanıtlarını ve fresh revalidation'ı okur. DataGrid: checkbox, ModuleName, FullPath, LastVersion, TargetVersion, LockStatus, Eligibility. Tooltip ve raporda reason, warning ve önerilen çözüm vardır. LastVersion son mevcut baseline'dır; progress'teki Previous sürüm numaralandırmasında dikkate alınan recorded version olabilir (silinmiş baseline watermark dahil).
6. UI, **seçili tüm modüller eligible olmadan** Review/Execute açmaz. Uygun olmayanları elle kaldırın veya **Select Eligible** seçin; bu açık seçim değişikliği planı geçersizleştirir, ardından preflight tekrar çalıştırılır. Bu UI kuralı çekirdeğin ContinueOnError/SkipBlocked davranışından daha katıdır.
7. Yazmak için ana penceredeki **Salt okunur mod** kutusunu kapatın. **İncele ve Kesin Onayla** modül sayısı, type, suffix/annotation, exact path/ID, previous/target, tüm etkilenecek modüller, warning, fingerprint ve riskleri gösterir. Hedef metni aynen yazıp acknowledgement işaretlenmeden modal onayı açılamaz. Onay doğrudan baseline oluşturmaz.
8. Yalnız başarılı preflight + canlı kesin onay + uygun runtime + yazma modu ile **Execute** aktif olur. **Execute** durable job oluşturur/aynı job'a devam eder. Her modül öncesi Core tekrar exact target ve preflight okur; başarısız koşul eski onayla yazılmaz.

Ağaç/scope, modül seçimi, repository, type, suffix/annotation, policy, profil veya read-only değişimi eski onayı geçersizleştirir. Sadece Search/Selected Only değişikliği planı veya seçimi bozmaz. **Refresh** yeni cursor ile mevcut selection profile'ını yeniden doğrular; henüz yeniden yüklenmemiş seçimler pending kalabilir. Scope değişimi ve Load Tree açık bir kapsam değişikliğidir, eski seçimi sıfırlar.

## Onay modeli ve Real ayrımı

`BaselineManagerApproval` memory'de bir plan ticket'ı tutar. Expiry, plan yaşı ve observation yaşının küçüğüyle sınırlıdır. Ticket exact selected module identity, current/expected version, effective suffix/annotation, source/state/metadata, type/policy/target/connection/time limitlerine bağlıdır. Tek durable JobId'ye bağlanır ve her modül için en fazla bir kez kullanılır. Journal veya history bir approval saklamaz.

Runner'ın tek-modüllük fresh planının request-list hash'i batch hash'inden farklıdır. Yeni `BaselinePrecondition.HasSameObservedState` yalnız bu daraltmayı destekler: state/context/revisions/ACL/locks/target availability ve metadata fingerprint exact eşleşmelidir. Gate ayrıca bütün write input ve policy/timeout bağlarını karşılaştırır. Bu metot tek başına yazma yetkisi değildir. Değişmiş metadata/content/session için approval portu false döner; yeni inceleme gerekir.

Real için ticket kaydı **iki ayrı pozitif dialog cevabı** ister: ilk batch review ve `CONFIRM REAL DATABASE WRITE N` hedefli ikinci yazma onayı. Mock kanıtı Native olarak kabul edilmez. Real banner farklı renkte ve açık metinle gösterilir. Mevcut native metadata/state/compatibility engelleri korunduğundan standart Real Review/Execute disabled kalır. Doğrulanmış bir host runtime sağlayıcısı olmadan UI'den enable eden ayar eklenmedi.

## Progress, cancellation ve history

Alt panelde Module/Previous/Target/Status/Duration/Error tablosu, JobId, başarı sayısı, elapsed ve overall progress vardır. Status'lar Task 24 modelinden gelir. Başarılı modül UI'de `Succeeded · no replay` olarak da işaretlenir. Global footer tamamlanmış işte Succeeded/CancelRequested sonucunu gösterir.

**Cancel Remaining** caller cancellation ister; aktif COM çağrısını güvenli abort ettiğini iddia etmez. Core aktif sonucu gözler, başlanmamış satırları durdurur ve journal kaydeder. Başarılar korunur. İptal edilmiş hiç denenmemiş satırlar yeni Preflight + yeni kesin onay ile **aynı JobId'de** sürdürülür; Succeeded modüller yeniden oluşturulmaz. Unknown/NeedsReconciliation diğer yazmaları durdurur. Overall progress, tamamlanmış/settled satır oranıdır; iptal edilmiş kalan satırlar başarı gibi yüzde 100 sayılmaz.

**Open Job History** ilgili source/version/repository'nin en yeni en fazla 1000 journal kaydını gösteren alt sekmeye geçer. **Open Selected Job** aynı JobId'yi read-only recovery ile açar; eski onay yüklenmez. **Reconcile (read-only)** Task 24 `OpenAsync` üzerinden exact baseline metadata/existence kontrolü yapar; baseline create/save/discard/delete çalıştırmaz. Çelişkiler NeedsReconciliation, önceki success marker sticky kalır.

**Retry Selected** ek bir seçenektir: eski başarısı olmayan Failed/Skipped/Unknown/NeedsReconciliation satır için Core'un fresh metadata/authoritative absence ve değişmemiş target kontrolleri çalışır. Retry dialog'u previous status/attempt/error, fresh existence, exact target ve riskleri gösterir; Real'de ikinci onay da gerekir. Onaydan sonra yeniden verification/preflight yapılır. Otomatik retry veya rollback yoktur.

**Export Result JSON / CSV** retained immutable job snapshot'ını export root altında yeni bir dizine yazar; mevcut dosya üzerine yazmaz. JSON full runtime snapshot'tır; CSV JobId/ModuleId/path/previous/target/status/duration/error taşır. UTF-8, invariant süre sayıları, quoted CSV ve `= + - @` ile başlayan değerler için injection önlemi kullanılır. Bu ekranda XLSX result export eklenmedi; diğer uygulama export servisleri korunur.

## Yerel persistence ve thread modeli

Varsayılan kök `%LOCALAPPDATA%\DoorsIntegration\WpfBulkBaseline\{Mock|RealDoors}\{9.5|9.7}`. `jobs` Task 24 atomik/checksum/revision journal'ını kullanır. Mock için ayrı `mock-baselines` JSON kayıtları bulunur; fake server durumu journal'dan türetilmez. Böylece create sonrası journal checkpoint hatasında da simüle server state ayrı kalır. Mock dosyaları app-owned test/demo verisidir ve native doğrulama kanıtı değildir.

Yeni `ClientSessionFactory` overload'u review portu ve storage root enjeksiyonu sağlar; eski constructor'lar korunur. `ClientSession.BaselineManager` opsiyonel extension'dır. Bu portu sağlamayan eski/custom factory'de manager disabled/unsupported görünür; mevcut read servisleri çalışır. Eski Mock Workspace/Task 20–22 fixture'ları korunur; coherent store yeni manager runtime'ına özeldir.

Service çağrıları ve dosya I/O mevcut `WorkspaceViewModel.RunToolAsync` worker'ında çalışır; native executor shared STA queue kullanır. View-model satırları ve görünüm projection'ı dispatcher'da güncellenir. Progress immutable snapshots olarak gönderilir; geç/eski generation veya revision callback'leri yeni bağlama uygulanmaz. UI thread'de COM çağrısı yoktur. Window close, cancel + drain akışını korur.

DataGrid row/column virtualization, recycling ve content scrolling açıktır. Tree çocukları lazy command ile yüklenir. Arama/filtre checkbox state'ini visible row container'ında değil selection service'in stabil module ID kaydında tutar. İptal UI'ı ana view IsIdle paneliyle komple disabled edilmez; Cancel Remaining aktif işte erişilebilir kalır.

## Değişen dosyalar

| Katman | Dosyalar |
|---|---|
| Yeni view | `Controls/BulkBaselineManagerView.xaml`, `.xaml.cs` |
| Yeni MVVM | `ViewModels/BulkBaselineManagerViewModel.cs` |
| Yeni session/onay/Mock | `Services/BaselineManagerSession.cs`, `BaselineManagerApproval.cs`, `MockBaselineManagerStore.cs` |
| Composition/lifetime | `ClientSession.cs`, `WorkspaceViewModel.cs`, `App.xaml.cs`, `MainWindow.xaml` |
| Core extension | `BulkBaselinePreflightModels.cs`: exact observed-state/metadata comparison; mevcut sözleşmeler korunur |
| Test | `BulkBaselineManagerWpfTests.cs`; iki eski render testinde ilk 8 sekme korunarak 9. sekme doğrulanır; test projesine framework UIAutomation referansları |
| Docs | Bu belge, README, WPF-CLIENT, VALIDATION |

Yeni NuGet paketi veya production UI automation bağımlılığı yoktur. UIAutomation framework referansları yalnız test içindir. Native DXL scriptleri, Settings/Watchdog tercihi ve ikon değiştirilmedi; eski release ZIP'leri korunur.

## Test ve build

23 yeni test: read-only/default-disabled/declined review; batch execute ve JSON/CSV; filtrelerde seçim/onay koruma; input/type/policy/selection/read-only/refresh invalidation; locks/eligibility; Unicode validation; persistent history/reconcile; Real disabled/source ayrımı; >1000 modülde gerçekleşen satır sayısının <100 olması; zero-binding-error render; active cancellation + same-job resume; multi-folder overlap/include-subfolders; Real iki cevap ve Mock-as-Native rejection; gerçek button automation + typed modal + execute; changed metadata/single-use ticket ve retry disclosure.

Gerçek WPF window/control testleri dispatcher STA üzerinde çalışır; servisler worker'dadır. Mock/native protocol fixture'ları gerçek DOORS compilation veya runtime doğrulaması değildir. Canlı DOORS senaryoları explicit ortam yokken mevcut NOT_RUN olarak kalır. Ekran görüntüleri `artifacts/BulkBaselineManager/{x86,x64}/{approved,completed,main-window}.png`; test TRX'leri `artifacts/TestResults/bulk-baseline-manager-*.trx` içindedir. Makine kaydı `artifacts/BulkBaselineManager/verification.json` içindedir.

Son kaynakla **Release x86 ve x64 build: 0 hata / 0 uyarı**. Her mimaride tam süit **1435 Passed, 15 NOT_RUN, 1450 total, 0 Failed**; process exit 0. Focused x64: 23/23 Passed. Yeni view render testlerinde binding error 0; >1000 aday ile gerçekleşen DataGridRow sayısı <100; typed modal + gerçek ButtonAutomationPeer Invoke senaryosu geçti. x64 görüntüleri görsel olarak incelendi; x86 karşılıkları da tam süitte üretildi. Eski sekmelerin sırası/işlevi korunur.

Repo kökünden Windows + .NET Framework 4.8 Developer Pack ve mevcut SDK 5.0.416 host ile:

```powershell
$env:DOTNET_CLI_HOME = Join-Path $PWD '.cli'
$env:DOORS_TEST_MODE = 'disabled'
Push-Location artifacts/SmokeBuildHost
foreach ($arch in @('x86', 'x64')) {
    dotnet restore ../../DoorsIntegration.sln -p:Platform=$arch --packages ../../.packages --configfile ../../NuGet.Config --ignore-failed-sources
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed' }
    dotnet build ../../DoorsIntegration.sln -c Release -p:Platform=$arch --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    dotnet test ../../tests/DoorsIntegration.Tests/DoorsIntegration.Tests.csproj -c Release -p:Platform=$arch --no-build --no-restore --settings ../../tests.runsettings --logger "trx;LogFileName=bulk-baseline-manager-full-$arch.trx" --results-directory ../TestResults -- RunConfiguration.TargetPlatform=$arch RunConfiguration.TestSessionTimeout=420000
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
}
Pop-Location
```

Focused filter: `FullyQualifiedName~BulkBaselineManagerWpfTests`. Son build/test sayıları [VALIDATION.md](VALIDATION.md) içindedir. Güncel exe her iki mimarinin `src/DoorsIntegration.Wpf/bin/{x86|x64}/Release/net48/DoorsIntegration.Wpf.exe` dosyasıdır. Manuel smoke: yukarıdaki 001/002 akışını iki type için çalıştırın; denied/dirty/locked satırları gözleyin; onayı reddedin; filtreleyip tekrar görün; iptal/resume; restart/history/reconcile; JSON/CSV; Real banner/disabled kontrollerini doğrulayın. Gerçek create testi bu UI smoke'un parçası değildir.
