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
  var flattenAuto = false;   // 저장할 때 서명·그림을 쪽 내용으로 박아 넣기 (호스트의 설정; 이 스크립트가 시작하면서 물어 받는다)
  var carriedRects = [];     // 합치기로 다시 불러온 문서에 주석으로 들어가 있는 "아직 박지 못한 서명·그림"의 자리들
  var carryNext = null;      // 합치기가 끝나 새 문서가 열리면 carriedRects 가 될 것
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
  //   평소 저장: 설정이 켜져 있으면 이번 저장에서 새로 넣었거나 자리를 옮긴 서명·그림(Stamp)만
  //   도구 메뉴: 고른 종류를 문서에 이미 있던 것까지 전부 (끝나면 다시 불러옴)
  var INFO_KEYS = ["done", "skipped", "blocked", "failedPages", "noAppearance", "special", "comment", "overlap", "content"];
  function setFlattenDefault() {
    try {
      var tr = app && app.pdfDocument && app.pdfDocument._transport;
      if (!tr) return;
      tr.pkFlatten = flattenAuto ? { subtypes: ["Stamp"], all: false } : null;
      if (!tr.__hostSaveWrapped) {
        // 저장 데이터가 PDF.js의 저장 처리를 거쳤는지 표시해 둔다 (거치지 않은 "원본 그대로 저장"·"쪽 편집 저장"과 구별).
        // 저장 처리가 실패하면 PDF.js는 원본을 그대로 내려받는 쪽으로 넘어가는데, 그것을 파일에 덮어쓰면
        // 수정한 내용이 빠진 채 "저장됨"이 되므로 실패도 표시해 둔다 (아래 download 에서 확인하고 지운다).
        var orig = tr.saveDocument;
        tr.__hostSaveWrapped = true;
        tr.saveDocument = function () {
          workerSaved = false;
          saveFailed = false;
          return orig.apply(this, arguments).then(
            function (d) { workerSaved = true; return d; },
            function (e) {
              saveFailed = true;
              post("log|saveDocument failed: " + (e && e.message ? e.message : e));
              throw e;
            });
        };
      }
    } catch (e) {}
  }
  // 방금 끝난 저장에서 박아 넣은 결과 { done, skipped, blocked, failedPages, 이유별 수 } (한 번 읽으면 지워진다)
  function flattenResult(transport) {
    try {
      var tr = transport || app.pdfDocument._transport;
      return tr.messageHandler.sendWithPromise("PkFlattenResult", null).then(null, function () { return null; });
    } catch (e) { return Promise.resolve(null); }
  }
  function addInfo(a, b) {
    if (!a && !b) return null;
    var o = {};
    for (var i = 0; i < INFO_KEYS.length; i++) {
      var k = INFO_KEYS[i];
      o[k] = ((a && a[k]) | 0) + ((b && b[k]) | 0);
    }
    return o;
  }
  function encodeInfo(o) {
    var s = "";
    for (var k in o) s += k + "=" + String(o[k]).replace(/[;\r\n]/g, " ") + ";";
    return s;
  }
  function postSave(data, name, query) {
    var body = (typeof Blob !== "undefined" && data instanceof Blob) ? data : new Blob([data], { type: "application/pdf" });
    return fetch("https://pdfjs.local/save/" + encodeURIComponent(name) + (query || ""), { method: "POST", body: body, cache: "no-store" })
      .then(function (r) { return r.json(); });
  }
  // 저장소에 있는 "새로 넣었거나 자리를 옮긴" 서명·그림의 자리(쪽 좌표 사각형)들
  function pendingStampRects() {
    var out = [];
    try {
      var T = globalThis.pdfjsLib.AnnotationEditorType;
      var st = app.pdfDocument.annotationStorage;
      var ser = st.serializable;
      if (!ser || !ser.map) return out;
      ser.map.forEach(function (v, key) {
        if (!v) return;
        var rawValue = st.getRawValue(key);
        var isEditor = rawValue !== v;       // 편집기는 매번 새로 직렬화된 사본을 내놓는다
        // 직렬화하며 만들어진 그림 사본은 여기서 쓰지 않으므로 바로 닫는다 (저장소에 직접 든 객체의 그림은 닫으면 안 된다)
        if (isEditor) { try { if (v.bitmap && typeof v.bitmap.close === "function") v.bitmap.close(); } catch (e) {} }
        if (v.deleted || !v.rect || v.rect.length < 4) return;
        if (v.annotationType !== T.STAMP && v.annotationType !== T.SIGNATURE) return;
        // 원래 있던 것은 자리나 크기를 바꾼 경우만 (댓글·설명만 고친 것은 박지 않는다)
        if (v.id && isEditor && !(rawValue && (rawValue._hasBeenMoved || rawValue._hasBeenResized))) return;
        out.push([v.rect[0], v.rect[1], v.rect[2], v.rect[3]]);
      });
    } catch (e) {}
    return out;
  }
  // 이미 만들어진 PDF 바이트에 주석으로 들어가 있는 서명·그림을, 자리(rects)로 찾아 박아 넣는다.
  // 쪽을 지우거나 순서를 바꾼 저장, 합친 문서의 저장은 PDF.js의 다른 길(ExtractPages)로 만들어져 평소의 박아 넣기를 거치지 않는다.
  // 그 바이트를 임시 문서로 열어 저장 처리(패치됨)를 한 번 더 거친다. 실패하면 받은 바이트를 그대로 돌려준다.
  function bakeBytes(data, rects) {
    var task = null;
    function finish(result) {
      var p;
      try { p = task ? Promise.resolve(task.destroy()) : Promise.resolve(); } catch (e) { p = Promise.resolve(); }
      return p.then(function () { return result; }, function () { return result; });
    }
    return Promise.resolve().then(function () {
      var opts = { data: data.slice(0) };      // 사본을 넘긴다 (넘긴 버퍼는 작업자 쪽으로 옮겨 가 못 쓰게 된다)
      try {
        var w = app.pdfLoadingTask && app.pdfLoadingTask._worker;
        if (w && !w.destroyed) opts.worker = w;
      } catch (e) {}
      task = globalThis.pdfjsLib.getDocument(opts);
      return task.promise;
    }).then(function (tmp) {
      tmp._transport.pkFlatten = { subtypes: ["Stamp"], all: true, rects: rects };
      return tmp.saveDocument().then(function (out) {
        return flattenResult(tmp._transport).then(function (res) {
          return finish({ data: (res && res.done) ? out : data, info: res });
        });
      });
    }).then(null, function (e) {
      post("log|bakeBytes failed: " + (e && e.message ? e.message : e));
      return finish({ data: data, info: { skipped: rects.length, content: rects.length, failedPages: 1 } });
    });
  }
  // 저장이 끝난 뒤의 알림: 박아 넣은 수를 덧붙이고, 박지 못해 주석으로 남은 것이 있으면 호스트에도 알린다
  function reportSaved(text, info) {
    var n = info ? (info.done | 0) : 0;
    var missed = info ? ((info.skipped | 0) + (info.blocked | 0)) : 0;
    var failedPages = info ? (info.failedPages | 0) : 0;
    toast(text + (n ? "  (서명·그림 " + n + "개를 문서에 박아 넣음)" : "") + (missed ? "  ※ " + missed + "개는 박지 못해 주석으로 남음" : ""), false);
    if (missed || failedPages) post("flatnote|" + encodeInfo(info));
  }
  function flattenReply(o) {
    post("flattendone|" + encodeInfo(o));
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
          var info = addInfo(res, null) || addInfo({}, null);
          if (!info.done) {
            // 박아 넣은 것이 없으면 파일을 건드리지 않는다
            keepDirty();
            flattenReply(info);
            return;
          }
          // 되돌릴 수 없는 일이므로, 저장 전의 파일을 백업하지 못하면 저장하지 않는다
          return postSave(data, app._docFilename || "document.pdf", "?needbackup=1").then(function (r) {
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

  // 서명 놓기: 서명을 고르거나 새로 만들면 화면 가운데에 바로 놓이는 대신, 마우스를 따라다니다가 누른 자리에 놓인다.
  // PDF.js는 서명을 "지금 보이는 쪽의 한가운데"에 만든다. 여기서는 그 순간을 붙잡아 만들지 않고 기다렸다가,
  // 마우스가 쪽 위에 오면 그 쪽에 임시 서명을 만들어 마우스에 붙이고(아직 문서에는 넣지 않는다), 쪽을 누르면 그 자리에 확정한다.
  var lastPointer = null;    // 마지막 마우스 위치 (창 좌표)
  var placing = null;        // { ui, value, ed, since }: 놓을 자리를 기다리는 서명. ed = 마우스에 붙은 임시 서명 (마우스가 쪽 위에 있을 때만 있다)
  var sigCreating = false;   // 우리가 서명을 만드는 중 (PDF.js의 addSignature를 그대로 통과시킨다)
  var sigDialogNext = false; // 곧 만들어질 서명 편집기는 "새 서명 추가" 창의 빈 편집기다
  var swallowClick = false;  // 자리를 정한 그 누름이 다른 일(선택 풀림, 끌기 시작)을 일으키지 않게
  var swallowAt = 0;
  var PLACE_HINT = "서명을 놓을 자리를 누르세요  (Esc: 취소)";
  function hintHide() {      // 안내 문구는 자리를 정했거나 그만두면 바로 거둔다 (다른 알림이 떠 있으면 건드리지 않는다)
    try { var t = document.getElementById("hostToast"); if (t && t.textContent === PLACE_HINT) t.style.opacity = "0"; } catch (e) {}
  }
  function sigMode() { try { return globalThis.pdfjsLib.AnnotationEditorType.SIGNATURE; } catch (e) { return 101; } }
  function isSignatureEditor(ed) { try { return !!ed && !!ed.constructor && ed.constructor._type === "signature"; } catch (e) { return false; } }
  function sigAlive(ed) {
    try { return !!(ed && ed.div && ed.parent && ed._uiManager.getEditor(ed.id) === ed); } catch (e) { return false; }
  }
  // 마우스 아래가 정말 쪽인가. 쪽의 사각형은 도구 줄 밑으로도 이어져 있으므로, 도구 줄·펼침 목록·창·스크롤 막대에 가려진 곳은 빼야 한다.
  // 돌려주는 값: 쪽 위면 그 쪽의 편집 층, 쪽과 쪽 사이의 틈이면 false, 쪽 영역 밖이면 null
  function pageLayerAt(ui, x, y) {
    try {
      var el = document.elementFromPoint(x, y);
      if (!el || !el.closest || !el.closest("#viewer")) return null;
      return ui.findParent(x, y) || false;
    } catch (e) { return null; }
  }
  // 창 좌표 → 그 쪽 편집 층 안의 좌표 (돌려 놓은 쪽은 층도 돌아가 있다)
  function layerOffset(layer, x, y) {
    var r = layer.div.getBoundingClientRect();
    switch (layer.viewport.rotation) {
      case 90: return [y - r.top, r.right - x];
      case 180: return [r.right - x, r.bottom - y];
      case 270: return [r.bottom - y, x - r.left];
      default: return [x - r.left, y - r.top];
    }
  }
  // 임시 서명은 주석 저장소에 넣지 않는다: 저장·인쇄에 나가지 않고, 문서가 "수정됨"으로 바뀌지도 않는다
  function quietly(ui, fn) {
    var own = Object.prototype.hasOwnProperty.call(ui, "addToAnnotationStorage"), prev = ui.addToAnnotationStorage;
    ui.addToAnnotationStorage = function () {};
    try { return fn(); }
    finally { if (own) ui.addToAnnotationStorage = prev; else delete ui.addToAnnotationStorage; }
  }
  // 아직 놓지 않은 서명은 흐리게 그린다 (놓으면 또렷해진다)
  function ghostFaint(ed, on) {
    try { ed.parent.drawLayer.updateProperties(ed._drawId, { root: { opacity: on ? 0.55 : null } }); } catch (e) {}
  }
  // 마우스가 있는 쪽에 임시 서명을 만든다 (PDF.js가 저장해 둔 서명을 넣을 때 쓰는 길 그대로, 자리만 마우스 위치로)
  function ghostCreate(layer, x, y) {
    var p = placing, ed = null, off = layerOffset(layer, x, y), focused = document.activeElement;
    var box = document.getElementById("viewerContainer"), top = box ? box.scrollTop : 0, left = box ? box.scrollLeft : 0;
    sigCreating = true;
    layer.addUndoableEditor = function () {};     // 임시 서명은 실행 취소 목록에 올리지 않는다 (놓을 때 한 번 올린다)
    try {
      ed = quietly(p.ui, function () { return layer.createAndAddNewEditor({ offsetX: off[0], offsetY: off[1] }, true, p.value); });
    } finally {
      sigCreating = false;
      delete layer.addUndoableEditor;
      // PDF.js가 새 편집기에 초점을 주면서 화면을 굴릴 수 있다(서명이 보기 영역 끝에 걸쳐 있을 때) → 굴린 것을 되돌린다
      try { if (box && (box.scrollTop !== top || box.scrollLeft !== left)) { box.scrollTop = top; box.scrollLeft = left; } } catch (e) {}
    }
    if (!ed || !ed.div || ed._drawId === null) {
      try { if (ed) quietly(p.ui, function () { ed.remove(); }); } catch (e) {}
      return null;
    }
    ed.serialize = function () {                  // 안전장치: 혹시 저장소에 들어가더라도 놓기 전에는 저장·인쇄에 나가지 않는다 (놓이면 스스로 걷힌다)
      if (placing && placing.ed === ed) return null;
      delete ed.serialize;
      return ed.serialize.apply(ed, arguments);
    };
    try { p.ui.unselect(ed); } catch (e) {}
    ed.div.style.pointerEvents = "none";          // 따라다니는 동안에는 마우스에도 글쇠에도 반응하지 않는다 (테두리나 도구 줄이 뜨지 않게)
    try { ed.div.inert = true; } catch (e) {}
    try { ed._onStartDragging(); } catch (e) {}   // 다른 그림보다 위에 그린다
    ghostFaint(ed, true);
    try {                                         // 만들면서 옮겨 간 글쇠 초점을 되돌린다 (쪽 번호 칸 등에 입력하던 중일 수 있다)
      if (focused && focused !== document.activeElement && focused !== document.body && document.contains(focused)) focused.focus({ preventScroll: true });
    } catch (e) {}
    p.ed = ed;
    return ed;
  }
  function ghostDrop() {
    var p = placing, ed = p && p.ed;
    if (!ed) return;
    p.ed = null;
    try { delete ed.serialize; } catch (e) {}
    try { if (ed._uiManager.getEditor(ed.id) === ed) quietly(p.ui, function () { ed.remove(); }); } catch (e) {}
  }
  // 서명의 가운데를 마우스 자리에 맞춘다 (쪽 밖으로는 나가지 않는다: 놓일 자리 그대로 보인다)
  function ghostMove(ed, x, y) {
    for (var i = 0; i < 3; i++) {
      var r = ed.div.getBoundingClientRect();
      var dx = x - (r.left + r.width / 2), dy = y - (r.top + r.height / 2);
      if (Math.abs(dx) < 0.5 && Math.abs(dy) < 0.5) return;
      var t = ed.screenToPageTranslation(dx, dy), pd = ed.parentDimensions;
      ed.x += t[0] / pd[0];
      ed.y += t[1] / pd[1];
      ed.fixAndSetPosition();
      ed._onTranslating(ed.x, ed.y);
    }
  }
  // 임시 서명을 이 쪽(layer)의 마우스 자리에 둔다. 없으면 만든다.
  // 다른 쪽에 있던 것은 옮기지 않고 그 쪽에 맞게 새로 만든다: 쪽마다 크기·방향·좌표 원점이 다를 수 있고, PDF.js는 편집기를 만들 때의 쪽 값을 계속 쓴다.
  function ghostAt(layer, x, y) {
    var p = placing, ed = p.ed;
    if (ed && (!sigAlive(ed) || ed.parent !== layer)) { ghostDrop(); ed = null; }
    if (!ed) ed = ghostCreate(layer, x, y);
    if (ed) ghostMove(ed, x, y);
    return ed;
  }
  function placingCancel() {
    if (!placing) return;
    ghostDrop();
    placing = null;
    window.__hostPlacing = false;
    hintHide();
  }
  function placingFail(err) {
    placingCancel();
    toast("서명을 넣지 못했습니다" + (err ? ": " + err : ""), true);
  }
  function placingTrack(x, y) {
    var p = placing;
    if (!p) return;
    try {
      if (p.ui.getMode() !== sigMode()) { placingCancel(); return; }
      var layer = pageLayerAt(p.ui, x, y);
      if (layer === null) { ghostDrop(); return; }    // 쪽 영역 밖(도구 줄, 창, 스크롤 막대): 임시 서명을 거둔다. 돌아오면 다시 생긴다
      if (!layer) return;                             // 쪽과 쪽 사이의 틈: 그대로 둔다
      if (!ghostAt(layer, x, y)) placingFail("");
    } catch (e) { placingFail(e); }
  }
  // 쪽을 눌렀다: 임시 서명을 그 자리에 확정하고 문서에 넣는다
  function placingCommit(layer, x, y) {
    var p = placing, ed = ghostAt(layer, x, y);
    if (!ed) return false;
    placing = null;
    window.__hostPlacing = false;
    try { delete ed.serialize; } catch (e) {}
    try { ed.div.inert = false; } catch (e) {}
    ed.div.style.pointerEvents = "";
    ghostFaint(ed, false);
    try { ed._onStopDragging(); } catch (e) {}
    try { ed._setParentAndPosition(layer, ed.x, ed.y); } catch (e) {}   // 쪽 안쪽으로 맞추고 그려진 서명을 그 자리에 확정한다
    p.ui.addToAnnotationStorage(ed);                                    // 여기서 문서에 들어간다 (이때부터 "수정됨")
    try { layer.addUndoableEditor(ed); } catch (e) {}                   // Ctrl+Z 한 번으로 이 서명이 빠진다
    try { p.ui.setSelected(ed); } catch (e) {}
    try { ed.div.focus({ preventScroll: true }); } catch (e) {}         // 화살표 글쇠로 조금씩 옮기거나 Delete 로 지울 수 있게
    hintHide();
    return true;
  }
  function placingStart(ui, value) {
    placingCancel();               // 아직 놓지 않은 서명이 있으면 그것은 버리고 새로 고른 것으로
    placing = { ui: ui, value: value, ed: null, since: Date.now() };
    window.__hostPlacing = true;
    toast(PLACE_HINT);
    if (lastPointer) placingTrack(lastPointer.x, lastPointer.y);
  }
  window.addEventListener("pointermove", function (e) {
    lastPointer = { x: e.clientX, y: e.clientY };
    if (placing) placingTrack(e.clientX, e.clientY);
  }, true);
  document.addEventListener("scroll", function () {        // 굴려서 쪽이 움직여도 임시 서명은 마우스 아래에 있게
    if (placing && placing.ed && lastPointer) placingTrack(lastPointer.x, lastPointer.y);
  }, true);
  window.addEventListener("mouseout", function (e) {       // 마우스가 창 밖(위쪽 메뉴 등)으로 나가면 임시 서명을 거둔다
    if (placing && !e.relatedTarget) ghostDrop();
  }, true);
  window.addEventListener("blur", function () { swallowClick = false; if (placing) ghostDrop(); });
  window.addEventListener("pointercancel", function () { swallowClick = false; }, true);
  window.addEventListener("pointerdown", function (e) {
    swallowClick = false;     // 새 누름이 시작됐다: 앞선 누름의 뒤처리는 끝난 것이다
    var p = placing;
    if (!p) return;
    lastPointer = { x: e.clientX, y: e.clientY };
    if (e.button !== 0) return;
    if (Date.now() - p.since < 400) return;   // 서명을 고른 누름에 바로 이어진 누름(두 번 누르기)은 놓는 것으로 치지 않는다
    var layer = null;
    try { if (p.ui.getMode() === sigMode()) layer = pageLayerAt(p.ui, e.clientX, e.clientY); } catch (x) {}
    if (!layer) return;       // 쪽이 아닌 곳(도구 줄, 창, 스크롤 막대, 쪽 사이)을 누른 것은 원래대로 처리되고, 자리 정하기는 이어진다
    var ok = false, err = "";
    try { ok = placingCommit(layer, e.clientX, e.clientY); } catch (x) { err = x; }
    if (!ok) placingFail(err);
    swallowClick = true;      // 이 누름이 방금 놓은 서명의 선택을 풀거나 끌기를 시작하지 않게
    swallowAt = Date.now();
    e.preventDefault(); e.stopImmediatePropagation();
  }, true);
  ["pointerup", "click"].forEach(function (type) {
    window.addEventListener(type, function (e) {
      if (!swallowClick) return;
      if (Date.now() - swallowAt > 3000) { swallowClick = false; return; }   // 그 누름의 것이 아니다 (떼는 신호를 못 받은 경우)
      e.preventDefault(); e.stopImmediatePropagation();
      if (type === "click") swallowClick = false;
      else setTimeout(function () { swallowClick = false; }, 300);   // 누름 뒤에 click 이 오지 않는 경우에 대비
    }, true);
  });
  // 서명 편집기를 처음 만날 때 한 번: 서명 창의 "추가"가 서명을 넣는 순간(addSignature)을 붙잡는다
  function hookSignatureEditor(editor) {
    if (!isSignatureEditor(editor)) return;
    var proto = Object.getPrototypeOf(editor);
    if (!proto || proto.__hostAddHooked || typeof proto.addSignature !== "function") return;
    proto.__hostAddHooked = true;
    // 서명 창이 열려 있는 동안의 빈 서명 편집기는 저장할 것이 없다 (PDF.js는 이때 저장하려 하면 오류를 낸다)
    var origSerialize = proto.serialize;
    proto.serialize = function () { return this._drawOutlines ? origSerialize.apply(this, arguments) : null; };
    var orig = proto.addSignature;
    proto.addSignature = function (data, heightInPage, description, uuid) {
      if (sigCreating) return orig.apply(this, arguments);        // 우리가 임시 서명을 만드는 중
      // 서명 창에서 "추가"를 누른 경우만 붙잡는다: 그때의 편집기는 창이 떠 있는 동안 숨겨져 있다(div.hidden).
      // 그 밖의 경우(PDF.js가 직접 그리는 경우, 모르는 꼴의 자료)는 그대로 둔다.
      if (!data || !data.newCurves || !this.div || !this.div.hidden) {
        var r = orig.apply(this, arguments);
        if (this.__hostNoUndo) {                                  // 창의 빈 편집기에 그대로 그려 넣은 경우: 빼 두었던 실행 취소 항목을 올린다
          this.__hostNoUndo = false;
          try { this.parent.addUndoableEditor(this); } catch (e) {}
        }
        return r;
      }
      // 가운데에 넣지 않고 놓을 자리를 기다린다 (저장해 둔 서명을 고를 때와 같은 꼴의 자료로 바꿔 둔다)
      var self = this;
      placingStart(this._uiManager, { signatureData: {
        lines: { curves: data.newCurves.map(function (pts) { return { points: pts }; }), thickness: data.thickness, width: data.width, height: data.height },
        mustSmooth: false, areContours: data.areContours, description: description, uuid: uuid, heightInPage: heightInPage } });
      // 비어 있는 이 편집기는 창이 닫히면서 PDF.js가 치운다. 혹시 남으면 여기서 치운다
      setTimeout(function () {
        try { if (self._drawId === null && self._uiManager.getEditor(self.id) === self) self.remove(); } catch (e) {}
      }, 500);
    };
  }
  // "새 서명 추가": PDF.js는 빈 서명 편집기를 먼저 만들고 창을 띄운다. 이 빈 편집기는
  //  - 실행 취소 목록에 올리지 않는다 (창이 닫히면 지워지는데, 목록에 남으면 "다시 실행"이 오류를 내고 실행 취소 기록이 멈춘다)
  //  - 그려진 것이 없는 동안에는 "비었다"고 답하게 한다 (PDF.js는 빈 것도 주석 저장소에 넣어, 창을 열기만 해도 문서가 "수정됨"이 된다)
  function newSignatureDialog(ui, origParams, args) {
    var layer = ui.currentLayer;
    if (!layer) return origParams.apply(ui, args);
    sigDialogNext = true;
    layer.addUndoableEditor = function () {};
    try { return origParams.apply(ui, args); }
    finally { sigDialogNext = false; delete layer.addUndoableEditor; }
  }
  function hookSignatures(ui) {
    if (!ui || ui.__hostSigHooked) return;
    ui.__hostSigHooked = true;
    if (placing && placing.ui !== ui) placingCancel();    // 다른 문서로 바뀌었다
    try {
      // 놓인 서명 위의 도구 줄에서 "설명 편집" 단추를 만들지 않는다 (PDF.js는 단추가 없으면 그냥 건너뛴다)
      var sm = ui.signatureManager;
      if (sm) sm.renderEditButton = function () { return Promise.resolve(null); };
    } catch (e) {}
    try {
      var origAdd = ui.addEditor;
      ui.addEditor = function (editor) {
        try {
          hookSignatureEditor(editor);
          if (sigDialogNext && isSignatureEditor(editor)) {
            sigDialogNext = false;
            editor.__hostNoUndo = true;
            editor.isEmpty = function () { return !this._drawOutlines; };
          }
        } catch (e) {}
        return origAdd.apply(this, arguments);
      };
    } catch (e) {}
    try {
      // 그릴 것이 없는 빈 서명 편집기는 되살리지 않는다 (PDF.js는 여기서 오류를 내고, 그러면 실행 취소 기록이 멈춘다)
      var origRebuild = ui.rebuild;
      ui.rebuild = function (editor) {
        if (isSignatureEditor(editor) && !editor._drawOutlines) return;
        return origRebuild.apply(this, arguments);
      };
    } catch (e) {}
    try {
      var origParams = ui.updateParams, CREATE = globalThis.pdfjsLib.AnnotationEditorParamsType.CREATE;
      ui.updateParams = function (type, value) {
        if (type === CREATE && this.getMode() === sigMode()) {
          // 저장해 둔 서명을 골랐다: PDF.js가 가운데에 만들기 전에 붙잡아, 놓을 자리를 기다린다
          if (value && value.signatureData && value.signatureData.lines) { placingStart(this, value); return; }
          if (!value || !value.signatureData) return newSignatureDialog(this, origParams, arguments);
        }
        return origParams.apply(this, arguments);
      };
    } catch (e) {}
  }

  // PDF.js가 띄우는 브라우저의 "변경 사항이 저장되지 않았습니다" 경고창 막기 (저장 여부는 탭을 닫을 때 프로그램이 직접 묻는다)
  window.addEventListener("beforeunload", function (e) { e.stopImmediatePropagation(); }, true);

  // 3) 단축키 (PDF.js보다 먼저 받음)
  //    Ctrl+Shift+S 다른 이름으로 저장, Ctrl+W 탭 닫기, Ctrl+Tab / Ctrl+Shift+Tab / Ctrl+PgDn / Ctrl+PgUp 탭 이동,
  //    F5·Ctrl+R 막기(편집 내용 보호), F12 개발자 도구, 시작 화면에서는 Ctrl+O 열기
  window.addEventListener("keydown", function (e) {
    var ctrl = e.ctrlKey || e.metaKey;
    var k = (e.key || "").toLowerCase();
    if (placing) {
      // Esc: 놓을 자리를 기다리던 서명을 그만둔다 (창이 떠 있으면 Esc 는 그 창의 것이다)
      if (k === "escape" && !document.querySelector("dialog[open]")) { e.preventDefault(); e.stopImmediatePropagation(); placingCancel(); return; }
      // 다른 글쇠: 글쇠 명령(모두 선택, 지우기, 실행 취소 등)이 임시 서명을 건드리지 않게 거둔다. 마우스를 움직이면 다시 생긴다
      if (placing.ed && k !== "control" && k !== "shift" && k !== "alt" && k !== "meta") ghostDrop();
    }
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

  if (isViewer) post("hello");   // 호스트가 설정(저장할 때 박아 넣기)을 바로 알려 준다

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
      // PDF.js는 저장 처리가 실패하면 같은 save() 안에서 원본을 내려받는 쪽으로 넘어온다 (그동안 _saveInProgress 가 켜져 있다)
      var failed = saveFailed && !!a._saveInProgress;
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
      var query = useSaveAs || isExport ? "?saveas=1" : "";
      // 평소의 저장 처리를 거치지 않은 데이터(쪽 편집 저장, 합친 문서)에는 서명·그림이 주석으로 들어가 있다 → 자리로 찾아 박아 넣는다
      var extraRects = [];
      if (flattenAuto) {
        extraRects = carriedRects.slice();
        if (!viaWorker) extraRects = extraRects.concat(pendingStampRects());
      }
      var first = viaWorker ? flattenResult() : Promise.resolve(null);
      return first
        .then(function (fi) {
          if (!extraRects.length || !(data instanceof Uint8Array)) return { data: data, info: fi };
          return bakeBytes(data, extraRects).then(function (b) { return { data: b.data, info: addInfo(fi, b.info) }; });
        })
        .then(function (ready) {
          return postSave(ready.data, name, query).then(function (res) {
            if (res.ok) {
              if (!isExport) markSaved();
              reportSaved(wasRotate ? "회전을 저장했습니다" : "저장됨: " + res.name, ready.info);
              if (wasRotate) post("reload");
            } else if (!res.cancelled) {
              toast("저장 실패: " + (res.error || ""), true);
            }
          });
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
    // 합치기: 지금 저장소에 있는 서명·그림은 합친 문서에 주석으로 들어간다 → 자리를 기억해 두었다가 저장할 때 박아 넣는다
    a.eventBus.on("saveandload", function () { carryNext = carriedRects.concat(pendingStampRects()); });
    a.eventBus.on("documentloaded", function () {
      carriedRects = carryNext || [];
      carryNext = null;
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
    // 서명: 넣는 창의 설명 칸을 숨기고(값은 PDF.js가 알아서 채운다), 편집기 관리자가 생길 때마다 위의 갈고리를 건다
    try { var dc = document.getElementById("addSignatureDescriptionContainer"); if (dc) dc.style.display = "none"; } catch (e) {}
    a.eventBus.on("annotationeditoruimanager", function (evt) { hookSignatures(evt && evt.uiManager); });
    a.eventBus.on("pagesloaded", function () {
      try { hookSignatures(a.pdfViewer._layerProperties.annotationEditorUIManager); } catch (e) {}
    });
    // 놓을 자리를 기다리는 서명: 다른 도구로 바꾸거나 문서·쪽 구성이 바뀌면 그만두고, 확대·회전 중에는 임시 서명만 거둔다(마우스를 움직이면 다시 생긴다)
    a.eventBus.on("annotationeditormodechanged", function (evt) { if (placing && (!evt || evt.mode !== sigMode())) placingCancel(); });
    ["scalechanging", "rotationchanging"].forEach(function (ev) { a.eventBus.on(ev, function () { if (placing) ghostDrop(); }); });
    ["pagesedited", "pagesdestroy", "documentloaded"].forEach(function (ev) { a.eventBus.on(ev, function () { placingCancel(); }); });
    a.eventBus.on("pagesloaded", function () { post("ready"); });
  });
})();
