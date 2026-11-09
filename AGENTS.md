<!-- BUILD_INFO_START -->
- **Latest Build Number**: 115 (Mandatory: increment by +1 on every build and include in every commit message)
- **Latest Commit ID (at last build)**: `78c45cb`
<!-- BUILD_INFO_END -->

# Project guidance & Strict Architectural Rules (MANDATORY — NO EXCEPTIONS)

Read [the documentation index](docs/README.md) and [documentation guidance](docs/DOCUMENTATION.md), then follow the documents relevant to the task. Keep reusable, verified project knowledge in the existing documents; distinguish source inspection from runtime verification.

When taking over work, follow [the Walkthrough guide](Walkthrough/README.md): select records by filename, read the newest context and relevant experience, and add a new dated record when a result affects future work. Keep these records concise; do not duplicate Git history or the documentation.

---

## KESİN MİMARİ KURALLAR (3. MİMARİ TEMİZLİK SONRASI ZORUNLU ANAYASA)

Bu kurallar herhangi bir AI ajanı (Claude, Gemini, GPT vb.) tarafından **İSTİSNASIZ** uygulanmak zorundadır. Kolaycılığa kaçıp spagetti kod yazmak kesinlikle yasaktır:

### 1. Çıplak CSS Sınıf Adı (`class="string"`) Kullanımı Kesinlikle Yasaktır
- Hiçbir `.razor` veya `*Css.razor` dosyasında tırnak içinde serbest/çıplak CSS sınıf adı (`class="mail-search-toggle"`, `.mail-search-toggle { ... }`) **YAZILAMAZ**.
- Her CSS sınıf adı ilgili C# `static class *CssClasses` (örn. `MailCssClasses`, `CoreButtonClasses`) içinde `public const string` olarak tanımlanmak zorundadır:
  - `.razor` içinde: `class="@MailCssClasses.SearchToggle"`
  - `*Css.razor` içinde: `.@(MailCssClasses.SearchToggle) { ... }`
- Böylece bir sınıf adı silindiğinde veya değiştirildiğinde C# derleyicisi anında derleme hatası (compile-time error) verir; sahipsiz veya yanlış yazılmış sınıf adı yüzünden görsel bozulma yaşanması imkânsız hale gelir.

### 2. Sayfalarda Ham HTML Elemanı Yerine `CoreElement` / C# Bileşen Kalıtımı Zorunludur
- CSS'in kalıtım ve seçici sistemini (inline `style` ile) yeniden icat etme; **kalıtımı (inheritance) C# bileşenleri üzerinde kur.**
- Sayfalarda (`Mail.razor`, `MailSidebar.razor`, `MailReaderPane.razor`, `Settings.razor`) tekrarlayan `<button class="...">`, `<input class="...">`, `<div class="splitter">` gibi ham HTML elemanları yazmak **YASAKTIR**.
- Bunun yerine `CoreElement` / `ThemedComponentBase` / `ModalBase` hiyerarşisinden türeyen C# bileşenlerini kullan:
  - Pill/filtre/araç çubuğu butonları için: `<PillButton IsActive="..." IsDanger="..." IsPinned="..." IsIcon="..." OnClick="...">` (veya `<DangerPillButton>`)
  - Sütun boyutlandırıcılar için: `<ColumnSplitter Index="1" IsDragging="..." OnPointerDown="..." OnDoubleClick="..." />`
  - Pencereler ve popover'lar için: `ModalBase` -> `CompactModal` / `DialogModal` / `WorkspaceModal` / `WideModal` -> `CoreModal`
- Bir bileşenin durumu (`Active`, `Danger`, `Pinned`, `Disabled`, `Dragging`) sayfa içinde string sınıf birleştirerek değil, C# bileşeninin `[Parameter]` özellikleri veya alt sınıfları üzerinden kontrol edilir.

### 3. `*Css.razor` İçinde Sihirli Sayı (Magic Number) ve Sabit Renk Yasaktır
- `*Css.razor` dosyalarında `150px`, `4px`, `11px`, `#4ADE80`, `rgba(...)` gibi çıplak sayı veya renk değerleri yazmak **YASAKTIR**.
- Her ölçü `AppMeasures` (`AppMeasures.Space.*`, `AppMeasures.Size.*`, `AppMeasures.Font.*`, `AppMeasures.Radius.*`), her renk `AppColors`, her ortak yüzey/gölge/scrollbar formülü `AppStyles` içindeki C# sabitlerinden okunmak zorundadır.
- Tüm `*Css.razor` bileşenleri `ThemedComponentBase` sınıfından türemek zorundadır.

### 4. Gereksiz JavaScript Yazmak Kesinlikle Yasaktır (Saf C# / Blazor Native Önceliği)
- Blazor'un `@onpointerdown`, `@onpointermove`, `@onpointerup`, `@onkeydown`, `@ondragstart`, `@ondragover`, `@ondrop` ve `PointerEventArgs` / `KeyboardEventArgs` ile C# içinde native olarak yapabildiği **hiçbir işlem** (sütun boyutlandırma, sürükle-bırak durumu, kısayol yakalama, modal/menü açma-kapama vb.) için JavaScript kodu **YAZILAMAZ**.
- JavaScript yalnızca tarayıcı motorunun C#'tan doğrudan erişime izin vermediği zorunlu DOM köprüleriyle (`mail-reader.js` içinde `iframe` içerik yüksekliği ölçümü ve `mouse-buttons.js` içinde yerel sağ-tık/yan-tuş yakalama) sınırlıdır.
- `MyLovelyMail/wwwroot/index.html` yalnızca `MainProject/wwwroot/` kökündeki scriptleri yükler; başka alt klasörlere (`wwwroot/js/` vb.) hayalet script dosyaları oluşturma.

### 5. Zorunlu Görsel ve Etkileşimli Doğrulama (`DebugApi` Üzerinden)
- Hiçbir arayüz, menü, modal veya CSS değişikliği sadece `dotnet build` başarılı oldu diye "bitti" sayılamaz!
- Her arayüz değişikliğinden sonra `DebugApi` (`http://127.0.0.1:52539`) üzerindeki şu 3 uç nokta ile arka planda (kullanıcının ekranını veya faresini işgal etmeden) gerçek test yapılmalıdır:
  1. `GET /screenshot?path=...` — Arka planda pencerenin gerçek PNG ekran görüntüsünü alır. Alınan görüntü mutlaka `read_file` ile görsel olarak incelenmeli, hizalama/renk/taşma hatası olmadığı gözle doğrulanmalıdır.
  2. `POST /input/mouse?x=...&y=...&action=click|hover|rightclick|drag&toX=...&toY=...` — Pencere içindeki koordinata tıklama, sağ tıklama, üzerine gelme (hover) ve sürükleme (drag) simülasyonu yapar.
  3. `POST /input/key?x=...&y=...&key=...&text=...` — Pencere içindeki koordinata klavye tuş vuruşu ve metin yazma simülasyonu yapar.
- Doğrulama tamamlandıktan sonra değişiklik mutlaka `C:\E\kp\aaBenimProgramlarim\MyLovelyMail\AppData` dizinine yayımlanmalı (`dotnet publish`) ve `Imzala.ps1` ile imzalanmalıdır.
