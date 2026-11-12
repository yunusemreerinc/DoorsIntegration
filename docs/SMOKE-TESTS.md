# DOORS Classic read-only smoke suite / diagnostic CLI

`IDoorsSmokeSuite` / `DoorsSmokeSuite` ve **`doors smoke`**, Classic 9.5/9.7 COM/DXL yollarını ayrı test kayıtları olarak raporlar. Varsayılan **offline**, veritabanı açısından **salt okunur**, bağlantı **AttachExisting** ve native test başına **tek deneme**dir. Gerçek ortam yoksa mock başarı üretilmez: `NOT_RUN` kaydedilir. Önceki `diagnose`, `ping` ve dry-run komutlarının davranışı korunmuştur; yeni deadline/rapor sözleşmesi `smoke` komutuna aittir.

## Kullanım

Console build klasöründe `doors.cmd` EXE'ye yönlendirir. Paket ZIP'ini tamamen çıkartın; DLL/config/script dosyalarını birlikte tutun.

```powershell
# Registry/process gözlemi; COM bağlantısı veya DXL dispatch yok.
.\doors.cmd smoke --target 9.7 --report C:\Reports\doors-offline-97

# Mevcut oturuma bağlanarak sabit read-only testler.
# Önceden var olan, onaylı test modülü ve link module yolu kullanın.
.\doors.cmd smoke --live --target 9.7 --root /TestProject `
  --module "/TestProject/Requirements" --link-module "/TestProject/Trace Links" `
  --timeout-seconds 30 --max-items 1000 --max-links 1000 `
  --report C:\Reports\doors-live-97

