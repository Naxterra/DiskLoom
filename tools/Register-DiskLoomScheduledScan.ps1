#requires -Version 7.4
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [string]$ScanPath,
    [Parameter(Mandatory)]
    [string]$ExportPath,
    [ValidateSet('Daily', 'Weekly')]
    [string]$Schedule = 'Weekly',
    [ValidatePattern('^([01]\d|2[0-3]):[0-5]\d$')]
    [string]$At = '03:00',
    [string]$TaskName = 'DiskLoom Storage Report',
    [string]$ExecutablePath = "$env:ProgramFiles\DiskLoom\DiskLoom.Cli.exe"
)

$ErrorActionPreference = 'Stop'
$resolvedExecutable = [IO.Path]::GetFullPath($ExecutablePath)
if (-not (Test-Path -LiteralPath $resolvedExecutable -PathType Leaf)) {
    throw "DiskLoom CLI not found: $resolvedExecutable"
}

# Windows argument parsing treats \" as a literal quote, so "D:\" would swallow the following
# arguments; doubling trailing backslashes keeps drive roots like D:\ intact.
function ConvertTo-QuotedArgument([string]$Value) {
    '"' + ($Value -replace '(\\+)$', '$1$1') + '"'
}
$arguments = "--scan $(ConvertTo-QuotedArgument $ScanPath) --export $(ConvertTo-QuotedArgument $ExportPath)"
$action = New-ScheduledTaskAction -Execute $resolvedExecutable -Argument $arguments
$triggerTime = [DateTime]::Today.Add([TimeSpan]::ParseExact($At, 'hh\:mm', [Globalization.CultureInfo]::InvariantCulture))
$trigger = if ($Schedule -eq 'Daily') {
    New-ScheduledTaskTrigger -Daily -At $triggerTime
} else {
    New-ScheduledTaskTrigger -Weekly -DaysOfWeek Sunday -At $triggerTime
}
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Hours 12)

if ($PSCmdlet.ShouldProcess($TaskName, "Register $Schedule DiskLoom scan")) {
    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Description "DiskLoom storage report for $ScanPath" -Force | Out-Null
    Write-Host "Registered scheduled task '$TaskName'."
}
