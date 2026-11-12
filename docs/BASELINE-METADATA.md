# Task 21 — Baseline metadata discovery

`BaselineMetadataService` / `IBaselineMetadataService` formal modül baseline geçmişini salt okunur DTO'lar olarak döndürür. Snapshot, next-version proposal ve hedef çakışması ayrı sonuçlardır. DOORS üzerinde baseline oluşturma eklenmemiştir; `CanCreateBaseline` ve `AuthorizesCreation` her zaman false olur.

**Native sınırlar:** 9.7 geçmiş okuması için belgelenmiş perm'ler üzerinden ayrı DXL scripti vardır; gerçek istemcide compilation/runtime kabulü **NOT_RUN** durumundadır. 9.5'e ait sürüme özgü API incelemesi bu ortamda tamamlanamadığından 9.5 sorgusu COM başlatmadan **ApiNotValidated** döner. Native `nextMajor`/`nextMinor` declaration'ları bulunmuştur; symbolic handle'ın sayısal sürüme read-only çözülmesi ve current-module davranışı hedef istemcilerde doğrulanmadığından gerçek proposal **RequiresEnvironmentValidation** döner ve ExpectedVersion null kalır. Tahmini arithmetic veya varsayımsal perm kullanılmaz. Mock çıktıları test fixture'ıdır; native uyumluluk kanıtı değildir.

## Sözleşmeler ve DTO'lar

| Tip | Görevi |
| --- | --- |
| `IBaselineMetadataService` | Bir seçili formal modül için history/proposal/collision orkestrasyonu |
| `IBaselineHistoryAdapter` | Salt okunur geçmiş ve bağımsız fresh baselineExists hedef sorgusu |
| `IBaselineNextVersionAdapter` | Exact snapshot/module/type/suffix bağlamında nextMajor/nextMinor sonucu; doğrulanamıyorsa açık durum |
| `BaselineHistoryRequest` | Stabil modül reference, open policy, seçili Major/Minor, suffix, isteğe bağlı collision target, sınırlar, timeout, target/connection |
| `BaselineMetadataSnapshot` | Status, read-only history, LatestExisting, LatestNumberingVersion, completeness, ownership/cleanup, UTC observation ve source/evidence |
| `BaselineHistoryEntry` | Major, Minor, Suffix, Annotation, Creator, CreationDateUtc/Raw, Identity ve existence evidence |
| `BaselineIdentity` | Module + version logical address; okunmuşsa optional NativeIdentity/native baseline index |
| `BaselineVersionProposal` | Beklenen sürüm, tür, suffix, kaynak/evidence ve exact module/ObservationId binding |
| `BaselineCollisionResult` | Exact target ve Exists / AbsentOrUnreadable / Unknown / NotRequested |
| `BaselineMetadataResult` | Snapshot, Proposal, Collision; ExpectedVersion ve CanCreateBaseline=false |

WPF get-only property'lere doğrudan bind edebilir. History koleksiyonu ve referanslar immutable/read-only'dir. `ClientSession.BaselineMetadata` Mock/Real composition içinde bulunur. Önceki 9/10/11 parametreli session constructor'ları korunmuş, yeni 12 parametreli injection overload'u eklenmiştir. Bu görev yeni UI sekmesi eklemez; Task 20 modül seçim sekmesinden alınan stabil referanslar bu servise geçirilebilir.

## Latest existing, numaralandırma ve silinmiş baseline

Native adapter iki ayrı sorgu yapar:

- `getMostRecentBaseline(module,false)`: son mevcut baseline. Varlığı ayrıca doğrulanır; tutarsız/okunamayan sonuç Unknown kalır.
- `getMostRecentBaseline(module,true)`: silinmiş olabilen son kaydedilmiş sürüm. DTO'da LatestNumberingVersion olarak korunur; History'deki görünür maximum veya son satırdan türetilmez.

`baselineExists` negatif sonucu ile silinme ve erişim kısıtı güvenilir biçimde ayrıştırılamaz. Bu nedenle DTO **AbsentOrUnreadable** ve `MayBeDeletedOrInaccessible` taşır; DeletedConfirmed iddiası yoktur. Silinmiş baseline'ın annotation/creator/date bilgileri mevcut değilse uydurulmaz. Bu designation, native nextMajor/nextMinor yerine manuel arithmetic yapmak için kullanılmaz.

