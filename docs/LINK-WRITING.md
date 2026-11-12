# Onaylı Classic link oluşturma ve kaldırma

`ILinkService.PreviewWriteAsync` ve `ApplyWriteAsync`, iki current formal nesne arasında **bir** link oluşturur veya kaldırır. `LinkWriteAction.Create` / `Remove` aynı immutable istek, dry-run ve onay altyapısını kullanır. COM `runFile` üzerinden harici `scripts/dxl/link-write.dxl` çalışır; DWA/OSLC yoktur. Okuma API'si değişmeden kullanılabilir.

## Desktop

**Workspace → Link editor** ekranında kaynak/hedef formal modülün tam yollarını, her iki nesnenin **Absolute Number** değerini ve mevcut link module yolunu girin. Object Number veya grid Sequence kimlik olarak kullanılmaz. `Create` ya da `Remove` seçin, ardından `Preview / dry-run` çalıştırın. İlgili modüller istemcide kapalı olmalıdır; ekran seçilmesi bağlantı kurmaz.

Önizleme DB/kullanıcı/modül/nesne kimliklerini, mevcut outgoing link referanslarını, kapsamı, hataları ve fingerprint'i gösterir. `Review and apply...` tam hedefin yazılmasını ve acknowledgement kutusunu gerektirir. **Remove için ikinci, ayrı “Explicit link removal approval” penceresi açılır:** `DELETE ` ile başlayan tam hedef yeniden yazılıp ayrıca onaylanır. Her iki pencereden birini kapatmak/iptal etmek yazmayı engeller. Link silme geri alınamaz; eski link attribute değerleri otomatik olarak yeniden oluşturulmaz.

Alan, limit, timeout, profile veya connection değişince plan geçersiz olur. Plan aynı servis örneğine bağlıdır, beş dakika geçerlidir ve tek kullanımlıdır. Başarısız/iptal edilen onaydan sonra da yeni önizleme gerekir. Settings/watchdog tercihi ve ikon değiştirilmedi.

## C# API ve DI

```csharp
var links = provider.GetRequiredService<ILinkService>();
var request = new LinkWriteRequest(
    LinkWriteAction.Create, // kaldırma için Remove; ayrıca silme onayı istenir
    sourceModulePath: "/Project/Source", sourceAbsoluteNumber: 17,
    targetModulePath: "/Project/Target", targetAbsoluteNumber: 42,
    linkModulePath: "/Project/Satisfies", clientVersion: DoorsVersion.V97);

var preview = await links.PreviewWriteAsync(request, cancellationToken);
ShowReview(preview); // host raporu gösterir; bu çağrı tek başına onay değildir
if (!preview.CanApply) return;
var result = await links.ApplyWriteAsync(preview, cancellationToken);
ShowResult(result); // Outcome, Persistence, Cleanup, AuditRecorded ve Comparison
```

`ShowReview`, `ShowResult`, provider ve token hosta aittir. `ApplyWriteAsync` doğrudan kullanıcı onayını `ILinkWriteConfirmation` üzerinden ister; `ConfirmAsync` ve **ayrı** `ConfirmRemovalAsync` metodları UI/host tarafından gerçek kullanıcı kararıyla uygulanır. Console varsayılanı `RejectLinkWriteConfirmation` her iki onayı reddeder; otomatik kabul seçeneği/CLI yazma komutu eklenmedi. Desktop görünür pencereye bağlı açık review kullanır. DI kayıtları `TryAdd` ile host override'larını korur.

Portlar: `ILinkWriteGateway`, `ILinkWriteConfirmation`, `ILinkWriteAuditSink`. Adaptör `DxlLinkWriteGateway`, seçenekler `LinkWriteScriptOptions` ve `LinkWriteAuditOptions`; varsayılan audit `%LOCALAPPDATA%\DoorsIntegration\audit\link-writes.jsonl`. Reader-only `new LinkService(reader)` çalışmaya devam eder ve yazmayı reddeder. Gateway, servis tarafından tek seferlik yetkilendirilmemiş preview'ı commit edemez. Bu sınır uygulama API'sidir; güvenilmeyen özel DXL için sandbox değildir.

## Kontroller ve kapsam

