# Onaylı gereksinim yazma

`IRequirementService` mevcut streaming okumanın yanında şu API'leri sunar:

- `GetAttributeSchemaAsync(RequirementWriteTarget)`: modülün gerçek attribute listesini ve enum seçeneklerini salt okunur alır.
- `PreviewWriteAsync(RequirementWriteRequest)`: kontroller, eski değerler, adım planı ve onay fingerprint'i üretir; create/move/save çağırmaz.
- `ApplyWriteAsync(RequirementWritePreview)`: aynı servis örneğinin kullanılmamış, süresi dolmamış ve açıkça onaylanmış planını bir kez uygular.

Desktop **Workspace → Requirement editor** bu API'yi gerçek katalog, değer/enum/rich-text düzenleyici, dry-run ve açık onay penceresiyle kullanır. Tam hedef + acknowledgement + Apply gerekir; otomatik onay yoktur. `DesktopWriteConfirmation`, bağlı görünür owner yoksa, expiry/iptal/Cancel durumlarında reddeder. Console varsayılan `RejectRequirementWriteConfirmation` kullanır; özel host gerçek onay sağlamalıdır. [Desktop kullanımı](DESKTOP-WORKSPACE.md). Settings, kayıtlı tercihler ve ikon değiştirilmedi. Burada gerçek DOORS istemcisine bağlanılmadı, native DXL derlenmedi/çalıştırılmadı ve veritabanına yazılmadı.

## Modülden alınan metadata ve doğrulama

`RequirementModuleSchema`: yol, database/user, `uniqueID`/`qualifiedUniqueID`, kontroller ve attribute tanımları. Her `RequirementAttributeSchema`: ad, tip adı, temel tip, object/DXL/system/multi/read/write-hint bayrakları ve enum etiketleri. Şema sorgusu DXL attribute değerlerini hesaplatmaz.

Değişiklikler **bu gerçek listeye göre** kontrol edilir: olmayan alan, nesneye uygulanmayan veya hesaplanan attribute, korunan system/kimlik alanı, yanlış rich-text tipi, geçersiz enum etiketi ve tekli/çoklu seçim engellenir. Native `attributeValue(AttrDef,string)` tipi/değeri ayrıca doğrular; yazılan alan `isAttributeValueInRange` ile denetlenir. Sayı ve tarih girişleri DOORS'un kabul ettiği native biçimde olmalıdır; varsayılan ISO tarih veya .NET kültür dönüşümü varsayılmaz.

Enumlar tam etiketlerle, çoklu seçim LF ile gönderilir. CRLF normalize edilir; çoklu readback etiket kümesiyle karşılaştırılır. Düz metin readback'inde yalnız CRLF/LF farkı normalize edilir. Diğer boşluk/karakterler değiştirilmez; beklenmeyen sayı/tarih gösterimi `ReadbackMismatch` üretir ve sessiz başarı sayılmaz.

**Zorunluluk:** doğrulanan standart DXL attribute metadata'sında genel bir `required`/`mandatory` özelliği kullanılmaz veya varsayılmaz. `RequiredRule=NotDeclaredByStandardMetadata` ve `RequiredRulesNotDeclared` uyarısı bu sınırı bildirir. Tüm attribute'lar zorunlu sayılmaz. Host iş kurallarını biliyorsa `requiredAttributes` listesi verir: alanın varlığı ve yazılacak/mevcut değerin boş olmaması preview/commit/readback'te denetlenir. Create'te bu alanların açıkça gönderilmesi gerekir; default değer yeterli varsayılmaz. Trigger/eklenti kuralları tahmin edilmez veya devre dışı bırakılmaz.

## İstekler, rich text ve hiyerarşi

`AbsoluteNumber` boşsa create; doluysa o kalıcı nesnenin update/move isteğidir. Object Identifier, Object Number ve Absolute Number kullanıcı tarafından attribute olarak yazılamaz. Sonuç yeni/mevcut Absolute Number, Object Identifier, ebeveyn/seviye ve modül kimliklerini döndürür.

| Position | Create | Mevcut nesne |
| --- | --- | --- |
| `Keep` | Desteklenmez | Mevcut konum; update varsayılanı |
| `FirstInModule` | İlk üst düzey; create varsayılanı | Desteklenmez |
| `After(anchor)` | Anchor ile aynı seviyede hemen sonra | Nesne ve alt ağacı anchor'dan sonra |
| `FirstChild(anchor)` | İlk çocuk | Nesne ve alt ağacı ilk çocuk |
| `LastChild(anchor)` | Son çocuk | Nesne ve alt ağacı son çocuk |

