param(
    [Parameter(Mandatory=$true)][ValidateSet('x64','x86')][string]$Platform,
    [Parameter(Mandatory=$true)][string]$RefName,
    [string]$SourceRoot='.'
)
$ErrorActionPreference='Stop'
$root=(Resolve-Path -LiteralPath $SourceRoot).Path
[xml]$project=Get-Content -LiteralPath (Join-Path $root 'EpgOverlay/EpgOverlay.csproj')
$version=[string]$project.Project.PropertyGroup[0].Version
if (!$version -or $RefName -cne "v$version") { throw 'Published tag and product version must match' }
$dll=Join-Path $root "EpgOverlay/bin/$Platform/Release/EpgOverlay.dll"
if (!(Test-Path -LiteralPath $dll)) { throw 'Plugin DLL missing' }
if ([Diagnostics.FileVersionInfo]::GetVersionInfo($dll).FileVersion -ne "$version.0") { throw 'DLL version differs' }
if ((Get-FileHash (Join-Path $root 'README.md')).Hash -ne (Get-FileHash (Join-Path $root 'README.txt')).Hash) { throw 'README files differ' }
$out="package/EpgOverlay_v${version}_${Platform}"
if (Test-Path -LiteralPath $out) { throw 'Package staging already exists. Use a clean checkout.' }
New-Item -ItemType Directory -Path $out -Force | Out-Null
Copy-Item -LiteralPath $dll -Destination $out
foreach ($doc in @('README.md','README.txt','LICENSE','NOTICE.md')) {
    Copy-Item -LiteralPath (Join-Path $root $doc) -Destination $out
}
Copy-Item -LiteralPath (Join-Path $root 'TvAIrPlugin/LICENSE') -Destination (Join-Path $out 'SDK_LICENSE.txt')
@"
EpgOverlay v$version ($Platform) - ビルド済み配布

このZIPはビルド済みです。Visual Studioによるビルドは不要です。
TvAIr 1.2.2以上と、TvAIrと同じアーキテクチャの配布物を使用してください。
TvAIrを終了してから、EpgOverlay.dllだけをTvAIr.exeと同じフォルダーのPluginsへ配置してください。
TvAIrを起動し、プラグインメニューにEpgOverlayが表示されることを確認してください。
設定画面でTVTestのEPGデータを指定してください。設定の詳細はREADMEを参照してください。
更新時はTvAIrを終了してDLLを置き換えます。
SDK_LICENSE.txtは同梱SDKの利用条件です。NOTICE.mdのソース内TvAIrPlugin/LICENSEと同じ正本文書です。
"@ | Set-Content -LiteralPath (Join-Path $out 'INSTALL.txt') -Encoding UTF8
Compress-Archive -Path "$out/*" -DestinationPath "package/EpgOverlay_v${version}_${Platform}.zip"