İlk baseline yalnız **başarılı + complete** history ve her iki latest sorgusunda None varsa `HasNoBaselineHistory=true` ile belirlenir. History boş olsa bile LatestNumberingVersion varsa numaralandırma sıfırlanmış sayılmaz. Partial/Unknown sonuç da ilk-baseline kanıtı değildir.

Baseline identity'nin `Key` alanı repository + qualified stable module ID + major/minor/suffix'in uzunluk kodlu adresidir. Path veya Object Number eşleştirme anahtarı değildir. Bu bir native GUID ya da delete/restore sonrasında aynı incarnation garantisi değildir. `NativeIdentity`, sadece ilgili baseline zaten read-only yüklüyse `baselineIndex` gözleminden gelir; `NativeIdentityAvailable` false ise native ID yoktur. Sırf indeks elde etmek için bütün baseline'lar yüklenmez; borrowed baseline handle'ları kapatılmaz. Native indeksin bağlamı ilgili modüldür.

## Native read-only akış

`DxlBaselineHistoryAdapter`, harici `scripts/dxl/baseline-metadata.dxl` dosyasını mevcut DxlExecutor üzerinden çalıştırır. Exact canonical path ve native qualified module identity doğrulanır. Sonra current ModuleVersion için:

1. Modül kapalıysa varsayılan `OpenIfClosed=false` nedeniyle **ClosedByPolicy** döner. İstenirse `OpenIfClosed=true` ile read-only yükleme yapılabilir.
2. Önceden açık read-only handle ödünç alınır. Edit/share açık handle read-only'ye çevrilmez; **AlreadyOpenForWrite** raporlanır. Save, downgrade, edit/share veya ACL değişikliği yapılmaz.
3. History alanları `for Baseline in Module` içinde okunur. Major/minor/suffix ayrı saklanır; display versionString parse edilmez. Baseline history okumak için her baseline'ın içeriği yüklenmez.
4. Tarih `intOf(Date)` epoch saniyesinden UTC'ye çevrilir; `stringOf(Date)` çıktısı ayrıca Raw olarak korunur. DXL'in dönüştüremediği `-1` değeri null UTC olur; yerine bugün/gece yarısı ya da locale'den tahmini UTC yazılmaz. Legacy intOf tarih aralığı gerçek ortam kontrol listesinde yer alır.
5. Yalnız kendisinin açtığı current read-only module kapatılır. Borrowed current/baseline handle'ları açık kalır. Cleanup başarısızsa başarılı metadata veya güvenli yaratma sonucu üretilmez.

Diğer durumlar AccessDenied, DeniedOrNotFound, NotFormalModule, ModuleDeleted, IdentityMismatch, ReadFailed, ApiNotValidated'dır. Native lookup boşluğu tek başına modülün yok olduğunun kanıtı olarak sunulmaz.

Geçici UTF-8 file transfer kullanılır. Varsayılan maxBaselines=10000, maxFileBytes=16 MiB; üst sınırlar 100000 / 256 MiB'dir. DXL row/character sınırında partial flag üretir; executor dosya byte sınırını da uygular. Managed parser satır satır çalışır, dosyanın tamamını string'e yüklemez; bounded DTO history'si bellektedir. Annotation 16384, creator/raw date/native index 4096 karakterle sınırlanır. Correlation ID, exact path/native ID, row/footer count, enum/escape/version, latest/history ve ownership/cleanup çelişkileri doğrulanır. LF/CRLF ve strict UTF-8 desteklenir. Corrupt/eksik dosya başarılı snapshot gibi yayınlanmaz.

## nextMajor / nextMinor adapter sınırı

Service, tür ve suffix'i `IBaselineNextVersionAdapter.ProposeAsync` portuna geçirir. Dönen proposal aynı Module ve ObservationId'ye bağlı olmalıdır; foreign context reddedilir. Proposal Available ise ve explicit CollisionTarget yoksa target için fresh `CheckCollisionAsync` yapılır. Explicit CollisionTarget verilirse sonuç o hedefe aittir; DTO target'ı açıkça taşır. Sorgu bir numara ayırmaz ve başka kullanıcıların sonradan yaptığı değişiklikleri engellemez.

Native adapter'da `nextMajor(module)` / `nextMinor(module)` gibi doğrulanmamış overload yoktur. Reference'da optional suffix'li Baseline handle declaration'ları vardır; bunların target module/current context bağlamında çözülmesi ve first/deleted/suffix durumları gerçek istemcide kabul edilmeden koddan çalıştırılmaz. Native fallback olarak `LatestExisting.Major+1` veya minor arithmetic yapılmaz.

