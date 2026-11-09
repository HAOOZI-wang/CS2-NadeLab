[CmdletBinding()]
param([Parameter(Mandatory)][string]$Root)
$ErrorActionPreference = 'Stop'
$null = [Reflection.Assembly]::Load('System.Reflection.Metadata')
$taskIdentity = [ordered]@{}
foreach ($taskEntry in @(@('CoreBuild','NadeLab.Core.dll'), @('PluginBuild','NadeLab.dll'))) {
    $taskPath = Join-Path $Root "src/NadeLab.Plugin/bin/Release/net10.0/$($taskEntry[1])"
    $taskStream = [IO.File]::OpenRead($taskPath)
    try {
        $taskReader = [System.Reflection.PortableExecutable.PEReader]::new($taskStream)
        try {
            $taskMetadata = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($taskReader)
            $taskIdentity[$taskEntry[0]] = $taskMetadata.GetGuid($taskMetadata.GetModuleDefinition().Mvid).ToString()
        } finally { $taskReader.Dispose() }
    } finally { $taskStream.Dispose() }
}
$taskIdentity | ConvertTo-Json -Compress
