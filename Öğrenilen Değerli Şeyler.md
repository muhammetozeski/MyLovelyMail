# Öğrenilen Değerli Şeyler

Yapılan hatalardan alınan dersler. Her madde gerçek bir vakadan çıktı.

## UI tasarımı

- **"0 = hepsi" programcı mantığıdır, kullanıcı arayüzüne koyulmaz.** Sihirli değerler yerine
  açık bir kontrol kullan: "Fetch everything" toggle'ı açılınca sayı kutusu disable olur.
  (Vaka: POP3 fetch limitinde "0 = tüm posta kutusu" hint'i yazılmıştı.)
- **Kullanıcıya gösterilen hiçbir metin elle yazılmaz; kaynağındaki nesneden türetilir.**
  Protokol adı, host örneği, port — hepsi seçili enum'un/nesnenin üzerinden gelir. String'ler
  yalnızca UI'a çıkarken üretilir; veri taşımada asla string kullanılmaz. (Vaka: wizard'da
  POP3 seçilince kutular hâlâ "imap.example.com" ve IMAP portu gösteriyordu, çünkü metinler
  tek tek elle yazılmıştı.)
- **Zemin rengi değişkense yazı rengi sabit yazılmaz, ölçülerek seçilir.** Pastel bir paletin
  üzerine beyaz yazı "çalışıyor gibi" görünür ama ölçünce çökebilir. Karar kodda verilir:
  `AppColors.InkOn(hex)` beyaz ile koyu erik arasında WCAG kontrast oranı büyük olanı döndürür.
  (Vaka: hesap okunmamış rozeti ve etiket çipleri kimlik paletinin üzerine beyaz yazıyordu;
  ölçüm 1,70–3,42:1 çıktı, yani AA sınırının epey altı. Koyu mürekkeple 5,45–10,95:1 oldu.)

## Loglama

- **Render içinde veya sıkı döngü içinde log yazılmaz.** Bir uzak kullanıcının 10 dakikalık
  oturumunda 1118 log satırının 1031'i tek bir butonun her render'da yazdığı satırdı; işe
  yarayan 87 satır gürültüye gömüldü.
- **Log dosyası kanıt kaynağıdır.** POP3 sıralama bug'ı yalnızca uzak kullanıcının log'undaki
  "0 new of 2276 → 2277" deseninden yakalandı. Loglara zaman damgası + sunucu sayısı gibi
  karşılaştırılabilir değerler koy.

## Protokol varsayımları

- **RFC geleneği garanti değildir; sunucu davranışı ölçülür.** POP3'te "mesaj 1 = en eski"
  varsayımı Yandex'te tersti (1 = en yeni). Sıra artık iki uçtaki Date başlığından ölçülüyor.
- **"Son N" penceresi yeni-mail tespiti için kullanılamaz.** Yeni mail listenin herhangi bir
  ucuna düşebilir; tespit her zaman TÜM kimlik listesine karşı yapılır, limit sonra uygulanır.

## Dağıtım

- **`dotnet publish -r <rid>` artık self-contained anlamına gelmez.** Bayrak açıkça
  yazılmazsa çıktı sessizce framework-dependent olur ve .NET kurulu olmayan makinede açılmaz.
  Geliştirme makinesinde fark edilmez, çünkü orada runtime hep vardır.
- **Autostart/registry gibi makine-genel kayıtlara dev build asla yazmamalı.** Debug sandbox
  bir kez kullanıcının gerçek autostart kaydını kendi bin\ yoluyla ezdi.
