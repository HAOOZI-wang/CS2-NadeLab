param([string]$CssArchive='', [string]$OutputDirectory='')
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'NadeLab.Common.ps1')
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (-not $CssArchive) { $CssArchive=Join-Path $project '.tools/downloads/css-runtime.zip' }
$locked=Read-NlJson (Join-Path $PSScriptRoot 'dependencies.lock.json')
$css=@($locked.packages | Where-Object name -eq 'css-runtime')[0]
if ((Get-NlHash $CssArchive) -ne $css.sha256.ToLowerInvariant()) { throw 'The test archive does not match the official release lock.' }
$testBase=Get-NlChild $project 'artifacts/dependency-tests'
$fixture=Get-NlChild $testBase ('run-'+[Guid]::NewGuid().ToString('N').Substring(0,8)+' spaces')
if ($fixture.Length -lt 60) { $fixture += 'x' * (60-$fixture.Length) }
$extract=Get-NlChild $fixture 'release-state/dependencies/css-runtime'
$installed=Get-NlChild $fixture 'instance/game/csgo'
try {
    Expand-NlVerifiedZip $CssArchive $extract -InstallDestination $installed
    Copy-NlTree (Join-Path $extract 'addons') (Join-Path $installed 'addons')
    $archive=[IO.Compression.ZipFile]::OpenRead($CssArchive)
    $files=0; $maximum=0; $longest=''
    try {
        foreach ($entry in $archive.Entries) {
            if (-not $entry.Name) { continue }
            $source=Get-NlChild $extract $entry.FullName
            $target=Get-NlChild $installed $entry.FullName
            if (-not (Test-Path -LiteralPath $source -PathType Leaf) -or -not (Test-Path -LiteralPath $target -PathType Leaf)) { throw "Missing extracted/copied file: $($entry.FullName)" }
            if ((Get-Item -LiteralPath $source).Length -ne $entry.Length -or (Get-NlHash $source) -ne (Get-NlHash $target)) { throw "Extraction/copy mismatch: $($entry.FullName)" }
            $files++
            if ($source.Length -gt $maximum) { $maximum=$source.Length; $longest=$entry.FullName }
        }
    } finally { $archive.Dispose() }
    $report=[ordered]@{passed=$true;powerShell=$PSVersionTable.PSVersion.ToString();rootLength=$fixture.Length;extractedFiles=$files;maxExtractedPathLength=$maximum;longestRelativePath=$longest;archiveSha256=(Get-NlHash $CssArchive);nativeGameLaunched=$false;steamFilesModified=$false}
    if ($OutputDirectory) { Write-NlJson (Join-Path $OutputDirectory 'release-dependency-path-test.json') $report }
    Write-Host ("PASS: official CSS runtime extracted and copied under Windows PowerShell {0}; root {1} chars, {2} files, longest extraction path {3} chars." -f $PSVersionTable.PSVersion,$fixture.Length,$files,$maximum)
} finally {
    $expected=[IO.Path]::GetFullPath($testBase).TrimEnd('\','/')+'\'
    if ($fixture.StartsWith($expected,[StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $fixture)) { Remove-Item -LiteralPath $fixture -Recurse -Force }
}
