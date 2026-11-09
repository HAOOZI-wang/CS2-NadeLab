[CmdletBinding()]
param([string]$DataDirectory)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/env.ps1"
Write-Host 'NadeLab: http://127.0.0.1:5178 (Ctrl+C to stop)'
$taskToolArgs = @('run','--project',"$taskRoot/src/NadeLab.Tool",'-c','Release')
if ($DataDirectory) {
    $taskResolvedData = (Resolve-Path -LiteralPath $DataDirectory -ErrorAction Stop).Path
    $taskToolArgs += @('--',"--data=$taskResolvedData")
}
& $taskDotnet @taskToolArgs
