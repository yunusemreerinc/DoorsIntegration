# DxlExecutor

`DoorsIntegration.Dxl.DxlExecutor`, harici UTF-8 DXL kaynaklarını COM üzerinden `runStr` veya `runFile` ile yürütür. Dxl projesi yalnızca Core'a bağlıdır; `IDxlExecutionSession` / `IDxlSessionFactory` portlarını Com projesindeki adaptör sağlar. Console ve Desktop DI kayıtlarında `IDxlExecutor` singleton olarak hazırdır. Servis çözümlemek script çalıştırmaz.

Bu geliştirmede gerçek DOORS üzerinde script çalıştırılmadı ve veritabanı verisi değiştirilmedi. Genel amaçlı executor bir DXL sandbox'ı değildir: yüklenen kaynak güvenilir ve gözden geçirilmiş olmalıdır. Desktop **Workspace → Run DXL**, yakalanmış kaynak/parametrelerin ayrı açık onayından sonra executor'ü çalıştırır; custom DXL veri değiştirebilir. Sabit probe/CLI tanılama salt okunurdur. [Desktop kaynak yakalama, sonuç ve timeout kullanımı](DESKTOP-WORKSPACE.md).

## API ve dosya yükleme

Executor her operasyon başlangıcında ortak `DxlWatchdogSettings.Enabled` değerini yakalar; `DxlExecutionOperation.WatchdogEnabled` bu snapshot'ı gösterir. UTF-8 pragma'sından sonra varsayılan `pragma runLim, 1000000`, ayar kapalıysa `pragma runLim, 0` yazılır. İki dispatch metodu aynı script hazırlığını kullanır. Desktop tercihi Settings sekmesinden saklanır; eski constructor overload'ları ve Console varsayılan denetim açık başlar. Ayar, COM sonucunu bekleme süresi ve yazma onayı kurallarını değiştirmez. [Ayar ve sınırlamalar](DXL-WATCHDOG.md).

IBM'in [DXL Reference Manual](https://www.ibm.com/docs/en/SSYQBZ_9.6.0/com.ibm.doors.requirements.doc/topics/dxl_reference_manual.pdf), `runStr(dxlText)` ve `runFile(dxlFileName)` üyelerini belgeler; `runFile` için tam yol ve UTF-8 içerik gereklidir. [Automation sonuç sözleşmesi](https://www.ibm.com/docs/en/SSYQBZ_9.7.2/com.ibm.doors.requirements.doc/topics/dxl_reference_manual.pdf), read-write `Result` property ile `oleGetResult()` / `oleSetResult(string)` fonksiyonlarını tanımlar.

- `RunString`: harici dosya yüklenir; parametreler ve tamamlanma protokolü eklenmiş snapshot `runStr` ile gönderilir.
- `RunFile`: aynı snapshot işleme özel `prepared.dxl` dosyasına BOM'suz UTF-8 yazılır; orijinal kaynak yerine bu dosyanın mutlak yolu `runFile` metoduna verilir.
- Kaynak UTF-8 olmalıdır; UTF-8 BOM kabul edilir. Geçersiz UTF-8, UTF-16/NUL, boş kaynak ve boyut aşımı COM bağlantısından önce hata verir.
- Kaynak ve parametreler snapshot olarak kullanılır; orijinal dosya değiştirilmez. Staging nedeniyle göreli `#include` veya çalışma dizini varsayımları korunmaz. Script bağımlılıklarını hedef ortamda açık ve gözden geçirilmiş yollarla yönetin.

## Küçük sonuç örneği

Kaynak dosya: `scripts/dxl/echo.dxl`:

```dxl
oleSetResult(di_arg_message)
```

