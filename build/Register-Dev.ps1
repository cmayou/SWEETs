<#
.SYNOPSIS
    Registers (or unregisters) a locally built SWEETs with SOLIDWORKS Electrical for development.
    Run from an elevated PowerShell. Close SOLIDWORKS Electrical first.

.DESCRIPTION
    Writes the same keys as the installer: COM class registration (RegAsm /codebase equivalent, written directly
    because RegAsm cannot load the add-in without SWE's Interop.EwAPI next to it) and the SOLIDWORKS Electrical
    add-in keys whose Path points at the built SWEETs.AddIn.dll. Also removes the add-in keys of the four
    stand-alone tools, so their old ribbon tabs do not appear next to SWEETs.

.EXAMPLE
    .\build\Register-Dev.ps1
.EXAMPLE
    .\build\Register-Dev.ps1 -Configuration Debug
.EXAMPLE
    .\build\Register-Dev.ps1 -Unregister
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')] [string] $Configuration,
    [string] $AssemblyPath,
    [switch] $Unregister
)
$ErrorActionPreference = 'Stop'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this script from an elevated PowerShell.' }

$AddInGuid = '{3ed791fd-feac-4081-99b5-ef363b2fa108}'
$ProgId    = 'SWEETs.AddIn'
$Title     = 'SWEETs'
$Desc      = 'SOLIDWORKS Electrical Efficiency Tools: drawing parameters, line styles, PDF to schematic and datasheet import.'
$StaleKeys = 'SOFTWARE\Trace Software\SOLIDWORKS Electrical\Addins'
$SweKeys   = 'SOFTWARE\Trace Software\Electrical\Addins',
             'SOFTWARE\SolidWorks\SOLIDWORKS Electrical\AddIns'
$UserKey   = "Software\SolidWorks\SOLIDWORKS Electrical\AddIns\$AddInGuid"
$Classes   = @(
    @{ Clsid = $AddInGuid;                                Class = 'SWEETs.AddIn.Entry.SweetsAddIn';                                  Dll = 'SWEETs.AddIn.dll' },
    @{ Clsid = '{7f9793bc-b597-42d3-9006-bcc967ed3178}';  Class = 'SWEETs.AddIn.Entry.SettingsCommand';                              Dll = 'SWEETs.AddIn.dll' },
    @{ Clsid = '{dc5849bc-304c-41df-9b0b-46d021fce334}';  Class = 'SWEETs.SweDrawingParameters.AddIn.Entry.UpdateDrawingParametersCommand'; Dll = 'SWEETs.SweDrawingParameters.AddIn.dll' },
    @{ Clsid = '{66f5514f-b55f-49bf-af2d-5fe8868f8dbc}';  Class = 'SWEETs.SweLineStyles.AddIn.Entry.LineStyleEditorCommand';                Dll = 'SWEETs.SweLineStyles.AddIn.dll' },
    @{ Clsid = '{3a59357d-16a4-4ca4-ae85-8e94c063a6bf}';  Class = 'SWEETs.SwePdfSchematic.AddIn.Entry.ImportPdfCommand';                    Dll = 'SWEETs.SwePdfSchematic.AddIn.dll' },
    @{ Clsid = '{5be2f177-f64d-4418-8c4a-d4f1102ca4f2}';  Class = 'DataSheetMfgPartsImport.ExtractCommand';                              Dll = 'DataSheetMfgPartsImport.dll' }
)
# The stand-alone tools' add-in CLSIDs and their original ProgIds (Drawing Parameters, Line Style Editor, PDF to Schematic, Data Sheet,
# VendorGrid, Change Log).
$OldAddIns = @(
    @{ Guid = '{21b069be-77c6-466c-bc9c-bac979b64a61}'; ProgId = 'TEW.SweDrawingParameters.AddIn' },
    @{ Guid = '{d854378b-d7f6-4a6a-99bc-211591e946e7}'; ProgId = 'TEW.SweLineStyles.AddIn' },
    @{ Guid = '{04fdfd9a-a04c-4264-824e-5dcf03a2d73d}'; ProgId = 'TEW.SwePdfSchematic.AddIn' },
    @{ Guid = '{C7234107-72C4-4EA4-A000-8D998EE70C15}'; ProgId = 'DataSheetMfgPartsImport.Addin' },
    @{ Guid = '{101df681-3a3a-4ce5-a370-4bef5a8ed4be}'; ProgId = 'VendorGrid.AddIn' },
    @{ Guid = '{D4C1A7E2-6B3F-4E58-9A21-7C0E5F3B8A64}'; ProgId = 'ChangeLog.SWE.AddIn' }
)

$hklm = [Microsoft.Win32.RegistryKey]::OpenBaseKey('LocalMachine', 'Registry64')
$hkcu = [Microsoft.Win32.RegistryKey]::OpenBaseKey('CurrentUser', 'Registry64')
function Set-Key($base, $path, [hashtable] $values) {
    $k = $base.CreateSubKey($path)
    foreach ($n in $values.Keys) {
        $v = $values[$n]
        if ($v -is [int]) { $k.SetValue($n, $v, 'DWord') } else { $k.SetValue($n, [string]$v, 'String') }
    }
    $k.Close()
}
function Remove-Key($base, $path) { $base.DeleteSubKeyTree($path, $false) }