`MockBaselineModuleState`, açıkça yapılandırılmış NextMajor / NextMinor çıktılarını alır. Böylece senaryolar LatestExisting'den bağımsız test edilebilir. Örnek fixture: existing 2.4, recorded 3.7(removed), next major 4.0 / minor 3.8; ilk-baseline fixture major 1.0 / minor 0.1. Bunlar **simüle edilen çıktılardır**, 9.5/9.7'nin doğrulanmış algoritması olarak ilan edilmez. Suffix, talep edilen proposal'a ayrı uygulanır.

Task 19'un eski `BaselinePlanner` arithmetic'i legacy draft plan davranışı olarak korunmuştur; native sonucu garanti etmez ve bu yeni servisin ExpectedVersion'ı onun üzerinden hesaplanmaz. Task 19 `IBaselineMetadataProvider` ve read-only `IBaselineExecutor` değiştirilmemiştir. Gelecekte gerçek baseline writer/preflight, doğrulanmış native proposal ve fresh collision/permission/lock bağlamını ayrıca kullanmalıdır. Şimdiki DTO'lar hiçbir write yetkisi vermez.

## Ortak COM STA kuyruğu

Üretim Console/Desktop DI ve Real WPF executor'ı `DoorsStaExecutionQueue.Shared` kullanır. Kuyruk tek process-lifetime background STA thread'idir, FIFO ve bounded kapasite 1024'tür. DxlExecutor'ın yeni overload'u worker action'ını buraya gönderir; COM open/call/cleanup aynı thread'de kalır. Mevcut formal module/requirement/link write gateway'leri aynı singleton/session executor'ını kullanır. Metadata adapter ayrı COM thread veya ayrı creation executor açmaz. Gerçek baseline creation henüz yoktur; gelecekteki gateway de aynı executor/queue dependency'sini kullanmalıdır.

Mevcut `DoorsOperationGate` fail-fast busy/drain davranışı korunur: aynı gate'teki ikinci işlem otomatik sıraya alınıp sessizce başlatılmaz. Kuyruk, farklı producer/executor'ların STA affinity/serialization'ını sağlar. Soft timeout veya cancellation başlamış COM çağrısını öldürmez; gate orijinal çağrı ve cleanup bitene kadar tutulur. Kuyrukta henüz başlamayan işlem iptal edilmişse native dispatch öncesindeki mevcut kontrol noktasında durur. Late file result cleanup gözlemlenir. Otomatik retry yoktur.

Eski DxlExecutor constructor'ları kaynak/binary uyumluluğu için korunur ve izole eski worker davranışını sürdürür; özel host'lar üretimle aynı güvence için yeni queue overload'unu kullanmalıdır. Process-lifetime Shared kuyruk DI scope dispose ile kapatılmaz; idle thread background'dır. Sabit diagnostics/smoke probe'ları mevcut shared gate mekanizmasını korur; bütün diagnostics çağrıları için yeni queue-affinity iddiası yapılmaz.

## Kullanım

```csharp
// selected.Module Task 20'den gelen stabil reference'dır.
var request = new BaselineHistoryRequest(selected.Module,
    openIfClosed: true, nextType: BaselineType.Minor, suffix: "SRR",
    target: DoorsVersion.V97);
var result = await session.BaselineMetadata.ReadAsync(request, token);
var historyRows = result.Snapshot.History; // WPF ItemsSource
var latest = result.Snapshot.LatestExisting;
var numbering = result.Snapshot.LatestNumberingVersion;
var expected = result.ExpectedVersion; // native: null while resolution is unvalidated
var status = result.Proposal?.Status;

// Exact, kullanıcı tarafından bilinen hedef için read-only collision sorgusu:
var check = await service.ReadAsync(new BaselineHistoryRequest(selected.Module,
    openIfClosed: true, collisionTarget: new BaselineVersion(3, 0, "SRR")), token);
```

Birden fazla modül için seçilen referansları sırayla işleyin; her DTO'yu yayınlayıp cancellation kontrol edin. Tek dev history listesi oluşturan zorunlu bulk API veya otomatik parallel COM fan-out eklenmedi. İstisna logları correlation/observation ID, durum, sayı ve exception type içerir; annotation/creator/native error text loglanmaz.

## API kanıtı ve gerçek ortam kontrol listesi