```csharp
var target = new RequirementWriteTarget("/TestProject/Sandbox/Requirements", DoorsVersion.V97);
var schema = await service.GetAttributeSchemaAsync(target);
// UI: gerçek adları, tipleri ve enum seçeneklerini schema.Attributes içinden gösterin.
var request = new RequirementWriteRequest(target, new[]
{
    new RequirementAttributeChange("Object Heading", "Motor kontrolü"),
    new RequirementAttributeChange("Object Text", "Sistem, belirtilen koşulda motoru durdurmalıdır.")
}, position: new RequirementPosition(RequirementPositionKind.LastChild, 17));
var preview = await service.PreviewWriteAsync(request);
// Host: kimlikler, eski/yeni değerler, konum ve tüm uyarılar için açık kullanıcı onayı almalı.
var result = await service.ApplyWriteAsync(preview);
```

Update için `absoluteNumber: 42`; move-only için `changes: null, absoluteNumber: 42, position: ...` kullanın. Özel alanlar şemadaki adlarıyla aynı değişiklik listesine eklenir. Yeni attribute tanımı/tipi/enum seçeneği oluşturulmaz. Silinmiş/erişilemeyen nesne, tablo/row/cell, kendi altına taşıma ve hierarchy cycle engellenir. Alt nesne kimlikleri/iç ebeveynleri korunur; başka modüle taşıma yoktur. Üst düzey sona eklemek için son üst düzey anchor ile `After` kullanın.

Rich text açık bir format ve beklenen düz metin gerektirir:

```csharp
new RequirementAttributeChange("Object Text", @"{\b Önemli}",
    RequirementWriteFormat.DoorsRichText, expectedPlainText: "Önemli");
```

Native atama `attrRef = richText(string)` kullanır. Plain readback ve tip/range doğrulanır; RTF/OLE'nin byte-for-byte aynı kalacağı iddia edilmez. Font/paragraf/OLE davranışı gerçek istemcide test edilmelidir. Düz metin replacement o alanın biçimini bilinçli olarak değiştirir; preview `BeforeValues` eski plain/richTextWithOle temsillerini ayrı içerir. İstenmeyen alanlar yazılmaz; move-only hiçbir alanı düz metne dönüştürmez.

## Dry-run, edit kilidi ve onay

Şema sorgusu mevcut read-only modülü ödünç alabilir ve kapatmaz. Write preview/commit için modül istemcide **kapalı** olmalıdır; kullanıcıya ait read/edit/share oturumu veya dirty değişiklikler kullanılmaz. DOORS client açık/oturumlu kalır. Preview kendi read-only yüklemesini kapatır. Yalnız current formal modül kabul edilir; baseline yazılmaz.

Preview item erişimi, gözlenen kilit, nesne/anchor, schema ve değerleri inceler. `IsWritableHint` kesin izin veya lock rezervasyonu değildir. Bazı permission API'leri exclusive edit gerektirdiği için `EditRecheckRequired` uyarısı vardır; `CanApply=true` koşullu preflight başarısıdır.

Onaydan sonra `isLocked(ModName_)` tekrar kontrol edilir, `edit(path,false,true,true)` ile servise ait exclusive edit alınır. Shared fallback/force-unlock yoktur. Module/object create/modify ve attribute-value write hakları edit altında doğrulanır. Yeni nesnenin gerçek value ACL'si create sonrasında denetlenebilir; buradaki hata partial sonuçtur. İlgili eski değerler/tip-enumlar/nesne-ata-alt ağaç durumu ve database/user/module kimlikleri approved state ile tekrar eşleştirilir. Değişmiş state `ContextChanged` olur.

Preview aynı servis örneğine aittir, 5 dakika geçerlidir ve tek kullanımlıdır. Fingerprint hedef, profile/timeout, değer/format/konum, required listesi ve ilgili eski state'e bağlıdır; bir yetkilendirme sırrı değildir. Reddedilen/expired/replayed/başka servis planı ve dispatch öncesi hata için yeni preview gerekir. Host override'ları `TryAdd` ile korunur. `RequirementService(reader)` constructor'ı read-only çalışır; yazma sağlayıcıları yoksa yeni API'leri reddeder.

Read-only preview database transaction değildir. `Keep` alan güncellemesi erişilemeyen ilgisiz nesneleri atlayabilir; hierarchy işlemleri daha sıkı okunabilirlik/alt ağaç kontrolü yapar. Native izin ve kurulu trigger kuralları son yetkilidir; harici trigger etkileri için genel rollback garantisi verilmez.

## Audit, partial failure ve timeout

