# 从受保护的用户目录启动，版本目录由安装脚本生成。
$ErrorActionPreference = 'Stop'
$profileRoot = Join-Path $env:LOCALAPPDATA 'WeChatInputConnector'
$versionName = (Get-Content -LiteralPath (Join-Path $profileRoot 'current-version.txt') -Raw).Trim()
if ($versionName -notmatch '^[A-Za-z0-9.-]+$') { throw '本地版本标记无效。' }
$executable = Join-Path $profileRoot "bin\$versionName\Connector.Probe.exe"
if (-not (Test-Path -LiteralPath $executable)) { throw '请先执行 scripts/install-local.ps1 安装本地版本。' }
Start-Process -FilePath $executable -ArgumentList '--show-history' -WindowStyle Normal
