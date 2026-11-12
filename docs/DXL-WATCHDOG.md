# DOORS DXL watchdog: otomatik Double execution time

DOORS'un DXL yürütme çevrimi sınırı ile uygulamanın COM sonucunu bekleme süresi farklıdır. IBM'in DXL Reference Manual belgesindeki `pragma runLim, cyc` çevrim sayısını belirler; `0` denetimi kapatır. `pragma runLim, 2000000` iki milyon çevrimlik bir sınır koyar, sonraki uyarıları otomatik yanıtlamaz. [IBM DXL Reference Manual](https://www.ibm.com/docs/en/SSYQBZ_9.7.2/com.ibm.doors.requirements.doc/topics/dxl_reference_manual.pdf).

## Settings: çevrim denetimini açma/kapatma

**Settings** sekmesindeki **Enable DXL execution-cycle watchdog (recommended)** kutusu ilk kullanımda seçilidir. Değiştirip **Save settings** düğmesine basın:

| Ayar | Sonraki entegrasyon DXL çağrısının başlangıcı |
| --- | --- |
| Açık (varsayılan) | `pragma runLim, 1000000` — sonlu 1.000.000 çevrim sınırı |
| Kapalı | `pragma runLim, 0` — çevrim uyarısı kaldırılır |

Seçim `%LOCALAPPDATA%\DoorsIntegration\settings.xml` dosyasında saklanır; aynı Windows kullanıcısının Desktop x86/x64 sürümleri bu tercihi kullanır. Kayıt yoksa veya dosya bozuk/okunamazsa denetim açık başlar; okuma sorunu Activity'ye kaydedilir. Kaydetme başarısızsa çalışan ayar değişmez. Normal kullanıcı yetkileri yeterlidir; uygulama klasörüne veya registry'ye yazılmaz.

Kaydetme COM bağlantısı kurmaz, DXL çalıştırmaz ve veritabanı değiştirmez. Aktif DOORS işlemi varken kontrol/kaydetme pasiftir. Executor ayarı operasyon başlangıcında yakalar; doğrudan API'den yapılan sonraki değişiklik mevcut scripti değiştirmez. Her yeni çağrıda sonlu/sınırsız pragma açıkça yazılır; kapalıdan açığa dönüş bir sonraki çağrıda sonlu sınırı geri getirir.

Ayar discovery, requirement/link okumaları, export'un bu okuyuculardan yaptığı okumalar, modül oluşturmanın önizleme/onaylı çağrıları ve sabit probe/health DXL betiğine uygulanır. Onay gereksinimleri değişmez. Requirement ve link kaynaklarındaki önceden sabit `runLim, 0` kaldırıldı; bunlar da merkezi seçimle hazırlanır. `RunString` ve `RunFile` aynı politikayı kullanır. Preview o anki probe ayarını gösterir.

Denetim kapalıyken hatalı bir script sonsuza kadar çalışabilir. Uygulamanın timeout/iptali başlamış COM çağrısını zorla durdurmaz; gate ve kaynaklar gerçek dönüşe kadar tutulur. Denetim kapatıldığında native Double seçeneği kapatılıp pasifleştirilir; yeniden açıldığında otomatik seçim kendiliğinden etkinleşmez.

Bu tercih entegrasyonun ürettiği script başlangıcını yönetir; DOORS'un genel ayar ekranına yazmaz. Doğrudan `DoorsComSession` kullanan kodlar ve DOORS editöründe manuel çalıştırılan scriptler bu hazırlıktan geçmez. Özel harici DXL/include/attribute kodu daha sonra kendi `runLim` pragmasını kullanırsa başlangıç politikasını değiştirebilir; ayar keyfi DXL üzerinde bir güvenlik sınırı değildir.

Console normalde denetim açık başlar ve Desktop tercih dosyasını otomatik okumaz. Özel DI hostu aynı `DxlWatchdogSettings` singleton'ını değiştirebilir; başlangıçta `Enabled = true` değerindedir.

## Desktop kullanımı

1. Güncel x86 veya x64 ZIP paketini tamamen yeni bir klasöre çıkarın. DOORS 9.5 veya 9.7 istemcisini açın ve oturum açın. Aynı Windows oturumunda tek `doors.exe` çalışmalı.
2. **Bind running DOORS...** düğmesine basın. Ekrandaki EXE yolu ve PID'nin kullanacağınız istemci olduğunu kontrol edin. Bağlama COM'a bağlanmaz ve script çalıştırmaz.
3. **Auto-select DOORS 'Double execution time'** kutusunu seçin ve entegrasyon işlemini başlatın. Bu ayrı seçenek varsayılan kapalıdır. Her uygulama açılışında yeniden bağlanıp seçilir.
4. Tanınan uyarıda yalnız Double seçeneği çalıştırılır. Her `runStr`/`runFile` çağrısında en fazla **4** otomatik seçim yapılır. Beşinci uyarı kullanıcıya bırakılır. Script veya veri yazma işlemi tekrar gönderilmez.
5. İşlem sürerken kutuyu kapatarak sonraki otomatik seçimleri durdurabilirsiniz. Yeniden açmak mevcut izleyiciyi canlandırmaz; sonraki DXL çağrısına uygulanır. İstemci yeniden başlatılırsa **Rebind DOORS** gerekir.

Uygulamanın sonuç bekleme süresi request üzerinden belirlenir. [Bekleme süreleri](TIMEOUTS.md).

## Tanıma ve sınırlar

- Yalnız bağlanmış sürecin PID + başlangıç zamanı + EXE yolu + Windows oturum kimliği eşleşirse işlem yapılır. İkinci bir DOORS istemcisi, erişilemeyen süreç bilgisi veya değişen süreç kimliği otomasyonu durdurur. COM API'sinden doğrulanmamış bir pencere/PID özelliği çağrılmaz. Bağlama kullanıcının hedef seçimidir; COM sunucusunun PID'sini kanıtlayan bir API olarak sunulmaz. Entegrasyon çalışırken aynı istemcide ayrıca manuel DXL çalıştırmayın.
- Windows UI Automation ile yalnız bu sürecin üst düzey pencereleri ve sınırlı büyüklükteki modal pencere içeriği okunur. Başlık tahmin edilmez: içerikte **DXL** işareti ve aynı türden tam üç eylem gerekir. Desteklenen İngilizce seçenekler Double execution time / Continue / Stop ile IBM belgesindeki Continue, doubling the timeout / Continue, with the same timeout / Halt execution karşılıklarıdır. Başka dil, farklı yapı, gizli/pasif kontrol, birden fazla eşleşme veya erişilemeyen UIA pattern varsa seçim yapılmaz.
- Double seçeneği UIA Button + Invoke veya RadioButton + SelectionItem olarak sunulmalıdır. RadioButton seçildikten sonra pencere kapanmıyorsa başka düğmeye basılmaz. Seçilen pencere yaklaşık 3 saniyelik gözlem boyunca kapanmazsa izleyici durur. Seçimin gerçekten yürütmeyi sürdürdüğü canlı istemcide doğrulanmalıdır.
- Giriş, yetki, modül oluşturma, kayıt, silme ve diğer onay pencereleri için genel Enter/Continue/OK otomasyonu yoktur. Mevcut modül oluşturma dry-run ve açık onay kuralları korunur. Çağrı başlamadan zaten açık olan tanınmış süre uyarısı devralınmaz.
- Aynı pencereye ikinci kez basılmaz; başka bir uzatma için önce önceki pencerenin kaybolduğu gözlenmelidir. Çok hızlı kapanıp açılan pencereler güvenli tarafta kalmak için atlanabilir. Hatalı/belirsiz UIA eylemi otomatik tekrarlanmaz.
- COM ve UIA çağrıları güvenle zorla iptal edilemeyebilir. UIA ayrı bir MTA arka plan iş parçacığında, COM kendi STA'sında çalışır. UIA sağlayıcısı takılırsa yeni izleyici biriktirilmez. Durdurma/ayar değişikliği sonraki eylemleri önler; çoktan sağlayıcıya gönderilmiş eylemi geri alamaz. Uygulama istemciyi öldürmez ve COM gate/workspace sahipliğini erken bırakmaz. [Microsoft UIA threading](https://learn.microsoft.com/en-us/dotnet/framework/ui-automation/ui-automation-threading-issues), [Invoke davranışı](https://learn.microsoft.com/en-us/dotnet/api/system.windows.automation.invokepattern.invoke?view=windowsdesktop-9.0).
- Uygulamanın soft timeout'u yalnız beklemeyi sonlandırır: izleyici, çağrı gerçekten dönene veya kullanıcı otomasyonu kapatana kadar kalabilir. Açık cancellation token iptali, soft timeout'tan sonra gelse de sonraki otomatik seçimleri durdurur. Bu, DXL scriptinin durduğu anlamına gelmez.

## Activity tanılaması

`DxlRunLimitClientBound` hedef PID'yi, `DxlRunLimitPolicyChanged` ayarı, `DxlRunLimitDoubleSelected` yapılan seçimi ve çağrı içindeki sayıyı kaydeder. Son olay UI seçiminin gönderildiğini belirtir; çevrim sınırının gerçekten değiştiğini veya scriptin başarıyla tamamlandığını kanıtlamaz. `DxlRunLimitPromptUnrecognized`, `DxlRunLimitAutomationUnavailable` veya `DxlRunLimitAutomationStopped` görüldüğünde DOORS penceresini manuel ele alın. Script gövdesi ve pencerenin veri içeriği bu olaylara yazılmaz.

## DI / özel host

Her iki host `DxlRunLimitAutomationSettings` ve `IDxlRunLimitAutomation` singleton'larını kaydeder. Constructor'lar masaüstünü taramaz. Console komutlarında otomatik bağlama/etkinleştirme yoktur; özel host açıkça ayarlayabilir:

```csharp
var settings = provider.GetRequiredService<DxlRunLimitAutomationSettings>();
settings.Bind(provider.GetRequiredService<IDoorsClientProcessLocator>().FindSingleClient());
// Kullanıcı hedef istemciyi kontrol edip bu davranışı seçtikten sonra:
settings.Enabled = true;
```

`DxlExecutor` tüm dosya/dize yürütmelerini, `ComDoorsDiagnostics` probe'u ve `DoorsHealthChecks` canlı DXL adımını kapsar. Eski constructor overload'ları ve doğrudan `new DoorsComSession()` çağrıları varsayılan olarak otomasyonsuz kalır. Portlar değiştirilebilir; fake backend testleri gerçek pencereye giriş göndermez.

## Canlı manuel doğrulama: 9.5 / 9.7 ve x86 / x64

Otomatik testler pencere modelini/fake backend'i doğrular. Bu geliştirme ortamında gerçek IBM DOORS penceresine basılmamış, DOORS'a bağlanılmamış ve veritabanına veri yazılmamıştır. Her istemci sürümü ve kullanılacak uygulama mimarisi için aşağıdaki matrisi uygulayın. DOORS ve uygulamayı aynı kullanıcı/masaüstü ve uygun aynı yetki düzeyiyle açın; uygulama kendini yükseltmez veya UI korumalarını aşmaz.

1. **Kapalı/offline:** yeni uygulamada native kutu kapalı/pasif olmalı. Bind sonrasında kutu seçilmeden işlem normal kalmalı. Preview hiçbir DOORS çağrısı yapmamalı.
2. **İlk gerçek pencere:** yalnız sonlu, veritabanına erişmeyen DXL ile laboratuvar ortamında deneyin. Örnek aşağıdadır. Entegrasyonun `DxlExecutor` API'sinden dosya olarak çalıştırın; Desktop şu anda keyfi script çalıştırma ekranı sunmaz. Aynı anda DXL editöründe çalıştırmak izleyici kapsamına girmez.
3. **İzleme:** native kutuyu açıp `RunFile` ve sonra `RunString` ile deneyin. Uyarı seçenekleri/pattern/modal bilgileri destekleniyorsa Double seçilmeli, başka düğmeye basılmamalı; Activity'de sayı artmalı. Yapı tanınmıyorsa manuel uyarı kalmalı. Gerçek başlık/etiket/pattern bilgilerini bu durumda kaydedin.
4. **Sonlu limit:** örnek script dört seçimden sonra da uyarı üretebilir. Beşinci uyarıya otomatik basılmamalı; manuel Halt/Stop seçin. Sonsuz döngü veya `runLim,0` kullanmayın.
5. **İptal:** bir sonraki uyarı gelmeden kutuyu kapatın; daha sonra otomatik seçim olmamalı. API cancellation token ve soft timeout sonrasında COM gate'in gerçek dönüşe kadar tutulduğunu doğrulayın. Tekrar işlem göndermeyin.
6. **Yanlış hedef:** ikinci istemciyi açın veya bağlı istemciyi yeniden başlatın; otomasyon durmalı ve manuel uyarı bırakılmalı. Rebind yeni kimliği almalı ve kutuyu kapalıya çevirmeli.
7. **Diğer pencereler:** normal oturum açma veya kaydetme penceresine otomasyon giriş göndermemeli. Bunun için gerçek veritabanı değişikliği başlatmayın. Gerçek yazma entegrasyon testleri bu özelliğin doğrulaması için gerekli değildir.

```dxl
// Yalnız sonlu hesaplama; veritabanı okuma/yazma primitifi yoktur.
pragma runLim, 10000
int watchdogProbeCounter = 0
while (watchdogProbeCounter < 200000) {
    watchdogProbeCounter++
}
oleSetResult("DOORS_RUNLIMIT_READONLY|OK")
```

Bu örnek IBM yorumlayıcısında burada çalıştırılmadı. Gerçek destek 9.5/9.7 istemcilerinin UIA ağacına bağlıdır; sürüm profili seçmek pencere uyumluluğunu kanıtlamaz.
