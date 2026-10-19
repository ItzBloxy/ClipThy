<#
.SYNOPSIS
Builds the release zips into dist\.

.DESCRIPTION
Produces two builds of ClipThy for 64-bit Windows, each zipped with the licence files and a short README.txt:
  ClipThy-<version>-win-x64.zip                       self-contained single file, no .NET install needed
  ClipThy-<version>-win-x64-framework-dependent.zip   small single file, needs the .NET 10 Desktop Runtime
plus SHA256SUMS.txt. The release workflow runs this script; it also runs on a developer PC.

.PARAMETER Version
The version to stamp into the build. Defaults to <Version> in ClipThy.csproj. When given (the release workflow
passes the tag), it must match the csproj, so a tag can't ship a build that reports a different version.

.EXAMPLE
powershell -ExecutionPolicy Bypass -File tools\package.ps1
#>
param(
    [string]$Version,
    [string]$Output = "dist"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\ClipThy\ClipThy.csproj"

$projectVersion = ([xml](Get-Content $project -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $projectVersion) { throw "No <Version> in $project" }
if (-not $Version) { $Version = $projectVersion }
if ($Version -ne $projectVersion) { throw "Version $Version does not match <Version>$projectVersion</Version> in ClipThy.csproj" }

$out = Join-Path $root $Output
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
$staging = Join-Path $out "staging"
Add-Type -AssemblyName System.IO.Compression.FileSystem

function New-Package([string]$Name, [bool]$SelfContained) {
    $dir = Join-Path $staging $Name
    $publishArgs = @("publish", $project, "-c", "Release", "-r", "win-x64", "-o", $dir,
        "-p:PublishSingleFile=true", "-p:DebugType=none", "-p:Version=$Version")
    # Use the bare flags: the .NET 10 CLI reads "--self-contained false" as "--self-contained".
    if ($SelfContained) {
        $publishArgs += @("--self-contained", "-p:IncludeNativeLibrariesForSelfExtract=true", "-p:EnableCompressionInSingleFile=true")
    }
    else {
        $publishArgs += @("--no-self-contained")
    }
    # Out-Host keeps dotnet's output out of this function's return value.
    & dotnet @publishArgs | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $Name" }

    Copy-Item (Join-Path $root "LICENSE") (Join-Path $dir "LICENSE.txt")
    Copy-Item (Join-Path $root "THIRD-PARTY-NOTICES.md") (Join-Path $dir "THIRD-PARTY-NOTICES.md")
    Copy-Item (Join-Path $PSScriptRoot "package-readme.txt") (Join-Path $dir "README.txt")

    $zip = Join-Path $out "$Name.zip"
    [IO.Compression.ZipFile]::CreateFromDirectory($dir, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
    return $zip
}

$zips = @(
    (New-Package "ClipThy-$Version-win-x64" $true),
    (New-Package "ClipThy-$Version-win-x64-framework-dependent" $false)
)
Remove-Item -Recurse -Force $staging

$sums = foreach ($zip in $zips) {
    "{0}  {1}" -f (Get-FileHash -Algorithm SHA256 $zip).Hash.ToLowerInvariant(), (Split-Path -Leaf $zip)
}
[IO.File]::WriteAllLines((Join-Path $out "SHA256SUMS.txt"), [string[]]$sums)

Get-ChildItem $out | ForEach-Object { "{0,-55} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB) }
