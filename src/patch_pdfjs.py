# pdf.js 배포본에 이 프로그램의 패치를 넣는다 (pdf.js 를 새 버전으로 바꾸면 다시 실행).
#   1) 페이지 회전 저장  : SaveDocument 가 pageRotations 를 받아 페이지 사전의 /Rotate 를 바꿔 증분 저장에 포함
#   2) 서명·주석 박아 넣기: SaveDocument 가 pkFlatten 을 받아, 주석의 모양(AP)을 쪽 내용으로 옮기고 주석 목록에서 뺀다
#   - build/pdf.worker.mjs : 위 두 가지의 실제 처리
#   - build/pdf.mjs        : saveDocument 메시지에 transport.pageRotations / transport.pkFlatten 을 실어 보냄
# 이전 판(v1: 회전 저장만)으로 패치된 파일은 먼저 원래대로 되돌린 뒤 새 판을 넣는다.
# 실행:  python3 patch_pdfjs.py <pdfjs 폴더>
import sys, os

root = sys.argv[1]
MARK = "PDFVIEWER-PATCH"
VERSION = MARK + " v2"

# ---------------------------------------------------------------------------------------------
# v1 (되돌리기용: 예전에 넣었던 문자열 그대로)
# ---------------------------------------------------------------------------------------------
SAVE_HEAD = (
    'handler.on("SaveDocument", async function ({\n'
    '      isPureXfa,\n'
    '      numPages,\n'
    '      annotationStorage,\n'
    '      supportsPrintToPDF,\n'
    '      filename\n'
    '    }) {'
)
SAVE_AFTER_PROMISES = (
    '      const refs = await Promise.all(promises);\n'
    '      let xfaData = null;\n'
)
MAIN_SAVE = (
    '    return this.messageHandler.sendWithPromise("SaveDocument", {\n'
    '      isPureXfa: !!this._htmlForXfa,\n'
    '      numPages: this._numPages,\n'
    '      annotationStorage: map,\n'
)

V1_WORKER = [
    (
        SAVE_HEAD,
        'handler.on("SaveDocument", async function ({\n'
        '      isPureXfa,\n'
        '      numPages,\n'
        '      annotationStorage,\n'
        '      supportsPrintToPDF,\n'
        '      filename,\n'
        '      pageRotations = null /* ' + MARK + ' */\n'
        '    }) {',
    ),
    (
        SAVE_AFTER_PROMISES,
        '      const refs = await Promise.all(promises);\n'
        '      /* ' + MARK + ': 페이지 회전 저장 - 페이지 사전을 복제해 /Rotate 만 바꿔 증분 저장에 넣는다 */\n'
        '      if (pageRotations && !isPureXfa) {\n'
        '        for (const key of Object.keys(pageRotations)) {\n'
        '          const delta = pageRotations[key] | 0;\n'
        '          if (!delta) continue;\n'
        '          const rotPage = await pdfManager.getPage(parseInt(key, 10));\n'
        '          const rotDict = rotPage.pageDict.clone();\n'
        '          rotDict.set("Rotate", (((rotPage.rotate + delta) % 360) + 360) % 360);\n'
        '          changes.put(rotPage.ref, { data: rotDict });\n'
        '        }\n'
        '      }\n'
        '      let xfaData = null;\n',
    ),
]
V1_MAIN = [
    (
        MAIN_SAVE,
        MAIN_SAVE +
        '      pageRotations: this.pageRotations || null, /* ' + MARK + ' */\n',
    ),
]

# ---------------------------------------------------------------------------------------------
# v2
# ---------------------------------------------------------------------------------------------
REFMAP_OLD = (
    '  putAlias(ref, aliasRef) {\n'
    '    this.#map.set(ref.toString(), this.get(aliasRef));\n'
    '  }\n'
    '  getOrPutComputed(ref, callback) {\n'
)
REFMAP_NEW = (
    '  putAlias(ref, aliasRef) {\n'
    '    this.#map.set(ref.toString(), this.get(aliasRef));\n'
    '  }\n'
    '  delete(ref) { /* ' + MARK + ' */\n'
    '    this.#map.delete(ref.toString());\n'
    '  }\n'
    '  getOrPutComputed(ref, callback) {\n'
)

