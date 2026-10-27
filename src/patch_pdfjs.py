# pdf.js 배포본에 "페이지 회전 저장" 패치를 넣는다 (pdf.js 를 새 버전으로 바꾸면 다시 실행).
#   - build/pdf.worker.mjs : SaveDocument 처리기에 pageRotations 를 받아 페이지 사전의 /Rotate 를 바꿔 증분 저장에 포함
#   - build/pdf.mjs        : saveDocument 메시지에 transport.pageRotations 를 실어 보냄
# 실행:  python3 patch_pdfjs.py <pdfjs 폴더>
import sys, os

root = sys.argv[1]
MARK = "PDFVIEWER-PATCH"

def patch(path, pairs):
    with open(path, encoding="utf-8") as f:
        s = f.read()
    if MARK in s:
        print("already patched:", path)
        return
    for old, new in pairs:
        n = s.count(old)
        if n != 1:
            raise SystemExit("anchor count %d (expected 1) in %s:\n%s" % (n, path, old))
        s = s.replace(old, new)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(s)
    print("patched:", path)

worker = os.path.join(root, "build", "pdf.worker.mjs")
patch(worker, [
    (
        'handler.on("SaveDocument", async function ({\n'
        '      isPureXfa,\n'
        '      numPages,\n'
        '      annotationStorage,\n'
        '      supportsPrintToPDF,\n'
        '      filename\n'
        '    }) {',
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
        '      const refs = await Promise.all(promises);\n'
        '      let xfaData = null;\n',
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
])

main = os.path.join(root, "build", "pdf.mjs")
patch(main, [
    (
        '    return this.messageHandler.sendWithPromise("SaveDocument", {\n'
        '      isPureXfa: !!this._htmlForXfa,\n'
        '      numPages: this._numPages,\n'
        '      annotationStorage: map,\n',
        '    return this.messageHandler.sendWithPromise("SaveDocument", {\n'
        '      isPureXfa: !!this._htmlForXfa,\n'
        '      numPages: this._numPages,\n'
        '      annotationStorage: map,\n'
        '      pageRotations: this.pageRotations || null, /* ' + MARK + ' */\n',
    ),
])
print("done")
