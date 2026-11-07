param([string]$Zig = '', [switch]$Test, [string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Zig)) {
    $Zig = Join-Path $root 'tools\ziglang\zig.exe'
    if (-not (Test-Path -LiteralPath $Zig)) {
        $version = '0.13.0'
        $archiveUrl = "https://ziglang.org/download/$version/zig-windows-x86_64-$version.zip"
        $expectedHash = 'd859994725ef9402381e557c60bb57497215682e355204d754ee3df75ee3c158'
        $tools = Join-Path $root 'tools'
        $destination = Join-Path $tools 'ziglang'
        if (Test-Path -LiteralPath $destination) {
            throw "Incomplete Zig installation at $destination. Remove that directory and retry."
        }
        New-Item -ItemType Directory -Force -Path $tools | Out-Null
        $temporary = Join-Path $tools ('.install-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $temporary | Out-Null
        try {
            $archive = Join-Path $temporary 'zig.zip'
            $expanded = Join-Path $temporary 'expanded'
            [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
            Write-Output "Downloading Zig $version from $archiveUrl"
            (New-Object Net.WebClient).DownloadFile($archiveUrl, $archive)
            if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ine $expectedHash) {
                throw 'Zig archive checksum mismatch.'
            }
            Expand-Archive -LiteralPath $archive -DestinationPath $expanded
            $extracted = Join-Path $expanded "zig-windows-x86_64-$version"
            if (-not (Test-Path -LiteralPath (Join-Path $extracted 'zig.exe'))) {
                throw 'Zig archive is missing zig.exe.'
            }
            Move-Item -LiteralPath $extracted -Destination $destination
            Write-Output "Installed Zig $version at $Zig"
        } finally {
            $resolvedTemporary = [IO.Path]::GetFullPath($temporary)
            $resolvedTools = [IO.Path]::GetFullPath($tools).TrimEnd('\') + '\'
            if (-not $resolvedTemporary.StartsWith($resolvedTools, [StringComparison]::OrdinalIgnoreCase)) { throw 'Temporary cleanup target is outside the tools directory.' }
            Remove-Item -LiteralPath $temporary -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $dist = Join-Path $root 'dist'
    $build = Join-Path $root 'build'
} else {
    $dist = [IO.Path]::GetFullPath($OutputDirectory)
    $build = Join-Path $dist 'build'
}
New-Item -ItemType Directory -Force -Path $build,$dist | Out-Null
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) { throw '.NET Framework 4.x compiler is required.' }
if (-not (Get-Command $Zig -ErrorAction SilentlyContinue)) { throw 'Zig not found. Place Zig 0.13.0 at tools\ziglang\zig.exe, or pass -Zig with its executable path.' }
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
    foreach ($name in @('HookTests','ProjectLabelTests','VoiceTests')) {
        $testExe = Join-Path $build "$name.exe"
        & $csc /nologo /platform:x64 /target:exe "/main:$name" "/out:$testExe" $refs $source (Join-Path $root "tests\$name.cs")
        if ($LASTEXITCODE -ne 0) { throw "$name build failed." }
        & $testExe
        if ($LASTEXITCODE -ne 0) { throw "$name failed." }
    }
    foreach ($package in @($exe)) {
        $check = Start-Process -FilePath $package -ArgumentList '--check-package' -WindowStyle Hidden -Wait -PassThru
        if ($check.ExitCode -ne 0) { throw "Embedded hook check failed for $package`: $($check.ExitCode)" }
    }
    Write-Output 'PASS: embedded hook loads, registers and unloads.'
}
Write-Output "Built: $exe"