- Tam yollar, pozitif Absolute Number, sürüm/mod/timeout/limitler dispatch öncesinde doğrulanır; wildcard kabul edilmez. Yalnız current formal nesneler yazılabilir; tablo/satır/hücre, silinmiş veya okunamayan nesne reddedilir.
- Kaynak/hedef/link module türü, okunabilirliği, qualified kimliği, mevcut oturumda açık olması ve gözlenen kilitleri kontrol edilir. Kullanıcının açık read/edit/share modülleri devralınmaz; force unlock/upgrade/discard yapılmaz.
- **Mevcut link module ve mevcut, tek current-source → target linkset zorunludur.** Eksik/çoklu/okunamayan linkset raporlanır. Linkset, modül veya descriptor oluşturulmaz/değiştirilmez. Native descriptor/mapping/trigger kuralları aşılmaz; hataları NativeFailure olarak raporlanır.
- Dry-run yalnız okur ve kendi açtıklarını kapatır. Item düzeyindeki Modify hakları önceden incelenir; nesne/module Modify ve link silme yetkileri exclusive edit altında kesin olarak tekrar kontrol edilir. `CanApply`, son edit kilidinin/iznin gelecekte alınabileceği garantisi değildir.
- Commit kaynak ve link module üzerinde sahip olduğu exclusive edit oturumlarını kullanır; farklı hedef read-only açılır. Aynı formal modüldeki nesneler de tek source handle ile desteklenir. Kaynak nesne Modify, hedef nesne Read, link module/linkset Modify ve Remove için `canDelete(Link)` gereklidir.
- DB/kullanıcı/modül/nesne kimlikleri, linkset nesnesi ve outgoing referansların canonical multiset'i onaylı state ile karşılaştırılır. Başka kullanıcı/oturum/veri durumuna sessiz geçilmez. Create eşleşme sayısı sıfır olmalı; mevcut link varsa `DuplicateLink`. Remove tam olarak bir eşleşme ister; sıfır `LinkMissing`, birden fazla `AmbiguousLink`. Duplicate silme temizliği/bulk delete yoktur.

Traceability karşılaştırmasının kapsamı **seçilen kaynak nesnenin, seçilen tam link module üzerinden bütün outgoing referanslarıdır**. Tüm veritabanı veya tüm incoming modüller için transaction iddiası yoktur. Her referans hedef tam yolu, qualified modül kimliği, Current/Baseline + sürüm ve Absolute Number taşır; baseline/current aynı Absolute Number olsa da ayrıdır. İlgisiz hedef nesnelerin içeriği açılmaz; bunlar referans kimlikleridir, bütün uçların çözümlendiği raporu değildir. Bilinmeyen/silinmiş/erişilemeyen modül referansı kapsamı belirsiz yaparsa yazma engellenir. Seçilen iki nesne ise gerçekten okunabilir ve mevcut olmalıdır. Surrogate nesneye işaret eden Classic link de aynı somut nesne kimliğiyle işlenir; dış sistemde değişiklik/senkronizasyon yapılmaz.

`Before` / `After` ve `Comparison.Added` / `Removed`, çoklukları korur; sıralama değişimi sahte delta üretmez. Save öncesi native karşılaştırma ve save/close/reopen sonrası karşılaştırma yapılır; hedef yeniden yüklenip kimliği de kontrol edilir. Başarı için sadece istenen **bir** kenar eklenmeli/kaldırılmalı, kapsam içindeki diğer referanslar aynı kalmalıdır. Managed parser ayrıca bu farkı, onaylı before-state'i, request ID ve plan ID'yi doğrular. Limit/eksik response yanlışlıkla sıfır link veya başarı sayılmaz.

Tek istek en fazla 2.048 referans, 30.000 karakterlik canonical trace ve 32.768 karakterlik onay state'iyle sınırlıdır; linkset taraması 100.000 nesnede durur. Aşım `LimitExceeded` ile yazmayı engeller. Bu tek-link yazma kapsamı bellekte sınırlıdır; büyük modül okuma ve export mevcut streaming servislerini kullanır.

## Sonuç, audit ve belirsizlik

