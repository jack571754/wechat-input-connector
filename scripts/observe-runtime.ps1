# 有界的发布后只读观察，不重启程序、不执行输入、不记录消息正文。
param([int]$DurationSeconds = 28800, [int]$IntervalSeconds = 60, [string]$OutputPath = '')
$ErrorActionPreference = 'Stop'
if ($DurationSeconds -le 0 -or $IntervalSeconds -le 0) { throw '观察时长和间隔必须为正数。' }
$profileRoot = Join-Path $env:LOCALAPPDATA 'WeChatInputConnector'
if (-not $OutputPath) {
    $healthDirectory = Join-Path $profileRoot 'health'
    New-Item -ItemType Directory -Path $healthDirectory -Force | Out-Null
    $OutputPath = Join-Path $healthDirectory ('observe-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.csv')
}
$deadline = [DateTimeOffset]::UtcNow.AddSeconds($DurationSeconds)
$count = 0
while ([DateTimeOffset]::UtcNow -lt $deadline) {
    $status = $null
    try { $status = Get-Content -LiteralPath (Join-Path $profileRoot 'probe-status.json') -Raw | ConvertFrom-Json }
    catch { }
    $processes = @(Get-Process -Name Connector.Probe -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith(($profileRoot + '\bin\'), [StringComparison]::OrdinalIgnoreCase) })
    $running = if ($processes.Count -eq 1) { $processes[0] } else { $null }
    [pscustomobject]@{
        TimeUtc = [DateTimeOffset]::UtcNow.ToString('O')
        ProcessCount = $processes.Count
        ProcessId = $running.Id
        Responding = $running.Responding
        WorkingSetMiB = if ($running) { [math]::Round($running.WorkingSet64 / 1MB, 2) } else { $null }
        PrivateMiB = if ($running) { [math]::Round($running.PrivateMemorySize64 / 1MB, 2) } else { $null }
        Handles = $running.HandleCount
        CpuSeconds = $running.CPU
        UpdatedAt = $status.UpdatedAt
        Phase = $status.Phase
        FailureType = $status.FailureType
        Messages = $status.UniqueMessages
        Queued = $status.Queued
        PendingReceipts = $status.PendingReceipts
    } | Export-Csv -LiteralPath $OutputPath -NoTypeInformation -Encoding utf8 -Append
    $count++
    $remaining = ($deadline - [DateTimeOffset]::UtcNow).TotalSeconds
    if ($remaining -gt 0) { Start-Sleep -Milliseconds ([int]([math]::Min($remaining, $IntervalSeconds) * 1000)) }
}
[pscustomobject]@{ Completed = $true; Samples = $count; Output = $OutputPath } | ConvertTo-Json -Compress