Default audit `%LOCALAPPDATA%\DoorsIntegration\audit\requirement-writes.jsonl`; `RequirementWriteAuditOptions` ile başka mutlak yol kullanılabilir. UTC zaman, phase, plan/operation ID, actor/database/module/yol, işlem türü, istenen nesne/konum/anchor, fingerprint, alan adları/formatı ve önceki/istenen içeriklerin SHA-256 özetleri yazılır. Tam requirement, RTF/OLE veya before-state loglanmaz. Final kayıt outcome/persistence/cleanup ve sıralı adım durumlarını içerir. Seri append, writer paylaşım koruması ve disk flush kullanılır; eksik son JSON satırında yeni append durur. Dosya imzalı/tamper-proof kayıt sistemi olarak sunulmaz.

Ön audit yazılamazsa commit başlamaz. Final audit hatası gerçek persistence bilgisiyle `AuditRecorded=false` döndürür; oluşmuş veriye rollback/tekrar iddiası yoktur.

Adımlar `Create`, `Position`, `Attribute`, `Save`, `Verify`; durumlar `Applied`, `Failed`, `Skipped`. İlk hatada kalan adımlar durur. Tüm değer/hierarchy/required kontrollerinden sonra save edilir; modül kapatılıp current sürüm salt okunur yeniden yüklenerek nesne ve içerik doğrulanır.

- `Applied + Saved + Closed + AuditRecorded`: `Succeeded=true`.
- `Blocked` / `ContextChanged`: mutasyon adımları atlanır.
- `PartialFailure`: bazı adımlar uygulanmış olabilir; `Steps` ile inceleyin. `NotAttempted`, servisin save çağırmadığını bildirir, trigger davranışını garanti etmez.
- `OutcomeUnknown` / `Persistence=Unknown`: save/COM sonucu kesinleşmedi; veri oluşmuş olabilir.
- `LeftOpenForReview`: dirty/partial exclusive modül bilinçli açık kalır ve kilit tutabilir. Kullanıcı incelemelidir. Otomatik discard-close, save-partial, delete/rollback yoktur.

Timeout/iptal başlamış COM çağrısını zorla durdurmaz. `RequirementWriteUncertainException.Completion` geç sonucu/audit'i takip eder; STA/gate/workspace gerçek dönüşe kadar tutulur. Yazma otomatik tekrarlanmaz. Parse/validation hatası başarı sayılmaz.

## Dış betik ve sınırlar

`scripts/dxl/requirement-write.dxl` ayrı dosyadır; build/ZIP'e kopyalanır. Ortak RunFile hazırlığı string escaping kullanır. Packed tab/newline/backslash girişleri veridir; `eval_` yoktur. Executor genel bir DXL sandbox'ı değildir; dış betik trusted uygulama kodudur.

Tek modül/tek nesne request'i; 32 değişiklik/32 required; tek değer 65.536 karakter ve toplam escaped paket 65.536 karakter; 256 attribute, alan başına 512/toplam 8.192 enum; 100.000 taranan nesne, 2.048 taşınan alt nesne; ilgili-state ve eski tek alan 32.768 karakter; response 1 Mi karakter. Aşım sessiz truncate/başarı yerine bloklanır. Çok büyük eski RTF/OLE state sınırına takılabilir; büyük okuma mevcut streaming ReadAsync ile yapılabilir. Timeout 1 ms–10 dakika, default 2 dakika. Settings watchdog politikası aynen korunur.