`Succeeded` yalnız Applied + Saved + Closed + doğrulanmış after-trace + başarılı audit ile true olur. `Comparison` sadece `AfterVerified` olduğunda vardır. PartialFailure/OutcomeUnknown ve `Persistence` birlikte incelenmelidir; Saved sonrasında readback/cleanup başarısız olabilir. `AfterVerified` tek başına kaydedildi anlamına gelmez; save öncesi gözlem de kısmi sonuçta bulunabilir.

Yalnız kaynak modül kaydedilir. Var olan linkset'in link module'ünde beklenmedik dirty durum görülürse o modül otomatik kaydedilmez. Mutation sonrası hata/dirty edit handle `LeftOpenForReview` olarak bırakılır; rollback, unsaved discard, kendiliğinden save/retry yoktur. Temiz ve sahip olunan handle'lar kapatılır; link module kapanmadan kendi yüklenmiş linkset'i unload edilir. Cleanup hataları ayrıca görünür.

Timeout/iptal COM'u durdurmaz. Başlatılmış commit'in transport/protokol/COM hatası `LinkWriteUncertainException` döndürür: `PlanId`, `OperationId`, `Task<LinkWriteResult> Completion`. İlk completion ve COM cleanup bitmeden gate yeni iş başlatmaz; Desktop bu geç sonucu bekleyip gösterir. Exception sonrasında tekrar Apply/otomatik retry yapılmaz. Dispatch öncesi busy gate, input veya audit hatası yazmanın gönderilmediği kesin hatadır.

Audit önizleme, genel onay, ayrı silme onayı, dispatch, sonuç ve belirsizliği kaydeder. UTC timestamp, hedef tuple, modül kimlikleri, plan/fingerprint, persistence/cleanup, trace sayıları ve eklenen/kaldırılan kenar hash'leri tutulur; requirement metni/state ham içeriği otomatik loglanmaz. Dispatch öncesi audit hatası yazmayı engeller. Sonuç audit'i başarısızsa kaydedilmiş sonucu korur, `AuditRecorded=false` ve `Succeeded=false` olur. Eksik son JSONL satırı mevcut delili ezmeden yeni yazmayı engeller.

## Native API kaynakları ve doğrulama sınırı

