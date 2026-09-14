# Builds Cestina-do-Prototype.exe from instalator\*.cs and puts the translations (preklad\*.tsv)
# into a data\ folder next to it - the installer reads them from there.
# Just needs Windows with .NET Framework 4 (included in Windows 10 and 11), nothing to install.
#
#   powershell -ExecutionPolicy Bypass -File sestavit.ps1
#   powershell -ExecutionPolicy Bypass -File sestavit.ps1 -Version 1.1 -Output build\Cestina-do-Prototype.exe
param(
    [string]$Version = 'dev',
    [string]$Output = 'build\Cestina-do-Prototype.exe'
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw 'Could not find csc.exe from .NET Framework 4.' }

if (-not [IO.Path]::IsPathRooted($Output)) { $Output = Join-Path (Get-Location) $Output }
$outDir = Split-Path $Output
New-Item -ItemType Directory -Force $outDir | Out-Null

# version number for the file properties: "1.1" -> 1.1.0.0, "dev" -> 0.0.0.0
$parts = @(($Version -replace '[^0-9.]', '').Split('.') | Where-Object { $_ -ne '' }) + @('0', '0', '0', '0')
$asm = ($parts | Select-Object -First 4) -join '.'
$versionCs = Join-Path ([IO.Path]::GetTempPath()) ('PrototypeCZ_Version_' + [Guid]::NewGuid().ToString('N') + '.cs')
# file properties shown in Explorer; a complete version resource also helps antivirus reputation
$content = @(
    'using System.Reflection;'
    'using System.Resources;'
    '[assembly: AssemblyTitle("Čeština do Prototype")]'
    '[assembly: AssemblyDescription("Czech fan translation installer for Prototype (2009)")]'
    '[assembly: AssemblyProduct("Čeština do Prototype")]'
    '[assembly: AssemblyCompany("ItsEmilyJpg")]'
    '[assembly: AssemblyCopyright("Copyright (c) 2026 ItsEmilyJpg. MIT License.")]'
    '[assembly: NeutralResourcesLanguage("cs-CZ")]'
    "[assembly: AssemblyVersion(""$asm"")]"
    "[assembly: AssemblyFileVersion(""$asm"")]"
    "[assembly: AssemblyInformationalVersion(""$Version"")]"
    "static class AppVersion { public const string Text = ""$Version""; }"
) -join "`r`n"
[IO.File]::WriteAllText($versionCs, $content, (New-Object Text.UTF8Encoding $true))

# The translation is NOT embedded in the exe: a small exe with plain data files next to it
# gets far fewer antivirus false positives than a large exe carrying its own payload.
$cscArgs = @('/nologo', '/optimize+', '/warn:4', '/codepage:65001', '/target:winexe',
          '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', "/out:$Output",
          "/win32icon:$(Join-Path $root 'instalator\ikona.ico')",
          "/win32manifest:$(Join-Path $root 'instalator\app.manifest')")
$cscArgs += 'Installer.cs', 'Formats.cs', 'FontPatcher.cs' | ForEach-Object { Join-Path $root "instalator\$_" }
$cscArgs += $versionCs

try {
    & $csc $cscArgs
    if ($LASTEXITCODE -ne 0) { throw "Compilation failed (code $LASTEXITCODE)." }
}
finally { [IO.File]::Delete($versionCs) }

# translation data + optional font verification deltas (private repo only) go to data\ next to the exe
$dataDir = Join-Path $outDir 'data'
New-Item -ItemType Directory -Force $dataDir | Out-Null
Get-ChildItem $dataDir -File | ForEach-Object { [IO.File]::Delete($_.FullName) }
$data = @(Get-ChildItem (Join-Path $root 'preklad') -Filter *.tsv -File)
$verification = Join-Path $root 'instalator\overeni'
if (Test-Path $verification) { $data += Get-ChildItem $verification -Filter *.delta -File }
$data | ForEach-Object { Copy-Item $_.FullName $dataDir }

$f = Get-Item $Output
Write-Host ("Done: {0} ({1:N0} kB, version {2}) + {3} data files in {4}" -f $f.FullName, ($f.Length / 1KB), $Version, $data.Count, $dataDir)