HELPERS_ANCHOR = 'class WorkerMessageHandler {\n'
HELPERS = r'''/* PDFVIEWER-PATCH v2: 서명·주석 박아 넣기.
   주석의 모양(AP, 폼 XObject)을 쪽의 /Resources /XObject 에 등록하고 쪽 내용 끝에 "그리기" 한 줄을 덧붙인 뒤
   주석 목록(/Annots)에서 뺀다. 그러면 다른 프로그램에서 주석처럼 따로 집어 옮기거나 복사·삭제할 수 없다. */
let pkFlattenLast = null;
function pkFlattenSubtypeOf(value) {
  switch (value?.annotationType) {
    case AnnotationEditorType.STAMP:
    case AnnotationEditorType.SIGNATURE:
      return "Stamp";
    case AnnotationEditorType.INK:
      return "Ink";
    case AnnotationEditorType.HIGHLIGHT:
      return value.quadPoints ? "Highlight" : "Ink";
    case AnnotationEditorType.FREETEXT:
      return "FreeText";
  }
  return null;
}
// 전자서명(또는 인증·사용 권한 서명)이 들어 있는 문서인지. 쪽 내용을 바꾸면 그 서명의 검증이 깨지므로 박아 넣지 않는다.
async function pkHasDigitalSignature(xref, acroForm) {
  try {
    const catalog = xref.trailer?.get("Root");
    const perms = catalog instanceof Dict ? catalog.get("Perms") : null;
    if (perms instanceof Dict && perms.size > 0) {
      return true;
    }
    const fields = acroForm instanceof Dict ? acroForm.get("Fields") : null;
    if (!Array.isArray(fields)) {
      return false;
    }
    const seen = new RefSet();
    const stack = fields.slice();
    for (let guard = 0; stack.length > 0 && guard < 20000; guard++) {
      const item = stack.pop();
      if (item instanceof Ref) {
        if (seen.has(item)) {
          continue;
        }
        seen.put(item);
      }
      const field = await xref.fetchIfRefAsync(item);
      if (!(field instanceof Dict)) {
        continue;
      }
      if (isName(field.get("FT"), "Sig") && field.get("V") instanceof Dict) {
        return true;
      }
      const kids = field.get("Kids");
      if (Array.isArray(kids)) {
        stack.push(...kids);
      }
    }
  } catch (ex) {
    warn(`pkHasDigitalSignature: "${ex}".`);
  }
  return false;
}
// 모양의 테두리 상자(BBox 에 Matrix 를 적용한 것)를 주석의 사각형(Rect)에 맞추는 행렬 (PDF 규격 12.5.5 의 계산)
function pkPlacementMatrix(rect, bbox, m) {
  let minX = Infinity,
    minY = Infinity,
    maxX = -Infinity,
    maxY = -Infinity;
  for (const [x, y] of [[bbox[0], bbox[1]], [bbox[2], bbox[1]], [bbox[0], bbox[3]], [bbox[2], bbox[3]]]) {
    const tx = m[0] * x + m[2] * y + m[4];
    const ty = m[1] * x + m[3] * y + m[5];
    minX = Math.min(minX, tx);
    maxX = Math.max(maxX, tx);
    minY = Math.min(minY, ty);
    maxY = Math.max(maxY, ty);
  }
  if (minX === maxX || minY === maxY) {
    return [1, 0, 0, 1, rect[0], rect[1]];
  }
  const sx = (rect[2] - rect[0]) / (maxX - minX);
  const sy = (rect[3] - rect[1]) / (maxY - minY);
  return [sx, 0, 0, sy, rect[0] - minX * sx, rect[1] - minY * sy];
}
function pkSameRect(a, b) {
  return Math.abs(a[0] - b[0]) < 0.05 && Math.abs(a[1] - b[1]) < 0.05 && Math.abs(a[2] - b[2]) < 0.05 && Math.abs(a[3] - b[3]) < 0.05;
}
function pkRectsOverlap(a, b) {
  return a[0] < b[2] - 0.1 && b[0] < a[2] - 0.1 && a[1] < b[3] - 0.1 && b[1] < a[3] - 0.1;
}
// 이번 저장에서 새로 만든 주석이거나, 원래 있던 주석의 자리·크기를 바꾼 것인가 (댓글이나 설명만 고친 기존 주석은 아니다)
async function pkIsNewOrMoved(xref, ref, dict) {
  if (!(ref instanceof Ref) || xref.getEntry(ref.num) === null) {
    return true;
  }
  let original = null;
  try {
    original = await xref.fetchAsync(ref);
  } catch {
    return true;
  }
  if (!(original instanceof Dict)) {
    return true;
  }
  const before = lookupNormalRect(original.getArray("Rect"), null);
  const after = lookupNormalRect(dict.getArray("Rect"), null);
  return !before || !after || !pkSameRect(before, after);
}
// 주석 하나를 쪽 내용으로 그릴 수 있으면 { apRef, matrix } 를, 아니면 { reason } 을 돌려준다 (그러면 주석으로 그대로 둔다).
//   hidden: 원래 보이지 않는 주석(세지 않는다) / special: 쪽 내용으로는 같게 그릴 수 없는 표시 방식 /
//   comment: 댓글이 달려 있음(박으면 댓글을 둘 곳이 없다) / noAppearance: 모양 정보가 없거나 온전치 않음
async function pkPlaceAppearance(dict, subtypeName, resolve, pageRotated) {
  const flags = dict.get("F");
  const f = Number.isInteger(flags) ? flags : 0;
  if (f & 0x22) {
    return {
      reason: "hidden"
    }; // 숨김(2), 화면에 보이지 않음(32)
  }
  if (f & 0x08 || f & 0x10 && pageRotated || dict.has("OC")) {
    return {
      reason: "special"
    }; // 확대해도 크기가 그대로인 주석, 돌려 놓은 쪽에서도 바로 서 있는 주석, 레이어에 따라 보였다 안 보였다 하는 주석
  }
  if (subtypeName !== "FreeText") {
    const contents = dict.get("Contents");
    if (typeof contents === "string" && stringToPDFString(contents).trim() !== "") {
      return {
        reason: "comment"
      };
    }
  }
  const ap = dict.get("AP");
  if (!(ap instanceof Dict)) {
    return {
      reason: "noAppearance"
    }; // 뷰어마다 알아서 그리는 주석 (pdf.js 가 저장한 한글 글상자 등)
  }
  let apRef = ap.getRaw("N");
  let stream = await resolve(apRef);
  if (stream instanceof Dict) {
    const state = dict.get("AS");
    if (!(state instanceof Name)) {
      return {
        reason: "noAppearance"
      };
    }
    apRef = stream.getRaw(state.name);
    stream = await resolve(apRef);
  }
  if (!(apRef instanceof Ref) || !(stream instanceof BaseStream) || !(stream.dict instanceof Dict)) {
    return {
      reason: "noAppearance"
    };
  }
  if (!isName(stream.dict.get("Subtype"), "Form")) {
    return {
      reason: "noAppearance"
    };
  }
  if (stream.dict.has("Group")) {
    return {
      reason: "special"
    }; // 투명 그룹으로 된 모양: 주석으로 그릴 때는 그룹을 무시하고 쪽 내용으로 그릴 때는 따르는 뷰어가 있어(pdf.js·poppler) 모습이 달라질 수 있다
  }
  const rect = lookupNormalRect(dict.getArray("Rect"), null);
  const bbox = lookupRect(stream.dict.getArray("BBox"), null);
  if (!rect || !bbox || !(rect[2] > rect[0]) || !(rect[3] > rect[1])) {
    return {
      reason: "noAppearance"
    };
  }
  const matrix = pkPlacementMatrix(rect, bbox, lookupMatrix(stream.dict.getArray("Matrix"), IDENTITY_MATRIX));
  if (!matrix.every(Number.isFinite)) {
    return {
      reason: "noAppearance"
    };
  }
  return {
    apRef,
    matrix
  };
}
// 쪽 내용 맨 앞에 q 하나를 붙였다고 치고 끝까지 읽어, 우리 그림을 덧붙이기 전에 닫아야 할 q 의 수(우리 것 포함)를 센다.
//   1 이상: 그만큼 Q 를 붙인다 / 0: 쪽 내용의 맨 마지막 Q 가 우리 q 를 닫았다(더 붙일 것 없음) /
//   -1: 덧붙이면 안 되는 쪽 — 우리 q 가 닫힌 뒤에도 내용이 이어지거나(앞뒤를 감싸면 쪽의 모습이 달라진다),
//       글자 묶음(BT)이나 표시 내용(BMC/BDC)이 닫히지 않은 채 끝난다(우리 그림이 그 안에 들어가 버린다)
async function pkUnclosedSaves(page, xref) {
  const stream = await page.getContentStream();
  const stateManager = new StateManager(new EvalState());
  stateManager.save();
  const preprocessor = new EvaluatorPreprocessor(stream, xref, stateManager);
  const operation = {
    fn: 0,
    args: null
  };
  let inText = false,
    marked = 0,
    closedEarly = false;
  for (let i = 0; i < 50000000; i++) {
    operation.args = null;
    if (!preprocessor.read(operation)) {
      return inText || marked > 0 ? -1 : stateManager.stateStack.length;
    }
    if (closedEarly) {
      return -1;
    }
    switch (operation.fn) {
      case OPS.beginText:
        inText = true;
        break;
      case OPS.endText:
        inText = false;
        break;
      case OPS.beginMarkedContent:
      case OPS.beginMarkedContentProps:
        marked++;
        break;
      case OPS.endMarkedContent:
        if (marked > 0) {
          marked--;
        }
        break;
    }
    if (stateManager.stateStack.length === 0) {
      closedEarly = true;
    }
  }
  return -1;
}
async function pkFlattenAnnotations({
  pdfManager,
  xref,
  changes,
  numPages,
  acroForm,
  options
}) {
  // done: 박아 넣은 수 / blocked: 전자서명 문서라 그대로 둔 수 / failedPages: 내용을 읽다 오류가 난 쪽 수
  // skipped: 박아 넣으려 했으나 주석으로 둔 수. 이유별로도 센다 —
  //   noAppearance(모양 정보 없음), special(쪽 내용으로는 같게 그릴 수 없음), comment(댓글이 달림),
  //   overlap(남는 주석·양식 칸 위에 겹쳐 있음), content(쪽 내용이 온전치 않아 덧붙일 수 없음)
  const result = {
    done: 0,
    skipped: 0,
    blocked: 0,
    failedPages: 0,
    pages: 0,
    noAppearance: 0,
    special: 0,
    comment: 0,
    overlap: 0,
    content: 0
  };
  const wanted = new Set(Array.isArray(options?.subtypes) ? options.subtypes : []);
  if (wanted.size === 0) {
    return result;
  }
  const all = !!options.all; // true: 문서에 이미 있던 주석까지, false: 이번 저장에서 새로 만들었거나 자리·크기를 바꾼 주석만
  // rects 가 있으면 사각형이 그 가운데 하나와 같은 주석만 (쪽 편집 저장처럼 다른 길로 먼저 저장된 서명을 찾아 박을 때)
  const rects = Array.isArray(options.rects) ? options.rects.filter(r => isNumberArray(r, 4)).map(r => Util.normalizeRect(r)) : null;
  const signed = await pkHasDigitalSignature(xref, acroForm);
  const resolve = async obj => {
    if (!(obj instanceof Ref)) {
      return obj;
    }
    const entry = changes.get(obj);
    return entry ? entry.data : xref.fetchAsync(obj);
  };
  const skip = (reason, count = 1) => {
    result[reason] += count;
    result.skipped += count;
  };
  for (let pageIndex = 0; pageIndex < numPages; pageIndex++) {
    let pending = 0; // 이 쪽에서 박으려는 수 (쪽을 읽다 오류가 나면 이만큼을 못 박은 것으로 센다)
    try {
      const page = await pdfManager.getPage(pageIndex);
      if (!(page.ref instanceof Ref)) {
        continue;
      }
      const changedDict = changes.get(page.ref)?.data;
      const pageChanged = changedDict instanceof Dict;
      if (!all && !pageChanged) {
        continue;
      }
      const basePageDict = pageChanged ? changedDict : page.pageDict;
      const annots = pageChanged ? changedDict.get("Annots") : page.annotations;
      if (!Array.isArray(annots) || annots.length === 0) {
        continue;
      }
      const changedRotate = pageChanged ? changedDict.get("Rotate") : undefined;
      const rotate = typeof changedRotate === "number" ? changedRotate : page.rotate;
      const pageRotated = (rotate % 360 + 360) % 360 !== 0;
      // 1) 쪽의 주석을 순서대로 살펴 박을 것을 고른다
      const entries = [];
      let candidates = 0;
      for (const item of annots) {
        const inChanges = item instanceof Ref && changes.has(item);
        let dict = null;
        try {
          dict = await resolve(item);
        } catch {
          dict = null;
        }
        const entry = {
          item,
          inChanges,
          dict: dict instanceof Dict ? dict : null,
          subtype: "",
          flags: 0,
          rect: null,
          placed: null,
          reason: null
        };
        entries.push(entry);
        if (!entry.dict) {
          continue;
        }
        const subtype = entry.dict.get("Subtype");
        entry.subtype = subtype instanceof Name ? subtype.name : "";
        const flags = entry.dict.get("F");
        entry.flags = Number.isInteger(flags) ? flags : 0;
        entry.rect = lookupNormalRect(entry.dict.getArray("Rect"), null);
        if (!wanted.has(entry.subtype)) {
          continue;
        }
        if (!all && !(inChanges && (await pkIsNewOrMoved(xref, item, entry.dict)))) {
          continue;
        }
        if (rects && !(entry.rect && rects.some(r => pkSameRect(r, entry.rect)))) {
          continue;
        }
        const placed = await pkPlaceAppearance(entry.dict, entry.subtype, resolve, pageRotated);
        if (placed.reason) {
          entry.reason = placed.reason;
        } else {
          entry.placed = placed;
          candidates++;
        }
      }
      // 2) 박지 않고 남는 "보이는" 주석(양식 칸 포함)보다 위에 겹쳐 있던 것은 박지 않는다.
      //    박으면 쪽 내용이 되어 남은 주석 밑으로 깔리므로 가려질 수 있다 (배경이 칠해진 양식 칸 위의 서명 등).
      const covering = [];
      for (const entry of entries) {
        if (entry.placed && covering.some(r => pkRectsOverlap(r, entry.rect))) {
          entry.placed = null;
          entry.reason = "overlap";
          candidates--;
        }
        if (!entry.placed && entry.dict && entry.rect && entry.subtype !== "Popup" && !(entry.flags & 0x22) && (entry.dict.has("AP") || entry.subtype === "Widget")) {
          covering.push(entry.rect);
        }
      }
      if (signed) {
        result.blocked += candidates;
        continue;
      }
      for (const entry of entries) {
        if (entry.reason && entry.reason !== "hidden") {
          skip(entry.reason);
        }
      }
      if (candidates === 0) {
        continue;
      }
      pending = candidates;
      const rawContents = basePageDict.getRaw("Contents");
      let items;
      if (rawContents instanceof Ref) {
        const fetched = await xref.fetchAsync(rawContents);
        items = Array.isArray(fetched) ? fetched.slice() : [rawContents];
      } else if (Array.isArray(rawContents)) {
        items = rawContents.slice();
      } else {
        items = [];
      }
      let unclosed = 0;
      if (items.length > 0) {
        unclosed = await pkUnclosedSaves(page, xref);
        if (unclosed < 0) {
          skip("content", candidates);
          pending = 0;
          continue;
        }
      }
      // 3) 쪽의 자원 사전을 복제해(물려받은 것 포함) 모양들을 XObject 로 등록한다. 원래 사전은 다른 쪽과 함께 쓰일 수 있어 건드리지 않는다.
      const baseResources = page.resources;
      const resources = new Dict(xref);
      if (baseResources instanceof Dict) {
        for (const [key, value] of baseResources.getRawEntries()) {
          resources.set(key, value);
        }
      }
      const baseXObjects = baseResources instanceof Dict ? baseResources.get("XObject") : null;
      const xobjects = new Dict(xref);
      if (baseXObjects instanceof Dict) {
        for (const [key, value] of baseXObjects.getRawEntries()) {
          xobjects.set(key, value);
        }
      }
      resources.set("XObject", xobjects);
      const ops = [];
      const drop = [];
      const popups = new Set();
      let counter = 0;
      for (const entry of entries) {
        if (!entry.placed) {
          continue;
        }
        let name;
        do {
          name = `PkF${++counter}`;
        } while (xobjects.has(name));
        xobjects.set(name, entry.placed.apRef);
        ops.push(`q ${entry.placed.matrix.map(numberToPDFString).join(" ")} cm /${name} Do Q`);
        if (entry.inChanges) {
          drop.push(entry.item); // 새로 만들어진 주석 객체는 파일에 쓰지 않는다
        }
        const popup = entry.dict.getRaw("Popup");
        if (popup instanceof Ref) {
          popups.add(popup.toString()); // (댓글이 있는 주석은 위에서 걸러졌으므로 여기 오는 것은 빈 팝업이다)
        }
      }
      const kept = [];
      for (const entry of entries) {
        if (entry.placed) {
          continue;
        }
        if (entry.item instanceof Ref && popups.has(entry.item.toString())) {
          if (entry.inChanges) {
            drop.push(entry.item);
          }
          continue;
        }
        kept.push(entry.item);
      }
      const pageDict = basePageDict.clone();
      pageDict.set("Resources", resources);
      const contents = [];
      const puts = [];
      if (items.length > 0) {
        // 원래 내용을 q ... Q 로 감싸, 원래 내용이 바꿔 놓은 좌표계·자르기 영역이 우리 그림에 미치지 않게 한다
        const preRef = xref.getNewTemporaryRef();
        puts.push([preRef, new StringStream("q\n", new Dict(xref))]);
        contents.push(preRef, ...items);
      }
      const postRef = xref.getNewTemporaryRef();
      puts.push([postRef, new StringStream(`${"\nQ".repeat(unclosed)}\n${ops.join("\n")}\n`, new Dict(xref))]);
      contents.push(postRef);
      pageDict.set("Contents", contents);
      pageDict.set("Annots", kept);
      // 여기부터는 실패할 일이 없는 대입만 한다 (중간에 끊겨 쪽이 반쯤 바뀐 채로 저장되는 일이 없게)
      for (const [ref, data] of puts) {
        changes.put(ref, {
          data
        });
      }
      changes.put(page.ref, {
        data: pageDict
      });
      for (const ref of drop) {
        changes.delete(ref);
      }
      result.done += candidates;
      result.pages++;
      pending = 0;
    } catch (ex) {
      result.failedPages++;
      if (pending > 0) {
        skip("content", pending);
      }
      warn(`pkFlattenAnnotations: page ${pageIndex + 1}: "${ex}".`);
    }
  }
  return result;
}
'''

