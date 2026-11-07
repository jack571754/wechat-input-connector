# 仅设置当前开发子进程环境，不更改系统配置。
$ErrorActionPreference = 'Stop'
$projectPath = Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $projectPath '.tools\home'
$env:NUGET_PACKAGES = Join-Path $projectPath '.tools\nuget'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_NOLOGO = 'true'
Push-Location $projectPath
try {
    & (Join-Path $projectPath '.tools\dotnet\dotnet.exe') @args
    exit $LASTEXITCODE
}
finally { Pop-Location }
