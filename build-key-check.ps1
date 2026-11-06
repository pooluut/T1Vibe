param([switch]$Test)
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework 4.x compiler is required.' }
$outputDir = Join-Path $PSScriptRoot 'dist'
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
$source = Join-Path $PSScriptRoot 'src\KeyCheck.cs'
$outputFile = Join-Path $outputDir 'T1VibeKeyCheck.exe'
$references = @('/reference:System.Windows.Forms.dll','/reference:System.Drawing.dll')
& $compiler /nologo /platform:x64 /optimize+ /target:winexe "/out:$outputFile" $references $source
if ($LASTEXITCODE -ne 0) { throw 'Key check build failed. Close T1VibeKeyCheck before rebuilding.' }
if ($Test) {
    $result = Start-Process -FilePath $outputFile -ArgumentList '--self-test' -WindowStyle Hidden -Wait -PassThru
    if ($result.ExitCode -ne 0) { throw "Key check self-test failed: $($result.ExitCode)" }
    Write-Output 'PASS: remote injected events, unknown voice events and result classification.'
}
Write-Output "Built: $outputFile"
