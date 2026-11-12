# Uygulama bekleme süreleri

Her işlem request üzerindeki `Timeout` değerini doğrudan kullanır. Bekleme süresine otomatik çarpan uygulanmaz.

| İşlem | Varsayılan bekleme süresi |
| --- | --- |
| Discovery toplam dolaşım bütçesi | 30 saniye |
| Doğrudan DxlExecutor yürütmesi | 30 saniye |
| RequirementService modül okuma | 2 dakika |
| LinkService link okuma | 2 dakika |
| ModuleService oluşturma önizlemesi | 30 saniye |
| ModuleService onaylı oluşturma | 30 saniye |

API'de farklı bir süre gerekiyorsa request oluştururken `timeout` parametresini kullanın. Genel DXL request süresi 1 ms ile 24 saat arasında olmalıdır; modül oluşturma request'inin kendi sınırları korunur.

`DxlExecutionOperation.EffectiveTimeout`, request'ten alınan bekleme süresini gösterir. `operation.WaitAsync(cancellationToken)` bu süreyi, `operation.WaitAsync(TimeSpan, cancellationToken)` verilen açık süreyi doğrudan kullanır. `DxlTimeoutConfigured` kaydı `TimeoutMs` değerini bildirir.

Discovery tek bir toplam bütçe kullanır: her klasör çağrısına toplam sürenin kalanı verilir. Bütçe her klasörde sıfırlanmaz. `DiscoveryStarted` kaydı toplam `TimeoutMs` değerini gösterir.

Timeout ve iptal yalnız beklemeyi sonlandırır. Başlamış COM çağrısı güvenle zorla iptal edilemez; STA, workspace ve operation gate gerçek tamamlanmaya kadar tutulur. Aynı DXL çağrısı otomatik tekrarlanmaz. ModuleService'in önizleme, açık onay ve tek kullanımlık commit kuralları korunur; insan onayının geçerlilik süresi değiştirilmez.

Sabit senkron probe ve canlı health-check akışlarında uygulama bekleme süresi sınırı bulunmaz. Export iptali ve okuyucuların kendi request timeout'ları korunur.

Settings sekmesindeki DXL çevrim denetimi ve DOORS penceresindeki Double seçimi farklı katmanlardır. Bunların mevcut davranışı [DXL-WATCHDOG.md](DXL-WATCHDOG.md) belgesindedir.
