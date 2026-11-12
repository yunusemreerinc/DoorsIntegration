# Task 26 — Kesinti sonrası tamamlanma kontrolü

Önceki Task 26 kaynak manifesti, test sonuçları ve kod bağlantıları tekrar kontrol edildi. Kaynak dosyaları önceki manifest ile eşleşiyordu; kesinti nedeniyle kaybolmuş dosya veya eksik DI kaydı bulunmadı. İnceleme ayrıca aşağıdaki hataları ve ekran eksiklerini ortaya çıkardı; düzeltmeler uygulandı.

## Bulunan ve düzeltilenler

1. **Authentication metninin eksik maskelenmesi:** `Authorization: Bearer VALUE` ve Basic/Digest başlıklarında eski işlem sırası scheme'i silip credential devamını bırakabiliyordu. Başlık önce bütünüyle maskeleniyor. JSON biçimindeki tırnaklı credential anahtarları ve onay kapsamı metni de aynı korumadan geçiyor. Synthetic verilerle önceki sürümde üç credential testi başarısız oldu; yeni regresyonlar başlık/JSON/scope yollarını kapsıyor. Gerçek bir parola veya token kullanılmadı.
2. **Reconciliation sırasında sürenin uzaması:** Tamamlanan modül tekrar açıldığında `CompletedAtUtc` reconciliation zamanıyla değişiyordu. Artık bitmiş attempt'in zamanı/süresi korunuyor. Yeni, açıkça onaylanan attempt başlayınca eski completion sıfırlanıyor; verification zamanı ve durum değişiklikleri ayrı audit olayları olarak kaydediliyor. Eski audit kayıtları geriye dönük değiştirilmedi.
3. **WPF history sayfalaması:** Core offset/limit sağlıyordu; UI ilk 100 job'ın ötesine geçemiyordu. Previous Page / Next Page ve sayfa aralığı eklendi. Filtre değişikliği eski sayfalama yeteneğini kapatır; Refresh/Open Job History ilk sayfadan başlar. 101 job ile ikinci sayfa, geri dönme, filtre ve read-only raporlama testi eklendi.
4. **History bellek kullanımı:** Listeleme bütün eşleşen job'ların module/annotation payload'larını tutuyordu. İlk tarama artık sadece JobId/revision/tarih gibi hafif sıralama bilgilerini saklar; tam payload yalnız istenen sayfa için tutulur. İkinci okumada revision değişmişse eski filtre sonucuyla yeni veri karıştırılmaz; yenileme uyarısı gösterilir.
5. **Reconciliation sonrası boş audit görünümü:** Reconcile artık güncel raporu yeniden okuyup gösterir. History'den job/report açıldığında type/policy/suffix/annotation bilgileri de o job ile eşitlenir. Read/re-report ve reconciliation çıktılarının revision'ları eşleşmezse kullanıcı yenilemeye yönlendirilir; yazma başlatılmaz.

## Task 26 gereksinim karşılaştırması

| İstek | Kontrol edilen uygulama |
|---|---|
| 1 — Job/operator/database/environment/client/start/end/type/suffix/annotation | Audit context, immutable job snapshot ve BulkBaselineJobs export tablosu. Native binary sürümü kanıt kaynağıyla etiketlenir; aktif session sürümü varsayılmaz. |
| 2 — Stable module identity/path/previous/expected/actual/status/duration/error | Module DTO ve BulkBaselineModules; actual yalnız doğrulanmış başarıdan gelir. Reconciliation süre koruma düzeltildi. |
| 3 — JSON/CSV/XLSX | Mevcut ExportService ve OpenXML writer; Unicode/UTC, güvenli hücreler ve hata manifesti testleri. Yeni bağımlılık yok. |
| 4 — Başarı/atlanan/kilitli/hata/doğrulanamayan grupları | Beş report group ve WPF audit tablosu; CSV/JSON/XLSX grup alanı. |
| 5 — Dört batch sayımı | Success/Failed/Skipped/Unknown; Locked ayrıca gösterilir ve Skipped'e bir kez dahil edilir. |
| 6 — Partial/Unknown tam başarı değildir | Outcome ve IsFullSuccess; unknown veya çelişkili sonuçta actual=null. |
| 7 — Append-only, hassas veri | Create-new/hash-chain audit, DPAPI korumalı recovery cache; credential masking düzeltmeleri ve no-plaintext-fallback testi. |
| 8 — Onaylanan plan + execution sonucu | WPF tam batch approval snapshot'ı, ortak ApprovalId ve modül sonuçları; audit planı yazma yetkisi değildir. |
| 9 — Filtre/açma/yeniden raporlama/reconciliation | Core history + WPF filtreler/sayfalama; local-only read/re-report, explicit read-only reconcile ve güncel ekran. |
| 10 — Native history yerine geçmez | Rapor başlığı/DTO disclosure, WPF ekranı ve dokümantasyonda açık ifade. |

## Doğrulama ve kalan ortam kontrolleri

Yeni dokuz test vakası eklenmiştir: dört authentication header örneği, tırnaklı JSON credential, bitmiş attempt zamanı/süresi, approval scope masking, history revision değişimi ve 101-job WPF sayfalaması. Mevcut reconciliation render testi güncel raporu ve korunmuş zamanı da kontrol eder. Task 26'nın ilk sürümündeki 27 vakayla birlikte toplam 36 yeni offline vaka bulunur.

Önceki hataları gösteren kanıt `artifacts/TestResults/task26-review-before-x64.trx` dosyasındadır (dört beklenen başarısız regresyon). Son build/test sayıları [VALIDATION.md](VALIDATION.md) ve `artifacts/BulkBaselineReporting/Review/verification.json` içindedir. Önceki Task 26 tam test kanıtları korunur; ilk review regresyon çalışmaları ile son kaynak sürümündeki test raporları ayrı adlandırılır.

**Gerçek DOORS ortamı kontrolleri açık kalır:** Canlı 9.5/9.7 testleri, native state/next-version/deployment ve aktif session sürümü doğrulaması bu yerel testlerle tamamlanmış sayılmaz. Real yazma engelleri korunur ve canlı testler NOT_RUN kalır.

**DPAPI:** Synthetic CurrentUser roundtrip yeniden denendi. Bu sandbox'ta CryptographicException `0x80131430` oluştu; Windows mesajı yüklü olmayan kullanıcı profili olasılığını belirtiyor. Kesin kök neden bu incelemede saptanmadı. Geçerli Windows kullanıcı profili altında DPAPI/restart kontrolü yapılmalı; offline journal/WPF testleri açıkça test assembly'sindeki koruma sağlayıcısını kullanır. Koruma hatasında uygulama plaintext fallback yapmaz ve write dispatch etmez.

Bu inceleme gerçek DOORS verisini değiştirmedi. Settings/watchdog/ikon ve DXL create/reconcile scriptleri değiştirilmedi; önceki release ZIP'leri korunur. Güncellenen Release exe'leri `src/DoorsIntegration.Wpf/bin/{x86,x64}/Release/net48/` altındadır.
