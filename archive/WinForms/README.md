# Arşivlenen WinForms istemcisi

`DoorsIntegration.Desktop` eski Windows Forms arayüzüdür. Kaynakları, settings, watchdog tercihleri ve ikon dosyaları bu klasörde korunur. Aktif `DoorsIntegration.sln` ve `DoorsIntegration.Tests` bu projeye referans vermez. Güncel uygulama `DoorsIntegration.Wpf`'tir.

## İsteğe bağlı build/test

Repo `artifacts/SmokeBuildHost` dizininden, uygun .NET SDK ve paket cache'i ile:

```powershell
dotnet restore ../../archive/WinForms/tests/DoorsIntegration.Legacy.Tests/DoorsIntegration.Legacy.Tests.csproj -p:Platform=x64 --packages ../../.packages --configfile ../../NuGet.Config --ignore-failed-sources
dotnet build ../../archive/WinForms/tests/DoorsIntegration.Legacy.Tests/DoorsIntegration.Legacy.Tests.csproj -c Release -p:Platform=x64 --no-restore
$env:DOORS_TEST_MODE='disabled'
dotnet test ../../archive/WinForms/tests/DoorsIntegration.Legacy.Tests/DoorsIntegration.Legacy.Tests.csproj -c Release -p:Platform=x64 --no-build --no-restore --filter 'FullyQualifiedName~DesktopTests|FullyQualifiedName~DesktopWorkspaceTests|FullyQualifiedName~DiscoveryDesktopTests|FullyQualifiedName~LinkWriteDesktopTests|FullyQualifiedName~ProcessingDesktopTests' -- RunConfiguration.TargetPlatform=x64
```

WinForms'a özgü altı dosya ve ortak testlerdeki eski WinForms varyantları toplam 16 özgün dosyanın `Snapshots` kopyalarında korunur. `original-test-snapshots.json` taşınmadan önceki SHA256 değerlerini içerir. Legacy test projesi bu snapshot'ları ve aktif testlerdeki ortak backend fixture'larını kaynak olarak bağlar. Aktif test projesinin EXE bağımlılığı yoktur. Legacy test assembly adı, mevcut friend-access sözleşmeleri için `DoorsIntegration.Tests` olarak korunur; çıktısı ayrı arşiv klasöründedir ve normal test koşusuna dahil edilmez.

Eski paket alias betiği `scripts/Package-Desktop.historical.txt` yalnızca tarihsel kayıttır. Manuel WinForms paketleme için bu klasördeki `scripts/Package-LegacyWinForms.ps1` kullanılabilir; çıktı arşive gider. Eski WinForms rehberleri `docs/` altındadır ve tarihsel yollar içerebilir.
