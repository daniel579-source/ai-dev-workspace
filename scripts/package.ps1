# 打包 LogTrace（#29、#218；ADR-0001、ADR-0007、ADR-0030）。產生：
#   publish/LogTrace/                          主程式的 self-contained 資料夾（含 WebView2Runtime\，開發與冒煙測試用）
#   publish/LogTrace-<版本>-win-x64.zip         完整包：首次安裝用，解到 D:\LogTrace\ 得到 D:\LogTrace\Program\
#   publish/LogTrace-<版本>-app.zip             程式包：放進 Program\updates\ 升級（不含 WebView2 Runtime）
#   publish/LogTrace-webview2-<Runtime 版本>-runtime.zip   Runtime 包：只有 Runtime 版本變更時才需要寄
# 用法：pwsh scripts/package.ps1             （Runtime 包已存在時不重做；-RuntimePackage 強制重做）
#       pwsh scripts/package.ps1 -Output out
# 分割大小由寄送的人在寄送前以 7-Zip 自行決定，這裡不分割（ADR-0030 第 2 點）。
param([string]$Output = "publish", [switch]$RuntimePackage)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src/LogTrace.App/LogTrace.App.csproj'
$launcherProject = Join-Path $root 'src/LogTrace.Launcher/LogTrace.Launcher.csproj'
$csproj = [xml](Get-Content $project -Raw)
$version = $csproj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "LogTrace.App.csproj 中沒有 <Version>" }

$outDir = Join-Path $root $Output
$folder = Join-Path $outDir 'LogTrace'
$program = Join-Path $outDir 'Program'
$launcherOut = Join-Path $outDir 'launcher'
$zip = Join-Path $outDir "LogTrace-$version-win-x64.zip"
$appZip = Join-Path $outDir "LogTrace-$version-app.zip"
foreach ($p in $folder, $program, $launcherOut) { if (Test-Path $p) { Remove-Item -Recurse -Force $p } }
foreach ($p in $zip, $appZip) { if (Test-Path $p) { Remove-Item -Force $p } }

# WebView2 固定版本 Runtime（ADR-0017 第 2 點）：必須先下載並解開到 third_party\webview2\runtime\（見 README）
$runtimeVersion = $csproj.Project.PropertyGroup.WebView2RuntimeVersion | Where-Object { $_ } | Select-Object -First 1
$runtimeSource = Join-Path $root "third_party/webview2/runtime/Microsoft.WebView2.FixedVersionRuntime.$runtimeVersion.x64"
if (-not (Test-Path (Join-Path $runtimeSource 'msedgewebview2.exe'))) { throw "找不到 WebView2 固定版本 Runtime $runtimeVersion：$runtimeSource（見 README「WebView2 Runtime」）" }
$runtimeZip = Join-Path $outDir "LogTrace-webview2-$runtimeVersion-runtime.zip"

dotnet publish $project -c Release -r win-x64 --self-contained -o $folder
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失敗（$LASTEXITCODE）" }
# 啟動程式（ADR-0030 第 4 點）：Native AOT 單一 exe，版本與主程式相同。
# AOT 以 vswhere.exe 找 Visual Studio 的 C++ 連結器；它通常不在 PATH，這裡只對本次執行加上
$vswhereDir = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
if (-not (Get-Command vswhere.exe -ErrorAction SilentlyContinue) -and (Test-Path (Join-Path $vswhereDir 'vswhere.exe'))) { $env:PATH += ";$vswhereDir" }
dotnet publish $launcherProject -c Release -r win-x64 -p:Version=$version -o $launcherOut
if ($LASTEXITCODE -ne 0) { throw "啟動程式 dotnet publish 失敗（$LASTEXITCODE）" }
$launcherExe = Join-Path $launcherOut 'LogTrace.exe'
if (-not (Test-Path $launcherExe)) { throw "找不到啟動程式 $launcherExe" }

# 程式包必須有的檔案：主程式、7-Zip、使用說明、更新紀錄、授權清單與全文；3D 回放與 Layout 編輯器（ADR-0017、ADR-0018）的網頁與 three.js
$appFiles = @('LogTrace.exe', '7z/7za.dll', '7z/License.txt', '使用說明.md', 'CHANGELOG.md', 'THIRD-PARTY-NOTICES.md', 'licenses/Apache-2.0.txt',
              'licenses/AvalonDock-LICENSE.txt', 'WebView2Loader.dll', 'web3d/index.html', 'web3d/editor.html', 'web3d/common.js',
              'web3d/vendor/three/three.module.js', 'licenses/three.js-LICENSE.txt', 'licenses/Microsoft.Web.WebView2-LICENSE.txt',
              'licenses/Microsoft-Edge-WebView2-Runtime-License.txt', 'launcher/LogTrace.exe')
$runtimeFiles = @('msedgewebview2.exe', 'msedge.dll')
foreach ($f in $appFiles | Where-Object { $_ -ne 'launcher/LogTrace.exe' }) {
    if (-not (Test-Path (Join-Path $folder $f))) { throw "發佈資料夾缺少 $f" }
}
if (-not (Test-Path (Join-Path $folder 'WebView2Runtime/msedgewebview2.exe'))) { throw "發佈資料夾缺少 WebView2Runtime/msedgewebview2.exe" }
# 不得包含有 unRAR 授權限制的 7z.dll
if (Get-ChildItem $folder -Recurse -Filter '7z.dll') { throw "發佈資料夾不應包含 7z.dll" }