.\doors.cmd smoke --help
```

9.5 için `--target 9.5` ve o kurulumla uyumlu runner paketini kullanın. x86/x64 EXE'nin mimarisi ayrı koşudur; profil seçimi process bitness'ini değiştirmez. `--report` verilmezse `%LOCALAPPDATA%\DoorsIntegration\SmokeReports\<UTC-time-guid>` kullanılır. Her rapor **yeni bir klasöre** yayımlanır; mevcut raporun üzerine yazılmaz. Modül ve link module yolları birlikte verilir. Verilmezse yalnız `LinkRead` testi `NOT_RUN / LinkTestPathsNotConfigured` olur; tam suite başarısı sayılmaz.

`--timeout-seconds` 1–1800, `--max-items` 1–10000, `--max-links` 1–100000 aralığındadır. Timeout native test başına bekleme deadline'ıdır; bütün suite veya cleanup için zorla durdurma süresi değildir. `--live` açıkça verilmeden hiçbir native probe başlatılmaz. `--retry`, `--retry-count`, `--script`, `--write` ve `--connection activate` geçersizdir. İstemciyi kullanıcı başlatıp oturum açmalıdır; suite aktivasyona veya otomatik login'e geçmez.

## Testler ve kanıtları

| Test | Doğrulanan kapsam |
| --- | --- |
| ComRegistrationX86 / ComRegistrationX64 | İki registry görünümünde ProgID/CLSID/server binary gözlemi; gerçek inceleme süresi |
| ProgId | Current registry view'da `DOORS.Application` → geçerli CLSID eşlemesi; registry üzerinden kontrol, aktivasyon yok |
| ClientProcessSession | Bu Windows session'ında `doors.exe` sayısı, PID + start UTC + session + executable/version/PE bilgisi |
| ArchitectureCompatibility | Runner bitness, current-view kaydı, kayıtlı/gözlenen binary sürümü ve PE kanıtı; canlı uyumluluk ayrıca sınanır |
| ComSession | Mevcut ROT nesnesine bağlanma ve aynı owned STA üzerinde referans cleanup |
| RunStr | Sabit salt okunur `runStr` script'i + fresh request nonce / Result doğrulaması |
| RunFile | Owned geçici dosyaya UTF-8 script, tam yol ile `runFile`, fresh nonce doğrulaması ve dosya cleanup |
| Result | `Result` setter/getter transient marker round-trip + DXL `oleSetResult` sonucunun fresh nonce ile okunması |
| ModuleListing | Mevcut read-only ModuleService/hierarchy reader ile seçili kökte formal modül listesi |
| LinkRead | Mevcut LinkReader ile tam modül/link module kapsamında Both yönünde read-only okuma ve tam dosya enumeration |

Default backend mevcut `IDxlSessionFactory` / `IDxlExecutionSession`, `IDxlProbeProtocol`, `IDxlWorkspaceFactory`, hierarchy ve link reader portlarını kullanır. Yeni veya tahmini bir DOORS COM üyesi eklenmemiştir. COM adları mevcut `DOORS.Application`, `runStr`, `runFile`, `Result` sözleşmesinden; fixed probe mevcut `ReadOnlyProbeProtocol` / `oleSetResult` yolundan gelir. API gerekçeleri: [DXL executor](DXL-EXECUTOR.md), [gereksinim okuma](REQUIREMENTS.md), [link okuma](LINKS.md).

`Result` COM automation alanı ve private script/log dosyaları değişebilir; bunlar DOORS veritabanı yazması değildir. Test kodunda edit/share/save/create/delete/baseline/ACL mutation yoktur. Modül/link okuması mevcut bounded read-only scriptleri kullanır. Ortamdaki üçüncü taraf module-open trigger'ları veya dış eklentiler bu sözleşmeyle sandbox edilmez; canlı kabul için onaylı test verisi ve uygun salt okunur hesap kullanın.

## Durum / exit code

Her test `PASS`, `FAIL`, `NOT_RUN`, `TIMEOUT` veya `CANCELLED` döndürür. `NOT_RUN` nedenleri arasında live opt-in olmaması, gerçek COM kaydı/istemci bulunmaması, process/session/version gözleminin tamamlanamaması, erişilebilir ROT olmaması, eksik link test yolları ve önceki native testin belirsiz durumu bulunur. Sıfır kayıt içeren fakat başarıyla okunmuş bir kapsam, ortam yokluğuyla aynı şey değildir.

- Birden çok aynı-session client ve bilinen hedef sürüm uyuşmazlığı `FAIL` üretir; native testler başlatılmaz.
- Bilinmeyen binary sürümü/mimarisi başarı varsayılmaz; eligibility `NOT_RUN` olur.
- `ModuleListing` kesilme/erişim/issue durumunda structured scope kanıtıyla `FAIL` olur. Başarılı boş liste geçerli bir read sonucudur.
- Link module/formal module okunamaması, enumeration/cleanup hatası veya truncation `FAIL` olur. Tamamlanan transport okumasındaki unresolved endpoint/incoming reference/izin görünürlüğü kaybı kanıtta ve `LINK_VISIBILITY / PARTIAL` logunda korunur; `PASS`, tam coverage anlamına gelmez. `isCompleteForReadableScope` ve sayaçları ayrıca değerlendirin.
- Runner'ın kullanmadığı registry view kaydı **advisory**dir (`required=false`); eksik olması tek başına çalışan runner'ın bütün sonucunu düşürmez. Current view ve bütün native testler required'dır. Her iki view yine ayrı raporlanır. Ortam incelemesinin bütünü bozulursa altyapı hatası required olarak kalır.

| Çıkış | Anlam |
| --- | --- |
| 0 | Bütün required testler PASS |
| 1 | Required test hatası |
| 2 | Geçersiz/yinelenen/yasak seçenek veya mevcut rapor hedefi |
| 3 | En az bir required NOT_RUN; gerçek ortam/opt-in/test verisi eksik olabilir |
| 4 | Native bekleme deadline'ı aşıldı; orijinal TIMEOUT korunur |
| 53 | Rapor/suite altyapısı yayımlanamadı veya yapılandırılamadı |
| 130 | İptal |

Timeout/cancellation, normal FAIL ve NOT_RUN'dan önce aggregate edilir. Hata/test metadata'sında mevcut Core error code, exception type ve alttaki HResult korunur; raw exception mesajı, requirements veya raw COM Result otomatik loglanmaz. `SmokeProbeFailure` scope reason/evidence taşır. Beklenmeyen host hataları mevcut merkezi handler'dan geçer.

## Raporlar

```text
<new-directory>/
  report.json
  logs/
    ComRegistrationX86.jsonl
    ComRegistrationX64.jsonl
    ProgId.jsonl
    ClientProcessSession.jsonl
    ArchitectureCompatibility.jsonl
    ComSession.jsonl
    RunStr.jsonl
    RunFile.jsonl
    Result.jsonl
    ModuleListing.jsonl
    LinkRead.jsonl
  settlement.json
