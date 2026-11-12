# Bulk Baseline — Gerçek ortam acceptance uygulama rehberi

Bu rehber Mock paketinden ayrıdır. Native 9.5 ve 9.7 için kendi kontrol listesi ve kanıt kaydı kullanılır. Varsayılan sonuç **NOT_RUN**. Mock PASS, fake transport veya ayrı Mock süreç restart testi COM/DXL, gerçek DOORS client crash'i, DPAPI ya da sürüme özgü DXL perm doğrulaması değildir.

## Hazırlık kapısı

- Ayrılmış test veritabanı ve test formal modülleri belirlenir. Database/environment identity, stable module ID ve canonical path kaydedilir. Üretim modülü seçilmez.
- Operator/reviewer, UTC zaman, client tam release/build, COM kayıtlı binary sürümü, process/client x86/x64, Windows ve uygulama build/hash'i kaydedilir. Registry sürümü aktif client sürümü olarak varsayılmaz. Parola/token/connection secret kanıt dosyasına yazılmaz.
- Release'e özel IBM DXL reference/deployment incelemesi, native state/next-version provider ve mevcut compatibility kanıtları tamamlanmış olmalıdır. Bugünkü standart dağıtımda Real baseline yazması bu doğrulamalar eksik olduğu için kapalıdır. Bu test paketi engelleri kaldırmaz; environment hazır değilse write satırları NOT_RUN kalır.
- Geçerli Windows kullanıcı profiliyle DPAPI journal roundtrip/restart kontrol edilir. Mock worker'ın sabit test anahtarı bu doğrulamanın yerine geçmez; gerçek job journal'ında kullanılmaz.
- Her gerçek write senaryosu için exact modül listesi, previous/target, suffix/annotation ve geri alınamaz etkiler dry-run'da incelenir. Kayıtlı açık kullanıcı onayı ve Real ikinci onayı olmadan write başlatılmaz. Bir batch onayı başka bir test veya retry için yeniden kullanılmaz.
- JobId, plan/fingerprint/ApprovalId, journal revision/hash ve önceki baseline geçmişi başlangıç kanıtına eklenir. Test hazırlığı için içerik/ACL/lock değiştirmek de yalnız yetkili tester'ın ayrı onayıyla yapılır. Test suite modül/baseline silmez veya başarıları geri almaz.

## Uygulama yöntemleri

**Metadata (read-only):** Aynı modülün history/major/minor/suffix/annotation/creator/date/identity bilgileri ve latest-existing ile numbering-watermark ayrımı alınır. History listesi mevcut DOORS UI ile karşılaştırılır; ilk baseline olmayan, kapalı ve erişim reddedilen modüller ayrıca denenir. Oturum/modül açılma durumu ve içerik/dirty/lock öncesi-sonrası değişmemelidir. BaselineExists negatif sonucu tek başına authoritative absence sayılmaz.

**Tek Major / tek Minor:** Ayrı onaylı test modülünde önceki sürüm kaydedilir. Örneğin 1.4 için beklenen Major 2.0 / Minor 1.5 adapter'ın nextMajor/nextMinor sonucuyla doğrulanır; yalnız C# aritmetiği kullanılmaz. Suffix/annotation dahil exact hedef dry-run ve ikinci onayda eşleşmelidir. Bir create yapılır; yeni handle ile exact baseline varlığı ve annotation yeniden okunur. Native algoritma/build farklıysa test FAIL veya doğrulanamıyorsa NOT_RUN olur; hedef sessizce değiştirilmez.

**Batch:** İki klasörden farklı son sürümlü birkaç uygun formal modül seçilir; link module dahil edilmez, overlap tekilleştirilir. Her modül kendi hedefini alır. Sonuç sayımları ve actual baseline, native history ile eşleşir. Kısmi başarı tam başarı değildir; başarılı modüller geri alınmaz. Yeni modül öncesi revalidation ve tek STA queue sırası kanıtlanır.

**Save / dirty:** Önce temiz modülle pozitif akış çalıştırılır. Sonra yalnız test fixture'ında yetkili tester'ın oluşturduğu current-session ve other-session unsaved durumları denenir. Uygulama kullanıcı değişikliğini habersiz save/discard etmez. Blocked/önerilen çözüm gösterilir. Değişikliklerin temizliği için ayrıca açık tester kararı gerekir.

**Lock / permission:** Başka test client'ı modülü exclusive kilitlediğinde Blocked/Skipped, erişim yetersizliğinde izin nedeni görülür. Uygulama lock kaldırmaz, ACL değiştirmez ve create dispatch etmez. Kilidin/erişim politikasının öncesi-sonrası aynı kaldığı kanıtlanır.

**create sonrası handle:** Onaylı release script'i ve execution trace incelenir: create sonrası eski Module/Object handle dereference edilmez. Modül kapanması güvenli gözlemle doğrulanır ve sonraki okuma yeni handle/context kullanır. Geçersiz handle'ı kasıtlı dereference ederek client çökertmeye çalışılmaz.

**Post-create verification / çakışma:** Yeniden sorguda expected version + suffix, module identity ve annotation eşleşir. Aynı hedefin başka oturumda oluşması kontrollü testle denenirse dispatch engellenmeli veya sonuç NeedsReconciliation olmalıdır. Timeout kesin failure değildir; Unknown/NeedsReconciliation kaydı ve boş actual alanı korunur. Yeni exact history/existence/context kanıtı olmadan retry yapılmaz.

**Application crash:** Ayrılmış test uygulaması, önceden onaylı fault-injection noktasında dispatch intent'ten sonra kapatılır. Job journal ve tamamlanmış modüller restart sonrası korunur. Aynı JobId ile read-only reconciliation yapılır; Succeeded tekrar create edilmez, belirsiz hedef operator incelemesi ister. Transaction/rollback beklenmez.

**DOORS client crash/reconnect:** Yalnız ayrılmış test client'ında, ayrıca onaylı fault injection ile bağlantı kaybedilir. Aktif COM çağrısının iptal edildiği varsayılmaz. Uygulama Unknown/NeedsReconciliation gösterir, yeni modül/otomatik retry başlatmaz. Yeni session bağlamı doğrulanır, baseline actual state native history ile karşılaştırılır; yalnız güncel evidence ve yeni açık onayla sonraki işlem düşünülebilir. Test başarısızsa production client üzerinde tekrar denenmez.

## Sonuç ve kanıt

Her satır için yalnız PASS / FAIL / NOT_RUN kullanılır. PASS için beklenen bütün gözlemler ve bağımsız native kanıt bulunmalıdır. Davranış uyuşmazlığı FAIL; eksik ortam, onay, capability veya gözlem NOT_RUN ve nedeni olur. Her satırın operator/reviewer, UTC başlangıç/bitiş, JobId/ApprovalId ve kanıt yolları kaydedilir. `native-results.template.json` iki sürüm için boş kayıt şablonudur; çıktı kopyasında doldurulur. Otomatik Mock reporter native sonuçları otomatik PASS yapmaz.

Raporlar uygulama audit kaydıdır; DOORS native history'nin yerine geçmez ve sertifikasyon/standart uyumluluğu kararı üretmez.