API adları IBM [9.7.2 DXL Reference Manual](https://www.ibm.com/docs/en/SSYQBZ_9.7.2/com.ibm.doors.requirements.doc/topics/dxl_reference_manual.pdf), [9.6 manual](https://www.ibm.com/docs/en/SSYQBZ_9.6.0/com.ibm.doors.requirements.doc/topics/dxl_reference_manual.pdf) ve karşılaştırma için [9.2 manual](https://download.boulder.ibm.com/ibmdl/pub/software/rationalsdp/documentation/product_doc/DOORS/version_9-2/dxl_reference_manual.pdf) Object management, Attribute definitions/types/value access, module open/save/close/state ve locking bölümlerinden seçildi. `move before` overload'u veya varsayımsal AttrDef.required kullanılmadı. Kurulu 9.5/9.7 yorumlayıcılarında compile/runtime doğrulaması hâlâ gerekir.

## Opt-in entegrasyon testleri

`RequirementWriteIntegrationTests` içindeki iki test default **Skipped/Inconclusive** olur. Bu geliştirmede çalıştırılmadılar. Yalnız doğrulanmış test DB'sinde mevcut formal sandbox modülüyle, doğru COM/mimari ve AttachExisting kullanılarak elle çalıştırılmalıdır. Hedef modül kapalı olmalıdır.

```powershell
$env:DOORS_TEST_MODE = 'requirement-preview'
$env:DOORS_TEST_VERSION = '9.7' # veya 9.5
$env:DOORS_TEST_DATABASE_ID = '<doğrulanmış-test-db-id>'
$env:DOORS_REQ_TEST_MODULE = '/TestProject/Sandbox/Requirements'
$env:DOORS_REQ_TEST_ACTION = 'create' # veya update / move
$env:DOORS_REQ_TEST_HEADING = 'Integration trial'
$env:DOORS_REQ_TEST_TEXT = 'The system shall record the test event.'
# update/move: DOORS_REQ_TEST_ABSOLUTE
# move: DOORS_REQ_TEST_POSITION = After / FirstChild / LastChild; DOORS_REQ_TEST_ANCHOR
# isteğe bağlı: DOORS_REQ_TEST_REQUIRED = 'Object Text'
dotnet test tests/DoorsIntegration.Tests/DoorsIntegration.Tests.csproj -c Release -p:Platform=x86 --no-build --no-restore --settings tests.runsettings --filter 'FullyQualifiedName~PreviewRequirementWriteInExplicitTestModule' --logger 'console;verbosity=detailed'
```

Katalog, eski/yeni alanlar, hedef/konum, actor/DB, uyarılar ve fingerprint'i inceleyin. CanApply=false ise ilerlemeyin. Basit lab testi Heading/Text gönderir; özel enum/rich text senaryolarını API hostundan hazırlayın. İncelenen aynı planı açıkça onaylamak için:

```powershell
$env:DOORS_REQ_TEST_APPROVED_FINGERPRINT = '<incelenen-64-karakter-fingerprint>'
$env:DOORS_REQ_TEST_CONFIRM_MODULE = '/TestProject/Sandbox/Requirements'
$env:DOORS_REQ_TEST_CONSENT = 'I_CONFIRM_REQUIREMENT_WRITE'
$env:DOORS_TEST_MODE = 'requirement-write'
dotnet test tests/DoorsIntegration.Tests/DoorsIntegration.Tests.csproj -c Release -p:Platform=x86 --no-build --no-restore --settings tests.runsettings --filter 'FullyQualifiedName~ApplyRequirementWriteInExplicitTestModule' --logger 'console;verbosity=detailed'
```

Write testi fresh preview fingerprint'ini eşleştirir, bir kez uygular ve mevcut streaming reader ile kalıcı nesneyi ayrıca okur. Test değişiklikleri/nesneler **silinmeden bırakılır**. Her yeni action/değer/target/nesne/konum için yeni preview/onay gerekir. Sonrasında MODE, CONSENT, APPROVED_FINGERPRINT ve CONFIRM_MODULE ortam değişkenlerini kaldırın; başarısız yazma testlerini otomatik tekrar ettirmeyin.

## Manuel 9.5/9.7 × x86/x64 matrisi

1. Text/String/Integer/Real/Date, tekli/çoklu enum, DXL/system/module alanlarını sorgulayın; gerçek tip/etiketlerle karşılaştırın.
2. Dry-run/default reject nesne sayısını, alanları veya hiyerarşiyi değiştirmemeli. Yanlış enum/tip, eksik alan, blank required, invalid hierarchy ve baseline bloklanmalı.
3. Başka client kilidi, kendi açık read/edit/share modülü, object/attribute ACL denial ve erişilemeyen/silinmiş target/anchor denenmeli; force-unlock/borrowed-edit-save olmamalı.
4. Reddedilen, expired, replayed ve farklı servis planı; approval sırasında DB/user/module/önceki değer/anchor değişikliği test edilmeli.
5. Açık onaylı lab create/update sonrası Unicode, Heading/Text, gerçek custom alan/enum ve nesne kimliklerini save/yeniden açma ile doğrulayın.
6. Alt ağaçlı nesneyi After/FirstChild/LastChild taşıyın; çocuk kimlikleri/iç ebeveynleri, payload/RTF/OLE korunmalı. Kendi altına/table target/erişilemeyen üyeler bloklanmalı veya native hata raporlanmalı.
7. Düz replacement ile explicit rich-text yazmayı ayrı deneyin. Plain readback'i byte-for-byte RTF/OLE garantisi saymayın; font/paragraf/OLE semantiğini gerçek istemcide inceleyin.
8. Kontrollü attribute/trigger/save hatasında Applied/Failed/Skipped, candidate kimliği, Saved/Unknown ve açık-kirli modül raporunu denetleyin; otomatik rollback/save-partial/retry olmamalı.
9. Yazılamayan audit veya eksik JSON tail ön commit'i durdurmalı. Final audit hatası oluşmuş veriyi saklamalı. Yavaş işlemde timeout/iptal sonrası gate/late Completion görülmeli; aynı write tekrar gönderilmemeli.
