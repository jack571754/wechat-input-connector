# 安装已经发布的独立版本，不删除旧程序和旧数据。
param([string]$PublishedDirectory = '', [string]$MigrateFrom = '')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $PublishedDirectory) { $PublishedDirectory = Join-Path $projectRoot 'artifacts\desktop-hardened' }
$PublishedDirectory = [IO.Path]::GetFullPath($PublishedDirectory)
if (-not (Test-Path -LiteralPath (Join-Path $PublishedDirectory 'Connector.Probe.exe'))) { throw '请先发布自包含版本。' }
$profileRoot = Join-Path $env:LOCALAPPDATA 'WeChatInputConnector'
New-Item -ItemType Directory -Path $profileRoot -Force | Out-Null
if ((Get-Item -LiteralPath $profileRoot).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw '安装目录不允许使用链接。' }
$identitySid = [Security.Principal.WindowsIdentity]::GetCurrent().User
$item = Get-Item -LiteralPath $profileRoot
$privateAcl = $item.GetAccessControl([System.Security.AccessControl.AccessControlSections]::Access)
$privateAcl.SetAccessRuleProtection($true, $false)
foreach ($sid in @($identitySid, [Security.Principal.SecurityIdentifier]::new('S-1-5-18'), [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))) {
    $rule = [Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
    $privateAcl.AddAccessRule($rule)
}
$item.SetAccessControl($privateAcl)
$versionName = '0.2.0-' + (Get-Date -Format 'yyyyMMddHHmmss')
$destination = Join-Path $profileRoot "bin\$versionName"
New-Item -ItemType Directory -Path $destination | Out-Null
Get-ChildItem -LiteralPath $PublishedDirectory | Copy-Item -Destination $destination -Recurse
$hashes = Get-ChildItem -LiteralPath $destination -File | Get-FileHash -Algorithm SHA256
$hashes | Select-Object @{Name='File';Expression={Split-Path $_.Path -Leaf}}, Hash | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $destination 'sha256.json') -Encoding utf8
$arguments = '--show-history'
if ($MigrateFrom) {
    $MigrateFrom = [IO.Path]::GetFullPath($MigrateFrom)
    if ($MigrateFrom.Contains('"')) { throw '迁移路径无效。' }
    $arguments += ' --migrate-from "' + $MigrateFrom + '"'
}
Set-Content -LiteralPath (Join-Path $profileRoot 'current-version.txt.tmp') -Value $versionName -Encoding utf8
Move-Item -LiteralPath (Join-Path $profileRoot 'current-version.txt.tmp') -Destination (Join-Path $profileRoot 'current-version.txt') -Force
Start-Process -FilePath (Join-Path $destination 'Connector.Probe.exe') -ArgumentList $arguments -WindowStyle Normal
Write-Output "已安装本地版本 $versionName"