# 該版的更新說明：CHANGELOG.md 的「## <版本>」一節；還沒改成版本號時用「## 未發佈」
function Get-Notes([string]$v) {
    $text = Get-Content (Join-Path $root 'CHANGELOG.md') -Raw -Encoding utf8
    foreach ($heading in "## $v", '## 未發佈') {
        $m = [regex]::Match($text, "(?ms)^" + [regex]::Escape($heading) + "\s*\r?\n(.*?)(?=^## |\z)")
        if ($m.Success -and $m.Groups[1].Value.Trim()) {
            if ($heading -ne "## $v") { Write-Warning "CHANGELOG.md 沒有「## $v」，改用「## 未發佈」的內容" }
            return $m.Groups[1].Value.Trim()
        }
    }
    Write-Warning "CHANGELOG.md 沒有 $v 的說明"
    return ''
}

function Write-Manifest([string]$path, [string]$kind, [string]$v, [string]$rv, [string[]]$files, [string]$notes) {
    $json = [ordered]@{ kind = $kind; version = $v; runtimeVersion = $rv; files = $files; notes = $notes } | ConvertTo-Json -Depth 3
    [System.IO.File]::WriteAllText($path, $json, [System.Text.UTF8Encoding]::new($false))
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$notes = Get-Notes $version

# 版本資料夾的內容：主程式（不含 WebView2Runtime\）＋ launcher\LogTrace.exe（新版啟動程式，第 3 點第 9 步）
function Copy-AppPayload([string]$dest) {
    New-Item -ItemType Directory -Force $dest | Out-Null
    Get-ChildItem $folder -Force | Where-Object { $_.Name -ne 'WebView2Runtime' } | Copy-Item -Destination $dest -Recurse -Force
    New-Item -ItemType Directory -Force (Join-Path $dest 'launcher') | Out-Null
    Copy-Item $launcherExe (Join-Path $dest 'launcher/LogTrace.exe')
}

# 1. 程式包：update.json ＋ app\
$stage = Join-Path $outDir 'stage-app'
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
Copy-AppPayload (Join-Path $stage 'app')
Write-Manifest (Join-Path $stage 'update.json') 'app' $version $runtimeVersion $appFiles $notes
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $appZip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
Remove-Item -Recurse -Force $stage

# 2. Runtime 包：update.json ＋ runtime\（已存在時不重做，約 680 MB）
if ($RuntimePackage -or -not (Test-Path $runtimeZip)) {
    if (Test-Path $runtimeZip) { Remove-Item -Force $runtimeZip }
    $stage = Join-Path $outDir 'stage-runtime'
    if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
    New-Item -ItemType Directory -Force $stage | Out-Null
    Copy-Item (Join-Path $folder 'WebView2Runtime') (Join-Path $stage 'runtime') -Recurse
    Write-Manifest (Join-Path $stage 'update.json') 'runtime' $runtimeVersion $runtimeVersion $runtimeFiles ''
    [System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $runtimeZip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
    Remove-Item -Recurse -Force $stage
}

# 3. 完整包（首次安裝）：Program\ 下放啟動程式、launcher.json、updates\、app-<版本>\、runtimes\webview2-<版本>\
New-Item -ItemType Directory -Force (Join-Path $program 'updates') | Out-Null
Copy-Item $launcherExe (Join-Path $program 'LogTrace.exe')
$appDir = Join-Path $program "app-$version"
Copy-AppPayload $appDir
Write-Manifest (Join-Path $appDir 'installed.json') 'app' $version $runtimeVersion $appFiles $notes
$rtDir = Join-Path $program "runtimes/webview2-$runtimeVersion"
New-Item -ItemType Directory -Force (Split-Path $rtDir) | Out-Null
Copy-Item (Join-Path $folder 'WebView2Runtime') $rtDir -Recurse
Write-Manifest (Join-Path $rtDir 'installed.json') 'runtime' $runtimeVersion $runtimeVersion $runtimeFiles ''
[System.IO.File]::WriteAllText((Join-Path $program 'launcher.json'), "{`n  `"current`": `"$version`"`n}", [System.Text.UTF8Encoding]::new($false))
New-Item -ItemType File -Force (Join-Path $program 'updates/把升級包放在這裡.txt') -Value "把 LogTrace-<版本>-app.zip 或 LogTrace-webview2-<版本>-runtime.zip 放在這個資料夾，重新啟動 LogTrace 就會詢問是否套用。`r`n分割檔請先用 7-Zip 合併成一個 .zip。`r`n" | Out-Null
[System.IO.Compression.ZipFile]::CreateFromDirectory($program, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $true)

foreach ($z in $zip, $appZip, $runtimeZip) {
    Write-Host ("已打包：{0}（{1:N1} MB）" -f $z, ((Get-Item $z).Length / 1MB))
}
