# Real mode: read-only native observation

**Dağıtım güncellemesi:** Eski WinForms Desktop ve önceki yinelenen ZIP'ler `archive/` altına taşınmıştır. Güncel tek masaüstü dağıtımı `artifacts/releases/current/DoorsIntegration-Wpf-x86.zip` ve `DoorsIntegration-Wpf-x64.zip` dosyalarıdır. Aşağıdaki tarihli teslim sonuçları ve dosya listesi, arşivleme öncesi kaydı anlatır; eski `src/DoorsIntegration.Desktop` kaynakları artık `archive/WinForms/src/DoorsIntegration.Desktop` altındadır.

Real moddaki öncelikli metadata ve endpoint-version boşlukları mevcut servislerin üzerine bağlandı. DOORS verisine yazma yapılmadı. Bu teslim, gerçek DOORS 9.5/9.7 üzerinde çalıştırılmış entegrasyon kabulü değildir.

## Eklenen davranış

| Alan | Yeni davranış | Sınır |
|---|---|---|
| Oturum | Database identifier, kullanıcı, exact client build ve session native DXL'den okunur. | Başka oturuma geçiş veya yanlış release ailesi reddedilir. |
| Independent preflight | Stabil modül kimliği, tür, item read/create/modify izinleri, açık modülün mevcut session edit/dirty durumu ve gözlenebilen lock bilgisi okunur. | ACL/content/lock revision token'ları ve başka session ownership kanıtlanmaz. Ready/write yetkisi üretilmez. |
| Native set metadata | Definition, görünür definition üyeleri, set listesi, Open/Closed, annotation, creator/date ve typed module baseline sürümleri okunur. | Read-filtered iteratörler tüm gizli üyeleri kanıtlamaz; cached definition freshness bilinmez. |
| Geçmiş üyelik | Bugünkü definition içinde bulunmayan, geçmiş set içinde gözlenen modül korunur. | Görünmeyen modül Missing sayılmaz; Unknown/AccessDenied ayrımı korunur. |
| Baseline/current linkler | `sourceVersion(Link/LinkRef)`, `targetVersion(Link)` ve echo kanıtı mevcut link okuyucusuna eklenir. | `targetVersion(LinkRef)` varsayılmaz. Incoming target, gerçekten okunan anchor ModuleVersion'dır. |
| Change impact | Caller tarafından yazılmış environment alanları gerçek session/module/link QUID gözlemiyle karşılaştırılır; capture sonunda bağlam yeniden okunur. | Current capture atomik değildir; değişmiş bağlam Incomplete olur. |
| WPF | Change Impact içinde **Read DOORS Session and Module Identity** komutu environment/repository/module identity alanlarını doldurur. Native set grid geçmiş üyeleri de gösterir; doğrulanmayan yokluk Unknown'dur. | UI thread üzerinde COM çalışmaz. |

Native set üyeliği için `MetadataComplete=false` kalır. Buna rağmen görünür, kimliği doğrulanmış modül baseline'ları ve linkler analiz edilebilir; sonuç tam üyelik doğrulaması gibi Passed sunulmaz. Tek modüllü impact analizinde completeness yalnızca açıkça seçilen modül/link kapsamını anlatır; nesne envanteri ve link completeness ayrıca değerlendirilir.

## Mimari ve güven sınırı

- `INativeReadObservationSource` / `DxlNativeReadObservationSource`: aynı `DxlExecutor`, operation gate ve STA kuyruğunu kullanır. AttachExisting ile bağlanır. Dış DXL dosyası ve bounded file transport kullanır; correlation, schema, row count, identity, release ve query scope kontrol edilir.
- `NativeReadQuery`: canonical path, control-character reddi, query başına 128 modül / 32 link-module sınırı. Set adapter büyük seçimleri 128'lik parçalara böler; session değişirse durur. Bir cevap 10.000 satır / 2.000.000 karakterle sınırlandırılır; fazla kapsam sessizce başarı sayılmaz.
- `NativeReadObservationProtocol`: set ve module sürümlerini ayrı tiplerle modeller. Opaque `versionString` parse edilmez. Revision, request ID veya saatten türetilmez; gözlenen satırların hash'idir, güvenilir DOORS content/ACL revision token'ı olduğu iddia edilmez.
- `DxlBaselinePreflightStateReader`: gözlenen fact'leri mevcut `BulkBaselinePreflightService`'e taşır; bilinmeyen revision/exclusive availability değerleri planı bloklar.
- `DxlReadOnlyBaselineSetAdapter`: yeni metadata okumasını mevcut native set servisine bağlar; `WritesValidated=false`, `ExecuteAsync=Blocked` ve hiçbir native write dispatch'i yoktur.
- `NativeBaselineSetVerificationMetadataProvider`: görünür set üyelerini typed sürümlerle bağlar; link QUID ve modül durumunu ayrıca, aynı session içinde okur. Definition locator'ı folder QUID + name bileşimidir; native immutable definition GUID olarak sunulmaz. Baseline locator'ı module QUID + typed version bileşimidir.
- `IImpactReadContextProvider` / `DxlImpactReadContextProvider`: explicit module scope için gerçek ortam ve kimlik kanıtını sağlar. `ServiceImpactSnapshotProvider` eski constructor çağrılarını koruyarak bu portu opsiyonel kabul eder; Real composition portu her zaman sağlar.
- `DxlReadOnlyLinkEvidenceReader`: Current ve historical endpoint kanıtını mevcut resolver'a taşır; anchor version uyuşmazlığını reddeder. `ObservedReadOnlyLinkEvidenceCompatibility` yalnızca session/release/script bütünlüğü için read-dispatch politikasıdır, runtime validation veya write authorization değildir. Önceki reviewed/write compatibility sınıfları korunur.

