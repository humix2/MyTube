<#
  MyTube を Release ビルドして exe にまとめ、%LOCALAPPDATA%\Programs\MyTube に配置する。
  スタートメニューにショートカットを作る（-Desktop でデスクトップにも作る）。

  使い方:
    pwsh tools/install.ps1
    pwsh tools/install.ps1 -Desktop
#>
param(
    [switch]$Desktop
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$project = Join-Path $root 'MyTube\MyTube.csproj'
$publishDir = Join-Path $root 'publish'
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\MyTube'
$exe = Join-Path $installDir 'MyTube.exe'

# 配置先の MyTube が起動中だと上書きできない
$running = Get-Process MyTube -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }
if ($running) {
    Write-Host 'インストール先の MyTube が起動中です。閉じてからもう一度実行してください。' -ForegroundColor Yellow
    exit 1
}

Write-Host 'ビルド中...'
dotnet publish $project -c Release -o $publishDir --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'ビルドに失敗しました' }

New-Item -ItemType Directory -Force $installDir | Out-Null
Copy-Item (Join-Path $publishDir '*') $installDir -Recurse -Force

function New-Shortcut([string]$path) {
    $shell = New-Object -ComObject WScript.Shell
    $lnk = $shell.CreateShortcut($path)
    $lnk.TargetPath = $exe
    $lnk.WorkingDirectory = $installDir
    $lnk.IconLocation = "$exe,0"
    $lnk.Description = '自分専用の YouTube 閲覧アプリ'
    $lnk.Save()
    Write-Host "ショートカット: $path"
}

New-Shortcut (Join-Path ([Environment]::GetFolderPath('Programs')) 'MyTube.lnk')
if ($Desktop) { New-Shortcut (Join-Path ([Environment]::GetFolderPath('Desktop')) 'MyTube.lnk') }

Write-Host "インストールしました: $exe" -ForegroundColor Green