Uygulama hostunda (bu çağrı gerçek DOORS'a bağlanır; burada çalıştırılmadı):

```csharp
var executor = provider.GetRequiredService<IDxlExecutor>();
var request = new DxlExecutionRequest(
    scriptPath: @"scripts\dxl\echo.dxl",
    method: DxlInvocationMethod.RunString,
    parameters: new Dictionary<string, string> { ["message"] = "Hello \"DOORS\"\n" },
    timeout: TimeSpan.FromSeconds(30),
    connectionMode: DoorsConnectionMode.AttachExisting,
    target: DoorsVersion.V97);

using (var result = await executor.ExecuteAsync(request))
{
    string text = result.Text;
}
```

`provider`, mevcut uygulamanın DI provider'ıdır; örnekte `DoorsIntegration.Core`, `DoorsIntegration.Dxl`, `Microsoft.Extensions.DependencyInjection`, `System` ve `System.Collections.Generic` ad alanları kullanılır. Doğrudan kurulum için `DxlExecutor` constructor'ına session factory, script loader, workspace factory, logger ve paylaşılan operation gate verilebilir. Varsayılan bağlantı modu `AttachExisting`'dir; otomatik aktivasyon isteniyorsa açıkça `Activate` seçilmelidir.

## Güvenli parametreler ve script sözleşmesi

| Context değişkeni | Anlam |
| --- | --- |
| `di_arg_<name>` | Çağıranın string parametresi |
| `di_requestId` | Güncel işlem kimliği |
| `di_outputPath` | İşleme özel büyük sonuç dosyasının yolu |
| `di_error` | Başlangıçta boş; script kontrollü hata bildirmek için doldurabilir |

Parametre adı ASCII harfle başlar, en fazla 64 karakterdir ve yalnızca harf/rakam/alt çizgi içerir. Adlar doğrudan script kodu veya dosya yolu olarak yorumlanmaz; `di_arg_` önekiyle değişken olur. `di_` ad alanı context için ayrılmıştır; script bu tanımları yeniden bildirmemelidir.

Değerler `DxlStringLiteral.Encode` üzerinden string literal olur. Tırnak, ters eğik çizgi, CR/LF ve tab escape edilir. NUL, desteklenmeyen kontrol/Unicode satır ayırıcıları ve geçersiz UTF-16 reddedilir. `\u` gibi DXL için doğrulanmamış kaçışlar üretilmez. Parametreler kod birleştirme, `#include`, shell argümanı veya string replace şablonu olarak kullanılmaz. Unicode korunmasının gerçek istemcideki görüntüleme/kod sayfası davranışı hedef DOORS sürümünde ayrıca doğrulanmalıdır.

Script sonuç üretmek için `oleSetResult(value)` çağırır. Executor önce `Result` alanına güncel PENDING işaretini yazar. Script normal sona ulaştığında eklenen footer, payload'ı istek kimliği taşıyan bir tamamlanma zarfına dönüştürür. Footer normal çalışmış fakat script bir değer üretmemişse boş inline sonuç döner. Eski `Result`, PENDING, başka istek kimliği, footer'a ulaşmadan `halt` veya DXL derleme/çalışma hatası başarı sayılmaz.

Kontrollü uygulama hatası için `di_error = "ERROR_CODE"` atayın ve normal sona ulaşın. `DxlScriptException.ScriptError` ham hata bilgisini isteyen çağırana verir; exception Message ve otomatik loglar bu içeriği taşımaz. Örnek: `scripts/dxl/reported-error.dxl`. COM bazı DXL hatalarında exception üretmeyebilir; tamamlanma işareti yoksa `DxlProtocolFailure` döner. Tam DXL derleyici çıktısı için istemcinin DXL çıkışı incelenmelidir; doğrulanmamış bir COM hata property’si kullanılmaz.

## Büyük sonuç: dosya ve stream

`scripts/dxl/file-result.dxl`, `di_outputPath` konumuna yazar ve stream'i kapatır. Kaynak için `resultMode: DxlResultMode.File` seçin:

```csharp
var request = new DxlExecutionRequest(
    scriptPath: @"scripts\dxl\file-result.dxl",
    method: DxlInvocationMethod.RunFile,
    resultMode: DxlResultMode.File,
    maxResultFileBytes: 512L * 1024 * 1024);

using (var result = await executor.ExecuteAsync(request))
using (var input = result.OpenResultStream())
using (var destination = File.Create(@"C:\Exports\output.dat"))
{
    await input.CopyToAsync(destination);
}
```

Hedef klasör çağıran tarafından hazırlanmalıdır. Sonuç belleğe tamamen yüklenmez. `ResultFilePath` / `ResultFileBytes` meta verisi ve `OpenResultStream()` sağlanır; `Text` null'dır. Dosya formatını ve kodlamasını DXL scripti belirler. Örnek yalnızca ASCII yazar; executor dosyayı kendiliğinden UTF-8 veya JSON varsaymaz.

COM `Result` içinden dosya yolu alınmaz: yalnızca sabit tamamlanma token'ı kabul edilir ve **executor'ın oluşturduğu** `di_outputPath` okunur. Yanlış token, eksik/bağlı dosya, açık yazıcı nedeniyle erişim hatası veya boyut aşımı hata verir. Sonuç alınırken dosyaya read lease açılır; sonuç nesnesi yaşadığı sürece dosya değiştirilmesi engellenir. Önce açtığınız stream'leri, sonra `DxlExecutionResult` nesnesini dispose edin. Result dispose, kendine ait workspace dosyalarını siler. Sonucu saklamak için dispose öncesinde başka bir konuma kopyalayın.

## Timeout, iptal ve geç tamamlanma

**Timeout, başlamış COM çağrısını durdurduğu anlamına gelmez.** Worker thread abort edilmez; süreç öldürülmez; RCW başka thread'den release edilmez. Varsayılan timeout yalnızca bekleme sınırıdır.

`Start(request)` işlem handle'ını hemen döndürür; kendiliğinden timeout uygulamaz. `ExecuteAsync` bunun üzerine `request.Timeout` ile bekler. İki API aynı gerçek tamamlanma task'ını kullanır:

```csharp
var operation = executor.Start(request);
try
{
    using (var result = await operation.WaitAsync(request.Timeout, cancellationToken))
    {
        // Sonuç veya dosya stream'i burada tüketilir.
    }
}
catch (DxlExecutionWaitException waitFailure)
{
    // Code: DxlTimeout veya DxlWaitCancelled.
    // UI beklemeden çıkabilir; worker ve COM temizliği hâlâ sürüyor olabilir.
    // Aynı handle daha sonra gerçek sonucu veya geç hatayı verir:
    try
    {
        using (var lateResult = await waitFailure.Operation.Completion)
        {
            // Gerekiyorsa geç sonucu burada tüketin.
        }
    }
    catch (IntegrationException lateFailure)
    {
        // Geç COM/DXL/cleanup hatasını merkezi hata politikanıza iletin.
    }
}
```

`ExecuteAsync` token zaten iptal edilmişse worker başlamadan normal `OperationCanceledException` verir. Başladıktan sonra iptal/timeout `DxlExecutionWaitException` ile handle'ı korur; `Completion` task'ı zorla canceled durumuna geçirilmez. Worker, bekleme sona erdiğini kaynak hazırlığı ve dispatch öncesindeki güvenli kontrol noktalarında görürse henüz gönderilmemiş scripti atlar. Kontrol noktası ile çağrı arasında yarış olabileceğinden bir timeout sonrasında scriptin çalışmadığı varsayılamaz.

COM bağlantısı, PENDING yazması, çalıştırma, Result okuması ve release tek worker STA üzerinde kalır. COM dönüşü ve temizliği bitene kadar gate tutulur. Timeout sonrası yeni script/probe başlatma `DoorsOperationBusy` verir. `prepared.dxl` ve çalışma dosyaları aktif COM tüketicisi varken silinmez. Geç başarı/hata loglanır; geç fault task'ı gözlenir ve `Completion` üzerinden tekrar await edilebilir.

Default DI kayıtları ve sabit `ComDoorsDiagnostics`, aynı process içi `DoorsOperationGate.Shared` nesnesini kullanır. Özel hostlar bütün COM tüketicilerinde aynı gate'i paylaşmalıdır. Düşük seviye `DoorsComSession` doğrudan kullanımı veya başka bir süreç bu gate'in dışındadır; aynı DOORS oturumunun global `Result` alanını eşzamanlı kullanmayın. Protokol bu tür bütün dış yarışları önlediği iddiasında değildir.

Kapanışta host `ActiveOperation?.Completion` bitmeden provider'ı/log sink'ini veya ilgili dosyaları kapatmamalıdır. Bir COM çağrısı hiç dönmezse Completion ve gate de beklemede kalır. İşletim sistemi düzeyinde kesin süre sınırı gereken ürünler için ayrı worker process ve belirsiz yürütme sonucu politikası gerekir; bu sürüm zorla iptal uygulamaz. COM'a gönderilmiş bir script hata/timeout verse bile kısmen çalışmış olabilir; otomatik retry veya rollback yapılmaz.

## Workspace, limitler ve loglama

Default workspace `%TEMP%\DoorsIntegration\Dxl\<request-id>` altındadır. Root `DxlWorkspaceFactory(root)` ile değiştirilebilir. Küçük sonuç ve hata yollarında worker kendi dosyalarını COM cleanup sonrasında temizler. Dosya sonuçlarının sahipliği `DxlExecutionResult`'a geçer; bu dosyalar result dispose edilene kadar yaşayabilir. Yalnızca `prepared.dxl`, `result.dat` ve boş işlem dizini silinir; scriptin oluşturduğu bilinmeyen dosyalar recursive silinmez. Cleanup hatası asıl hatayı örtmez; `WorkspaceDirectory` tamamlanan handle'da inceleme için kalır. Doğrulanmış tamamlanma yanıtı alındıktan sonra oluşan COM release veya workspace cleanup hataları sonucu başarısızlığa çevirmez: `DxlCleanupFailed` (`ResultAcknowledged=True`) olarak loglanır ve sonuç döner. Böylece DOORS'un tamamlayıp onayladığı bir yazma, yalnızca geçici dosya temizliği nedeniyle başarısız veya `OutcomeUnknown` raporlanmaz.

| Limit | Varsayılan |
| --- | --- |
| Kaynak dosya | 1 MiB; en fazla 16 MiB yapılandırılabilir |
| Inline sonuç | 65.536 UTF-16 karakter |
| Dosya sonucu | 256 MiB; çağıran değiştirebilir |
| Parametre | 128 adet, değer başına 65.536 ve toplam 262.144 karakter |
| Bekleme | 30 saniye; 1 ms–24 saat aralığında |

Inline boyut kontrolü COM BSTR marshaling'inden sonra yapılabilir; dev payload'ı `Result` üzerinden gönderip sonra limitin belleği korumasını beklemeyin. Büyük payload script tarafından doğrudan dosyaya yazılmalıdır.

Loglar istek kimliği, metot, mod, hedef profil, süre, hata kodu/tipi ve HRESULT içerir. Script metni, dosya yolu, parametre değerleri, ham Result ve `di_error` içeriği loglanmaz. Bir log sink'i hata verirse worker/gate/COM cleanup engellenmez. Workspace dosyaları script/parametre/sonuç içerebilir; logların aksine bu dosyalar hassas veriler taşıyabilir. Root erişim izinleri host hesabının sorumluluğundadır.

| Kod | Anlam |
| --- | --- |
| 12 | Tamamlanma protokolü geçersiz/eksik |
| 23 | Bekleme timeout'u |
| 24 | Başlamış işlemde bekleme iptali veya dispatch öncesi güvenli durma |
| 25 | Önceki DOORS işlemi/temizliği sürüyor |
| 26 | Script yükleme, kodlama veya boyut hatası |
| 27 | Inline/dosya sonucu boyut sınırı |
| 28 | Workspace/sonuç dosyası/cleanup hatası |
| 29 | Script `di_error` ile kontrollü hata bildirdi |

Mevcut COM HRESULT sınıflandırması ve merkezi `IntegrationErrorHandler` kullanılmaya devam eder. Testler gerçek DOORS yerine session/loader/gate adaptörlerini kullanır. Üretilen DXL'in 9.5/9.7 motorunda derlenmesi ve canlı COM davranışı hedef ortamda ayrıca doğrulanmalıdır; [doğrulama kaydı](VALIDATION.md) bu sınırı ve güncel test sonuçlarını belirtir.
