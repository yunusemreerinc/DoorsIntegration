# DOORS Classic 9.5 — Bulk Baseline Manual Acceptance

**Başlangıç: NOT_RUN — gerçek 9.5 ortamı ve write onayı sağlanmadı.** Bu sürüm için sonuçlar diğer DOORS sürümünden taşınmaz.

Önce [ortak hazırlık, prosedür ve kanıt kurallarını](REAL-ACCEPTANCE-GUIDE.md) uygulayın. `Test-BulkBaselineAcceptance.ps1` bu tabloyu çalıştırmaz veya PASS olarak işaretlemez.

| Ortam alanı | Kayıt |
|---|---|
| Operator / reviewer / UTC tarih | Doldurulacak |
| Aktif client tam release/build / Windows | Doldurulacak |
| Client/process x86/x64 / COM kayıt kanıtı | Doldurulacak |
| Database/environment / test module stable ID + full path | Doldurulacak |
| Uygulama build/hash / deployment manual doğrulaması | Doldurulacak |
| DPAPI kullanıcı profili/restart kanıtı | Doldurulacak |
| JobId / plan fingerprint / write ApprovalId / Real ikinci onay | Write öncesi doldurulacak |

| ID | Senaryo | Beklenen gözlem / prosedür | Sonuç | Kanıt / neden |
|---|---|---|---|---|
| R95-01 | Read-only metadata sorguları | Metadata prosedürü; normal, hiç baseline olmayan, kapalı ve denied modüller; numbering watermark ayrımı. | NOT_RUN | Ortam/onay/doğrulama bekliyor |
| R95-02 | Test modülünde tek Major | Tek Major prosedürü; bir create, exact beklenen version/suffix/annotation ve yeni-handle verification. | NOT_RUN | Ortam/onay/doğrulama bekliyor |
| R95-03 | Test modülünde tek Minor | Ayrı güncel plan/onay ile tek Minor; bir create ve exact sonucun doğrulanması. | NOT_RUN | Ortam/onay/doğrulama bekliyor |
| R95-04 | Çoklu modül batch | Batch prosedürü; multi-folder, farklı previous sürümler, sıralı STA, sayımlar ve partial outcome. | NOT_RUN | Ortam/onay/doğrulama bekliyor |
| R95-05 | Save / unsaved | Save prosedürü; temiz ve dirty current/other session; habersiz save/discard yok. | NOT_RUN | Ortam/onay/doğrulama bekliyor |
| R95-06 | Lock | Diğer session exclusive lock; block/skip ve açıklama; otomatik unlock veya write yok. | NOT_RUN | Ortam/onay/doğrulama bekliyor |
| R95-07 | Permission | Read/create/modify reddi; capability durumunu gizlemeyen neden ve dispatch olmaması. | NOT_RUN | Ortam/onay/doğrulama bekliyor |
| R95-08 | create sonrası handle geçersizliği | Eski Module/Object handle tekrar kullanılmaz; kapanma gözlemi ve yeni context ile okuma kanıtı. | NOT_RUN | Ortam/onay/doğrulama bekliyor |
| R95-09 | Post-create exact existence verification | Exact version/suffix/annotation/identity ve native history; negatif existence tek başına retry izni değil. | NOT_RUN | Ortam/onay/doğrulama bekliyor |
| R95-10 | Application crash / journal recovery | Onaylı test app fault injection; restart, aynı JobId, başarı korunması ve duplicate engeli. | NOT_RUN | Ortam/onay/doğrulama bekliyor |
| R95-11 | Client crash / reconnect / reconciliation | Ayrılmış test DOORS client'ında onaylı fault injection; Unknown, oturum doğrulama ve read-only reconciliation. | NOT_RUN | Ortam/onay/doğrulama bekliyor |

Her satır için UTC başlangıç/bitiş, uygulama log/audit/journal revision/hash, gerekli DOORS native history ekranı/metadata çıktısı, operator ve reviewer kaydı eklenir. Gerekli onay/capability yoksa ilgili write satırı NOT_RUN kalır. Baseline silme veya otomatik rollback bu paketin parçası değildir.

