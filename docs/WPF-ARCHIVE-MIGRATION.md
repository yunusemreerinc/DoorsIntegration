# Tek aktif masaüstü uygulaması

Aktif istemci `src/DoorsIntegration.Wpf/DoorsIntegration.Wpf.csproj` projesidir. Ana `DoorsIntegration.sln` WinForms uygulamasını içermez; aktif test projesi de eski EXE'ye referans vermez. WPF/MVVM, Core, COM/DXL, Processing, Export ve CLI servisleri korunmuştur.

## Dizinler ve paketler

| Konum | İçerik |
|---|---|
| `src/DoorsIntegration.Wpf` | Tek aktif masaüstü uygulaması |
| `artifacts/releases/current/DoorsIntegration-Wpf-x64.zip` | Güncel x64 dağıtımı |
| `artifacts/releases/current/DoorsIntegration-Wpf-x86.zip` | Güncel x86 dağıtımı |
| `archive/WinForms/src/DoorsIntegration.Desktop` | Eski WinForms kaynakları |
| `archive/WinForms/tests/DoorsIntegration.Legacy.Tests` | İsteğe bağlı legacy test projesi ve 16 özgün test dosyası snapshot'ı |
| `archive/WinForms/DoorsIntegration.Legacy.sln` | Ayrı, isteğe bağlı arşiv solution'ı |
| `archive/releases` | Önceki tarihli ve yinelenen Desktop/WPF/WinForms dağıtımları |

`Package-Desktop.ps1` alias betiği ve eski `Package-WinForms.ps1` aktif scripts dizininden kaldırılıp tarihsel dosya olarak arşivlenmiştir. Aktif GUI paketleme komutu yalnızca `scripts/Package-Wpf.ps1`'dir; varsayılan çıktısı `artifacts/releases/current` olur. ZIP'ten `DoorsIntegration.Wpf.exe` veya `DoorsIntegration.cmd` çalıştırılır. x86 ve x64 aynı ürünün mimari seçenekleridir.

## Korunan içerik

- WinForms kaynakları silinmedi. Settings, watchdog tercih kodu ve eski ikon arşivde korunur; kullanıcı tercih/veri dizinleri taşınmadı veya değiştirilmedi.
- WPF'nin ikon kaynağı kendi `Assets` dizinine byte-for-byte kopyalandı. Eski WinForms projesine veya arşive build/runtime bağımlılığı yoktur.
- WinForms'a özgü altı test dosyası arşivdedir. Ortak dosyalardaki WinForms varyantlarının özgün halleri de arşiv snapshot'larında tutulur. Core/CLI testleri aktif projede kalır; WPF testleri backend fixture'larını artık bağımsız `WpfServiceTestDoubles.cs` üzerinden kullanır.
- Önceki release ZIP'leri ve içlerindeki dosyalar yeniden yazılmadan `archive/releases` altına taşındı. Geçmiş test kanıtları `artifacts` altında tarihsel kayıt olarak kalır; içlerindeki eski yollar o teslimin kayıtlarıdır.
- DOORS verisi değiştirilmedi; mevcut read-only default, write approval, timeout ve reconciliation davranışları değişmedi.

## Build ve doğrulama

Aktif solution için `scripts/Build.ps1 -Platform x64` veya x86; yalnız paket için `scripts/Package-Wpf.ps1 -Platform x64 -SkipBuild` kullanılır. Bu makinenin SDK seçimiyle komutlar `artifacts/SmokeBuildHost` dizininden çalıştırılmıştır. Gerçek DOORS testleri disabled/NOT_RUN kalır.

Aktif x86/x64 restore/build ve tam regresyon kanıtları `artifacts/ArchiveMigration/active-x86-final` ve `active-x64-final` altındadır. Arşiv WinForms testlerinin ayrı TRX kayıtları `artifacts/ArchiveMigration/LegacyTests` altında tutulur. Test sayısı önceki teslimden farklıdır çünkü WinForms vakaları aktif test kümesinden arşive ayrılmıştır; başarısız veya atlanan test gizlemek için filtrelenmemiştir.

Her mimaride tam koşu 1568 PASS / 0 FAIL / 45 NOT_RUN verdi. Ayrıca iki audit-cache recovery varyantı yeniden çalıştırıldı (`recovery-x86` / `recovery-x64`); bunlardan biri tam koşudaki vakayla ortaktır. Tekilleştirilmiş aktif kapsam her mimaride **1569 PASS / 0 FAIL / 45 NOT_RUN** olur. Bu toplam iki koşunun PASS sayılarını körlemesine toplamaz. Gerçek DOORS vakaları NOT_RUN kalır.

Arşivdeki WinForms ekran testleri 55 PASS, eski ortak-host/settings varyantları 33 PASS verdi. İlk x64 koşusunda timeout testinin geç temizliğe karşı eksik beklemesi görüldü; assertion dosyanın değil tüm çalışma klasörünün temizlenmesini bekleyecek şekilde güçlendirildi. İlk başarısız koşu da kanıt klasöründe korunur. Ürün COM davranışına değişiklik yapılmadı.

WinForms arşivinin isteğe bağlı build/test/paketleme yönergeleri: [archive/WinForms/README.md](../archive/WinForms/README.md).
