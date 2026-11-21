<#
  配布用の zip を release フォルダに作る。バージョンは MyTube.csproj の <Version> を使う。

    MyTube-<版>-win-x64.zip                  .NET 同梱（.NET が入っていない PC でも動く。約 60MB）
    MyTube-<版>-win-x64-requires-dotnet.zip  .NET 10 デスクトップ ランタイムが必要（約 1.5MB）

  どちらも中身は MyTube.exe と「お読みください.txt」。広告ブロック（uBlock Origin Lite）は同梱しない。

  使い方:
    pwsh tools/release.ps1
#>

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$project = Join-Path $root 'MyTube\MyTube.csproj'
$releaseDir = Join-Path $root 'release'
$workDir = Join-Path $releaseDir 'work'

$version = ([xml](Get-Content $project -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'MyTube.csproj に <Version> がありません' }

# コミットしていない変更があると、zip の中身とリポジトリの版がずれる
$dirty = git -C $root status --porcelain
if ($dirty) {
    Write-Host 'コミットしていない変更があります（このまま作ります）:' -ForegroundColor Yellow
    $dirty | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
}

$readme = @"
MyTube $version
================

YouTube を中心に、ニコニコ動画・TVer・Netflix・Disney+・Prime Video・Hulu・Twitch を
1 つのウィンドウで切り替えたり、画面分割で並べたりして見られる Windows アプリです。
各サイトをそのまま表示するので、ログインすればそれぞれのアカウントと同期されます。
詳しい機能: https://github.com/humix2/MyTube

■ 動かすのに必要なもの
- Windows 10 / 11（64 ビット）
- Microsoft Edge WebView2 ランタイム（Windows 11 には最初から入っています）
- requires-dotnet の zip を選んだ場合のみ: .NET 10 デスクトップ ランタイム
  https://dotnet.microsoft.com/download/dotnet/10.0

■ 使い方
zip を好きな場所に展開して MyTube.exe を起動します。インストールは不要です。
スタートメニューに出したいときは、MyTube.exe のショートカットを
  %APPDATA%\Microsoft\Windows\Start Menu\Programs
に置いてください。

■ 広告ブロック（任意）
uBlock Origin Lite などの拡張機能を入れられます（同梱はしていません）。
1. https://github.com/uBlockOrigin/uBOL-home/releases から
   uBOLite_<版>.chromium.zip をダウンロードする
2. 一度 MyTube を起動して閉じる（データフォルダが作られる）
3. %LOCALAPPDATA%\MyTube\Extensions\uBOLite に展開する
   （そのフォルダの直下に manifest.json がある配置）
4. MyTube を起動する。左メニューの「拡張機能」から設定を開ける

■ データの保存場所
%LOCALAPPDATA%\MyTube（ログイン情報・設定・視聴済みの記録・拡張機能）
左メニューの「データフォルダ」から開けます。

■ アンインストール
MyTube.exe を削除し、必要なら %LOCALAPPDATA%\MyTube も削除してください。
"@

function New-Package([string]$name, [string[]]$publishArgs) {
    $out = Join-Path $workDir $name
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    Write-Host "ビルド中: $name"
    dotnet publish $project -c Release -o $out --nologo -v q @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "ビルドに失敗しました: $name" }

    # メモ帳などで文字化けしないよう BOM 付き UTF-8・CRLF で書く
    Set-Content (Join-Path $out 'お読みください.txt') ($readme -replace "`r?`n", "`r`n") -Encoding utf8BOM -NoNewline

    $zip = Join-Path $releaseDir "$name.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive (Join-Path $out '*') $zip
    $mb = '{0:N1}' -f ((Get-Item $zip).Length / 1MB)
    Write-Host "  → $zip ($mb MB)" -ForegroundColor Green
}

New-Item -ItemType Directory -Force $releaseDir | Out-Null
New-Package "MyTube-$version-win-x64" @('-p:SelfContained=true', '-p:EnableCompressionInSingleFile=true')
New-Package "MyTube-$version-win-x64-requires-dotnet" @('-p:SelfContained=false')
Remove-Item $workDir -Recurse -Force

Write-Host ''
Write-Host "できました: $releaseDir" -ForegroundColor Green
Write-Host "GitHub に出すときは、タグ v$version を付けて push し、Releases の画面で zip をアップロードしてください。"
