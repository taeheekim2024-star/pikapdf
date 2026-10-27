# PDF 뷰어를 이 사용자 계정에 등록한다 (관리자 권한 불필요).
#  - PDF 우클릭 > 연결 프로그램 목록과 설정 > 기본 앱 목록에 나타나게 함
#  - 시작 메뉴와 바탕화면에 바로가기
# 실행:  powershell -ExecutionPolicy Bypass -File .\등록.ps1
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$exe = Join-Path $root 'PdfViewer.exe'
if (-not (Test-Path $exe)) { throw "PdfViewer.exe 가 없습니다: $exe" }
$progId = 'PdfViewer.Document'
$appName = 'PDF 뷰어'

function Set-Default($key, $value) {
    if (-not (Test-Path $key)) { New-Item -Path $key -Force | Out-Null }
    Set-ItemProperty -Path $key -Name '(default)' -Value $value
}
function Set-Val($key, $name, $value) {
    if (-not (Test-Path $key)) { New-Item -Path $key -Force | Out-Null }
    Set-ItemProperty -Path $key -Name $name -Value $value
}

$cmd = "`"$exe`" `"%1`""

# 문서 형식(ProgID)
Set-Default "HKCU:\Software\Classes\$progId" 'PDF 문서'
Set-Val     "HKCU:\Software\Classes\$progId" 'FriendlyTypeName' 'PDF 문서'
Set-Default "HKCU:\Software\Classes\$progId\DefaultIcon" "`"$exe`",0"
Set-Default "HKCU:\Software\Classes\$progId\shell\open\command" $cmd
Set-Val     "HKCU:\Software\Classes\.pdf\OpenWithProgids" $progId ''

# 응용 프로그램 항목 (연결 프로그램 목록)
$appKey = 'HKCU:\Software\Classes\Applications\PdfViewer.exe'
Set-Val     $appKey 'FriendlyAppName' $appName
Set-Default "$appKey\DefaultIcon" "`"$exe`",0"
Set-Default "$appKey\shell\open\command" $cmd
Set-Val     "$appKey\SupportedTypes" '.pdf' ''

# 기본 앱 후보 (설정 > 앱 > 기본 앱)
$cap = 'HKCU:\Software\PDF뷰어\Capabilities'
Set-Val $cap 'ApplicationName' $appName
Set-Val $cap 'ApplicationDescription' 'PDF 보기, 형광펜, 메모, 서명, 양식 입력, 인쇄'
Set-Val "$cap\FileAssociations" '.pdf' $progId
Set-Val 'HKCU:\Software\RegisteredApplications' $appName 'Software\PDF뷰어\Capabilities'

# 바로가기
$ws = New-Object -ComObject WScript.Shell
foreach ($dir in @([Environment]::GetFolderPath('Programs'), [Environment]::GetFolderPath('DesktopDirectory'))) {
    $lnk = Join-Path $dir "$appName.lnk"
    $s = $ws.CreateShortcut($lnk)
    $s.TargetPath = $exe
    $s.WorkingDirectory = $root
    $s.IconLocation = "$exe,0"
    $s.Description = $appName
    $s.Save()
    "바로가기: $lnk"
}

# 탐색기에 알림
if (-not ('Win32.Shell32Notify' -as [type])) {
    Add-Type -Namespace Win32 -Name Shell32Notify -MemberDefinition '[DllImport("shell32.dll")] public static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);'
}
[Win32.Shell32Notify]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero)

"등록 완료."
"기본 앱으로 쓰려면: PDF 파일 우클릭 > 연결 프로그램 > 다른 앱 선택 > '$appName' > 항상"
