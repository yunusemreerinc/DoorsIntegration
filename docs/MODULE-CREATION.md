# Onaylı formal modül oluşturma

`IModuleService.PreviewFormalModuleCreationAsync` hedef klasör, modül adı ve açıklamayı alır; veritabanına yazmadan kontrolleri ve planı döndürür. `CreateFormalModuleAsync` yalnız bu servis örneğinin ürettiği, henüz kullanılmamış ve süresi dolmamış önizlemeyi kabul eder. Plan 5 dakika geçerlidir. Parametreler immutable'dır; değişiklik için yeni önizleme gerekir.

Console/Desktop DI, `IFormalModuleCreationGateway` için `DxlModuleCreationGateway` kaydeder. Console onayı **RejectModuleCreationConfirmation** ile reddeder. Desktop **Workspace → Create module**, dry-run ardından tam hedef + acknowledgement + Apply gerektiren `DesktopWriteConfirmation` kullanır; bağlı görünür owner yoksa veya iptal/expiry halinde onay verilmez. Otomatik oluşturma/onay yoktur. [Desktop kullanım ve manuel testler](DESKTOP-WORKSPACE.md). Özel hostlar kendi açık onay sağlayıcısını kaydedebilir.

## Akış ve API

```csharp
var request = new FormalModuleCreateRequest(
    "/TestProject/Sandbox", "IntegrationTrial", "İncelenecek modül açıklaması",
    target: DoorsVersion.V97, connectionMode: DoorsConnectionMode.AttachExisting);

var service = provider.GetRequiredService<IModuleService>(); // Aynı örneği iki aşamada da tutun.
var preview = await service.PreviewFormalModuleCreationAsync(request);
// UI'da Request.FullPath, Description, Checks.DatabaseId/UserName/FolderId,
// Checks.Summary ve ApprovalFingerprint gösterin. Bu aşama DXL okur, yazmaz.
if (!preview.CanCreate) return;

// Servis burada IModuleCreationConfirmation.ConfirmAsync(preview, token) çağırır.
// Varsayılan onay sağlayıcısıyla bu çağrı 37 koduyla reddedilir.
var created = await service.CreateFormalModuleAsync(preview);
Console.WriteLine(created.ModuleId);          // DXL uniqueID
Console.WriteLine(created.QualifiedModuleId); // DXL qualifiedUniqueID
Console.WriteLine(created.Cleanup);           // Closed veya Failed
```

Host, `IModuleCreationConfirmation` uygulamasını `AddDoorsIntegration` / `AddDoorsDesktop` **öncesinde** kaydeder. `ConfirmAsync`, kullanıcıya tam hedefi, açıklamayı, kimlikleri ve kontrol sonuçlarını göstermeli; yalnız kullanıcının açık onayında true dönmelidir. İptal, kapatma, cevapsız onay veya hata false/exception olmalı; koşulsuz true sağlayıcı kullanmayın. WinForms hostu iletişim kutusunu kendi UI iş parçacığında/owner penceresiyle açmalıdır. Onay sağlayıcısı kimlik doğrulama mekanizmasının yerini almaz; host uygulamasının kullanıcı etkileşimi sözleşmesidir.

Önizleme, onay ve create ayrı eylemlerdir. Önizleme sırasında onay istenmez. Kontroller başarısızsa onay sağlayıcısı çağrılmaz. Reddedilen, başarısız veya belirsiz girişimden sonra aynı plan tekrar kullanılamaz. Gateway de servis tarafından onaylanan tek kullanımlık commit yetkisini tüketir; onaylanmamış preview ile gateway'ye doğrudan çağrı yazma başlatmaz.

## Kontroller

