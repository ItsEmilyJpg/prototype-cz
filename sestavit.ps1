# Sestavi Cestina-do-Prototype.exe z instalator\*.cs a prekladu v preklad\*.tsv.
# Staci Windows s .NET Frameworkem 4 (je soucasti Windows 10 a 11), nic se neinstaluje.
#
#   powershell -ExecutionPolicy Bypass -File sestavit.ps1
#   powershell -ExecutionPolicy Bypass -File sestavit.ps1 -Verze 1.1 -Vystup build\Cestina-do-Prototype.exe
param(
    [string]$Verze = 'dev',
    [string]$Vystup = 'Cestina-do-Prototype.exe'
)
$ErrorActionPreference = 'Stop'
$koren = $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw 'Nenasel jsem csc.exe z .NET Frameworku 4.' }

if (-not [IO.Path]::IsPathRooted($Vystup)) { $Vystup = Join-Path (Get-Location) $Vystup }
New-Item -ItemType Directory -Force (Split-Path $Vystup) | Out-Null

# cislo verze do vlastnosti souboru: "1.1" -> 1.1.0.0, "dev" -> 0.0.0.0
$cisla = @(($Verze -replace '[^0-9.]', '').Split('.') | Where-Object { $_ -ne '' }) + @('0', '0', '0', '0')
$asm = ($cisla | Select-Object -First 4) -join '.'
$verzeCs = Join-Path ([IO.Path]::GetTempPath()) ('PrototypeCZ_Verze_' + [Guid]::NewGuid().ToString('N') + '.cs')
$obsah = @(
    'using System.Reflection;'
    '[assembly: AssemblyTitle("Čeština do Prototype")]'
    '[assembly: AssemblyProduct("Čeština do Prototype")]'
    "[assembly: AssemblyVersion(""$asm"")]"
    "[assembly: AssemblyFileVersion(""$asm"")]"
    "static class Verze { public const string Text = ""$Verze""; }"
) -join "`r`n"
[IO.File]::WriteAllText($verzeCs, $obsah, (New-Object Text.UTF8Encoding $true))

$argy = @('/nologo', '/optimize+', '/warn:4', '/codepage:65001', '/target:winexe',
          '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', "/out:$Vystup")
# preklad + volitelne overovaci rozdily fontu (jen v soukromem repu)
$data = @(Get-ChildItem (Join-Path $koren 'preklad') -Filter *.tsv -File)
$overeni = Join-Path $koren 'instalator\overeni'
if (Test-Path $overeni) { $data += Get-ChildItem $overeni -Filter *.delta -File }
$argy += $data | ForEach-Object { "/resource:$($_.FullName),$($_.Name)" }
$argy += 'Instalator.cs', 'Pomocne.cs', 'Fonty.cs' | ForEach-Object { Join-Path $koren "instalator\$_" }
$argy += $verzeCs

try {
    & $csc $argy
    if ($LASTEXITCODE -ne 0) { throw "Kompilace selhala (kod $LASTEXITCODE)." }
}
finally { Remove-Item $verzeCs -ErrorAction SilentlyContinue }

$f = Get-Item $Vystup
Write-Host ("Hotovo: {0} ({1:N0} kB, verze {2}, dat {3})" -f $f.FullName, ($f.Length / 1KB), $Verze, $data.Count)
