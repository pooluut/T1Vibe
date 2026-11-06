param([string]$Zig = '', [switch]$Test)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Zig)) { $Zig = Join-Path $root 'tools\ziglang\ziglang\zig.exe' }
$build = Join-Path $root 'build'
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force -Path $build,$dist | Out-Null
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) { throw '.NET Framework 4.x compiler is required.' }
if (-not (Get-Command $Zig -ErrorAction SilentlyContinue)) { throw 'Zig not found. Place Zig 0.13.0 at tools\ziglang\ziglang\zig.exe, or pass -Zig with its executable path.' }
$wpf = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\WPF'
$refs = @('/reference:System.Windows.Forms.dll','/reference:System.Drawing.dll',"/reference:$wpf\UIAutomationClient.dll","/reference:$wpf\UIAutomationTypes.dll","/reference:$wpf\WindowsBase.dll")
$source = Join-Path $root 'src\RemoteBridge.cs'
$hook = Join-Path $build 'HomeHook.dll'
& $Zig cc -target x86_64-windows-gnu -shared -O2 (Join-Path $root 'src\HomeHook.c') -o $hook -luser32
if ($LASTEXITCODE -ne 0) { throw 'Native hook build failed.' }
$exe = Join-Path $dist 'T1Vibe.exe'
& $csc /nologo /platform:x64 /optimize+ /target:winexe "/out:$exe" "/resource:$hook,T1Vibe.HomeHook" $refs $source
if ($LASTEXITCODE -ne 0) { throw 'Application build failed. Exit T1Vibe before rebuilding.' }
if ($Test) {
    foreach ($name in @('HookTests','ProjectLabelTests')) {
        $testExe = Join-Path $build "$name.exe"
        & $csc /nologo /platform:x64 /target:exe "/main:$name" "/out:$testExe" $refs $source (Join-Path $root "tests\$name.cs")
        if ($LASTEXITCODE -ne 0) { throw "$name build failed." }
        & $testExe
        if ($LASTEXITCODE -ne 0) { throw "$name failed." }
    }
    $check = Start-Process -FilePath $exe -ArgumentList '--check-package' -WindowStyle Hidden -Wait -PassThru
    if ($check.ExitCode -ne 0) { throw "Embedded hook check failed: $($check.ExitCode)" }
    Write-Output 'PASS: embedded hook loads, registers and unloads.'
}
Write-Output "Built: $exe"