IBM [DXL Reference Manual 9.2](https://download.boulder.ibm.com/ibmdl/pub/software/rationalsdp/documentation/product_doc/DOORS/version_9-2/dxl_reference_manual.pdf), basılı sayfalar 244–249, 332 ve 343–361: explicit link operator, `all` outgoing iteration, version/absolute identity, `canDelete(Link)`, `delete(Link)`, `flushDeletions()`, `linkset(Object)`, `getSourceVersion(Linkset)`, `getTarget(Linkset)`, unload ve clean close imzaları kontrol edildi. Silme iterator dışında flush edilir; yalnız sonlandırmaya bırakılıp erken save/readback yapılmaz.

IBM [9.7 link oluşturma](https://www.ibm.com/docs/en/engineering-lifecycle-management-suite/doors/9.7.0?topic=links-creating) ve [9.7 link silme](https://www.ibm.com/docs/en/engineering-lifecycle-management-suite/doors/9.7.1?topic=links-deleting-from-multiple-objects) belgeleri kaynak/ hedef erişimlerini açıklar; silmede link module Modify de gerekir. Uygulama daha dar sahiplik ve mevcut-linkset politikası uygular. 9.7.2 PDF adresi bu ortamda 403 döndürdü; doğrulanmamış yeni API adı eklenmedi.

**Bu geliştirmede native DXL IBM DOORS 9.5/9.7 yorumlayıcısında derlenmedi/çalıştırılmadı; canlı veritabanına yazılmadı.** Fake COM/protokol ve gerçek WinForms testleri native ACL/kilit/trigger/linkset davranışını kanıtlamaz. Aşağıdaki kabul testleri iki sürüm ve uygun x86/x64 client kaydıyla ayrı test DB'sinde yapılmalıdır.

## Manuel kabul ve opt-in entegrasyon testleri

1. Eksik/yanlış tipli/erişimsiz kaynak, hedef, link module; olmayan/silinmiş nesne; baseline; açık/dirty/share/exclusive module ve başkasının kilidi: mutation olmadan açıklayıcı hata.
2. Eksik/okunamayan/çoklu linkset: module/linkset oluşturulmamalı; Create/Remove Apply açılmamalı.
3. Önizleme/Cancel, yanlış hedef, boş acknowledgement, ikinci silme onayını reddetme, expiry ve alan/profile değişimi: veri ve save sayısı değişmemeli.
4. Create exact link bir kez oluşmalı; aynı tuple tekrar dry-run'da DuplicateLink olmalı. Aynı Absolute Number'a baseline linki mevcut current link sayılmamalı.
5. Remove yalnız tek onaylı current linki kaldırmalı; başka kaynak/hedef/baseline linkleri kalmalı. Olmayan veya duplicate mevcut link bulk silinmemeli.
6. Preview sonrası actor/DB/nesne kimliği/linkset/trace/kilit/ACL değişsin: commit recheck durmalı. İki eşzamanlı create denemesinde source exclusive kilidi altında ikinci duplicate oluşturamamalı.
7. Önce/sonra native grid + JSON/CSV traceability export ile karşılaştırın; sadece bir beklenen kenar değişmeli. Same-module ve farklı-module, gizli/filtrelenmiş nesneler, Unicode yollar denenmeli.
8. Native descriptor/mapping reddi, trigger ek yan etkisi, save/readback/cleanup hatası ve audit disk hatası: partial/unknown görünmeli; dirty modüller kaybedilmemeli; otomatik retry olmamalı.
9. Yavaş çağrıda timeout/Cancel: tek dispatch, gate'in native dönüşe kadar tutulması, geç sonuç ve audit kontrolü. Silmede flush/save/reload sonrası kalıcılığı özellikle doğrulayın.

`LinkWriteIntegrationTests` varsayılan kapalı üç gerçek ortam senaryosu içerir. Environment ayarları:

```powershell
$env:DOORS_TEST_VERSION='9.7' # veya 9.5
$env:DOORS_TEST_DATABASE_ID='<exact test database ID>'
$env:DOORS_LINK_TEST_SOURCE='/Test/Source'
$env:DOORS_LINK_TEST_SOURCE_ABSOLUTE='17'
$env:DOORS_LINK_TEST_TARGET='/Test/Target'
$env:DOORS_LINK_TEST_TARGET_ABSOLUTE='42'
$env:DOORS_LINK_TEST_MODULE='/Test/Satisfies'
$env:DOORS_LINK_TEST_ACTION='Create' # veya Remove; ayrı preview gerekir
$env:DOORS_TEST_MODE='link-preview'
dotnet test tests/DoorsIntegration.Tests/DoorsIntegration.Tests.csproj -c Release -p:Platform=x86 --settings tests.runsettings --filter 'FullyQualifiedName~LinkWriteIntegrationTests' -- RunConfiguration.TargetPlatform=x86
```

Önizlemenin tam hedefini ve before-trace'ini elle inceleyin. Yalnız o işlemi gerçekten uygulamak için aynı istekle:

```powershell
$env:DOORS_LINK_APPROVED_FINGERPRINT='<reviewed preview fingerprint>'
$env:DOORS_LINK_WRITE_CONFIRMATION="APPLY $env:DOORS_LINK_APPROVED_FINGERPRINT"
$env:DOORS_TEST_MODE='link-create' # Create için tek mutation testi
# Remove için ayrıca: DOORS_TEST_MODE=link-remove ve aşağıdaki ayrı silme onayı
# $env:DOORS_LINK_REMOVAL_CONFIRMATION='DELETE /Test/Source #17 -> /Test/Target #42 via /Test/Satisfies'
# Yukarıdaki aynı dotnet test komutu; teardown yazması yok, sonuç yerinde kalır.
```

Write testi yeniden preview alır, DB/fingerprint'i eşleştirir ve bağımsız mevcut LinkReader ile persisted count'u okur. Değişmiş preview yazmayı engeller. Otomatik test çalıştırmaları için `DOORS_TEST_MODE=disabled` bırakın; x64 için önce ilgili platform restore/build yapın. Hata sonrası testi kendiliğinden tekrar çalıştırmayın.
