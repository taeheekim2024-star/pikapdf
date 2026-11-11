# 등록.ps1 이 만든 레지스트리 항목과 바로가기를 되돌린다. 프로그램 폴더와 백업 폴더는 지우지 않는다.
# 실행:  powershell -ExecutionPolicy Bypass -File .\등록해제.ps1
$ErrorActionPreference = 'Continue'
$progId = 'PdfViewer.Document'
$appName = 'PDF 뷰어'

Remove-Item "HKCU:\Software\Classes\$progId" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item 'HKCU:\Software\Classes\Applications\PdfViewer.exe' -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item 'HKCU:\Software\PDF뷰어' -Recurse -Force -ErrorAction SilentlyContinue
Remove-ItemProperty 'HKCU:\Software\RegisteredApplications' -Name $appName -ErrorAction SilentlyContinue
Remove-ItemProperty 'HKCU:\Software\Classes\.pdf\OpenWithProgids' -Name $progId -ErrorAction SilentlyContinue

# 그림 파일 우클릭 메뉴 ("PDF로 변환", "PDF 하나로 합치기"). 우리가 만든 키만 지우고, 비게 된 윗 키는 정리한다.
$imageExts = '.jpg', '.jpeg', '.jpe', '.jfif', '.png', '.bmp', '.dib', '.gif', '.tif', '.tiff', '.webp', '.heic', '.heif', '.avif'
foreach ($ext in $imageExts) {
    $extKey = "HKCU:\Software\Classes\SystemFileAssociations\$ext"
    foreach ($verb in 'PdfViewer.ToPdf', 'PdfViewer.ToPdfMerge') {
        Remove-Item "$extKey\shell\$verb" -Recurse -Force -ErrorAction SilentlyContinue
    }
    foreach ($k in "$extKey\shell", $extKey) {
        if ((Test-Path $k) -and -not (Get-ChildItem $k) -and ((Get-Item $k).ValueCount -eq 0)) { Remove-Item $k -Force -ErrorAction SilentlyContinue }
    }
}

foreach ($dir in @([Environment]::GetFolderPath('Programs'), [Environment]::GetFolderPath('DesktopDirectory'))) {
    Remove-Item (Join-Path $dir "$appName.lnk") -Force -ErrorAction SilentlyContinue
}

if (-not ('Win32.Shell32Notify' -as [type])) {
    Add-Type -Namespace Win32 -Name Shell32Notify -MemberDefinition '[DllImport("shell32.dll")] public static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);'
}
[Win32.Shell32Notify]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero)
"등록 해제 완료. 기본 앱이 이 프로그램이었다면 다른 PDF 앱을 다시 지정하세요."