Timeout/cancellation COM'u zorla iptal etmez. Parser completion task'i geç gelen dosyanın sahibi olmaya devam eder. Otomatik retry yapılmaz; operation gate gerçek completion/cleanup bitene kadar tutulur. Script failure veya protocol failure Mock veriye geçiş yapmaz.

`native-read-snapshot.dxl` modül açmaz; `read(BaselineSetDefinition)`, save, edit veya lock çağırmaz. `readonly-link-versions.dxl` yalnızca read-only Current/baseline handle açabilir; kullanıcının edit-open modülünü dönüştürmez, dirty handle üzerinden okumayı reddeder ve yalnızca kendi açtığı handle'ı `close(..., false)` ile kapatır. `delete(ModuleVersion/Buffer)` yalnızca kaynak temizliğidir.

## DXL kaynakları ve doğrulama durumu

| API / davranış | Belge incelemesi | Gerçek 9.5 | Gerçek 9.7 |
|---|---|---|---|
| Definition/set/member iterators; typed `baseline(ModuleVersion)`, major/minor/suffix | IBM DXL manual; visibility filtering ve cached-definition davranışı dikkate alındı. | NOT_RUN | NOT_RUN |
| ModuleVersion/data/load/read; sourceVersion, targetVersion, echo | IBM 9.7.2 kaynağı ve IBM tarafından yazılmış 9.6.1 referansı incelendi. | Exact 9.5 manual/runtime acceptance bekliyor | NOT_RUN |
| isRead/isEdit/isShare, unsaved, isLocked, everSectioned | Mevcut session gözlemi ile konfigürasyon riski ayrı tutuldu. | NOT_RUN | NOT_RUN |

