# DoorsComSession ve COM tanılama

`DoorsIntegration.Com/DoorsComSession.cs`, `DOORS.Application` için late binding oturumunu yönetir. `doors.tlb` veya derleme zamanında IBM kurulumu gerekmez. `IDoorsComSessionFactory`, kayıt denetimi ve COM runtime adaptörü ayrı test noktalarıdır.

## Doğrulanan API adları

IBM'in [DXL Reference Manual, 9.7.2](https://www.ibm.com/docs/en/SSYQBZ_9.7.2/com.ibm.doors.requirements.doc/topics/dxl_reference_manual.pdf) belgesindeki **Controlling DOORS from applications that support automation / Automation interface / runStr** bölümleri temel alınmıştır:

| Üye | Kullanım |
| --- | --- |
| `DOORS.Application` | Belgelenmiş automation nesnesi / ProgID |
| `runStr(dxlText)` | DXL string'ini çalıştıran metot; sonucu metot dönüşünden okumayız |
| `runFile(dxlFileName)` | Mutlak yoldaki UTF-8 DXL dosyasını çalıştıran metot |
| `Result` | Read-write automation sonuç property; executor PENDING yazar ve sonucu okur |
| `oleSetResult(string)` | Sabit tanılama DXL'inin yanıt yazdığı DXL fonksiyonu; COM metodu değildir |

Kod `Connect`, `Login`, `Version`, `Visible` veya `Quit` gibi doğrulanmamış DOORS COM üyeleri çağırmaz. `runFile`, genel [DxlExecutor](DXL-EXECUTOR.md) katmanında UTF-8 staging dosyasıyla kullanılır; sabit probe `runStr` kullanır. Desteklenmeyen dispatch adı için `ComApiUnavailable` döner; farklı isimler denenmez.

9.5 ve 9.7 hedef profilleri aynı temel protokolü kullanır. Bu çalışmada sürüme özel 9.5 PDF adreslerine erişilemedi; 9.5 istemcisinin Help menüsündeki DXL Reference Manual ve gerçek 9.5/9.7 istemcileriyle saha doğrulaması hâlâ gereklidir. Paket testleri gerçek DOORS sürüm uyumluluğu sertifikası değildir.

## Bağlantı davranışları

| Mod | Windows çağrısı | Davranış |
| --- | --- | --- |
| `Activate` | `Type.GetTypeFromProgID` + `Activator.CreateInstance` | Kayıtlı automation sunucusunu aktive eder; Windows/DOORS mevcut sunucuyu kullanabilir veya istemci başlatabilir. Yeni ve ayrı bir süreç garantisi vermez. |
| `AttachExisting` | `Marshal.GetActiveObject` | Yalnızca Windows Running Object Table içindeki erişilebilir nesneye bağlanır; aktivasyon veya başlatma fallback'i yoktur. |

[Microsoft GetActiveObject belgesi](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.marshal.getactiveobject?view=netframework-4.8) bu işlemin ROT sorgusu olduğunu tanımlar. Çalışan bir DOORS penceresi, erişilebilir ROT nesnesi bulunduğunu garanti etmez. Kullanıcı/masaüstü/yetki farkı veya istemcinin ROT davranışı nedeniyle attach başarısız olabilir. Bu durumda aktivasyonun istenip istenmediğini çağıran taraf açıkça seçer.

