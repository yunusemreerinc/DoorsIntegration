# DoorsIntegration

IBM DOORS Classic 9.5 / 9.7 için .NET Framework 4.8 tabanlı entegrasyon çözümü. DOORS'a late-bound COM (`DOORS.Application`) ve DXL (`runStr` / `runFile`) üzerinden bağlanır; DWA/OSLC/HTTP kullanılmaz. Birleşik WPF masaüstü istemcisi ve tanılama amaçlı bir konsol uygulaması içerir.

## Solution yapısı

| Proje | Sorumluluk |
| --- | --- |
| `src/DoorsIntegration.Core` | Domain modelleri, portlar (arayüzler), servisler, mock adaptörler, dosya tabanlı journal'lar |
| `src/DoorsIntegration.Dxl` | DXL script hazırlama/yürütme protokolü ve özellik gateway'leri (gereksinim, link, baseline, modül oluşturma) |
| `src/DoorsIntegration.Com` | COM oturumu, registry tanılama, DXL run-limit istemi otomasyonu |
| `src/DoorsIntegration.Processing` | Kalite kuralı motoru, batch içe aktarma |
| `src/DoorsIntegration.Export` | XLSX (OpenXml), HTML ve metin export |
| `src/DoorsIntegration.Application` | Ön yüzlerin paylaştığı bileşim katmanı |
| `src/DoorsIntegration.Wpf` | Birleşik masaüstü istemcisi (MVVM) |
| `src/DoorsIntegration.Console` | Komut satırı tanılama (`--diagnose-com`, `--probe`, …) |
| `tests/DoorsIntegration.Tests` | MSTest birim/entegrasyon testleri (mock adaptörlerle, DOORS gerektirmez) |
| `scripts/dxl` | Uygulamanın yürüttüğü DXL kaynakları |
| `archive/WinForms` | Eski WinForms istemcisi; build/test akışına dahil değildir |

## Gereksinimler

- Windows, .NET Framework 4.8 ve .NET SDK (`dotnet` CLI)
- Gerçek DOORS işlemleri için kurulu ve kayıtlı DOORS Classic 9.5 veya 9.7 istemcisi. Build ve testler DOORS gerektirmez.

## Build ve test

```powershell
.\scripts\Build.ps1 -Configuration Release -Platform x86
```

Script, paketleri yerel `.packages/` klasörüne restore eder, solution'ı derler ve testleri çalıştırır (`-SkipTests` ile atlanabilir; x64 için `-Platform x64`). `.packages/` ve `.cli/` yerel önbelleklerdir ve repoya eklenmez.

## Paketleme

```powershell
.\scripts\Package-Wpf.ps1 -Platform x64
.\scripts\Package-Console.ps1
```

## Belgeler

Ayrıntılı tasarım, kullanım ve doğrulama notları `docs/` altındadır. Başlangıç noktaları:

- [WPF istemcisi](docs/WPF-CLIENT.md) ve [birleşik masaüstü](docs/UNIFIED-DESKTOP.md)
- [COM oturumu](docs/COM-SESSION.md), [DXL executor](docs/DXL-EXECUTOR.md), [zaman aşımları](docs/TIMEOUTS.md)
- [Gereksinim yazma](docs/REQUIREMENT-WRITING.md), [link yazma](docs/LINK-WRITING.md)
- [Bulk baseline mimarisi](docs/BULK-BASELINE-ARCHITECTURE.md)
- [Smoke testleri](docs/SMOKE-TESTS.md) ve [manuel testler](docs/MANUAL-TESTS.md)
