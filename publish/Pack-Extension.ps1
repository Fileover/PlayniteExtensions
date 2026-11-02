<#
.SYNOPSIS
    Builds the extension and packs it into a .pext file that Playnite will accept.

.DESCRIPTION
    Playnite's ExtensionInstaller.VerifyExtensionPackage rejects any package that ships
    Playnite.dll, Playnite.Common.dll or Playnite.SDK.dll (the host already provides them).
    Because the project references PlayniteSDK as a NuGet package, those assemblies end up in
    bin\<Configuration> and must be filtered out while packing.

    Run this instead of zipping the build output by hand.

.PARAMETER Configuration
    Build configuration to pack. Defaults to Release.

.PARAMETER OutputDirectory
    Where the .pext is written. Defaults to the current directory.

.EXAMPLE
    .\publish\Pack-Extension.ps1 -OutputDirectory "$env:USERPROFILE\Desktop"
#>
param(
    [string]$Configuration = 'Release',
    [string]$OutputDirectory = '.'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectDir = Join-Path $repoRoot 'src\MetadataImageOptimizer'
$projectFile = Join-Path $projectDir 'MetadataImageOptimizer.csproj'
$buildDir = Join-Path $projectDir "bin\$Configuration"

# Assemblies that Playnite forbids inside a package
$forbiddenAssemblies = @('Playnite.dll', 'Playnite.Common.dll', 'Playnite.SDK.dll')

Write-Host "Building $projectFile ($Configuration)..." -ForegroundColor Cyan
& dotnet build $projectFile -c $Configuration -v quiet --nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

if (-not (Test-Path $buildDir)) { throw "Build output not found: $buildDir" }

# Read the version from the manifest so the file name matches the packaged version
$manifestPath = Join-Path $buildDir 'extension.yaml'
$version = (Select-String -Path $manifestPath -Pattern '^Version:\s*(.+)$').Matches[0].Groups[1].Value.Trim()
$extensionName = (Select-String -Path $manifestPath -Pattern '^Name:\s*(.+)$').Matches[0].Groups[1].Value.Trim()

if (-not (Test-Path $OutputDirectory)) { New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null }
$outputPath = Join-Path (Resolve-Path $OutputDirectory) "$($extensionName)_$($version -replace '\.', '_').pext"
if (Test-Path $outputPath) { Remove-Item $outputPath -Force }

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$files = Get-ChildItem -Path $buildDir -Recurse -File |
    Where-Object { $forbiddenAssemblies -notcontains $_.Name -and $_.Extension -ne '.pdb' } |
    Sort-Object FullName

$archive = [System.IO.Compression.ZipFile]::Open($outputPath, 'Create')
try {
    # extension.yaml must be at the archive root; Playnite reads it with zip.GetEntry("extension.yaml")
    foreach ($file in $files) {
        $entryName = $file.FullName.Substring($buildDir.Length + 1).Replace('\', '/')
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $archive, $file.FullName, $entryName, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        Write-Host "  + $entryName"
    }
}
finally {
    $archive.Dispose()
}

# Sanity check: a package that ships a forbidden assembly is rejected by Playnite
$verify = [System.IO.Compression.ZipFile]::OpenRead($outputPath)
try {
    $names = $verify.Entries | ForEach-Object { $_.Name }
    if ($names -notcontains 'extension.yaml') { throw "Packed file is missing extension.yaml." }
    foreach ($forbidden in $forbiddenAssemblies) {
        if ($names -contains $forbidden) { throw "Packed file contains $forbidden, which Playnite rejects." }
    }
}
finally {
    $verify.Dispose()
}

$size = [math]::Round((Get-Item $outputPath).Length / 1KB, 1)
Write-Host "`nPacked: $outputPath ($size KB)" -ForegroundColor Green
