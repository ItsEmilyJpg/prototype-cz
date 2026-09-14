# Builds Cestina-do-Prototype.exe from instalator\*.cs and the translations in preklad\*.tsv.
# Just needs Windows with .NET Framework 4 (included in Windows 10 and 11), nothing to install.
#
#   powershell -ExecutionPolicy Bypass -File sestavit.ps1
#   powershell -ExecutionPolicy Bypass -File sestavit.ps1 -Version 1.1 -Output build\Cestina-do-Prototype.exe
param(
    [string]$Version = 'dev',
    [string]$Output = 'Cestina-do-Prototype.exe'
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw 'Could not find csc.exe from .NET Framework 4.' }

if (-not [IO.Path]::IsPathRooted($Output)) { $Output = Join-Path (Get-Location) $Output }
New-Item -ItemType Directory -Force (Split-Path $Output) | Out-Null

# version number for the file properties: "1.1" -> 1.1.0.0, "dev" -> 0.0.0.0
$parts = @(($Version -replace '[^0-9.]', '').Split('.') | Where-Object { $_ -ne '' }) + @('0', '0', '0', '0')
$asm = ($parts | Select-Object -First 4) -join '.'
$versionCs = Join-Path ([IO.Path]::GetTempPath()) ('PrototypeCZ_Version_' + [Guid]::NewGuid().ToString('N') + '.cs')
$content = @(
    'using System.Reflection;'
    '[assembly: AssemblyTitle("Čeština do Prototype")]'
    '[assembly: AssemblyProduct("Čeština do Prototype")]'
    "[assembly: AssemblyVersion(""$asm"")]"
    "[assembly: AssemblyFileVersion(""$asm"")]"
    "static class AppVersion { public const string Text = ""$Version""; }"
) -join "`r`n"
[IO.File]::WriteAllText($versionCs, $content, (New-Object Text.UTF8Encoding $true))

$cscArgs = @('/nologo', '/optimize+', '/warn:4', '/codepage:65001', '/target:winexe',
          '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', "/out:$Output")
# translation data + optional font verification deltas (private repo only)
$data = @(Get-ChildItem (Join-Path $root 'preklad') -Filter *.tsv -File)
$verification = Join-Path $root 'instalator\overeni'
if (Test-Path $verification) { $data += Get-ChildItem $verification -Filter *.delta -File }
$cscArgs += $data | ForEach-Object { "/resource:$($_.FullName),$($_.Name)" }
$cscArgs += 'Installer.cs', 'Formats.cs', 'FontPatcher.cs' | ForEach-Object { Join-Path $root "instalator\$_" }
$cscArgs += $versionCs

try {
    & $csc $cscArgs
    if ($LASTEXITCODE -ne 0) { throw "Compilation failed (code $LASTEXITCODE)." }
}
finally { Remove-Item $versionCs -ErrorAction SilentlyContinue }

$f = Get-Item $Output
Write-Host ("Done: {0} ({1:N0} kB, version {2}, {3} data files)" -f $f.FullName, ($f.Length / 1KB), $Version, $data.Count)