if (Get-Process -Name 'SolidWorksElectrical', 'ElectricalSchematic' -ErrorAction SilentlyContinue) {
    Write-Warning 'SOLIDWORKS Electrical is running; changes apply after it restarts.'
}

foreach ($k in $StaleKeys) { Remove-Key $hklm "$k\$AddInGuid" }

# Stand-alone tools: SWE must not load them next to SWEETs.
foreach ($old in $OldAddIns) {
    foreach ($k in $SweKeys + $StaleKeys) { Remove-Key $hklm "$k\$($old.Guid)" }
    Remove-Key $hkcu "Software\SolidWorks\SOLIDWORKS Electrical\AddIns\$($old.Guid)"
    Remove-Key $hkcu "Software\Trace Software\Electrical\AddIns\$($old.Guid)"
    Remove-Key $hklm "SOFTWARE\Classes\CLSID\$($old.Guid)"
    Remove-Key $hklm "SOFTWARE\Classes\$($old.ProgId)"
}
# ProgId of SWEETs builds from before the TEW -> SWEETs rename (same CLSID, rewritten below).
Remove-Key $hklm 'SOFTWARE\Classes\TEW.SWEETs.AddIn'

if ($Unregister) {
    foreach ($k in $SweKeys) { Remove-Key $hklm "$k\$AddInGuid" }
    foreach ($c in $Classes) { Remove-Key $hklm "SOFTWARE\Classes\CLSID\$($c.Clsid)" }
    Remove-Key $hklm "SOFTWARE\Classes\$ProgId"
    Remove-Key $hkcu $UserKey
    Write-Host 'SWEETs unregistered.' -ForegroundColor Green
    return
}

$root = Split-Path -Parent $PSScriptRoot
if (-not $AssemblyPath) {
    $configs = if ($Configuration) { @($Configuration) } else { @('Release', 'Debug') }
    $AssemblyPath = $configs | ForEach-Object {
        Join-Path $root "src\SWEETs.AddIn\bin\x64\$_\SWEETs.AddIn.dll"
        Join-Path $root "src\SWEETs.AddIn\bin\$_\SWEETs.AddIn.dll"
    } | Where-Object { Test-Path $_ } | Sort-Object { (Get-Item $_).LastWriteTime } -Descending | Select-Object -First 1
}
if (-not $AssemblyPath -or -not (Test-Path $AssemblyPath)) { throw 'SWEETs not built. Build SWEETs.sln first (Visual Studio, x64) or run Build.ps1 -NoPackage.' }
$AssemblyPath = (Resolve-Path $AssemblyPath).Path
$dir = Split-Path $AssemblyPath
foreach ($stale in 'Interop.EwAPI.dll', 'Interop.x64.EwAPI.dll') {
    if (Test-Path (Join-Path $dir $stale)) { Remove-Item (Join-Path $dir $stale) -Force; Write-Host "Removed stale $stale" }
}

foreach ($c in $Classes) {
    $dll = Join-Path $dir $c.Dll
    if (-not (Test-Path $dll)) { throw "Missing next to SWEETs: $($c.Dll)" }
    $asmName  = [System.Reflection.AssemblyName]::GetAssemblyName($dll)
    $codeBase = ([Uri]$dll).AbsoluteUri
    $ver      = $asmName.Version.ToString()
    $clsKey   = "SOFTWARE\Classes\CLSID\$($c.Clsid)"
    Remove-Key $hklm $clsKey
    Set-Key $hklm $clsKey @{ '' = $c.Class }
    $common = @{ Class = $c.Class; Assembly = $asmName.FullName; RuntimeVersion = 'v4.0.30319'; CodeBase = $codeBase }
    Set-Key $hklm "$clsKey\InprocServer32" ($common + @{ '' = 'mscoree.dll'; ThreadingModel = 'Both' })
    Set-Key $hklm "$clsKey\InprocServer32\$ver" $common
    Set-Key $hklm "$clsKey\Implemented Categories\{62C8FE65-4EBB-45e7-B440-6E39B2CDBF29}" @{}
}
Set-Key $hklm "SOFTWARE\Classes\CLSID\$AddInGuid\ProgId" @{ '' = $ProgId }
Set-Key $hklm "SOFTWARE\Classes\$ProgId" @{ '' = $Classes[0].Class }
Set-Key $hklm "SOFTWARE\Classes\$ProgId\CLSID" @{ '' = $AddInGuid }

foreach ($k in $SweKeys) {
    Set-Key $hklm "$k\$AddInGuid" @{ '' = '0'; Path = $AssemblyPath; Title = $Title; Description = $Desc; StartUp = 1; StartShowMsg = 0 }
}
Set-Key $hkcu $UserKey @{ StartUp = 1; StartShowMsg = 0 }

Write-Host "Registered $AssemblyPath" -ForegroundColor Green
Write-Host 'Start SOLIDWORKS Electrical; SWEETs loads at startup and adds the SWEETs ribbon tab. Log: %LocalAppData%\SWEETs\logs'
