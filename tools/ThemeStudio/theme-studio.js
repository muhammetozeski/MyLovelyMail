// ------------------- UI Helpers -------------------
    function showToast(msg) {
      const t = document.getElementById('toast');
      t.textContent = msg;
      t.classList.add('show');
      setTimeout(() => t.classList.remove('show'), 2500);
    }

    function copyToClipboard(text) {
      navigator.clipboard.writeText(text).then(() => showToast(text + " Kopyalandı!"))
      .catch(err => {
        const el = document.createElement('textarea');
        el.value = text; document.body.appendChild(el); el.select(); document.execCommand('copy'); document.body.removeChild(el);
        showToast(text + " Kopyalandı!");
      });
    }

    // ------------------- C# Kodu Ayrıştırıcı (Parser) -------------------
    function parseHexAdv(hex, isArgb) {
      hex = hex.replace('#', '');
      let r=0, g=0, b=0, a=1;
      if (hex.length === 6) {
        r = parseInt(hex.substring(0,2), 16)/255; g = parseInt(hex.substring(2,4), 16)/255; b = parseInt(hex.substring(4,6), 16)/255;
      } else if (hex.length === 8) {
        if (isArgb) { a = parseInt(hex.substring(0,2), 16)/255; r = parseInt(hex.substring(2,4), 16)/255; g = parseInt(hex.substring(4,6), 16)/255; b = parseInt(hex.substring(6,8), 16)/255; } 
        else { r = parseInt(hex.substring(0,2), 16)/255; g = parseInt(hex.substring(2,4), 16)/255; b = parseInt(hex.substring(4,6), 16)/255; a = parseInt(hex.substring(6,8), 16)/255; }
      }
      return { red: r, green: g, blue: b, alpha: a };
    }

    function evaluateColorExpression(expr) {
      let str = expr.replace(/\s+/g, '');
      let finalAlpha = null;
      const alphaMatch = str.match(/\.WithAlpha\(([\d.]+)[fF]?\)/);
      if (alphaMatch) { finalAlpha = parseFloat(alphaMatch[1]); str = str.split('.WithAlpha')[0]; }

      let colorObj = { red: 0, green: 0, blue: 0, alpha: 1 };
      if (str.startsWith('Color.FromArgb("') || str.startsWith('Color.FromRgba("')) {
        const hexMatch = str.match(/"([^"]+)"/);
        if (hexMatch) colorObj = parseHexAdv(hexMatch[1], str.startsWith('Color.FromArgb'));
      } else if (str.startsWith('Colors.')) {
        const name = str.substring(7);
        const parsed = resolveCSSColor(name);
        if (parsed) colorObj = parsed;
      }
      if (finalAlpha !== null) colorObj.alpha = finalAlpha;
      return colorObj;
    }

    function formatColorData(obj) {
      const r = Math.round(obj.red * 255), g = Math.round(obj.green * 255), b = Math.round(obj.blue * 255);
      const hex = `#${(1 << 24 | r << 16 | g << 8 | b).toString(16).slice(1).toUpperCase()}`;
      if (obj.alpha < 1) return { value: `rgba(${r},${g},${b},${obj.alpha.toFixed(2)})`, type: 'rgba', solidHex: hex, colorObj: obj };
      return { value: hex, type: 'hex', solidHex: hex, colorObj: obj };
    }

    document.getElementById('parse-btn').addEventListener('click', () => {
      const rawText = document.getElementById('csharp-input').value;
      if (!rawText.trim()) return;
      let code = rawText.replace(/\/\/.*$/gm, '').replace(/\/\*[\s\S]*?\*\//g, '');
      const colorExprStr = `(?:Color\\.(?:FromArgb|FromRgba)\\s*\\(\\s*"[^"]+"\\s*\\)|Colors\\.[A-Za-z]+)(?:\\.WithAlpha\\s*\\(\\s*[\\d.]+[fF]?\\s*\\))?`;
      const propRegex = new RegExp(`([A-Za-z0-9_]+)\\s*=\\s*(${colorExprStr})(?=\\s*[,;\\}])`, 'g');
      const gradientRegex = new RegExp(`\\(\\s*(${colorExprStr})\\s*,\\s*(${colorExprStr})\\s*\\)`, 'g');
      const auroraRegex = new RegExp(`new[^\\(]*\\(\\s*(${colorExprStr})\\s*,\\s*"([^"]+)"\\s*,\\s*"([^"]+)"\\s*\\)`, 'g');

      let parsedColors = [], parsedGradients = [], parsedAuroras = [], match;
      while ((match = propRegex.exec(code)) !== null) parsedColors.push({ name: match[1], ...formatColorData(evaluateColorExpression(match[2])) });
      while ((match = gradientRegex.exec(code)) !== null) parsedGradients.push({ pair: [formatColorData(evaluateColorExpression(match[1])).value, formatColorData(evaluateColorExpression(match[2])).value] }); 
      while ((match = auroraRegex.exec(code)) !== null) parsedAuroras.push({ color: formatColorData(evaluateColorExpression(match[1])).value, pos1: match[2], pos2: match[3] });

      if (parsedColors.length > 0) colors = parsedColors;
      if (parsedGradients.length > 0) monthGradients = parsedGradients;
      if (parsedAuroras.length > 0) auroraStops = parsedAuroras;

      initializeDataObjects();
      document.getElementById('search-input').value = ''; searchQuery = '';
      updateSortUi(); renderAll(); showToast("Başarıyla Aktarıldı!");
    });

    // ------------------- C# Export (Dışa Aktarım) -------------------
    document.getElementById('export-btn').addEventListener('click', () => {
      let output = `public static readonly AppTheme CustomTheme = new()\n{\n    Name = "Exported Theme",\n\n`;
      colors.forEach(c => {
        let code = c.colorObj.alpha < 1 ? `Color.FromArgb("${c.solidHex}").WithAlpha(${c.colorObj.alpha.toFixed(2)}f)` : `Color.FromArgb("${c.solidHex}")`;
        output += `    ${c.name.padEnd(20)} = ${code},\n`;
      });
      if(monthGradients.length > 0) {
        output += `\n    MonthGradients =\n    [\n`;
        monthGradients.forEach((g, idx) => {
          let c1 = `Color.FromArgb("${g.pair[0].startsWith('rgb') ? rgbaToHex(g.pair[0]) : g.pair[0]}")`;
          let c2 = `Color.FromArgb("${g.pair[1].startsWith('rgb') ? rgbaToHex(g.pair[1]) : g.pair[1]}")`;
          output += `        (${c1}, ${c2})${idx < monthGradients.length-1 ? ',' : ''}\n`;
        });
        output += `    ],\n`;
      }
      if(auroraStops.length > 0) {
        output += `\n    AuroraStops =\n    [\n`;
        auroraStops.forEach((a, idx) => {
          let col = `Color.FromArgb("${a.color.startsWith('rgb') ? rgbaToHex(a.color) : a.color}")`;
          output += `        new(${col}, "${a.pos1}", "${a.pos2}")${idx < auroraStops.length-1 ? ',' : ''}\n`;
        });
        output += `    ]\n`;
      }
      output += `};`;
      document.getElementById('csharp-input').value = output; copyToClipboard(output);
    });

    function initializeDataObjects() {
      colors.forEach(c => { if (!c.colorObj) c.colorObj = FromArgb(c.solidHex); });
      monthGradients.forEach((g) => {
        const c1 = FromArgb(g.pair[0].startsWith('rgb') ? rgbaToHex(g.pair[0]) : g.pair[0]);
        const c2 = FromArgb(g.pair[1].startsWith('rgb') ? rgbaToHex(g.pair[1]) : g.pair[1]);
        g.colorObj = { red: (c1.red+c2.red)/2, green: (c1.green+c2.green)/2, blue: (c1.blue+c2.blue)/2, alpha: (c1.alpha+c2.alpha)/2 };
      });
      auroraStops.forEach(stop => { stop.colorObj = FromArgb(stop.color.startsWith('rgb') ? rgbaToHex(stop.color) : stop.color); });
    }

    // ------------------- Filtreleme & Sıralama -------------------
    function sortList(list) {
      const targetColor = parseSearchQueryToColor(searchQuery);
      if (sortMode === "name-asc") list.sort((a, b) => a.name.localeCompare(b.name));
      else if (sortMode === "name-desc") list.sort((a, b) => b.name.localeCompare(a.name));
      else if (sortMode === "readability") {
         // Okunabilirliğe (Kontrast Oranına) göre büyükten küçüğe sırala
         list.sort((a, b) => {
           let objA = a.colorObj || (a.pair && a.pair.colorObj); let objB = b.colorObj || (b.pair && b.pair.colorObj);
           let txtA = getActiveTextObjForBg(objA); let txtB = getActiveTextObjForBg(objB);
           return getContrastRatio(objB, txtB) - getContrastRatio(objA, txtA);
         });
      }
      else if (sortMode === "similarity") {
        if (targetColor) {
           list.sort((a, b) => {
             let objA = a.colorObj || (a.pair && a.pair.colorObj); let objB = b.colorObj || (b.pair && b.pair.colorObj);
             return calculateSimilarity(objB, targetColor) - calculateSimilarity(objA, targetColor);
           });
        } else {
           list.sort((a, b) => {
             let hslA = getHSL(a.colorObj || (a.pair && a.pair.colorObj)); let hslB = getHSL(b.colorObj || (b.pair && b.pair.colorObj));
             let isGrayA = hslA.s < 10 || hslA.l < 15 || hslA.l > 95; let isGrayB = hslB.s < 10 || hslB.l < 15 || hslB.l > 95;
             if(isGrayA && !isGrayB) return 1; if(!isGrayA && isGrayB) return -1;
             if(isGrayA && isGrayB) return hslB.l - hslA.l; 
             return hslA.h - hslB.h; 
           });
        }
      } else list.sort((a, b) => a.originalIndex - b.originalIndex);
      return list;
    }

    function processItems(sourceArray, isGradient) {
      let list = sourceArray.map((item, idx) => {
        let copy = { ...item, originalIndex: idx };
        if(isGradient) copy.name = monthNames[idx] || `Ay ${idx + 1}`; else if(!copy.name) copy.name = `Stop ${idx + 1}`;
        return copy;
      });

      if (searchQuery.trim() !== "") {
        const q = searchQuery.toLowerCase().trim(); const targetColor = parseSearchQueryToColor(searchQuery);
        list = list.filter(item => {
          const nameMatches = item.name.toLowerCase().includes(q) || (item.pos1 && item.pos1.toLowerCase().includes(q));
          let valMatches = false;
          if(isGradient) valMatches = item.pair[0].toLowerCase().includes(q) || item.pair[1].toLowerCase().includes(q);
          else valMatches = (item.value && item.value.toLowerCase().includes(q)) || (item.color && item.color.toLowerCase().includes(q));
          let simMatches = false;
          if (targetColor) { let cObj = item.colorObj || (item.pair && item.pair.colorObj); simMatches = calculateSimilarity(cObj, targetColor) >= 60; }
          return nameMatches || valMatches || simMatches;
        });
      }
      return sortList(list);
    }

    // ------------------- Görselleştirme (DOM) -------------------
    function renderList(containerId, processedData, type) {
      const list = document.getElementById(containerId);
      list.innerHTML = '';
      if (processedData.length === 0) { list.innerHTML = `<div class="empty-state">🔍 Aranan kriterlere uygun renk bulunamadı.</div>`; return; }
      
      const targetColor = parseSearchQueryToColor(searchQuery);

      processedData.forEach((item) => {
        const li = document.createElement('li');
        li.className = 'color-item';
        
        let bgColorStr = ""; let colorObj = null;
        if (type === 'color') { bgColorStr = item.solidHex; colorObj = item.colorObj; }
        else if (type === 'gradient') { bgColorStr = `linear-gradient(to right, ${item.pair[0]}, ${item.pair[1]})`; colorObj = item.colorObj || (item.pair && item.pair.colorObj); }
        else if (type === 'aurora') { bgColorStr = item.color.startsWith('rgb') ? rgbaToHex(item.color) : item.color; colorObj = item.colorObj; }

        li.style.background = bgColorStr;
        
        // --- OKUNABİLİRLİK MATEMATİĞİ ---
        let isAuto = (document.getElementById('text-color-mode').value === 'auto');
        let activeTextObj = getActiveTextObjForBg(colorObj);

        let ratio = getContrastRatio(colorObj, activeTextObj);
        let perc = getReadabilityPercentage(ratio);
        let hexColorForText = rgbaToHex(`rgba(${Math.round(activeTextObj.red*255)},${Math.round(activeTextObj.green*255)},${Math.round(activeTextObj.blue*255)})`);

        // Okunabilirlik Yüzde Rozeti Sınıfı
        let pClass = perc >= 75 ? 'readability-100' : (perc >= 45 ? 'readability-75' : 'readability-50');
        let pIcon = perc >= 75 ? '✓' : (perc >= 45 ? '!' : '✗');
        
        // Sol Bilgi Alanı
        const infoDiv = document.createElement('div');
        infoDiv.className = 'color-info';
        
        const nameSpan = document.createElement('span');
        nameSpan.className = 'name-preview';
        nameSpan.textContent = item.name;
        
        // Rozet/Yazı Görünüm Kontrolü ve Gölge iptali
        if (isAuto) {
           nameSpan.classList.add('badge-mode');
        } else {
           nameSpan.style.color = hexColorForText;
           nameSpan.classList.remove('badge-mode');
           // GÖLGE KALDIRILDI! Zayıf okunabilirlikli bir renk seçtiğinde gerçekten okunmaz olacak.
        }
        infoDiv.appendChild(nameSpan);

        if(type === 'aurora') {
          const p1 = document.createElement('span'); p1.className='badge'; p1.style.background='rgba(0,0,0,0.4)'; p1.textContent = item.pos1;
          const p2 = document.createElement('span'); p2.className='badge'; p2.style.background='rgba(0,0,0,0.4)'; p2.textContent = item.pos2;
          infoDiv.appendChild(p1); infoDiv.appendChild(p2);
        }

        if (targetColor && searchQuery.trim() !== "") {
          const sim = calculateSimilarity(colorObj, targetColor);
          const simBadge = document.createElement('span'); simBadge.className = 'badge badge-sim'; simBadge.textContent = `%${sim} Benzer`;
          infoDiv.appendChild(simBadge);
        }

        // --- YENİ DİNAMİK ROZET RENGİ ---
        // Rozetin üstündeki yazının (#000 veya #FFF) tam zıttını rozet arkaplanı yapıyoruz
        // Alfa yoksayılarak direkt RGB luminans hesabı yapılıyor
        let badgeLuminance = getLuminance(colorObj.red, colorObj.green, colorObj.blue);
        let badgeTextColor = badgeLuminance > 0.5 ? '#000000' : '#FFFFFF';
        let badgeBgColor = badgeTextColor === '#FFFFFF' ? 'rgba(0,0,0,0.5)' : 'rgba(255,255,255,0.7)';

        const contBadge = document.createElement('span');
        contBadge.className = `readability-badge ${pClass}`;
        contBadge.style.color = badgeTextColor;
        contBadge.style.backgroundColor = badgeBgColor;
        contBadge.style.borderColor = badgeTextColor === '#FFFFFF' ? 'rgba(255,255,255,0.2)' : 'rgba(0,0,0,0.2)';
        contBadge.innerHTML = `<strong>${pIcon}</strong> %${perc} Okunabilir`;
        contBadge.title = `Kontrast Oranı: ${ratio.toFixed(2)}:1`;
        infoDiv.appendChild(contBadge);

        li.appendChild(infoDiv);

        // Sağ Kontroller (Sabit görünümlü Hex ve Renk Seçiciler)
        const controlsDiv = document.createElement('div');
        controlsDiv.className = 'color-controls';

        if (type === 'gradient') {
          const hex1 = document.createElement('input'); hex1.className = 'hex-input'; hex1.readOnly = true; hex1.value = item.pair[0]; hex1.onclick = () => copyToClipboard(hex1.value);
          const btn1 = document.createElement('button'); btn1.className = 'btn-action'; btn1.textContent = '🎨'; btn1.onclick = (e) => { e.stopPropagation(); openWidget(e, {type: 'gradient', rowIdx: item.originalIndex, colorIdx: 0}); };
          const hex2 = document.createElement('input'); hex2.className = 'hex-input'; hex2.readOnly = true; hex2.value = item.pair[1]; hex2.onclick = () => copyToClipboard(hex2.value);
          const btn2 = document.createElement('button'); btn2.className = 'btn-action'; btn2.textContent = '🎨'; btn2.onclick = (e) => { e.stopPropagation(); openWidget(e, {type: 'gradient', rowIdx: item.originalIndex, colorIdx: 1}); };
          controlsDiv.appendChild(hex1); controlsDiv.appendChild(btn1); controlsDiv.appendChild(hex2); controlsDiv.appendChild(btn2);
        } else {
          const hexInp = document.createElement('input'); hexInp.className = 'hex-input'; hexInp.readOnly = true; hexInp.value = type === 'color' ? item.value : item.color; hexInp.onclick = () => copyToClipboard(hexInp.value);
          const btn = document.createElement('button'); btn.className = 'btn-action'; btn.textContent = '🎨'; btn.onclick = (e) => { e.stopPropagation(); openWidget(e, {type: type, rowIdx: item.originalIndex}); };
          controlsDiv.appendChild(hexInp); controlsDiv.appendChild(btn);
        }

        li.appendChild(controlsDiv); list.appendChild(li);
      });
    }

    function renderAll() {
      renderList('color-list', processItems(colors, false), 'color');
      renderList('gradient-list', processItems(monthGradients, true), 'gradient');
      renderList('aurora-list', processItems(auroraStops, false), 'aurora');
    }

    function updateSortUi() {
      const targetColor = parseSearchQueryToColor(searchQuery);
      const simBtn = document.getElementById('sort-btn-similarity');
      const previewDiv = document.getElementById('search-color-preview');

      if (targetColor && searchQuery.trim() !== "") {
        const rgbaStr = `rgba(${Math.round(targetColor.red*255)}, ${Math.round(targetColor.green*255)}, ${Math.round(targetColor.blue*255)}, ${targetColor.alpha})`;
        previewDiv.style.backgroundColor = rgbaStr; previewDiv.style.display = 'block';
      } else {
        previewDiv.style.display = 'none';
      }

      document.querySelectorAll('.btn-sort').forEach(btn => btn.classList.remove('active'));
      const activeBtn = document.getElementById(`sort-btn-${sortMode}`);
      if (activeBtn) activeBtn.classList.add('active');
    }

    // ------------------- Simülatör ve Test Olayları -------------------
    document.getElementById('text-color-mode').addEventListener('change', (e) => {
      const isCustom = e.target.value === 'custom';
      document.getElementById('custom-color-controls').style.display = isCustom ? 'flex' : 'none';
      renderAll();
    });

    document.getElementById('custom-text-color').addEventListener('input', (e) => {
      const val = e.target.value;
      customTextColorObj = FromArgb(val);
      renderAll();
    });

    document.getElementById('color-blindness-mode').addEventListener('change', (e) => {
      const val = e.target.value;
      const simContainer = document.getElementById('simulated-container');
      if(val === 'none') {
        simContainer.style.filter = 'none';
      } else {
        simContainer.style.filter = `url(#${val})`;
      }
    });

    // ------------------- Olaylar (Events) -------------------
    document.getElementById('search-input').addEventListener('input', (e) => { searchQuery = e.target.value; updateSortUi(); renderAll(); });
    document.getElementById('sort-btn-default').addEventListener('click', () => { sortMode = 'default'; updateSortUi(); renderAll(); });
    document.getElementById('sort-btn-name-asc').addEventListener('click', () => { sortMode = 'name-asc'; updateSortUi(); renderAll(); });
    document.getElementById('sort-btn-readability').addEventListener('click', () => { sortMode = 'readability'; updateSortUi(); renderAll(); });
    document.getElementById('sort-btn-similarity').addEventListener('click', () => { sortMode = 'similarity'; updateSortUi(); renderAll(); });

    // Info Modal
    const modal = document.getElementById('info-modal');
    document.getElementById('btn-info').addEventListener('click', () => modal.classList.add('active'));
    document.getElementById('close-modal').addEventListener('click', () => modal.classList.remove('active'));
    modal.addEventListener('click', (e) => { if(e.target === modal) modal.classList.remove('active'); });

    // ------------------- Widget Mantığı -------------------
    let widgetTarget = null, widgetPosX = 150, widgetPosY = 150, isDragging = false, dragOffsetX = 0, dragOffsetY = 0;
    const widget = document.getElementById('color-picker-widget'), widgetHeader = document.getElementById('widget-header'), widgetColorInput = document.getElementById('widget-color-input');

    function openWidget(event, targetInfo) {
      widgetTarget = targetInfo;
      let currentHex = "#000000";
      if (targetInfo.type === 'color') currentHex = colors[targetInfo.rowIdx].solidHex;
      else if (targetInfo.type === 'gradient') { const val = monthGradients[targetInfo.rowIdx].pair[targetInfo.colorIdx]; currentHex = val.startsWith('rgb') ? rgbaToHex(val) : val; }
      else if (targetInfo.type === 'aurora') { const val = auroraStops[targetInfo.rowIdx].color; currentHex = val.startsWith('rgb') ? rgbaToHex(val) : val; }
      
      widgetColorInput.value = currentHex;
      if (widgetPosX === null || widgetPosY === null) {
        const rect = event.currentTarget.getBoundingClientRect();
        widgetPosX = rect.right + 8; widgetPosY = rect.top;
      }
      widget.style.left = widgetPosX + "px"; widget.style.top = widgetPosY + "px";
      widget.style.display = 'block';
      widgetColorInput.click();
    }

    widgetColorInput.addEventListener('input', (e) => {
      const newHex = e.target.value;
      if (!widgetTarget) return;
      if (widgetTarget.type === 'color') { colors[widgetTarget.rowIdx].value = newHex; colors[widgetTarget.rowIdx].solidHex = newHex; } 
      else if (widgetTarget.type === 'gradient') { monthGradients[widgetTarget.rowIdx].pair[widgetTarget.colorIdx] = newHex; } 
      else if (widgetTarget.type === 'aurora') { auroraStops[widgetTarget.rowIdx].color = newHex; }
      initializeDataObjects(); renderAll();
    });

    document.addEventListener('click', (e) => {
      if (widget.contains(e.target) || e.target.closest('.btn-action')) return;
      widget.style.display = 'none'; widgetTarget = null;
    });

    widgetHeader.addEventListener('mousedown', (e) => {
      isDragging = true; const rect = widget.getBoundingClientRect();
      dragOffsetX = e.clientX - rect.left; dragOffsetY = e.clientY - rect.top; e.preventDefault();
    });
    document.addEventListener('mousemove', (e) => {
      if (!isDragging) return;
      widgetPosX = e.clientX - dragOffsetX; widgetPosY = e.clientY - dragOffsetY;
      widget.style.left = widgetPosX + 'px'; widget.style.top = widgetPosY + 'px';
    });
    document.addEventListener('mouseup', () => { isDragging = false; });

    // ------------------- İlk Çalıştırma -------------------
    document.addEventListener('DOMContentLoaded', () => {
      updateSortUi(); renderAll();
    });
