// ------------------- Renk Matematik & Dönüşüm -------------------
    
    function rgbaToHex(rgba) {
      const m = rgba.match(/rgba?\((\d+),\s*(\d+),\s*(\d+)/);
      if (!m) return "#000000";
      const r = parseInt(m[1]), g = parseInt(m[2]), b = parseInt(m[3]);
      return "#" + ((1 << 24) + (r << 16) + (g << 8) + b).toString(16).slice(1).toUpperCase();
    }

    function ParseInt(hexSpan) { return parseInt(hexSpan, 16); }

    function FromArgb(colorAsHex) {
      let red = 0, green = 0, blue = 0, alpha = 255;
      if (colorAsHex && colorAsHex.length > 0) {
        let hex = colorAsHex[0] === '#' ? colorAsHex.slice(1) : colorAsHex;
        if (hex.length === 6) {
          red = ParseInt(hex.slice(0, 2)); green = ParseInt(hex.slice(2, 4)); blue = ParseInt(hex.slice(4, 6));
        } else if (hex.length === 3) {
          red = ParseInt(hex[0]+hex[0]); green = ParseInt(hex[1]+hex[1]); blue = ParseInt(hex[2]+hex[2]);
        } else if (hex.length === 4) {
          alpha = ParseInt(hex[0]+hex[0]); red = ParseInt(hex[1]+hex[1]); green = ParseInt(hex[2]+hex[2]); blue = ParseInt(hex[3]+hex[3]);
        } else if (hex.length === 8) {
          alpha = ParseInt(hex.slice(0, 2)); red = ParseInt(hex.slice(2, 4)); green = ParseInt(hex.slice(4, 6)); blue = ParseInt(hex.slice(6, 8));
        }
      }
      return { red: red/255, green: green/255, blue: blue/255, alpha: alpha/255 };
    }

    function resolveCSSColor(str) {
      if (!str) return null;
      if (str.toLowerCase() === 'transparent') return { red: 0, green: 0, blue: 0, alpha: 0 };
      const temp = document.createElement('div');
      temp.style.color = 'transparent'; temp.style.color = str;
      if (temp.style.color === 'transparent') return null;
      document.body.appendChild(temp);
      const computed = window.getComputedStyle(temp).color;
      document.body.removeChild(temp);
      const match = computed.match(/rgba?\((\d+),\s*(\d+),\s*(\d+)(?:,\s*([\d.]+))?\)/);
      if (match) return { red: parseInt(match[1])/255, green: parseInt(match[2])/255, blue: parseInt(match[3])/255, alpha: match[4] !== undefined ? parseFloat(match[4]) : 1.0 };
      return null;
    }

    function parseSearchQueryToColor(query) {
      query = query.trim();
      if (!query) return null;
      if (query.startsWith('#') || /^[0-9A-F]{3,8}$/i.test(query)) return FromArgb(query);
      return resolveCSSColor(query);
    }

    function getColorDistance(c1, c2) {
      if (!c1 || !c2) return Infinity;
      const dr = c1.red - c2.red, dg = c1.green - c2.green, db = c1.blue - c2.blue, da = c1.alpha - c2.alpha;
      return Math.sqrt(dr*dr + dg*dg + db*db + da*da);
    }

    function calculateSimilarity(c1, c2) {
      const dist = getColorDistance(c1, c2);
      const similarity = Math.max(0, 1 - dist / 2.0);
      return Math.round(similarity * 100);
    }

    function getHSL(colorObj) {
      let r = colorObj.red, g = colorObj.green, b = colorObj.blue;
      let max = Math.max(r, g, b), min = Math.min(r, g, b);
      let h, s, l = (max + min) / 2;
      if (max === min) { h = s = 0; } 
      else {
        let d = max - min;
        s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        switch (max) {
          case r: h = (g - b) / d + (g < b ? 6 : 0); break;
          case g: h = (b - r) / d + 2; break;
          case b: h = (r - g) / d + 4; break;
        }
        h /= 6;
      }
      return { h: h * 360, s: s * 100, l: l * 100 };
    }

    // ------------------- Gelişmiş Okunabilirlik (% Test) Modülü -------------------
    
    function getLuminance(r, g, b) {
      let a = [r, g, b].map(v => v <= 0.03928 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4));
      return a[0] * 0.2126 + a[1] * 0.7152 + a[2] * 0.0722;
    }

    // İki renk arasındaki tam WCAG Kontrast Oranını (1.0 - 21.0) döndürür
    function getContrastRatio(bgObj, textObj) {
      // ALFA YOKSAYILDI (Kullanıcı talebi): Arkaplan tahmini yapılamayacağı için saf RGB değerleri baz alınıyor
      const l1 = getLuminance(bgObj.red, bgObj.green, bgObj.blue);
      const l2 = getLuminance(textObj.red, textObj.green, textObj.blue);
      return (Math.max(l1, l2) + 0.05) / (Math.min(l1, l2) + 0.05);
    }

    // İnsan diline çevrilmiş (Yüzdelik) Okunabilirlik Skoru
    function getReadabilityPercentage(ratio) {
      if (ratio >= 7.0) return 100; // Mükemmel (AAA)
      if (ratio >= 4.5) return Math.round(75 + ((ratio - 4.5) / 2.5) * 25); // 75-100% arası (AA)
      if (ratio >= 3.0) return Math.round(40 + ((ratio - 3.0) / 1.5) * 35); // 40-75% arası
      return Math.max(0, Math.round(((ratio - 1.0) / 2.0) * 40)); // 0-40% arası
    }

    // İlgili zemin için seçili olan ideal metin rengini döndürür
    function getActiveTextObjForBg(bgObj) {
      let isAuto = (document.getElementById('text-color-mode').value === 'auto');
      if (isAuto) {
        let ratioW = getContrastRatio(bgObj, {red:1, green:1, blue:1, alpha:1});
        let ratioB = getContrastRatio(bgObj, {red:0, green:0, blue:0, alpha:1});
        return ratioW >= ratioB ? {red:1, green:1, blue:1, alpha:1} : {red:0, green:0, blue:0, alpha:1};
      } else {
        return customTextColorObj;
      }
    }

    // ------------------- Global Veriler -------------------
    let colors = [];
    let monthGradients = [];
    let auroraStops = [];
    const monthNames = ["Ocak", "Şubat", "Mart", "Nisan", "Mayıs", "Haziran", "Temmuz", "Ağustos", "Eylül", "Ekim", "Kasım", "Aralık"];

    let searchQuery = "";
    let sortMode = "similarity"; 

    // Metin Rengi Testi Durumu
    let customTextColorObj = { red: 168/255, green: 177/255, blue: 217/255, alpha: 1 }; // Varsayılan #A8B1D9