| Kontrol | Davranış |
| --- | --- |
| Klasör varlığı/türü | Mutlak yol ile item/folder çözülür; Folder/Project ve okunabilir handle gerekir. Bulunamama ile kullanıcıya görünmezlik `DeniedOrNotFound` olarak birlikte raporlanır. Klasör oluşturulmaz. |
| Yetki | DXL `canCreate(folder)` kontrol edilir. Denied/Unknown yazmayı engeller. ACL veya kullanıcı yetkisi değiştirilmez. |
| Ad/açıklama | C# tek yol bileşeni, boyut ve Unicode kontrolü; DXL native `isValidName` ve `isValidDescription` kontrolü. Boş açıklama desteklenir. |
| İsim çakışması | Bilinen hedef ve okunabilir silinmiş/silinmemiş doğrudan çocuklar kontrol edilir; case-fold eşleşmesi konservatif olarak çakışmadır. `NoVisibleConflict`, gizli adların yokluğu garantisi değildir. Native create nihai yetkilidir ve mevcut modül overwrite edilmez. |
| Kilit | `getLocksInDatabase(true)` ile bildirilen kilitlerde hedef klasör ve atalarının write kilitleri kimlikleriyle karşılaştırılır. `isLocked(Folder)` gibi doğrulanmamış bir overload kullanılmaz. Locked/Unknown yazmayı engeller. |
| Onaydan sonra durum | Commit betiği tüm kontrolleri yeniden yapar; önizlemedeki veritabanı, DOORS kullanıcısı ve klasör uniqueID'si ile karşılaştırır. Klasörün silinip aynı yolda yeniden oluşturulması da yeni onay gerektirir. |

**Kilit görünürlüğü sınırı:** IBM'in allUsers kilit listesi May Manage yetkisi ister; null, hata veya 10.000 kayıt sınırında `Locks=Unknown` üretilir ve oluşturma engellenir. Yetkisiz kullanıcı için yalnız kendi kilitlerini okuyup “kilitsiz” sonucu çıkarılmaz. API açık öğeleri listeden hariç tutabilir; `NoBlockingLockObserved` yalnız gözlemdir ve kilit rezervasyonu değildir. Kontrol ile create arasındaki dış istemci yarışları native create tarafından ele alınır; servis atomik preflight garantisi vermez. Gerekli liste görünürlüğü yoksa bu uygulamada güvenli biçimde oluşturma yapılamaz; yetki yükseltme/forced unlock yapılmaz.

İsim dolaşımı ve kilit listesi 10.000 kayıtla sınırlıdır. Önizleme veritabanı kilidi edinmez. Tam hedef yol native create'a doğrudan verilir; current folder/project atanmaz. DXL `fullName` en yakın projeye göre yol döndürebildiği için klasör/ata ve oluşturulan modül doğrulaması mutlak yolun çözülmesi + uniqueID karşılaştırmasıyla yapılır. Veritabanı kimliği klonlanmış veritabanlarını tek başına ayırt edemeyebilir; kullanıcı doğru istemci ve test veritabanını ayrıca seçmelidir.

## Oluşturma, sonuç ve hata

Harici `scripts/dxl/module-create.dxl`, preview modunda yalnız okuma yapar. Commit dalı açık onay parametreleri ve başarılı güncel kontroller olmadan çalışmaz. C# katmanı parametreleri `DxlStringLiteral` ile kaçırır. Betik yalnız bir formal module create çağrısı yapar: boş object prefix, başlangıç absolute number=1, display=false. Ek nesne/attribute/link yazma çağrısı yapılmaz. Modül kimliği, hedefteki item kimliği ve Formal türü doğrulanır; yeni modül save edilir ve yalnız bu çağrının oluşturduğu modül `close(..., false)` ile kapatılır. Önceden açık modüller düzenlenmez/kapatılmaz.

| Kod | Anlam |
| --- | --- |
| 37 ModuleCreationApprovalRequired | Geçerli preview/onay yok; write dispatch edilmedi. |
| 38 ModuleCreationBlocked | Önizleme engeli veya commit sırasında değişen koşullar/bağlam; create dalına girilmedi. |
| 39 ModuleCreationProtocolFailure | Eksik, bozuk, yanlış kimlikli veya tutarsız cevap; başarı kabul edilmedi. |
| 40 ModuleCreationOutcomeUnknown | Commit dispatch sonrasında timeout, iptal, COM/protokol hatası veya save/kimlik belirsizliği. Modül oluşturulmuş olabilir. |