Birincil kaynaklar: [IBM 9.7.2 DXL reference](https://www.ibm.com/docs/en/SSYQBZ_9.7.2/com.ibm.doors.requirements.doc/topics/dxl_reference_manual.pdf) (baseline fields/enumeration, getMostRecentBaseline, baselineExists, nextMajor/nextMinor, baselineIndex); [IBM 9.6.1 reference, IBM Jazz üzerinde](https://jazz.net/dxl/resource/dxl_reference_manual.pdf) (Date conversion ve current-module/handle semantics); [IBM 9.2 reference](https://download.boulder.ibm.com/ibmdl/pub/software/rationalsdp/documentation/product_doc/DOORS/version_9-2/dxl_reference_manual.pdf) (önceki declaration karşılaştırması). Başka sürümde aynı ismin bulunması 9.5'e ait sürüme özgü doğrulama yerine geçirilmemiştir. 9.5 referans URL'si bu ortamda erişilemedi; 9.6 dokümanında 9.5'e değinen introductory text de 9.5 manual kanıtı sayılmadı.

Gerçek kabul için 9.5 ve 9.7 ayrı ayrı:

1. İlgili sürümün manual'ı/kurulu headers üzerinden tüm kullanılan perm declaration'larını ve script compilation'ını doğrulayın. 9.5 native gating ancak doğrulanmış adapter değişikliğiyle kaldırılmalıdır.
2. İlk baseline yok; mevcut history; son baseline silinmiş; bütün mevcut baseline'lar silinmiş; suffix'li major/minor örnekleri.
3. nextMajor/nextMinor handle'ının hangi current module bağlamında, create çağırmadan nasıl güvenilir sayısal designation'a çevrildiğini doğrulayın. Özgün current context'in korunmasını ve read-only hiçbir değişiklik olmadığını gözlemleyin. Sonucu test dataset'iyle kaydedin; belgesiz getter/overload eklemeyin.
4. baselineExists pozitif/negatif ve baseline/module ACL kısıtlarını kontrol edin; false'yu kesin absence/deletion garantisi yapmayın.
5. ClosedByPolicy, explicit read-only open/owned close, borrowed read-only leave-open, already-edit/share leave-unchanged ve deleted/wrong-ID modül durumları.
6. Creator, annotation Unicode, epoch UTC/raw date ve intOf tarih aralığı sınırlarını; baselineIndex'in yalnız doğru loaded baseline'a bağlandığını doğrulayın.
7. Metadata ile onaylı creation gateway çağrılarının ortak STA'da seri olduğunu; timeout sonrası busy/drain, cancellation-before-dispatch ve late result cleanup'ı doğrulayın. Bu görevin default çalıştırması hiçbir baseline oluşturmaz.

## Değişen dosyalar ve build/test

Yeni: Core/BaselineMetadataModels.cs, BaselineMetadataService.cs, MockBaselineHistoryAdapter.cs; Dxl/DxlBaselineHistoryAdapter.cs, DoorsStaExecutionQueue.cs; scripts/dxl/baseline-metadata.dxl; Tests/BaselineMetadataTests.cs, BaselineMetadataProtocolTests.cs, BaselineStaQueueTests.cs; bu belge.

Güncellenen: Core/Errors.cs (additive code 57), Dxl/DxlExecutor.cs (eski overload'lar korunur), Console/Desktop DI extensions, WPF ClientSession/MockDoorsAdapter/MockModuleSelectionAdapter ve belgeler. Yeni NuGet paketi yoktur. Var olan DXL scriptleri, Settings, icon, eski testler ve write approval akışları değiştirilmedi.

Windows/net48 üzerinde `DOORS_TEST_MODE=disabled` ile:

```powershell
dotnet restore DoorsIntegration.sln -p:Platform=x86 --configfile NuGet.Config
dotnet build DoorsIntegration.sln -c Release -p:Platform=x86 --no-restore
dotnet test tests/DoorsIntegration.Tests/DoorsIntegration.Tests.csproj -c Release -p:Platform=x86 --no-build --no-restore --settings tests.runsettings --filter 'FullyQualifiedName~BaselineMetadataTests|FullyQualifiedName~BaselineMetadataProtocolTests|FullyQualifiedName~BaselineStaQueueTests' -- RunConfiguration.TargetPlatform=x86
```

x64 için restore dahil bütün architecture değerlerini değiştirin; shared assets nedeniyle restore'ları eşzamanlı çalıştırmayın. Bu ortamda mevcut offline SDK 5.0.416 host'u kullanılır. Sonuçlar VALIDATION.md'dedir. Güncel bin çıktıları Task 21'i içerir; önceki release ZIP'leri yeniden yayımlanmamıştır.
