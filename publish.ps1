<#
.SYNOPSIS
    Publish ParPar as a zero-install Windows app.

.DESCRIPTION
    Default: self-contained, single-file, compressed. The .NET runtime is bundled
    inside the exe, so the target machine needs NOTHING installed - no .NET, no admin
    prompt. Larger file (~65 MB), but truly portable.

    -FrameworkDependent: a tiny exe (~0.3 MB) that instead REQUIRES the
    .NET 8 Desktop Runtime to be installed on the target machine.

    (Trimming is intentionally not used: the .NET SDK does not support trimming
    WPF apps - NETSDK1168.)

.EXAMPLE
    pwsh ./publish.ps1                      # zero-install exe -> dist\parpar.exe
    pwsh ./publish.ps1 -FrameworkDependent  # tiny exe, needs .NET 8 Desktop Runtime
#>
param(
    [string]$Runtime = "win-x64",
    [string]$OutDir = "dist",
    [switch]$FrameworkDependent
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$out = Join-Path $root $OutDir
if (Test-Path $out) { Remove-Item -Recurse -Force $out }

$args = @(
    "$root\Par2.App\Par2.App.csproj", "-c", "Release", "-r", $Runtime,
    "-p:DebugType=none", "-p:PublishSingleFile=true", "-o", $out
)
if ($FrameworkDependent) {
    $args += "--self-contained"; $args += "false"
} else {
    $args += "--self-contained"; $args += "true"
    $args += "-p:EnableCompressionInSingleFile=true"
    $args += "-p:IncludeNativeLibrariesForSelfExtract=true"
}

& dotnet publish @args
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

$exe = Join-Path $out "parpar.exe"
$mb = "{0:N1} MB" -f ((Get-Item $exe).Length / 1MB)
Write-Host ""
Write-Host "Published: $exe  ($mb)"
if ($FrameworkDependent) {
    Write-Host "NOTE: target needs the .NET 8 Desktop Runtime installed." -ForegroundColor Yellow
} else {
    Write-Host "Zero-install: nothing required on the target machine."
}
