# PDF 뷰어 빌드. Windows에 기본 포함된 .NET Framework 4.x C# 컴파일러만 사용 (별도 설치 불필요).
# 실행:  powershell -ExecutionPolicy Bypass -File .\build.ps1
$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
$root = Split-Path -Parent $src
$csc = Join-Path $env:windir 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "C# 컴파일러가 없습니다: $csc" }
foreach ($f in @('Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.WinForms.dll', 'WebView2Loader.dll', 'app.ico', 'inject.js')) {
    if (-not (Test-Path (Join-Path $root $f))) { throw "필요한 파일이 없습니다: $f" }
}
$out = Join-Path $root 'PdfViewer.exe'
& $csc /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 `
    /win32icon:"$root\app.ico" /win32manifest:"$src\app.manifest" /out:"$out" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:Microsoft.CSharp.dll /r:System.Net.Http.dll `
    /r:"$root\Microsoft.Web.WebView2.Core.dll" /r:"$root\Microsoft.Web.WebView2.WinForms.dll" `
    "$src\Program.cs" "$src\AssemblyInfo.cs"
if ($LASTEXITCODE -ne 0) { throw "컴파일 실패 (exit $LASTEXITCODE)" }
"빌드 완료: $out"