Commit başladıktan sonra hata, işlemin geri alındığını göstermez. Otomatik retry, silerek rollback, force unlock, COM thread abort veya process kill yoktur. `ModuleCreationUncertainException.OperationId/PlanId` ile çağrı izlenir; `Completion` beklenerek geç sonuç/hata alınabilir. Sonuç `OutcomeUnknown` kalırsa biliniyorsa aday ModuleId/QualifiedModuleId taşır; bu kimlik başarı iddiası değildir. Save/kimlik hatasında yeni modül açık bırakılabilir; veri kaybetmemek için otomatik discard yapılmaz. Başarılı create/save sonrasında yalnız close başarısızsa sonuç `Created` ve kimliklerle döner, `Cleanup=Failed` ayrıca ele alınmalıdır.

STA ve paylaşılan operation gate mevcut DxlExecutor tarafından korunur. Gate COM temizliği bitene kadar yeni işlem/probe kabul etmez. Önizleme, kullanıcı onayı ve dispatch öncesi iptal yazma yapmaz. Oluşturma logları yalnız mod, plan/DXL request kimlikleri, enum kontrol sonuçları, outcome ve cleanup taşır; hedef, açıklama, kullanıcı adı, DB/module kimlikleri ve ham DXL hata metni otomatik loglanmaz. UI'da açıkça incelenen preview ve isteğe bağlı entegrasyon testi çıktısı ayrı kullanıcı çıktısıdır.

## Test projesinde gerçek entegrasyon testi

`tests/DoorsIntegration.Tests/ModuleCreationIntegrationTests.cs` içindeki iki test `DoorsWriteIntegration` kategorisindedir. Varsayılan durumda ikisi de **Inconclusive/Skipped** olur; COM'a bağlanmaz. Bu geliştirmede gerçek testler çalıştırılmadı. İlk test yalnız önizleme üretir. İkinci test, kullanıcı tarafından incelenen aynı parametre/bağlam fingerprint'i ve açık yazma onayı gerektirir; tek modül oluşturur ve ayrı salt okunur betikle tür, kimlik ve kalıcı açıklamayı doğrular. Test modülü **silinmeden bırakılır**, her çalıştırmada yeni bir test adı ve yeni onay gerekir.

DOORS 9.5/9.7 test istemcisinde onaylı test veritabanına giriş yapın. Mevcut test projesinde ayrılmış bir klasör ve gerekli create/kilit-listesi erişimi kullanın. Testler AttachExisting kullanır; istemci başlatmaz. Runtime x86/x64 seçimini çalışan test hostu ve kayıtlı COM görünümüne göre yapın. Gerçek DB ID'yi test ortamı yöneticisinden doğrulayın; örnekteki yer tutucuları kullanmayın.

```powershell
# Örnek: sadece kullanıcı tarafından elle çalıştırılacak test hazırlığı.
$env:DOORS_TEST_MODE = 'preview'
$env:DOORS_TEST_VERSION = '9.7' # veya 9.5
$env:DOORS_TEST_DATABASE_ID = '<onaylı-test-veritabanı-id>'
$env:DOORS_TEST_FOLDER = '/TestProject/Sandbox'
$env:DOORS_TEST_MODULE_NAME = 'IntegrationTrial_001'
$env:DOORS_TEST_DESCRIPTION = 'Onaylı entegrasyon testi'

dotnet restore DoorsIntegration.sln -p:Platform=x86 --packages .packages --configfile NuGet.Config
dotnet build DoorsIntegration.sln -c Release -p:Platform=x86 --no-restore
dotnet test tests/DoorsIntegration.Tests/DoorsIntegration.Tests.csproj -c Release -p:Platform=x86 --no-build --no-restore --settings tests.runsettings --filter 'FullyQualifiedName~PreviewFormalModuleInExplicitTestFolder' --logger 'console;verbosity=detailed'
```

