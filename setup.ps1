# PikaPDF 빌드 준비: pdf.js 배포본과 WebView2 SDK를 내려받아 제자리에 풀고, 패치(회전 저장, 서명·주석 박아 넣기)를 적용한다.
# 실행:  powershell -ExecutionPolicy Bypass -File .\setup.ps1
#   - 다시 실행해도 안전합니다 (이미 있으면 건너뜀). 패치 적용에는 Python 3이 필요합니다(Windows python 또는 WSL python3).
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$deps = Join-Path $root '_deps'
New-Item -ItemType Directory -Force $deps | Out-Null

$PDFJS_VERSION = '6.4.299'
$WEBVIEW2_VERSION = '1.0.4191.47'
$pdfjsZip = Join-Path $deps "pdfjs-$PDFJS_VERSION-dist.zip"
$wv2Pkg  = Join-Path $deps "microsoft.web.webview2.$WEBVIEW2_VERSION.nupkg"

function Get-File($url, $dest) {
    if (Test-Path $dest) { "있음: $(Split-Path $dest -Leaf)"; return }
    "받는 중: $url"
    Invoke-WebRequest -Uri $url -OutFile $dest -UseBasicParsing
}

Get-File "https://github.com/mozilla/pdf.js/releases/download/v$PDFJS_VERSION/pdfjs-$PDFJS_VERSION-dist.zip" $pdfjsZip
Get-File "https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/$WEBVIEW2_VERSION/microsoft.web.webview2.$WEBVIEW2_VERSION.nupkg" $wv2Pkg

# pdf.js
$pdfjsDir = Join-Path $root 'pdfjs'
if (-not (Test-Path (Join-Path $pdfjsDir 'web\viewer.html'))) {
    "pdf.js 푸는 중"
    $tmp = Join-Path $deps 'pdfjs-extract'
    if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
    Expand-Archive -Path $pdfjsZip -DestinationPath $tmp -Force
    New-Item -ItemType Directory -Force $pdfjsDir | Out-Null
    Copy-Item (Join-Path $tmp 'build') (Join-Path $pdfjsDir 'build') -Recurse -Force
    Copy-Item (Join-Path $tmp 'web') (Join-Path $pdfjsDir 'web') -Recurse -Force
    Copy-Item (Join-Path $tmp 'LICENSE') (Join-Path $pdfjsDir 'LICENSE') -Force
    Get-ChildItem $pdfjsDir -Recurse -Include '*.map' | Remove-Item -Force
    Remove-Item (Join-Path $pdfjsDir 'web\compressed.tracemonkey-pldi-09.pdf') -Force -ErrorAction SilentlyContinue
    Remove-Item $tmp -Recurse -Force
}

# WebView2 SDK (net462 어셈블리 + x64 로더)
if (-not (Test-Path (Join-Path $root 'WebView2Loader.dll'))) {
    "WebView2 SDK 푸는 중"
    $tmp = Join-Path $deps 'wv2-extract'
    if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
    $zipCopy = Join-Path $deps 'webview2.zip'
    Copy-Item $wv2Pkg $zipCopy -Force
    Expand-Archive -Path $zipCopy -DestinationPath $tmp -Force
    Copy-Item (Join-Path $tmp 'lib\net462\Microsoft.Web.WebView2.Core.dll') $root -Force
    Copy-Item (Join-Path $tmp 'lib\net462\Microsoft.Web.WebView2.WinForms.dll') $root -Force
    Copy-Item (Join-Path $tmp 'runtimes\win-x64\native\WebView2Loader.dll') $root -Force
    Remove-Item $tmp -Recurse -Force
    Remove-Item $zipCopy -Force
}

# pdf.js 패치: 회전 저장, 서명·주석 박아 넣기. 지금 판(src/patch_pdfjs.py 의 VERSION)이 이미 들어 있으면 건너뛴다.
# 예전 판으로 패치된 파일이면 스크립트가 먼저 되돌린 뒤 새로 넣는다. (VERSION 을 올리면 아래 문자열도 함께 올릴 것)
$marker = Select-String -Path (Join-Path $pdfjsDir 'build\pdf.worker.mjs') -Pattern 'PDFVIEWER-PATCH v2' -SimpleMatch -Quiet
if (-not $marker) {
    $patch = Join-Path $root 'src\patch_pdfjs.py'
    $py = Get-Command python -ErrorAction SilentlyContinue
    if ($py -and ($py.Source -notlike '*WindowsApps*')) {
        & $py.Source -I $patch $pdfjsDir
    } elseif (Get-Command wsl -ErrorAction SilentlyContinue) {
        $toWsl = { param($p) '/mnt/' + $p.Substring(0,1).ToLower() + $p.Substring(2).Replace('\','/') }
        wsl python3 -I (& $toWsl $patch) (& $toWsl $pdfjsDir)
    } else {
        throw "패치를 적용할 Python 3이 없습니다 (python 또는 wsl python3)"
    }
    if ($LASTEXITCODE -ne 0) { throw "패치 적용 실패" }
} else { "패치: 이미 적용됨" }

"준비 완료. 이제 src\build.ps1 을 실행하세요."
