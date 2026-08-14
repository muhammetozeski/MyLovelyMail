# Sistem Mimarisi Öğrenme ve "Blank Template" (Boş Şablon) Çıkartma Planı

Bu belge, `MyLovelyMail` projesinin **tamamen arındırılmış, projelere özgü hiçbir mantık barındırmayan, siyah/beyaz/gri varsayılan temalı bomboş jenerik bir UI şablonu** olarak inşa edilmesi için hazırlanmış yol haritasıdır. Bu şablon üst düzey mimariyi, Glassmorphism tabanlı UI motorunu, dinamik test altyapısını ve servis katmanını barındırmaktadır.

Benden daha düşük hafızalı bir yapay zeka asistanı bile bu belgeyi okuyarak `MyLovelyMail` projesini kusursuz bir jenerik UI şablonuna dönüştürebilir.

## 1. Hedef ve Kapsam
* **Amaç:** `MyLovelyMail` projesini, yeni elementler, fonksiyonlar ve sayfalar eklemek isteyen herhangi bir geliştiricinin kolayca üzerine inşa edebileceği **saf bir mimari şablon** haline getirmek.
* **Yasaklar:** JavaScript kullanımı kesinlikle yasaktır. Sadece C#, Blazor ve HTML kullanılacaktır. Herhangi bir projeye ait spesifik isimler, logolar, renkler, eğitim modülleri (Grammar, Wheel, Dictionary, CloudBooks vb.) kesinlikle yer almayacaktır. Bu şablon tamamen jenerik kalacaktır.

---

## 2. Klasör Klasör Aktarım ve Arındırma (Purification) Stratejisi

### 2.1. `Constants` (Sabitler)
* **Aktarılacaklar (Arındırılarak):**
  * `AppConstants.cs`: İçerisindeki spesifik uygulama isimleri "MyLovelyMail" veya "TemplateApp" olarak değiştirilecek. AppStore URL'leri boşaltılacak.
  * `DatabaseConstants.cs`: Sadece `Users` koleksiyonu (ID, Name, Email, PhotoUrl, CreatedAt vb. jenerik alanlar) tutulacak. `DailyContents`, `BooksRoot`, gamification alanları (`Xp`, `Streak`, `Badges`) silinecek.
  * `NavigationConstants.cs`: Temel rotalar (`/`, `/login`, `/profile`) tutulacak, spesifik rotalar (okuma, çark vb.) silinecek.
  * `UiConstants.cs` ve `ThemeConstants`: Hatalar ve genel UI metinleri tutulacak.
  * `ResiliencePolicy.cs`: Polly pipeline altyapısı korunacak.
* **Tamamen Silinecekler:** `ContentConstants.cs`, `GamificationConstants.cs`, `InventoryCatalog.cs`, `MonetizationConstants.cs`, `CurrencyConstants.cs`.

### 2.2. `DataModels` (Veri Modelleri)
* **Aktarılacaklar (Arındırılarak):**
  * `UserProfileData.cs`: İçerisindeki XP, level, okuma sayıları, rozetler gibi uygulamaya özel alanlar silinecek. Temel bir kullanıcı profili modeline indirgenecek.
  * `Events/`: Olay (Event) payload modelleri (`BalanceChange` vb.) şablonda tutulmak istenirse jenerik olarak kalabilir, değilse silinecek.
* **Tamamen Silinecekler:** `CloudBook.cs`, `ContentModel.cs`, `GrammarModel.cs`, `DailySpinModel.cs`, `DictionaryWord.cs` vb. içeriğe özel tüm veri yapıları.

### 2.3. `Events` (Olay Yönetimi)
* **Aktarılacaklar (Arındırılarak):**
  * `MainEvents.cs`, `EventBridges.cs`, `NativeAndroidEvents.cs`, `NativeIOSEvents.cs` ve `EventsBase.cs`. 
  * Bunlar platformlar arası native event (OnAppResumed, OnAppPaused vb.) takibi için kusursuz bir mimari sunar, aynen korunmalıdır.
  * `IsFirebaseInitialized` gibi temel eventler tutulacak.
* **Silinecek Kısımlar:** `OnBadgeEarned`, `OnStreakAdvanced`, `OnXpGained` gibi oyunsallaştırma eventleri `MainEvents` içinden temizlenecek.

### 2.4. `Services` (Servisler)
* **Aktarılacaklar (Arındırılarak):**
  * `AuthService.cs` & `AuthGate.cs`: Firebase tabanlı anonim ve email oturum yönetimi, şablonun en değerli parçasıdır. Projeye özgü kayıt sonrası XP verme gibi mantıklar temizlenip aktarılacak.
  * `UserProfileService.cs`: Sadece temel CRUD (Create, Read, Update, Delete) işlemleri bırakılacak.
* **Tamamen Silinecekler:** `CloudContentService`, `ContentRepository`, `SpinRepository`, `DictionaryService`, `TtsService`, `XpService`.