Hedefi, açıklamayı, DB/user/folder kimliklerini, `CanCreate` ve kontrol sonuçlarını inceleyin. CanCreate=false ise yazma aşamasına geçmeyin. Uygunsa preview çıktısındaki 64 karakterlik fingerprint'i kopyalayın; aşağıdaki komutlar açık oluşturma onayıdır:

```powershell
$env:DOORS_TEST_APPROVED_FINGERPRINT = '<incelenen-preview-fingerprint>'
$env:DOORS_TEST_CONFIRM_TARGET = '/TestProject/Sandbox/IntegrationTrial_001'
$env:DOORS_TEST_WRITE_CONSENT = 'I_CONFIRM_CREATE_FORMAL_MODULE'
$env:DOORS_TEST_MODE = 'create'
dotnet test tests/DoorsIntegration.Tests/DoorsIntegration.Tests.csproj -c Release -p:Platform=x86 --no-build --no-restore --settings tests.runsettings --filter 'FullyQualifiedName~CreateFormalModuleInExplicitTestFolder_RequiresReviewedFingerprintAndConsent' --logger 'console;verbosity=detailed'

# Testten sonra oturumda kazara tekrar etkinleştirmeyi önleyin.
Remove-Item Env:\DOORS_TEST_MODE, Env:\DOORS_TEST_WRITE_CONSENT, Env:\DOORS_TEST_APPROVED_FINGERPRINT, Env:\DOORS_TEST_CONFIRM_TARGET -ErrorAction SilentlyContinue
```

Oluşturma testi girişte yeniden preview çalıştırır ve fingerprint'i karşılaştırır; parametre, kimlik veya kontrol değiştiyse commit başlamaz. Parmak izi bir yetkilendirme sırrı değildir; kullanıcı tarafından incelenen planın eşleşmesini sağlar. DB ID/target/consent eksikse test başarısız olur ve yazma aşamasına geçmez. Sonraki doğrulama hatası modülün yaratılmadığı anlamına gelmez; hedefi ve dönen kimliği inceleyin, körlemesine yeniden çalıştırmayın.

## Manuel ve otomatik doğrulama

- Fake COM testleri: salt okunur preview, varsayılan/ret onayı, bilinmeyen veya başarısız kontroller, onaydan sonra DB/user/folder/yetki/ad/kilit değişimi, tek kullanım/replay, parametre escaping, bozuk yanıtlar, iptal/timeout sonrası gate ve geç sonuç.
- Gerçek test ortamında: preview öncesi/sonrası hiyerarşi aynı olmalı. Oluşturma yalnız onaydan sonra yapılmalı; dönen ID ve açıklama ayrı read-only doğrulamayla eşleşmeli. Mevcut çakışan adı preview reddetmeli; mevcut veriyi değiştirmemeli.
- Mevcut erişimsiz klasör, create yetkisi olmayan hesap ve önceden kilitli kapsamlarla salt okunur preview durumlarını gözleyin; test için ACL veya kilitleri bu servisle değiştirmeyin.
- 9.5 ve 9.7 için kurulu DXL Help'te imzaları kontrol edin ve onaylı testte derleme/çalıştırmayı doğrulayın. Fake testlerin geçmesi IBM DXL yorumlayıcısında derleme veya istemci sertifikasyonu değildir. Native create/save sırasında yerel DOORS trigger'ları çalışabilir; test ortamını buna göre seçin.

## API kaynakları

[IBM DXL Reference Manual](https://download.boulder.ibm.com/ibmdl/pub/software/rationalsdp/documentation/product_doc/DOORS/version_9-2/dxl_reference_manual.pdf) içindeki Module manipulation, Hierarchy information, Item access controls, Module information ve Locking bölümleri temel alındı. [9.7.2 kılavuzu](https://www.ibm.com/docs/en/SSYQBZ_9.7.2/com.ibm.doors.requirements.doc/topics/dxl_reference_manual.pdf) da sürüm karşılaştırma kaynağıdır. Bunlar COM'a yeni bir API üyesi eklemez; iletişim mevcut runStr ve Result üzerinden yapılır.
