// PDF 뷰어: 호스트(PdfViewer.exe)와 PDF.js 뷰어를 잇는 스크립트.
// 뷰어 페이지(와 시작 화면)가 만들어질 때 PDF.js보다 먼저 실행된다.
(function () {
  "use strict";
  if (!window.chrome || !window.chrome.webview) return;
  if (location.hostname !== "pdfjs.local") return;
  var wv = window.chrome.webview;
  var isViewer = location.pathname.indexOf("/web/viewer.html") >= 0;
  function post(s) { try { wv.postMessage(String(s)); } catch (e) {} }

  // 1) 뷰어 기본 설정 (이미 값이 있으면 건드리지 않음)
  //    댓글, 서명, 글자 선택 시 형광펜 버튼, 페이지 관리(선택·삭제·추출·순서 바꾸기), 파일 추가(합치기), 밝은 테마 고정
  try {
    var KEY = "pdfjs.preferences";
    var want = {
      enableComment: true,
      enableSignatureEditor: true,
      enableHighlightFloatingButton: true,
      enableSplitMerge: true,
      enableMerge: true,
      viewerCssTheme: 1
    };
    var prefs = null;
    try { prefs = JSON.parse(localStorage.getItem(KEY) || "null"); } catch (e) { prefs = null; }
    if (!prefs || typeof prefs !== "object") prefs = {};
    var changed = false;
    for (var k in want) { if (!(k in prefs)) { prefs[k] = want[k]; changed = true; } }
    if (changed) localStorage.setItem(KEY, JSON.stringify(prefs));
  } catch (e) {}

  // 1b) 뷰어 툴바 색을 프로그램 헤더에 맞춤 (흰 툴바, 연한 테두리, 남색 진행 막대)
  if (isViewer) {
    var css =
      ":root{--toolbar-bg-color:#fff !important;--toolbar-border-color:#d9dde3 !important;--body-bg-color:#e9ebef !important;" +
      "--sidebar-bg-color:#fff !important;--sidebar-toolbar-bg-color:#f5f6f8 !important;--progressBar-color:#1b4e91 !important;" +
      "--progressBar-bg-color:#dfe3ea !important;--button-background-color-hover:rgba(27,78,145,.10);--button-background-color-active:rgba(27,78,145,.16)}" +
      "#toolbarContainer{box-shadow:none}";
    function addStyle() {
      try {
        var st = document.createElement("style");
        st.id = "hostTheme";
        st.textContent = css;
        (document.head || document.documentElement).appendChild(st);
      } catch (e) {}
    }
    if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", addStyle);
    else addStyle();
  }

  var app = null;
  var saveAsNext = false;    // 다음 저장은 "다른 이름으로"
  var exportNext = false;    // 다음 저장은 "추출한 페이지를 다른 파일로"
  var rotateSaveNext = false; // 다음 저장은 "회전 저장" (끝나면 다시 불러옴)
  var cancelled = false;     // 내보내기 취소 요청
  var flattenAuto = false;   // 저장할 때 서명·그림을 쪽 내용으로 박아 넣기 (호스트의 설정; 문서가 열리면 호스트가 알려 준다)
  var workerSaved = false;   // 방금 넘어온 저장 데이터가 PDF.js의 저장 처리(SaveDocument)를 거친 것인지
  var saveFailed = false;    // 방금 PDF.js의 저장 처리가 실패했는지 (그 뒤에 넘어오는 것은 수정 내용이 빠진 원본이다)

  // 수정 상태: 주석(annotationStorage) + 페이지 구조(삭제·순서·합치기)
  var annDirty = false;
  var savedStruct = "";
  var dirty = false;

  function structKey() {
    try {
      var tv = app && app.pdfThumbnailViewer;
      if (!tv || !tv.hasStructuralChanges()) return "";
      var c = tv.getStructuralChanges();
      return c ? JSON.stringify(c) : "";
    } catch (e) { return ""; }
  }
  function recomputeDirty() {
    var merged = !!(app && app._mergedDocumentNeedsSaving);
    var v = annDirty || merged || (structKey() !== savedStruct);
    if (dirty !== v) { dirty = v; window.__hostDirty = v; post("dirty|" + (v ? "1" : "0")); }
  }
  function markSaved() {
    annDirty = false;
    savedStruct = structKey();
    recomputeDirty();
  }

  // 2) 화면 아래 알림
  function toast(text, isError) {
    try {
      var el = document.getElementById("hostToast");
      if (!el) {
        el = document.createElement("div");
        el.id = "hostToast";
        el.style.cssText = "position:fixed;left:50%;bottom:28px;transform:translateX(-50%);z-index:100000;" +
          "padding:10px 18px;border-radius:8px;font:14px/1.4 'Malgun Gothic','Segoe UI',sans-serif;color:#fff;" +
          "background:rgba(30,30,30,.92);box-shadow:0 4px 16px rgba(0,0,0,.35);pointer-events:none;opacity:0;" +
          "transition:opacity .2s;max-width:70vw;white-space:nowrap;overflow:hidden;text-overflow:ellipsis";
        (document.body || document.documentElement).appendChild(el);
      }
      el.style.background = isError ? "rgba(170,30,30,.95)" : "rgba(30,30,30,.92)";
      el.textContent = text;
      el.style.opacity = "1";
      clearTimeout(el._t);
      el._t = setTimeout(function () { el.style.opacity = "0"; }, isError ? 5000 : 2200);
    } catch (e) {}
  }

  function save() {
    if (!app || !app.pdfDocument) { toast("열린 문서가 없습니다", true); return; }
    saveAsNext = false;
    app.downloadOrSave();
  }
  function saveAs() {
    if (!app || !app.pdfDocument) { toast("열린 문서가 없습니다", true); return; }
    saveAsNext = true;
    app.downloadOrSave();
  }
  function doPrint() {
    if (!app || !app.pdfDocument) { toast("열린 문서가 없습니다", true); return; }
    try {
      if (typeof app.triggerPrinting === "function") app.triggerPrinting();
      else window.print();
    } catch (e) { toast("인쇄 실패: " + e, true); }
  }

  // 회전 저장: pdf.js(패치됨)의 증분 저장에 /Rotate 변경을 끼워 넣는다. 주석·책갈피는 그대로 보존된다.
  function doRotate(pages, delta) {
    if (!app || !app.pdfDocument) { toast("열린 문서가 없습니다", true); return; }
    var map = {};
    for (var i = 0; i < pages.length; i++) map[String(pages[i] - 1)] = delta;
    var tr = app.pdfDocument._transport;
    tr.pageRotations = map;
    rotateSaveNext = true;
    Promise.resolve()
      .then(function () { return app.save(); })
      .then(null, function (e) { rotateSaveNext = false; toast("회전 저장 실패: " + e, true); })
      .then(function () { tr.pageRotations = null; });
  }

  // 서명·주석 박아 넣기: pdf.js(패치됨)가 저장할 때 주석의 모양을 쪽 내용으로 옮기고 주석 목록에서 뺀다.
  // 그러면 다른 프로그램에서 주석처럼 따로 집어 옮기거나 복사·삭제할 수 없다.
  //   평소 저장: 설정이 켜져 있으면 이번 저장에서 새로 넣었거나 고친 서명·그림(Stamp)만
  //   도구 메뉴: 고른 종류를 문서에 이미 있던 것까지 전부 (끝나면 다시 불러옴)
  function setFlattenDefault() {
    try {
      var tr = app && app.pdfDocument && app.pdfDocument._transport;
      if (!tr) return;
      tr.pkFlatten = flattenAuto ? { subtypes: ["Stamp"], all: false } : null;
      if (!tr.__hostSaveWrapped) {
        // 저장 데이터가 PDF.js의 저장 처리를 거쳤는지 표시해 둔다 (거치지 않은 "원본 그대로 저장"과 구별).
        // 저장 처리가 실패하면 PDF.js는 원본을 그대로 내려받는 쪽으로 넘어가는데, 그것을 파일에 덮어쓰면
        // 수정한 내용이 빠진 채 "저장됨"이 되므로 실패도 표시해 둔다.
        var orig = tr.saveDocument;
        tr.__hostSaveWrapped = true;
        tr.saveDocument = function () {
          workerSaved = false;
          saveFailed = false;
          return orig.apply(this, arguments).then(
            function (d) { workerSaved = true; return d; },
            function (e) {
              saveFailed = true;
              setTimeout(function () { saveFailed = false; }, 3000);
              post("log|saveDocument failed: " + (e && e.message ? e.message : e));
              throw e;
            });
        };
      }
    } catch (e) {}
  }
  // 방금 끝난 저장에서 박아 넣은 결과 { done, skipped, blocked, failedPages } (한 번 읽으면 지워진다)
  function flattenResult() {
    try {
      var tr = app.pdfDocument._transport;
      return tr.messageHandler.sendWithPromise("PkFlattenResult", null).then(null, function () { return null; });
    } catch (e) { return Promise.resolve(null); }
  }
  function postSave(data, name, query) {
    var body = (typeof Blob !== "undefined" && data instanceof Blob) ? data : new Blob([data], { type: "application/pdf" });
    return fetch("https://pdfjs.local/save/" + encodeURIComponent(name) + (query || ""), { method: "POST", body: body, cache: "no-store" })
      .then(function (r) { return r.json(); });
  }
  function flattenReply(o) {
    var s = "";
    for (var k in o) s += k + "=" + String(o[k]).replace(/[;\r\n]/g, " ") + ";";
    post("flattendone|" + s);
  }
  function doFlatten(subtypes) {
    if (!app || !app.pdfDocument) { flattenReply({ error: "열린 문서가 없습니다" }); return; }
    var doc = app.pdfDocument, tr = doc._transport;
    var wasDirty = annDirty;
    function keepDirty() { if (wasDirty) { annDirty = true; recomputeDirty(); } }
    setFlattenDefault();
    tr.pkFlatten = { subtypes: subtypes, all: true };
    Promise.resolve()
      .then(function () { return doc.saveDocument(); })
      .then(function (data) {
        workerSaved = false;
        return flattenResult().then(function (res) {
          res = res || {};
          var info = { done: res.done | 0, skipped: res.skipped | 0, blocked: res.blocked | 0, failed: res.failedPages | 0 };
          if (!info.done) {
            // 박아 넣은 것이 없으면 파일을 건드리지 않는다
            keepDirty();
            flattenReply(info);
            return;
          }
          return postSave(data, app._docFilename || "document.pdf", "").then(function (r) {
            if (r && r.ok) {
              markSaved();
              flattenReply(info);
              post("reload");
            } else {
              keepDirty();
              info.error = (r && r.cancelled) ? "저장을 취소했습니다" : ((r && r.error) || "파일을 쓰지 못했습니다");
              flattenReply(info);
            }
          });
        });
      })
      .then(null, function (e) {
        workerSaved = false;
        saveFailed = false;
        keepDirty();
        flattenReply({ error: (e && e.message) ? e.message : String(e) });
      })
      .then(function () { setFlattenDefault(); });
  }

  // PDF.js가 띄우는 브라우저의 "변경 사항이 저장되지 않았습니다" 경고창 막기 (저장 여부는 탭을 닫을 때 프로그램이 직접 묻는다)
  window.addEventListener("beforeunload", function (e) { e.stopImmediatePropagation(); }, true);

  // 3) 단축키 (PDF.js보다 먼저 받음)
  //    Ctrl+Shift+S 다른 이름으로 저장, Ctrl+W 탭 닫기, Ctrl+Tab / Ctrl+Shift+Tab / Ctrl+PgDn / Ctrl+PgUp 탭 이동,
  //    F5·Ctrl+R 막기(편집 내용 보호), F12 개발자 도구, 시작 화면에서는 Ctrl+O 열기
  window.addEventListener("keydown", function (e) {
    var ctrl = e.ctrlKey || e.metaKey;
    var k = (e.key || "").toLowerCase();
    if (ctrl && e.shiftKey && k === "s") { e.preventDefault(); e.stopImmediatePropagation(); saveAs(); return; }
    if (ctrl && !e.shiftKey && !e.altKey && k === "w") { e.preventDefault(); e.stopImmediatePropagation(); post("close"); return; }
    if (ctrl && (k === "tab" || k === "pagedown" || k === "pageup")) {
      e.preventDefault(); e.stopImmediatePropagation();
      var back = k === "pageup" || (k === "tab" && e.shiftKey);
      post(back ? "prevtab" : "nexttab");
      return;
    }
    if (!isViewer && ctrl && !e.shiftKey && k === "o") { e.preventDefault(); e.stopImmediatePropagation(); post("open"); return; }
    if (k === "f5" || (ctrl && !e.shiftKey && k === "r")) { e.preventDefault(); e.stopImmediatePropagation(); return; }
    if (k === "f12") { e.preventDefault(); e.stopImmediatePropagation(); post("devtools"); return; }
  }, true);

  // 4) 열기 버튼 / Ctrl+O → 호스트의 열기 대화상자 (실제 경로를 알아야 덮어쓰기 저장이 되므로)
  document.addEventListener("click", function (e) {
    var t = e.target;
    if (t && t.id === "fileInput") { e.preventDefault(); e.stopImmediatePropagation(); post("open"); }
  }, true);

  // 5) 파일 끌어다 놓기 → 호스트가 실제 경로로 연다 (페이지 관리자 안으로의 끌어놓기는 PDF.js에 맡긴다: 그림을 쪽으로 끼워 넣기 등)
  function hasFiles(e) {
    try { return !!e.dataTransfer && Array.prototype.indexOf.call(e.dataTransfer.types, "Files") >= 0; }
    catch (x) { return false; }
  }
  function insideViewsManager(e) {
    try { var vm = document.getElementById("viewsManager"); return !!(vm && e.target && vm.contains(e.target)); }
    catch (x) { return false; }
  }
  window.addEventListener("dragover", function (e) {
    if (!hasFiles(e) || insideViewsManager(e)) return;
    e.preventDefault(); e.stopImmediatePropagation();
    try { e.dataTransfer.dropEffect = "copy"; } catch (x) {}
  }, true);
  window.addEventListener("drop", function (e) {
    if (!hasFiles(e) || insideViewsManager(e)) return;
    e.preventDefault(); e.stopImmediatePropagation();
    // PDF는 탭으로 열고, 그림 파일은 호스트가 PDF로 바꾸는 창을 띄운다
    var files = Array.prototype.filter.call(e.dataTransfer.files || [], function (f) {
      return /\.(pdf|jpe?g|jpe|jfif|png|bmp|dib|gif|tiff?|webp|heic|heif|avif)$/i.test(f.name);
    });
    if (!files.length) { toast("PDF나 그림 파일만 열 수 있습니다", true); return; }
    try { wv.postMessageWithAdditionalObjects("drop", files); }
    catch (x) { toast("파일을 열지 못했습니다: " + x, true); }
  }, true);

  // 6) 내보내기 도우미: 호스트의 /export/<이름> 으로 파일을 보낸다 (호스트가 정한 폴더에 저장)
  function postFile(name, blob) {
    return fetch("https://pdfjs.local/export/" + encodeURIComponent(name), { method: "POST", body: blob, cache: "no-store" })
      .then(function (r) { return r.json(); })
      .then(function (j) { if (!j.ok) throw new Error(j.error || "파일을 쓰지 못했습니다"); });
  }
  function pad(n, w) { var s = String(n); while (s.length < w) s = "0" + s; return s; }
  function withTimeout(p, ms) {
    return new Promise(function (res) {
      var t = setTimeout(function () { res(null); }, ms);
      p.then(function (v) { clearTimeout(t); res(v); }, function () { clearTimeout(t); res(null); });
    });
  }
  function getObj(page, name) {
    return withTimeout(new Promise(function (res, rej) {
      var store = (typeof name === "string" && name.indexOf("g_") === 0) ? page.commonObjs : page.objs;
      try { store.get(name, res); } catch (e) { rej(e); }
    }), 8000);
  }
  function canvasToBlob(c, mime, q) { return new Promise(function (res) { c.toBlob(res, mime, q); }); }

  // PDF.js가 넘겨주는 그림 객체(ImageBitmap 또는 원시 픽셀)를 캔버스로
  function imgToCanvas(img) {
    var w = img.width, h = img.height;
    if (!w || !h) return null;
    var c = document.createElement("canvas");
    c.width = w; c.height = h;
    var ctx = c.getContext("2d");
    if (img.bitmap) { ctx.drawImage(img.bitmap, 0, 0); return c; }
    if (!img.data) return null;
    var out = ctx.createImageData(w, h), d = out.data, src = img.data, kind = img.kind, i, j, o;
    if (kind === 3) {               // RGBA 32bpp
      d.set(src.subarray(0, w * h * 4));
    } else if (kind === 2) {        // RGB 24bpp
      for (i = 0, j = 0; i < w * h; i++, j += 3) { o = i * 4; d[o] = src[j]; d[o + 1] = src[j + 1]; d[o + 2] = src[j + 2]; d[o + 3] = 255; }
    } else if (kind === 1) {        // 흑백 1bpp (비트 1 = 흰색)
      var rowBytes = (w + 7) >> 3;
      for (var y = 0; y < h; y++) {
        for (var x = 0; x < w; x++) {
          var bit = (src[y * rowBytes + (x >> 3)] >> (7 - (x & 7))) & 1;
          var v = bit ? 255 : 0;
          o = (y * w + x) * 4; d[o] = d[o + 1] = d[o + 2] = v; d[o + 3] = 255;
        }
      }
    } else return null;
    ctx.putImageData(out, 0, 0);
    return c;
  }

  // 문서에 들어 있는 그림(XObject) 꺼내기 → PNG
  async function extractImages() {
    var doc = app.pdfDocument, OPS = globalThis.pdfjsLib.OPS, seen = {}, count = 0;
    for (var p = 1; p <= doc.numPages && !cancelled; p++) {
      post("progress|" + count + "|" + p + " / " + doc.numPages + " 쪽 살피는 중 (" + count + "개 저장)");
      var page = await doc.getPage(p);
      var ops = await page.getOperatorList();
      for (var i = 0; i < ops.fnArray.length && !cancelled; i++) {
        var fn = ops.fnArray[i], img = null;
        if (fn === OPS.paintImageXObject || fn === OPS.paintImageXObjectRepeat) {
          var key = ops.argsArray[i][0];
          if (seen[key]) continue;
          seen[key] = true;
          img = await getObj(page, key);
        } else if (fn === OPS.paintInlineImageXObject) {
          img = ops.argsArray[i][0];
        } else continue;
        if (!img || !img.width || !img.height || img.width < 24 || img.height < 24) continue;
        var c = imgToCanvas(img);
        if (!c) continue;
        var blob = await canvasToBlob(c, "image/png");
        c.width = c.height = 0;
        if (!blob) continue;
        count++;
        await postFile(pad(p, 3) + "쪽_" + pad(count, 3) + ".png", blob);
      }
      try { page.cleanup(); } catch (e) {}
    }
    return count;
  }

  // 페이지를 그림으로 렌더 → PNG/JPG
  async function pagesToImages(opts) {
    var doc = app.pdfDocument, count = 0;
    var jpeg = opts.format === "jpeg";
    var mime = jpeg ? "image/jpeg" : "image/png", ext = jpeg ? ".jpg" : ".png";
    var dpi = Math.max(36, Math.min(600, opts.dpi || 150));
    for (var k = 0; k < opts.pages.length && !cancelled; k++) {
      var n = opts.pages[k];
      post("progress|" + count + "|" + (k + 1) + " / " + opts.pages.length + " 쪽 그리는 중");
      var page = await doc.getPage(n);
      var vp = page.getViewport({ scale: dpi / 72 });
      var c = document.createElement("canvas");
      c.width = Math.ceil(vp.width); c.height = Math.ceil(vp.height);
      var ctx = c.getContext("2d");
      if (jpeg) { ctx.fillStyle = "#fff"; ctx.fillRect(0, 0, c.width, c.height); }
      await page.render({ canvasContext: ctx, viewport: vp }).promise;
      var blob = await canvasToBlob(c, mime, 0.92);
      c.width = c.height = 0;
      if (!blob) continue;
      await postFile(pad(n, 3) + "쪽" + ext, blob);
      count++;
    }
    return count;
  }

  // 글자만 뽑기 → 텍스트 파일
  async function extractText() {
    var doc = app.pdfDocument, parts = [];
    for (var p = 1; p <= doc.numPages && !cancelled; p++) {
      post("progress|" + (p - 1) + "|" + p + " / " + doc.numPages + " 쪽");
      var page = await doc.getPage(p);
      var tc = await page.getTextContent();
      var s = "";
      for (var i = 0; i < tc.items.length; i++) {
        var it = tc.items[i];
        if (typeof it.str !== "string") continue;
        s += it.str;
        if (it.hasEOL) s += "\n";
      }
      parts.push("===== " + p + "쪽 =====\n" + s.replace(/[ \t]+\n/g, "\n").trim());
    }
    var blob = new Blob([parts.join("\n\n")], { type: "text/plain" });
    await postFile("__text__.txt", blob);
    return parts.length;
  }

  function runExport(fn) {
    if (!app || !app.pdfDocument) { post("exporterror|열린 문서가 없습니다"); return; }
    cancelled = false;
    Promise.resolve().then(fn).then(
      function (n) { post("exportdone|" + (n || 0)); },
      function (e) { post("exporterror|" + (e && e.message ? e.message : e)); });
  }

  // 7) 호스트 → 뷰어
  wv.addEventListener("message", function (e) {
    var m = String(e.data == null ? "" : e.data);
    var i = m.indexOf("|");
    var type = i < 0 ? m : m.slice(0, i);
    var arg = i < 0 ? "" : m.slice(i + 1);
    if (type === "save") save();
    else if (type === "saveas") saveAs();
    else if (type === "print") doPrint();
    else if (type === "toast") toast(arg);
    else if (type === "toasterr") toast(arg, true);
    else if (type === "cancel") cancelled = true;
    else if (type === "rotate") { try { var o = JSON.parse(arg); doRotate(o.pages, o.delta); } catch (x) { toast("회전 실패: " + x, true); } }
    else if (type === "flattenauto") { flattenAuto = arg === "1"; setFlattenDefault(); }
    else if (type === "flatten") { try { doFlatten(JSON.parse(arg)); } catch (x) { flattenReply({ error: String(x) }); } }
    else if (type === "extract") runExport(extractImages);
    else if (type === "pages2img") { try { var po = JSON.parse(arg); runExport(function () { return pagesToImages(po); }); } catch (x) { post("exporterror|" + x); } }
    else if (type === "text") runExport(extractText);
  });

  if (!isViewer) {
    // 시작 화면: 다 그려지면 호스트에 준비됨 신호
    if (document.readyState === "complete") post("ready");
    else window.addEventListener("load", function () { post("ready"); });
    return;
  }

  // 8) PDF.js가 준비되면: 저장을 가로채 호스트로 보내고, 수정 여부를 추적한다
  function whenApp(cb) {
    var a = window.PDFViewerApplication;
    if (a && a.initializedPromise) { a.initializedPromise.then(function () { cb(a); }); }
    else setTimeout(function () { whenApp(cb); }, 25);
  }

  whenApp(function (a) {
    app = a;
    if (!a.downloadManager) { post("log|no downloadManager"); return; }

    // PDF.js의 "내려받기"를 "호스트에 저장"으로 바꿈
    a.downloadManager.download = function (data, url, filename) {
      var useSaveAs = saveAsNext;
      var isExport = exportNext;
      var wasRotate = rotateSaveNext;
      var viaWorker = workerSaved;
      var failed = saveFailed;
      saveAsNext = false;
      exportNext = false;
      rotateSaveNext = false;
      workerSaved = false;
      saveFailed = false;
      if (failed && !isExport) {
        toast("저장 실패: 수정한 내용을 PDF에 넣지 못했습니다 (파일은 그대로입니다)", true);
        post("savefailed|" + (filename || ""));
        return;
      }
      if (!data) { toast("저장할 데이터가 없습니다", true); return; }
      var name = filename || "document.pdf";
      if (isExport) name = "export:" + name.replace(/\.pdf$/i, "") + "_추출.pdf";
      return postSave(data, name, useSaveAs || isExport ? "?saveas=1" : "")
        .then(function (res) {
          if (res.ok) {
            if (!isExport) markSaved();
            if (wasRotate) { toast("회전을 저장했습니다"); post("reload"); return; }
            if (isExport || !viaWorker) { toast("저장됨: " + res.name); return; }
            // 이번 저장에서 서명·그림을 박아 넣었으면 알려 준다
            return flattenResult().then(function (fi) {
              var n = fi ? (fi.done | 0) : 0;
              toast("저장됨: " + res.name + (n ? "  (서명·그림 " + n + "개를 문서에 박아 넣음)" : ""));
              if (fi && (fi.blocked | 0) > 0) post("flatblocked|" + (fi.blocked | 0));
            });
          } else if (!res.cancelled) {
            toast("저장 실패: " + (res.error || ""), true);
          }
        })
        .catch(function (err) { toast("저장 실패: " + err, true); });
    };

    // 페이지 관리자의 "선택한 페이지 내보내기"는 현재 파일을 덮어쓰면 안 되므로 표시해 둔다
    a.eventBus.on("saveextractedpages", function () { exportNext = true; });

    // 주석 수정 감지: PDF.js가 annotationStorage에 심어 둔 콜백을 감싼다
    function wrapStorage() {
      var doc = a.pdfDocument;
      if (!doc) return false;
      var st = doc.annotationStorage;
      if (!st) return false;
      if (st.__hostWrapped) return true;
      if (typeof st.onSetModified !== "function") return false;
      var os = st.onSetModified, orr = st.onResetModified;
      st.__hostWrapped = true;
      st.onSetModified = function () { try { os.apply(this, arguments); } catch (e) {} annDirty = true; recomputeDirty(); };
      st.onResetModified = function () { try { if (orr) orr.apply(this, arguments); } catch (e) {} annDirty = false; recomputeDirty(); };
      return true;
    }
    ["documentloaded", "pagesinit", "pagesloaded", "annotationeditoruimanager"].forEach(function (ev) {
      a.eventBus.on(ev, function () { wrapStorage(); });
    });
    a.eventBus.on("documentloaded", function () {
      setFlattenDefault();
      annDirty = false;
      savedStruct = "";
      recomputeDirty(); // 합치기 뒤 다시 불러온 문서는 _mergedDocumentNeedsSaving 때문에 수정 상태가 된다
      var tries = 0;
      (function retry() { if (!wrapStorage() && tries++ < 40) setTimeout(retry, 250); })();
    });
    // 페이지 삭제·순서 바꾸기·합치기 등 구조 변경
    a.eventBus.on("pagesedited", function () { setTimeout(recomputeDirty, 0); });
    a.eventBus.on("saveandload", function () { setTimeout(recomputeDirty, 0); });
    a.eventBus.on("documenterror", function (e) { post("error|" + (e && e.message)); });
    a.eventBus.on("pagesloaded", function () { post("ready"); });
  });
})();