V2_WORKER = [
    (REFMAP_OLD, REFMAP_NEW),
    (HELPERS_ANCHOR, HELPERS + HELPERS_ANCHOR),
    (
        SAVE_HEAD,
        'handler.on("PkFlattenResult", function () { /* ' + MARK + ': 방금 저장에서 박아 넣은 결과 (한 번 읽으면 지운다) */\n'
        '      const last = pkFlattenLast;\n'
        '      pkFlattenLast = null;\n'
        '      return last;\n'
        '    });\n'
        '    handler.on("SaveDocument", async function ({\n'
        '      isPureXfa,\n'
        '      numPages,\n'
        '      annotationStorage,\n'
        '      supportsPrintToPDF,\n'
        '      filename,\n'
        '      pageRotations = null, /* ' + MARK + ' */\n'
        '      pkFlatten = null /* ' + MARK + ' */\n'
        '    }) {\n'
        '      /* ' + MARK + ': 박아 넣을 주석에는 구조 트리 항목을 만들지 않게 한다 (주석 객체가 파일에 남지 않으므로) */\n'
        '      pkFlattenLast = null;\n'
        '      if (pkFlatten && !isPureXfa && annotationStorage instanceof Map) {\n'
        '        const pkWanted = new Set(Array.isArray(pkFlatten.subtypes) ? pkFlatten.subtypes : []);\n'
        '        let pkSigned = true;\n'
        '        try {\n'
        '          pkSigned = await pkHasDigitalSignature(await pdfManager.ensureDoc("xref"), await pdfManager.ensureCatalog("acroForm"));\n'
        '        } catch {}\n'
        '        if (!pkSigned) {\n'
        '          for (const value of annotationStorage.values()) {\n'
        '            /* 새로 만든 것이고 댓글이 없는 것만: 전자서명 문서의 주석, 댓글이 달린 주석, 원래 있던 주석은 주석으로 남으므로 설명을 지우지 않는다 */\n'
        '            if (value && !value.id && pkWanted.has(pkFlattenSubtypeOf(value)) && !(value.popup && !value.popup.deleted && value.popup.contents)) {\n'
        '              delete value.accessibilityData;\n'
        '            }\n'
        '          }\n'
        '        }\n'
        '      }',
    ),
    (
        SAVE_AFTER_PROMISES,
        '      const refs = await Promise.all(promises);\n'
        '      /* ' + MARK + ': 페이지 회전 저장 - 페이지 사전의 /Rotate 만 바꿔 증분 저장에 넣는다\n'
        '         (이번 저장에서 이미 바뀐 페이지 사전이 있으면 그것을 고친다: 새 주석 목록을 잃지 않게) */\n'
        '      if (pageRotations && !isPureXfa) {\n'
        '        for (const key of Object.keys(pageRotations)) {\n'
        '          const delta = pageRotations[key] | 0;\n'
        '          if (!delta) continue;\n'
        '          const rotPage = await pdfManager.getPage(parseInt(key, 10));\n'
        '          const rotPrev = changes.get(rotPage.ref)?.data;\n'
        '          const rotDict = rotPrev instanceof Dict ? rotPrev : rotPage.pageDict.clone();\n'
        '          rotDict.set("Rotate", (((rotPage.rotate + delta) % 360) + 360) % 360);\n'
        '          changes.put(rotPage.ref, { data: rotDict });\n'
        '        }\n'
        '      }\n'
        '      /* ' + MARK + ': 서명·주석 박아 넣기 */\n'
        '      if (pkFlatten && !isPureXfa) {\n'
        '        try {\n'
        '          pkFlattenLast = await pkFlattenAnnotations({\n'
        '            pdfManager,\n'
        '            xref,\n'
        '            changes,\n'
        '            numPages,\n'
        '            acroForm,\n'
        '            options: pkFlatten\n'
        '          });\n'
        '        } catch (ex) {\n'
        '          /* 박아 넣기에 실패해도 저장은 평소대로(주석으로) 한다 */\n'
        '          pkFlattenLast = { done: 0, skipped: 0, blocked: 0, failedPages: numPages, pages: 0, noAppearance: 0, special: 0, comment: 0, overlap: 0, content: 0 };\n'
        '          warn(`pkFlattenAnnotations: "${ex}".`);\n'
        '        }\n'
        '      }\n'
        '      let xfaData = null;\n',
    ),
]
V2_MAIN = [
    (
        MAIN_SAVE,
        MAIN_SAVE +
        '      pageRotations: this.pageRotations || null, /* ' + VERSION + ' */\n'
        '      pkFlatten: this.pkFlatten || null, /* ' + MARK + ' */\n',
    ),
]


def apply(s, pairs, path):
    for old, new in pairs:
        n = s.count(old)
        if n != 1:
            raise SystemExit("anchor count %d (expected 1) in %s:\n%s" % (n, path, old))
        s = s.replace(old, new)
    return s


def revert(s, pairs, path):
    for old, new in reversed(pairs):
        n = s.count(new)
        if n != 1:
            raise SystemExit("cannot revert the old patch (count %d, expected 1) in %s:\n%s" % (n, path, new))
        s = s.replace(new, old)
    return s


def patch(path, v1, v2):
    with open(path, encoding="utf-8") as f:
        s = f.read()
    if VERSION in s:
        print("already patched:", path)
        return
    if MARK in s:
        s = revert(s, v1, path)
        if MARK in s:
            raise SystemExit("unknown older patch in " + path)
        print("old patch removed:", path)
    s = apply(s, v2, path)
    if VERSION not in s:
        raise SystemExit("version mark missing after patching " + path)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(s)
    print("patched:", path)


patch(os.path.join(root, "build", "pdf.worker.mjs"), V1_WORKER, V2_WORKER)
patch(os.path.join(root, "build", "pdf.mjs"), V1_MAIN, V2_MAIN)
print("done")