```

`report.json` schemaVersion 1 içerir: run/case correlation ID, UTC ISO 8601 zamanlar, monotonic Stopwatch süreleri, hedef profil, salt okunur/AttachExisting/tek-deneme/no-retry politikası, status/exit code, session uncertainty, seçilen kapsam ve her testin environment/evidence/logFile bilgisi. Environment her testte OS/runner mimarisi, CLR/target framework/integration assembly sürümü, Windows session, iki kayıt görünümü ve gözlenen client metadata'sını taşır. Registry/process inceleme süreleri gerçek gözlem başlangıçlarından hesaplanır; diğer aşamalar ayrı Stopwatch ile ölçülür. `attempts`, native probe denemesini sayar: offline metadata gözleminde sıfır, native bir testte en fazla birdir. Native API çağrıları kendi phase loglarıyla ayrıca görünür.

Per-test JSONL logu başlangıç/sonuç ve native probe'da connect, Result set/get, runStr/runFile, nonce kontrolü, COM/local-file cleanup aşamalarını ve sürelerini taşır. stdout ilerlemeyi, stderr FAIL/TIMEOUT/CANCELLED ve belirsizlik açıklamasını verir. Observer/log sink hatası redispatch sebebi olamaz. Tam rapor private staging'den yeni hedef klasöre taşınır; yarım rapor başarı olarak yayımlanmaz.

Kayıtlı dosya sürümü ve `doors.exe` binary sürümü **gözlem kanıtıdır**; çalışan COM nesnesinin undocumented bir Version/ProcessId API'si varmış gibi davranılmaz. PID gözlemi bağlanılan ROT nesnesinin sahiplik kanıtı değildir. Birden çok client bu yüzden native aşamayı engeller. Registry view, server binary bitness'i değildir; x86 runner/x64 client veya tersi için davranış tahmin edilmez. Fresh nonce içermeyen, eski/stale veya başka isteğe ait Result reddedilir.

## Timeout, iptal ve retry sınırı

Core suite yalnız `IdempotentReadOnly` beyan eden smoke probe adaptörünü kabul eder. Unknown/non-idempotent adaptör **Start çağrılmadan** reddedilir. Native test başına `MaximumAttempts=1`, `AutomaticRetryAllowed=false` sabittir; CLI bunları açamaz. Default adaptör keyfi script veya mutation planı almaz. Bu kurallar suite sınırına aittir; harici plugin uygulaması bir güven sınırıdır ve kendi davranışını doğru beyan etmelidir.

Deadline dolduğunda veya Ctrl+C geldiğinde orijinal COM çağrısı Abort/Release/Quit ile zorla kesilmez. Stop flag yalnız sonraki güvenli dispatch noktalarını durdurur. Orijinal worker aynı STA'da COM/file cleanup'ını yapana kadar shared native gate tutulur. Kalan native testler `NOT_RUN` olur; otomatik retry, yeniden attach, client restart veya alternatif yazma denemesi yoktur. Aynı `runStr` API'si Result testinde ayrı nonce ile kullanılabilir; bu farklı case ID'li sabit testtir, başarısız bir write script'inin yeniden gönderimi değildir.

CLI **önce initial `report.json` ve mevcut logları yayımlar**, sonra orijinal completion/cleanup'ı bekler. Bu nedenle native çağrı hiç dönmezse CLI process'i de bekleyebilir; ilk TIMEOUT/CANCELLED raporu yine disktedir. İptal sonrası report yayınlanması ve settlement beklemesi cancellation token ile kesilmez. Initial rapor yazılamazsa da orijinal worker gözlemi tamamlanmadan CLI normal biçimde çıkmaz. Başka uygulamalarla paylaşılan gate bu process kapsamındadır; distributed/exactly-once koruma olarak sunulmaz.

Orijinal probe sonlandığında `settlement.json` eklenir. Initial deadline sonucu değiştirilmez; geç başarı TIMEOUT'u PASS yapmaz. `OriginalProbeFaultObserved` bir probe/wait fault'unu ifade edebilir. Reader timeout köprüsünde **ORIGINAL_NATIVE_COMPLETION PASS/FAIL** logu, gerçek native worker'ın geç sonucunu wait exception'dan ayırır. Late exception içeriği loglanmaz; type/HResult ve phase sonucu korunur. Cleanup'ın gözlenmesi bütün client'ın sağlıklı/oturumun yeniden kullanılabilir olduğuna garanti değildir. Belirsiz veya cleanup-failed rapordan sonra yeniden deneme kararı kullanıcıya aittir; önce eski çağrı/client durumu incelenmelidir. Entegrasyon process'i zorla sonlandırılırsa process-local gate ve completion gözlemi yaşayamaz; initial belirsizlik raporunu esas alın.

## Build / doğrulama

Mevcut dokuz projeli net48 çözümü ve cache'lenmiş bağımlılıklar kullanılır; yeni NuGet paketi eklenmemiştir. Bu çalışmada **SDK 5.0.416 / MSBuild 16.11** ile doğrulandı. Varsayılan SDK 10.0.402 restore'u çevrimdışı NuGet audit sorgusunda NU1900 üretti; proje audit ayarları kapatılmadı veya paket sürümleri değiştirilmedi. Eski SDK seçimi yalnız excluded `artifacts/SmokeBuildHost/global.json` dosyasında yapıldı. SDK seçimi gereken benzer bir makinede repo kökünden:

```powershell
$repo = (Get-Location).Path
$buildHost = Join-Path $repo 'artifacts\SmokeBuildHost'
New-Item -ItemType Directory -Path $buildHost -Force | Out-Null
[System.IO.File]::WriteAllText((Join-Path $buildHost 'global.json'),
  '{"sdk":{"version":"5.0.416","rollForward":"disable"}}', [System.Text.UTF8Encoding]::new($false))