IBM kaynakları: [9.7.2 DXL Reference Manual](https://www.ibm.com/docs/en/SSYQBZ_9.7.2/com.ibm.doors.requirements.doc/topics/dxl_reference_manual.pdf), [IBM 9.6.1 DXL manual, Jazz üzerinde](https://jazz.net/dxl/resource/dxl_reference_manual.pdf). 9.6.1 belgesi 9.5 belgesi olarak gösterilmez. C# build veya mocked transport testi native DXL compile/runtime kanıtı değildir.

`read(BaselineSetDefinition)` cached unsaved definition değişikliklerini kaybettirebildiği için read-only sorguda kullanılmaz. `everSectioned` pozitif sonucu shareable riskini gösterir; negatif sonucu tüm konfigürasyonun güvenli olduğu anlamına dönüştürülmez. Baseline argümanı üzerindeki false sonucu bu amaçla kullanılmaz.

IBM'in [baseline set / edit shareable link-loss uyarısı](https://www.ibm.com/support/pages/ibm-doors-9xreproducible-scenario-where-baseline-set-links-are-lost) dikkate alınır. [IBM fix list](https://www.ibm.com/support/pages/node/611407) PH49714 düzeltmesini 9.7.2.8 altında listeler; bu bilgi bütün 9.x build'lerinin veya kurulumun doğrulandığı anlamına gelmez. Bu teslimde set oluşturma, link onarma veya otomatik write açılmaz.

## Yerel build ve test

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Test-NativeReadOnlyAcceptance.ps1 -Architecture x64 -FullSuite
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Test-NativeReadOnlyAcceptance.ps1 -Architecture x86 -FullSuite
```

ExecutionPolicy yalnızca çalıştırılan PowerShell process'i içindir; sistem ayarı değiştirilmez. Runner gerçek DOORS testlerini `disabled` tutar, yeni kanıt dizini açar ve build/test log + TRX + PASS/FAIL/NOT_RUN JSON/CSV üretir. `LocalVerification=PASS`, native runtime kabulü anlamına gelmez. Son teslim sonuçları `artifacts/NativeReadOnly/delivery-x64` ve `delivery-x86` altındadır.

Yeni testler: protocol/identity/Unicode/typed version, geçmiş set üyeliği, erişim ve session değişimi, 1000 modül için bounded chunking, fail-closed write, gerçek executor üzerinde mocked STA/file cleanup, geç cancellation ve no-retry, Current endpoint/yanlış anchor, script bütünlüğü ve WPF context binding. Mock/port testleri DOORS COM/DXL doğrulaması olarak raporlanmaz.

Teslim sonucu (30 Ekim 2026, yerel saat):

| Release | Build | Tam paket PASS / FAIL / NOT_RUN | Yeni feature PASS / NOT_RUN |
|---|---|---|---|
| x64 net48 | PASS, 0 warning / 0 error | 1642 / 0 / 45 | 33 / 8 |
| x86 net48 | PASS, 0 warning / 0 error | 1642 / 0 / 45 | 33 / 8 |

TRX tam paket süreleri x64 5 dakika 29 saniye, x86 5 dakika 37 saniyedir. İki mimari koşusu ayrı process'lerde yürütülmüştür; süreler native DOORS performans ölçümü değildir. Yeni 8 NOT_RUN vaka, her release ailesi için Session/ModuleState/Definitions/CurrentLinks'tir. Toplam 45 NOT_RUN eski gerçek-ortam testlerini de içerir. Settings/watchdog/ikon için önceki 4 hash korunmuştur.

Güncel masaüstü paketleri `artifacts/releases/2026-10-30-real-readonly/` altındadır. WPF EXE ve iki yeni DXL dosyası birlikte dağıtılmalıdır; eski ZIP dosyaları bu geliştirmeyi içermez. Değişen 31 dosyanın listesi `artifacts/NativeReadOnly/source-files.txt` içindedir.

## Gerçek ortamda read-only kabul

DOORS'ta oturum açın; WPF Real + doğru client family seçin. Canonical module ve link-module path'lerini yazın. **Read DOORS Session and Module Identity** ile kimlikleri gözleyin; sonra baseline listesini ve Source → Target seçimini yükleyin. Native Set ekranında ilgili folder'ın görünür definition/set/member listesini okuyun. Eksik görünür üyeliği Unknown, açık seti Open ve geçmiş module version'ı set version'ından bağımsız kontrol edin.

Otomatik probe opt-in örneği (repo `artifacts/SmokeBuildHost` dizininde):

```powershell
$env:DOORS_TEST_MODE='real-readonly'
$env:DOORS_NATIVE_READ_VERSION='9.7' # Diğer koşu için 9.5
$env:DOORS_NATIVE_READ_MODULE='/TestProject/FormalModule'
$env:DOORS_NATIVE_READ_FOLDER='/TestProject'
$env:DOORS_NATIVE_READ_LINK_MODULE='/TestProject/Links'
dotnet test ../../tests/DoorsIntegration.Tests/DoorsIntegration.Tests.csproj -c Release -p:Platform=x64 --no-build --no-restore --filter TestCategory=NativeReadOnlyReal -- RunConfiguration.TargetPlatform=x64
```

Session, ModuleState, Definitions ve CurrentLinks her client family için ayrı testtir. Doğru bitness ile derlenmiş çıktıyı kullanın. Diğer family koşusu NOT_RUN olur. Definitions senaryosu mevcut definition içeren bir folder bekler. CurrentLinks sonucunu DOORS UI ile karşılaştırın; boş scope testi fiziksel linklerin semantiğini doğrulamaz. Historical links, echo, edit-open/dirty handle, access-denied, client disconnect ve büyük modül senaryolarını önceki Task 29/30 acceptance listeleriyle ayrıca doğrulayın. Test bitince `$env:DOORS_TEST_MODE='disabled'` yapın.

Gerçek baseline/set yazmasını açmadan önce kalanlar: exact 9.5/9.7 API/runtime kabulü, güvenilir content/permission/lock revision sağlayıcısı, başka oturum lock/dirty ownership kanıtı, authoritative next-version/collision davranışı, cached definition state doğrulaması ve build'e özgü link davranışı. Yerel reviewer/audit depoları ve mevcut bütün explicit confirmation kapıları korunur.

## Değişen dosyalar

Yeni: `Core/NativeReadModels.cs`; `Dxl/DxlNativeReadObservationSource.cs`, `NativeReadServices.cs`, `DxlReadOnlyLinkEvidenceReader.cs`; `scripts/dxl/native-read-snapshot.dxl`, `readonly-link-versions.dxl`; `tests/NativeReadOnlyTests.cs`, `NativeReadDispatchTests.cs`, `NativeReadOnlyWpfTests.cs`, `NativeReadOnlyIntegrationTests.cs`; acceptance runner ve bu belge.

Güncellenen: Core native-set/verification DTO'ları, native-set read-list kapısı, snapshot selection ve impact provider; mevcut DXL endpoint reader'ın read-only-policy yolu; Console/Desktop DI; WPF ClientSession, BaselineManagerSession, NativeBaselineSetSession, BaselineVerificationSession, ChangeImpactSession; ChangeImpact/NativeBaselineSet view-model ve XAML; native preflight composition testi; README. Independent baseline ve Mock iş akışları yeniden yazılmadı. Watchdog Settings ve uygulama ikonu değiştirilmedi.