- **`gh release create` `--target` verilmezse VARSAYILAN dalı etiketler.** Geliştirme başka
  bir dalda ilerliyorsa tag, asset'lerin derlendiği koddan yüzlerce commit geride bir yeri
  gösterir. Ayrıca `gh release list` sıralaması güvenilmez: `createdAt` tag'den geldiği için
  daha yeni bir release daha eski görünebilir — sürüm seçerken tarihe değil, semantik sürüm
  sırasına bak. (Vaka: v1.3.0 master'ın ucuna bağlandı ve bir sonraki çağrı "en son sürüm"
  olarak v1.2.0'ı gördü.)
- **Her döngü turu dağıtımla biter.** Kod commit'lenmiş olması yetmiyor; `Export.ps1` ile
  kurulu kopya, `Release.ps1` ile GitHub release güncellenmezse programı deneyen kişi
  düzeltilmiş hataları yeniden bildiriyor.

## Hata ayıklama disiplini

- **Kesin bilinmeyen şeye "bilmiyorum" denir ve kanıt toplayacak iz bırakılır.** Boot'ta
  argümansız kopyayı neyin başlattığı bilinmiyordu; tahmin sunmak yerine ikinci kopyaların
  argümanlarını dosyaya yazan bir iz eklendi — sonraki açılış kesin cevabı verecek.
- **Ekrana tıklayarak test yapılmaz.** Kullanıcı makineyi aktif kullanıyor olabilir. Uygulama
  DebugApi (127.0.0.1:52539) üzerinden sürülür; eksik endpoint varsa endpoint eklenir.
- **Sorun üretilmeden açıklanmaz.** "Muhtemelen X yüzünden" yazmak yasak; mock sunucu, log
  kanıtı veya piksel karşılaştırması gibi bir kanıt üretilir, sonra konuşulur.

## Ayar varsa karşılığı da olmalı

- **Tanımlanmış ama hiçbir yerin okumadığı ayar, kullanıcıya verilmiş boş sözdür.** Projede 8
  tane çıktı (ConversationView, SignatureHtml, FollowSystemTheme, UiScalePercent,
  ShowUnreadBadge, DownloadAttachmentsAutomatically, OfflineKeepDays, DefaultComposeFormat).
  Her biri ya gerçekten uygulandı ya da silindi — arada kalan yok. Bunu düzenli tara:
  `Settings.cs`'teki her alan adını tüm projede ara, sıfır sonuç veren varsa karar ver.
- **Ayarın adı yaptığı işi söylemeli.** `SignatureHtml` düz metin gövdeye ekleniyordu;
  `DefaultComposeFormat` HTML seçeneği sunuyordu ama HTML yazma yok. Biri yeniden adlandırıldı,
  diğeri silindi.
- **Platform API'si "hazır" sanılmamalı.** `Application.Current.RequestedTheme`, tema seçimi
  yapılırken henüz null (MAUI uygulaması kurulmamış); sessizce "açık tema" cevabı veriyordu.
  Doğrulama snapshot'ı yakaladı; sonda doğrudan işletim sisteminden okuyacak şekilde değişti.

## Doğrulama tuzakları (hepsi gerçekten yaşandı)

- **Aynı anda iki kopya çalışıyorsa snapshot yanlış pencereyi çeker.** Kullanıcının kurulumu
  ile geliştirme derlemesi yan yana çalışıyordu; snapshot script'i pencere bulan ilk process'i
  aldığı için hiç dokunulmamış bir derlemeyi "doğruladı". Script artık `bin\Debug` yoluna
  sabitli ve hedefi ekrana yazıyor.
- **Aranan metin yanlış bölgede eşleşebilir.** Alıntı katlamayı doğrularken `mlm-quote` metnini
  tüm HTML'de aradım; `<style>` bloğundaki CSS kuralına takıldı ve "çalışıyor" sandım. Kontrol
  her zaman ilgilenilen bölgede yapılmalı (burada `<body>` sonrası).
- **Anahtar adının varlığı, değerin varlığı demek değildir.** Snooze kalıcılığını denerken
  `index.jsonl` satırında "Snoozed" kelimesini aradım ve "kaydediliyor" sandım; oysa satır
  `"SnoozedUntilUtc":null` içeriyordu. Doğru kontrol değeri okur, adı değil. Bu, sync'in
  uygulama-yerel alanları (etiketler, "önemli", snooze) sildiğini ortaya çıkardı.
- **PowerShell tek elemanlı diziyi düzleştirir.** `@(Invoke-RestMethod ...).Count` her sorgu için
  1 döndürdü; API doğruydu, sayım yanlıştı. Bütün sayılar birbirinin aynı çıkıyorsa önce ölçüm
  yöntemini şüphelen — gerçek sayılar 313/149/27 idi.
- **Razor, çift tırnaklı attribute içinde iç içe `$"..."` ayrıştıramaz.** Attribute'u tek tırnakla
  yaz (`@onclick='() => F("x" + y)'`) ya da ifadeyi code-behind'a taşı.

- **Ekran görüntüsüyle animasyon ölçülmez.** PrintWindow kareleri arasında geçen süre
  belirsiz, gradyan zemin de kanal başına 1-2 adım dithering üretiyor; eşik düşük olunca
  gürültü hareket sanılıyor, yüksek olunca gerçek animasyon kayboluyor. Aynı ölçüm arka arkaya
  %40 ve %3 verdi. Animasyonun durduğunu göstermek gerekiyorsa üretilen CSS'i ve kararı
  doğrula, pikselle uğraşma; kare kare ölçüm bu araçlarla yapılamıyor — bunu söyle ve geç.
- **Kayıt defteri değerinin TÜRÜNÜ varsayma.** `AppsUseLightTheme` REG_DWORD ama
  `WindowMetrics\MinAnimate` REG_SZ ("1"/"0"). Çalışan bir prob'u kopyalayıp tip desenini
  aynen bırakmak sessizce her kullanıcı için yanlış cevap üretirdi; değeri önce oku, sonra yaz.

- **Bir sayının tek bir anlamı olmalı; iki kaynaktan yazılan alan er geç yalan söyler.** Klasörün
  okunmamış rozetini senkron sunucunun sayısıyla (9.624 mesajın 8.607'si), yerel işlemler ise
  yalnızca önbellekteki dilimle (513 mesaj) yazıyordu. Tek bir mail açmak rozeti 8.607'den
  149'a düşürüyor, sonraki senkron geri koyuyordu. Alanın sahibi tek bir katman olmalı; ötekiler
  ona delta bildirir. Yeniden-hesap ancak önbellek tüm klasörü tutuyorsa doğrudur.

- **Bir anahtar ancak kendi alanı içinde benzersizdir; başka bir alana taşınırken yeniden
  anahtarlanmalı.** IMAP uid'leri her klasörde 1'den başlıyor. INBOX uid 7 ile Sent uid 7'yi aynı
  yerel klasöre taşımak hem .eml'i hem indeks satırını eziyordu — üstelik uygulama-yerel durum
  taşıması, yok olan mesajın etiketlerini hayatta kalanın üzerine geçiriyordu, yani enkaz yok olan
  mesaj gibi görünüyordu. Diğer bütün çakışmalar yeniden senkronla düzeliyor; sunucu kopyası
  olmayan yolda düzelmiyor. Bir kimliği başka bir isim alanına taşırken "orada bu numara boş mu"
  diye sor.
- **Geri çekilme (backoff) hızı sınırlar, toplamı değil.** IDLE döngüsü her kopuşta 2, 4, 8, 16, 32
  saniyeyle yeniden bağlanıyordu — reddedilmiş bir şifre için bu, uygulama açık kaldığı sürece aynı
  başarısız girişi tekrarlamak demek. Ölçüm: sahte sunucuya 35 saniyede 10 giriş denemesi ve
  artıyor. Yeniden denemenin düzeltebileceği hata (soket, TLS, zaman aşımı) ile düzeltemeyeceği
  hata (şifre reddi) ayrılmalı; ikincisinde döngü durmalı.
- **Merkezî bir politika düzeltilince iş bitmiş sayılmaz; aynı işi kendi başına yapan döngüler
  aranmalı.** Polly'nin yeniden deneme yordamını düzeltmek senkron pasını tek denemeye indirdi ama
  denemelerin çoğu IDLE servisinin kendi yeniden bağlanma döngüsünden geliyordu. Ölçmeden
  "düzeldi" denseydi asıl kaynak elde kalacaktı.
- **"Bulunamadı" değeri geçerli bir değerse, o metot kullanılmaz.** List<int>.FindLast ELEMANI
  döndürür; bulamayınca default(int) yani **0** verir — indeks listesinde 0 tamamen geçerli bir
  indeks. Yazdığım pencere kontrolü bu yüzden hiç çalışmadı: eşleşme bulunmayan her mesaj sessizce
  0 numaralı mesajla birleşti, yani düzeltmeye çalıştığım hatanın aynısı. FindLastIndex -1 döner,
  ya da elle döngü yazılır. Ölçmeseydim "düzeldi" diye commit'lenmişti.
## Süreç

- **Tek concern = tek commit.** Deneysel değişiklik ile sağlam düzeltme aynı commit'e girerse
  kötü olan geri alınamaz hale gelir.
- **İşler biriktirilmez.** Yarım işler yığın olduktan sonra toparlamak, anında bitirmekten
  pahalıdır; her turda eldeki iş bitirilip commit'lenir.