$env:DOTNET_CLI_HOME = Join-Path $repo '.cli'
$env:DOORS_TEST_MODE = 'disabled'
Push-Location -LiteralPath $buildHost
try {
  dotnet restore ../../DoorsIntegration.sln -p:Platform=x86 --packages ../../.packages --configfile ../../NuGet.Config --ignore-failed-sources
  dotnet build ../../DoorsIntegration.sln -c Release -p:Platform=x86 --no-restore
  dotnet test ../../tests/DoorsIntegration.Tests/DoorsIntegration.Tests.csproj -c Release -p:Platform=x86 --no-build --no-restore --settings ../../tests.runsettings -- RunConfiguration.TargetPlatform=x86
} finally { Pop-Location }
```

x64 için restore/build/test ve TargetPlatform değerlerini birlikte değiştirin. SDK 5'in kurulu olması gerekir (`dotnet --list-sdks`); bu SDK seçimi runtime gereksinimini net48'den değiştirmez. Güncel farklı SDK ile build/audit doğrulaması ayrı bir ortam kontrolüdür.

Yeni managed testler fake ortam/COM/read portlarıyla 9.5/9.7 profil aktarımı, 11 case, NOT_RUN nedenleri, ambiguous/unknown/mismatch, semantic guard, tek deneme, üç ayrı COM blok noktasında stop ve gate/STA cleanup, stale Result, UTF-8 runFile, modül/link scope, partial link evidence, fresh nonce, orijinal DXL completion'ın geç başarı/hatası, timeout/cancellation, immutable initial rapor + settlement, merkezi CLI, yasak/tekrarlı seçenekler ve full DI çözümlemesini doğrular. Bunlar native DOORS testlerinin geçtiği anlamına gelmez.

Manuel kabul için 9.5 / 9.7 × desteklenen x86 / x64 matrisinde mevcut test verilerini kullanın:

1. Offline raporu üretin; registry view, file metadata, PID/start/session ve hedef profil kanıtlarını inceleyin.
2. Tek client'ı kullanıcı açıp login olduktan sonra `--live` + onaylı scope ile suite'i çalıştırın. Her case'de ayrı log, süre ve fresh nonce doğrulaması bekleyin.
3. Kayıtsız runner görünümü, no-client, multiple-client, ROT bulunamaması, yetki/yanlış sürüm ve eksik link yollarında native başlangıcın veya ilgili testin NOT_RUN/FAIL gerekçesini doğrulayın.
4. Modül/link sayımlarını UI ile karşılaştırın; unreadable/unresolved/partial kapsamı tam coverage diye yorumlamayın. Veri/baseline/ACL/link oluşturmayın veya değiştirmeyin.
5. Normal uzun süren bir read sırasında deadline/Ctrl+C uygulayın. Initial rapor önce çıkmalı, kalan native case'ler başlamamalı, orijinal çağrı bitene kadar gate/STA yaşamı korunmalı ve settlement ayrı yazılmalı. Sonsuz döngü veya veri yazan DXL ile test üretmeyin.

Bu makinede gerçek DOORS kaydı/istemcisi bulunmadı; üretim CLI'nın offline 9.5/9.7 raporları `NOT_RUN`, native attempts=0 olarak üretildi. Gerçek DOORS'a bağlanılmadı ve veritabanı değiştirilmedi. Native 9.5/9.7 interpreter/client uyumluluğu manuel kabul bekler.