Her iki modda da önce işlemin x86/x64 registry görünümündeki `HKCR\DOORS.Application\CLSID` ve ilgili CLSID sınıf kaydı salt okunur denetlenir. Eksik/geçersiz/okunamayan kayıtta COM çözümlemesi yapılmaz. Kayıt oluşturma, onarma veya sürüm değiştirme işlemi yoktur. Bu sıra önemlidir: [CLSIDFromProgID](https://learn.microsoft.com/en-us/windows/win32/api/combaseapi/nf-combaseapi-clsidfromprogid), bulunamayan ProgID için eski OLE1 kayıtları oluşturabilir; offline tanılama bu çözümleme API'lerini hiç çağırmaz.

## STA ve yaşam döngüsü

`DoorsComSession` oluşturulması, `RunString`, `ReadResult` ve canlı referansın `Dispose` işlemi aynı STA iş parçacığında yapılır. MTA veya farklı STA çağrısı `InvalidThread` ile reddedilir. Oturum iş parçacığı değiştirecek şekilde `await` sınırlarından taşınmamalıdır. Desktop host, oluşturma/çalıştırma/bırakma işlemlerinin tamamını tek STA worker delegesinde yürütür; Console giriş noktası `[STAThread]` kullanır.

```csharp
// Bu kodun tamamı aynı STA iş parçacığında çalışmalıdır.
using (var session = new DoorsComSession(DoorsConnectionMode.AttachExisting))
{
    // Üst düzey ComDoorsDiagnostics, sabit ve salt okunur DXL protokolünü kullanır.
}
```

`Dispose` idempotent'tir. Alınan RCW referansı bir kez `Marshal.ReleaseComObject` ile bırakılır. `FinalReleaseComObject`, zorlanmış GC, istemci kapatma veya süreç öldürme kullanılmaz. Dispose sırasında bir COM hatası oluşursa türlenmiş hata döner; ikinci Dispose tekrar release yapmaz. Bir tanılama zaten başarısız olmuşsa cleanup hatası asıl hatanın üzerini örtmez.

`RunString` düşük seviyeli bir DXL API'sidir ve kendi başına bir DXL güvenlik sandbox'ı değildir. Uygulamanın CLI ve GUI akışları yalnızca gözden geçirilmiş sabit tanılama betiğini kullanır. Kullanıcıdan serbest script veya dosya yürütme girdisi alınmaz; veritabanı yazma kullanım senaryosu eklenmedi.

Genel executor, `IDxlExecutionSession` portu üzerinden `RunString`, `RunFile`, `SetResult` ve `ReadResult` çağırır. `ComDxlSessionFactory` mevcut COM factory'sine adaptördür. Default probe ve executor aynı process içi `DoorsOperationGate.Shared` nesnesini paylaşır. Düşük seviye session doğrudan kullanıldığında bu gate çağıranın sorumluluğundadır.

## Tanılama komutları

```powershell
$x86 = '.\src\DoorsIntegration.Console\bin\x86\Release\net48\DoorsIntegration.Console.exe'
$x64 = '.\src\DoorsIntegration.Console\bin\x64\Release\net48\DoorsIntegration.Console.exe'

# Offline: istemci başlatmaz, ROT veya DXL çağırmaz.
& $x86 --diagnose-com
& $x64 --diagnose-com
& $x86 --diagnose-compatibility --target 9.5
& $x64 --diagnose-compatibility --target 9.7

# Yalnızca uygun test istemcisinde kullanıcı tarafından başlatılacak bağlı kontroller:
& $x86 --probe --connection attach --target 9.5
& $x64 --probe --connection activate --target 9.7
```

`--diagnose-com` registry durumunu; `--diagnose-compatibility` buna ek olarak seçili hedefle kayıtlı sunucu dosyasının major/minor sürüm karşılaştırmasını değerlendirir. Her iki komut da OS/işlem mimarisi, çağıran apartment, **iki registry görünümü**, CLSID, kayıtlı EXE yolu, dosya sürümü, PE machine ve MATCH/MISMATCH/UNKNOWN raporlar. Çıkış kodu işlemin kendi registry görünümüne göre belirlenir; diğer görünüm de bilgi olarak gösterilir.

`LocalServer32\ServerExecutable` varsa önceliklidir. Yoksa yalnızca açıkça tırnaklanmış veya boşluksuz EXE yolu ayrıştırılır. Argümanlar raporlanmaz, loglanmaz ve çalıştırılmaz. Boşluk içeren tırnaksız yollar tahmin edilmez. Ortam değişkenleri mimariler arasında tahmini genişletilmez; UNC, göreli veya çözümlenemeyen yollarda dosya sürümü UNKNOWN kalır. Registry okuyucu yalnızca okuma erişimi açar. Sunucu dosyası yalnızca sürüm bilgisi ve PE başlığı için okunur; çalıştırılmaz.

Registry görünümü sunucunun gerçek bitliğini belirlemez. Microsoft'un [LocalServer32 belgesi](https://learn.microsoft.com/en-us/windows/win32/com/localserver32) yerel sunucunun farklı mimaride olabileceğini açıklar. PE machine ayrı bir dosya göstergesidir. Dosya sürümü eşleşmesi, aktif oturum sürümünü veya canlı COM/DXL uyumluluğunu kanıtlamaz. Sürüme özel ProgID, otomatik registry değişikliği veya doğrulanmamış `Version` property çağrısı kullanılmaz.

## Hata kodları

| Kod | Anlam / örnek HRESULT |
| --- | --- |
| 10 | Automation nesnesi alınamadı |
| 11 | Diğer COM hatası; ör. sunucu aktivasyon hatası |
| 13 | STA veya iş parçacığı sahipliği ihlali |
| 14 | COM kaydı eksik/geçersiz; `REGDB_E_CLASSNOTREG`, `CO_E_CLASSSTRING` |
| 15 | Erişilebilir aktif ROT oturumu yok; `MK_E_UNAVAILABLE` |
| 16 | Belgelenmiş automation üyesi yok veya `Result` türü beklenenden farklı; `DISP_E_MEMBERNOTFOUND`, `DISP_E_UNKNOWNNAME` |
| 17 | Erişim reddi; `E_ACCESSDENIED` |
| 18 | Meşgul / çağrı reddedildi; `RPC_E_CALL_REJECTED`, `RPC_E_SERVERCALL_RETRYLATER` |
| 19 | Bağlantı koptu / RCW geçersiz; `RPC_E_DISCONNECTED`, RPC sunucusu kullanılamıyor |
| 20 | Registry erişimi başarısız |
| 21 | Uyumluluk komutunda dosya sürümü belirlenemedi |
| 22 | Uyumluluk komutunda kayıtlı sunucu dosyasının major/minor sürümü hedefle eşleşmiyor |

Reflection kaynaklı `TargetInvocationException` açılır, asıl exception ve HRESULT korunur. Kullanıcıya dış exception metni veya script içeriği verilmez. Meşgul istemciye otomatik retry yapılmaz; COM çağrısı senkron olduğu için zorla iptal/timeout vaadi verilmez.

## Test sınırı

Aktivasyon, attach, başarısız attach sırasında aktivasyon yapılmaması, kayıt hataları, MTA/farklı STA, üye adları, HRESULT çevirisi, tek release ve dispose hatası senaryoları test çiftleriyle doğrulanır. Registry görünümleri ve dosya meta verisi enjekte edilebilir adaptörlerle test edilir. Windows üzerinde gerçek offline komutlar ayrıca çalıştırılır. Gerçek DOORS başlatma/bağlanma ve veritabanı işlemi bu geliştirme sırasında yapılmaz.