### 2.5. `UI` (Kullanıcı Arayüzü Mimarisi) - **EN KRİTİK BÖLÜM**
Bu klasör şablonun kalbidir. Modüler CSS (Razor) ve Glassmorphism altyapısı tamamen korunacak ancak renkler nötralize edilecektir.
* **`UI/Architecture`**: `PageBase`, `LoadablePage`, `FeedView` sanallaştırma mimarisi olduğu gibi aktarılacak.
* **`UI/ThemeConstants`**: `ThemeManager`, `AppColors`, `AppStyles` aktarılacak. **ÖNEMLİ:** `AppThemes.cs` içindeki tüm temalar siyah, beyaz ve gri (monochrome/grayscale) tonlarına çevrilecek. Canlı renkler varsayılan şablondan çıkarılacak.
* **`UI/Elements`**: Butonlar (`CoreButton`, `IconButton` vb.), Kartlar (`CoreCard`, `GlassCard`), `CoreInput`, `CoreSpinner`, `CoreModal` gibi tüm temel yapı taşları olduğu gibi aktarılacak.
* **`UI/Components`**: `AppBottomNav` ve `AuthGatePromptHost` gibi evrensel bileşenler tutulacak. Gamification bileşenleri (`StreakIndicator`, `XpProgressBar`, `ProUpsellModal`) silinecek.
* **`UI/Pages`**: 
  * Tutulacak ve İçi Boşaltılacaklar: `Home` (Sadece "Hello World" tarzı boş bir feed/dashboard), `Profile` (Sadece kullanıcı resmi, adı ve çıkış butonu), `Login` (Giriş arayüzü), `Splash` (Sadece jenerik bir logo ve loading animasyonu).
  * Tamamen Silinecekler: `Calendar`, `Grammar`, `Wheel`, `Library`, `Reader`, `DiamondShop`, `Leaderboard` vb.

### 2.6. `zTests` (Test Altyapısı)
* **Aktarılacaklar:** `TestCenter.cs`, `Logger.cs` ve `DevTools/` klasörü şablonun hata ayıklama motorudur. Olduğu gibi (refactor edilmeden) aktarılacak.
* **Güncellenecekler:** `UnitTests/` klasöründeki testler (`AuthServiceTests`, `UserCrudTests`) yeni arındırılmış modellere göre güncellenecek. `ContentCrudTests`, `TtsServiceTests` gibi silinen servislere ait testler kaldırılacak.

### 2.7. `Resources` & `PlatformResources` (Kaynaklar)
* `Resources/Fonts`: Kullanılan genel fontlar (örn. OpenSans) aktarılacak.
* `Resources/Raw`: İçi boşaltılacak (dictionary, contents json'ları silinecek). Sadece şablona ait temel bilgiler varsa kalacak.
* `Resources/Sounds`: Silinecek.
* İkonlar ve Splash ekranı tamamen jenerik (örn. düz bir .NET bot veya gri bir logo) hale getirilecek.
* `PlatformResources`: Sadece temel `AndroidManifest.xml`, `Info.plist` ayarları alınacak, Firebase konfigürasyon dosyaları (örn. `google-services.json`) yeni projeye göre üretilecek veya placeholder olarak bırakılacak.

### 2.8. `GlobalUsings.cs`
* Jenerik yapıya uygun oluşturulacak, namespace'ler `MyLovelyMail.MainProject...` şeklinde tasarlanacaktır.

---

## 3. Uygulama Sırası (Execution Steps)

Eğer bu belgeyi okuyan AI asistanı, şablon çıkarma işlemine başlayacaksa şu adımları izlemelidir:

1. **Temizlik:** `MyLovelyMail` projesinin `MainProject` klasörü içindeki gereksiz/eski her şeyi temizle. (Şu an sadece boş Docs klasörü var).
2. **Mimari Taşıma:** Sırasıyla `Constants`, `Events`, `DataModels` ve `Services` klasörlerini (yukarıdaki arındırma kurallarına göre) `MyLovelyMail/MainProject` içine oluştur.
3. **UI Motoru:** `UI/Architecture`, `UI/ThemeConstants`, `UI/Elements` ve `UI/Components` klasörlerini taşı. Siyah/beyaz temayı ayarla.
4. **Sayfalar:** Sadece `Login`, `Home`, `Profile` ve `Splash` sayfalarını oluştur.
5. **App Shell Entegrasyonu:** `_Imports.razor`, `Routes.razor` ve `wwwroot/index.html` dosyalarını Glassmorphism yapısına uygun şekilde bağla.
6. **Test ve DevTools:** `zTests` klasörünü ve `Logger`'ı ekleyip testlerin derlendiğinden emin ol.

Bu plan, `MyLovelyMail`'ı tamamen bağımsız, modüler, ultra performanslı (JS içermeyen Blazor) ve üzerine istenilen herhangi bir uygulamanın kolayca inşa edilebileceği kusursuz bir boş şablon yapacaktır.
