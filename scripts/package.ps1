# 打包 LogTrace（#29，ADR-0001、ADR-0007）：self-contained 單一資料夾，壓成 zip，解壓後直接執行。
# 用法：pwsh scripts/package.ps1            → publish/LogTrace/ 與 publish/LogTrace-<版本>-win-x64.zip
#       pwsh scripts/package.ps1 -Output out
param([string]$Output = "publish")

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src/LogTrace.App/LogTrace.App.csproj'
$version = ([xml](Get-Content $project -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "LogTrace.App.csproj 中沒有 <Version>" }

$outDir = Join-Path $root $Output
$folder = Join-Path $outDir 'LogTrace'
$zip = Join-Path $outDir "LogTrace-$version-win-x64.zip"
if (Test-Path $folder) { Remove-Item -Recurse -Force $folder }
if (Test-Path $zip) { Remove-Item -Force $zip }

dotnet publish $project -c Release -r win-x64 --self-contained -o $folder
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失敗（$LASTEXITCODE）" }

# 發佈資料夾必須有的檔案：主程式、7-Zip、使用說明、授權清單與全文
foreach ($f in 'LogTrace.exe', '7z/7za.dll', '7z/License.txt', '使用說明.md', 'THIRD-PARTY-NOTICES.md', 'licenses/Apache-2.0.txt', 'licenses/AvalonDock-LICENSE.txt') {
    if (-not (Test-Path (Join-Path $folder $f))) { throw "發佈資料夾缺少 $f" }
}
# 不得包含有 unRAR 授權限制的 7z.dll
if (Get-ChildItem $folder -Recurse -Filter '7z.dll') { throw "發佈資料夾不應包含 7z.dll" }

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($folder, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $true)

$size = (Get-Item $zip).Length / 1MB
Write-Host ("已打包：{0}（{1:N1} MB）" -f $zip, $size)
