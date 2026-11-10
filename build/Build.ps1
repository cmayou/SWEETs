<#
.SYNOPSIS
    Builds and stages SWEETs (SOLIDWORKS Electrical 2026 add-in hosting every SWEETs tool), then produces the setup EXE
    with Inno Setup 6.

.DESCRIPTION
    1. Checks the EwAPI interop (SWE's bin\Interop.EwAPI.dll, lib\Interop.EwAPI.dll or EwAPIX.dll for TlbImp).
    2. Builds src\SWEETs.AddIn (x64, .NET Framework 4.8). It references the four tool projects, so its output
       folder holds the whole payload: SWEETs host, Drawing Parameters, Line Style Editor, PDF to Schematic,
       Data Sheet MFG Parts Import and their libraries (ACadSharp, PdfPig, System.Text.Json, …).
    3. Stages the payload under artifacts\stage (never Interop.EwAPI: SWE loads its own).
    4. Writes artifacts\SWEETs.iss and compiles it with ISCC.exe into artifacts\installer\SWEETs-<version>-setup.exe.

    The installer:
      - removes the stand-alone installs of the four tools (runs their uninstallers silently when found) and always
        deletes their SOLIDWORKS Electrical add-in keys, so their old ribbon tabs do not come back next to SWEETs;
      - writes the COM class registration and the SOLIDWORKS Electrical add-in keys for SWEETs directly (no RegAsm,
        which cannot load the add-in without SWE's Interop.EwAPI next to it);
      - checks for .NET Framework 4.8.

.EXAMPLE
    .\build\Build.ps1
.EXAMPLE
    .\build\Build.ps1 -Version 1.0.1
.EXAMPLE
    .\build\Build.ps1 -InnoSetupCompiler "D:\Tools\Inno Setup 6\ISCC.exe"
.EXAMPLE
    .\build\Build.ps1 -NoPackage      # build and stage only
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')] [string] $Configuration = 'Release',
    [string] $Version = '1.0.0',
    [string] $SweInstallDir = (Join-Path $env:ProgramW6432 'SOLIDWORKS Corp\SOLIDWORKS Electrical'),
    # Native EwAPIX.dll (type library); only needed when no Interop.EwAPI.dll is available.
    [string] $SweTypeLibrary,
    # ISCC.exe; defaults to the standard Inno Setup 6 install locations.
    [string] $InnoSetupCompiler,
    [switch] $NoPackage
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root      = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
$stage     = Join-Path $artifacts 'stage'
$outDir    = Join-Path $artifacts 'installer'
$hostProj  = Join-Path $root 'src\SWEETs.AddIn\SWEETs.AddIn.csproj'

$AddInGuid   = '3ed791fd-feac-4081-99b5-ef363b2fa108'   # SweetsAddIn.AddInGuid
$UpgradeCode = '17788920-4793-4447-92EC-38A76D1BC778'   # never change: Inno AppId
$AddInDll    = 'SWEETs.AddIn.dll'
$Title       = 'SWEETs'
$Desc        = 'SOLIDWORKS Electrical Efficiency Tools: drawing parameters, line styles, PDF to schematic, datasheet import, vendor search and change log.'
$SweKeys     = @(
    # Same two locations SOLIDWORKS Electrical's own add-ins use. The add-in manager also scans
    # "Trace Software\SOLIDWORKS Electrical\Addins"; registering there as well loads the add-in twice and crashes
    # SOLIDWORKS Electrical on exit, so that key is only ever deleted.
    'SOFTWARE\Trace Software\Electrical\Addins',
    'SOFTWARE\SolidWorks\SOLIDWORKS Electrical\AddIns'
)
$UserKey     = "Software\SolidWorks\SOLIDWORKS Electrical\AddIns\{$AddInGuid}"

# COM classes written by the installer (RegAsm /codebase equivalent). The tool command classes keep the CLSIDs they
# had stand-alone; they are handed to SWE's addCommand as objects, and were registered the same way before.
$ComClasses = @(
    @{ Clsid = $AddInGuid;                               Class = 'SWEETs.AddIn.Entry.SweetsAddIn';                                   Dll = $AddInDll;                              ProgId = 'SWEETs.AddIn' },
    @{ Clsid = '7f9793bc-b597-42d3-9006-bcc967ed3178';   Class = 'SWEETs.AddIn.Entry.SettingsCommand';                               Dll = $AddInDll;                              ProgId = '' },
    @{ Clsid = 'dc5849bc-304c-41df-9b0b-46d021fce334';   Class = 'SWEETs.SweDrawingParameters.AddIn.Entry.UpdateDrawingParametersCommand';  Dll = 'SWEETs.SweDrawingParameters.AddIn.dll';   ProgId = '' },
    @{ Clsid = '66f5514f-b55f-49bf-af2d-5fe8868f8dbc';   Class = 'SWEETs.SweLineStyles.AddIn.Entry.LineStyleEditorCommand';                 Dll = 'SWEETs.SweLineStyles.AddIn.dll';          ProgId = '' },
    @{ Clsid = '3a59357d-16a4-4ca4-ae85-8e94c063a6bf';   Class = 'SWEETs.SwePdfSchematic.AddIn.Entry.ImportPdfCommand';                     Dll = 'SWEETs.SwePdfSchematic.AddIn.dll';        ProgId = '' },
    @{ Clsid = '5be2f177-f64d-4418-8c4a-d4f1102ca4f2';   Class = 'DataSheetMfgPartsImport.ExtractCommand';                               Dll = 'DataSheetMfgPartsImport.dll';          ProgId = '' }
)

function Step($text) { Write-Host "`n==> $text" -ForegroundColor Cyan }
function Done($text) { Write-Host "    OK  $text" -ForegroundColor Green }

# ---------------------------------------------------------------- tools
Step 'Checking tools'
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'The .NET SDK (8.0 or later) is required: https://dotnet.microsoft.com/download' }

# The SOLIDWORKS Electrical API key is not in the repository. An installer built without it would install an add-in that
# SOLIDWORKS Electrical refuses to connect.
$apiKeys = Join-Path $root 'src\SWEETs.AddIn\Secrets\SweApiKeys.local.cs'
if (-not (Test-Path $apiKeys)) {
    throw "Missing $apiKeys. Copy SweApiKeys.template.cs (same folder) to SweApiKeys.local.cs and put your SOLIDWORKS Electrical API key in it. git ignores that file."
}
Done "dotnet $(dotnet --version)"

$libDir     = Join-Path $root 'lib'
$sweInterop = Join-Path $SweInstallDir 'bin\Interop.EwAPI.dll'
$libInterop = Join-Path $libDir 'Interop.EwAPI.dll'
if (Test-Path $sweInterop) {
    Done "EwAPI interop (SWE): $sweInterop"
} elseif (Test-Path $libInterop) {
    Done "EwAPI interop: $libInterop"
} else {
    if (-not $SweTypeLibrary) {
        $SweTypeLibrary = @((Join-Path $libDir 'EwAPIX.dll'), (Join-Path $SweInstallDir 'bin\ewapix.dll'), (Join-Path $root 'SWEETs.SwePdfSchematic\lib\EwAPIX.dll')) |
            Where-Object { Test-Path $_ } | Select-Object -First 1
    }
    if (-not $SweTypeLibrary -or -not (Test-Path $SweTypeLibrary)) {
        throw "No EwAPI interop found ('$sweInterop', '$libInterop') and no EwAPIX.dll. Install SWE 2026, copy EwAPIX.dll to lib\ or pass -SweTypeLibrary <path>."
    }
    Done "EwAPI type library: $SweTypeLibrary (interop generated during build)"
}

function Find-Iscc {
    if ($InnoSetupCompiler) { return $InnoSetupCompiler }
    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    )
    foreach ($hive in 'HKLM:\SOFTWARE\WOW6432Node', 'HKLM:\SOFTWARE', 'HKCU:\SOFTWARE') {
        $props = Get-ItemProperty "$hive\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1" -ErrorAction SilentlyContinue
        if ($props -and $props.PSObject.Properties['InstallLocation'] -and $props.InstallLocation) {
            $candidates += (Join-Path $props.InstallLocation 'ISCC.exe')
        }
    }
    $hit = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if ($hit) { return $hit }
    $cmd = Get-Command iscc -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($cmd) { return $cmd.Source }
    return $null
}

if (-not $NoPackage) {
    $iscc = Find-Iscc
    if (-not $iscc -or -not (Test-Path $iscc)) {
        throw 'Inno Setup 6 compiler (ISCC.exe) not found. Install Inno Setup 6 (https://jrsoftware.org/isdl.php) or pass -InnoSetupCompiler <path to ISCC.exe>.'
    }
    Done "Inno Setup: $iscc"
}

# ---------------------------------------------------------------- build
Step "Building SWEETs ($Configuration, x64, v$Version)"
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must be major.minor.patch (got '$Version')." }
$fileVersion = "$Version.0"
# SweetsVersion is read only by src\Directory.Build.props (the SWEETs host). A plain -p:Version would be a global
# property and would also re-stamp the four tool assemblies, which keep the versions in their own Directory.Build.props.
$buildArgs = @('build', $hostProj, '-c', $Configuration, '--nologo', '--verbosity', 'minimal', '-p:Platform=x64', "-p:SweetsVersion=$Version")
if ($SweTypeLibrary) { $buildArgs += "-p:SweTypeLibrary=$SweTypeLibrary" }
& dotnet @buildArgs
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
$bin = Join-Path $root "src\SWEETs.AddIn\bin\x64\$Configuration"
if (-not (Test-Path (Join-Path $bin $AddInDll))) { $bin = Join-Path $root "src\SWEETs.AddIn\bin\$Configuration" }
if (-not (Test-Path (Join-Path $bin $AddInDll))) { throw "Build output not found ($AddInDll)." }
Done "Output: $bin"

# Full assembly name of every DLL that holds a COM class (written into the CLSID keys).
$asmNames = @{}
foreach ($dll in ($ComClasses | ForEach-Object { $_.Dll } | Select-Object -Unique)) {
    $path = Join-Path $bin $dll
    if (-not (Test-Path $path)) { throw "Missing from the build output: $dll" }
    $asmNames[$dll] = [System.Reflection.AssemblyName]::GetAssemblyName($path)
    Done "$dll  ($($asmNames[$dll].FullName))"
}

# ---------------------------------------------------------------- stage
Step 'Staging payload'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
$app = Join-Path $stage 'app'
New-Item -ItemType Directory -Force -Path $app | Out-Null

# Everything except Interop.EwAPI (SWE ships and loads its own; a second copy breaks the EwAPI interface casts),
# debug symbols and XML docs.
Get-ChildItem $bin -File | Where-Object { $_.Name -notlike 'Interop*EwAPI*.dll' -and $_.Extension -notin '.pdb', '.xml' } |
    Copy-Item -Destination $app
foreach ($dir in Get-ChildItem $bin -Directory) {
    # Resources\ and Icons\ (ribbon icons); language folders of NuGet packages are not needed.
    if ($dir.Name -in 'Resources', 'Icons') { Copy-Item $dir.FullName -Destination $app -Recurse }
}
Copy-Item (Join-Path $root 'README.md') (Join-Path $app 'README.md')
# PDF to Schematic seed data, as its stand-alone installer shipped it: {app}\data and, once, %ProgramData%\SWEETs\PdfSchematic.
New-Item -ItemType Directory -Force -Path (Join-Path $stage 'data') | Out-Null
Copy-Item (Join-Path $root 'SWEETs.SwePdfSchematic\src\SWEETs.SwePdfSchematic.Core\Rules\default-rules.json') (Join-Path $stage 'data\rules.example.json')
Copy-Item (Join-Path $root 'SWEETs.SwePdfSchematic\build\seed\symbol-catalog.txt') (Join-Path $stage 'data\symbol-catalog.txt')
foreach ($required in @($AddInDll, 'SWEETs.UI.dll', 'VendorGrid.dll', 'ChangeLog.AddIn.dll', 'ChangeLog.Core.dll',
                         'Resources\settings-32.png', 'Resources\drawing-parameters-32.png', 'Resources\line-styles-32.png',
                         'Resources\pdf-to-schematic-32.png', 'Resources\datasheet-importer-32.png', 'Resources\vendorgrid-32.png',
                         'Resources\changelog_32.png', 'Resources\compare_32.png', 'Resources\baseline_32.png', 'Resources\export_32.png',
                         'Resources\verify_32.png', 'Resources\settings_32.png', 'Resources\sweets.ico')) {
    if (-not (Test-Path (Join-Path $app $required))) { throw "Staged payload is missing $required" }
}
Done "$(@(Get-ChildItem $stage -Recurse -File).Count) files staged"

New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
$zip = Join-Path $artifacts "SWEETs-$Version-payload.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
Done $zip

if ($NoPackage) { Write-Host "`nStaged only (-NoPackage)."; return }
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

# ================================================================ Inno Setup 6
function Write-InnoScript {
    $g    = '{{'                                   # literal '{' in Inno strings
    $ic   = "$g$AddInGuid}"
    $impl = "$g" + '62C8FE65-4EBB-45e7-B440-6E39B2CDBF29}'   # .NET Category

    $reg = New-Object System.Collections.Generic.List[string]
    function Add-Reg($rootKey, $subkey, $name, $type, $data, $flags) {
        $line = "Root: $rootKey; Subkey: ""$subkey""; ValueType: $type; ValueName: ""$name""; ValueData: ""$data"""
        if ($flags) { $line += "; Flags: $flags" }
        $reg.Add($line)
    }

    # SOLIDWORKS Electrical add-in discovery keys (Path is what SWE loads)
    foreach ($k in $SweKeys) {
        $s = "$k\$ic"
        Add-Reg 'HKLM64' $s ''             'string' '0' 'uninsdeletekey'
        Add-Reg 'HKLM64' $s 'Path'         'string' "{app}\$AddInDll" ''
        Add-Reg 'HKLM64' $s 'Title'        'string' $Title ''
        Add-Reg 'HKLM64' $s 'Description'  'string' $Desc ''
        Add-Reg 'HKLM64' $s 'StartUp'      'dword'  '1' ''
        Add-Reg 'HKLM64' $s 'StartShowMsg' 'dword'  '0' ''
    }
    # Per-user load state for the installing user (SWE creates it for other users)
    $u = $UserKey.Replace("{$AddInGuid}", $ic)
    Add-Reg 'HKCU' $u 'StartUp'      'dword' '1' 'uninsdeletekey'
    Add-Reg 'HKCU' $u 'StartShowMsg' 'dword' '0' ''

    # COM class registration (equivalent of RegAsm /codebase, written directly)
    foreach ($cls in $ComClasses) {
        $clsid = "$g$($cls.Clsid)}"
        $asm   = $asmNames[$cls.Dll]
        $ver   = $asm.Version.ToString()
        $codeBase = 'file:///{app}\' + $cls.Dll
        $c = "SOFTWARE\Classes\CLSID\$clsid"
        Add-Reg 'HKLM64' $c '' 'string' $cls.Class 'uninsdeletekey'
        foreach ($ip in "$c\InprocServer32", "$c\InprocServer32\$ver") {
            if ($ip -like '*InprocServer32') {
                Add-Reg 'HKLM64' $ip ''               'string' 'mscoree.dll' ''
                Add-Reg 'HKLM64' $ip 'ThreadingModel' 'string' 'Both' ''
            }
            Add-Reg 'HKLM64' $ip 'Class'          'string' $cls.Class ''
            Add-Reg 'HKLM64' $ip 'Assembly'       'string' $asm.FullName ''
            Add-Reg 'HKLM64' $ip 'RuntimeVersion' 'string' 'v4.0.30319' ''
            Add-Reg 'HKLM64' $ip 'CodeBase'       'string' $codeBase ''
        }
        Add-Reg 'HKLM64' "$c\Implemented Categories\$impl" '' 'none' '' ''
        if ($cls.ProgId) {
            Add-Reg 'HKLM64' "$c\ProgId" '' 'string' $cls.ProgId ''
            Add-Reg 'HKLM64' "SOFTWARE\Classes\$($cls.ProgId)" '' 'string' $cls.Class 'uninsdeletekey'
            Add-Reg 'HKLM64' "SOFTWARE\Classes\$($cls.ProgId)\CLSID" '' 'string' $clsid ''
        }
    }

    $appSrc  = "$stage\app\*"
    $dataSrc = "$stage\data\*"
    $icon    = "$stage\app\Resources\sweets.ico"

    @"
; Generated by build\Build.ps1 - edit the script, not this file.
#define AppName "$Title"
#define AppVersion "$Version"

[Setup]
AppId=$g$UpgradeCode}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=SWEETs
VersionInfoVersion=$fileVersion
VersionInfoDescription={#AppName} for SOLIDWORKS Electrical Setup
DefaultDirName={commonpf64}\SWEETs
; Releases before the TEW -> SWEETs rename installed to Program Files\TEW\SWEETs: do not reuse that folder.
UsePreviousAppDir=no
DisableProgramGroupPage=yes
DisableDirPage=auto
OutputDir=$outDir
OutputBaseFilename=SWEETs-{#AppVersion}-setup
SetupIconFile=$icon
UninstallDisplayIcon={app}\Resources\sweets.ico
UninstallDisplayName={#AppName} for SOLIDWORKS Electrical
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
WelcomeLabel2=This installs SWEETs for SOLIDWORKS Electrical 2026: Drawing Parameters, Line Style Editor, PDF to Schematic, Datasheet Importer, VendorGrid and Change Log on one SWEETs ribbon tab.%n%nStand-alone installs of these tools are removed. Close SOLIDWORKS Electrical before continuing.

[InstallDelete]
; Stale interop copies shadow SWE's own Interop.EwAPI and break the add-in.
Type: files; Name: "{app}\Interop.EwAPI.dll"
Type: files; Name: "{app}\Interop.x64.EwAPI.dll"

[Dirs]
Name: "{commonappdata}\SWEETs\PdfSchematic"; Permissions: users-modify; Flags: uninsneveruninstall

[Files]
Source: "$appSrc"; DestDir: "{app}"; Excludes: "*.pdb,*.xml"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "$dataSrc"; DestDir: "{app}\data"; Flags: ignoreversion
Source: "$dataSrc"; DestDir: "{commonappdata}\SWEETs\PdfSchematic"; Flags: onlyifdoesntexist uninsneveruninstall

[Registry]
$($reg -join "`r`n")

[Code]
const
  UninstallRoot = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\';
  StaleKey = 'SOFTWARE\Trace Software\SOLIDWORKS Electrical\Addins\{$AddInGuid}';

{ Inno AppIds of the stand-alone installers: Drawing Parameters, Line Style Editor, PDF to Schematic, Data Sheet MFG Parts Import. }
function OldAppId(I: Integer): String;
begin
  case I of
    0: Result := '{861F7C78-848B-4A29-A4A7-34AD068EB7AA}';
    1: Result := '{AC257B4A-5C07-4B2F-A67E-033ECE0F0B4C}';
    2: Result := '{5A8C19E7-7E97-43FA-BAFF-604E22B0DFD5}';
    3: Result := '{75AF52BB-1286-43AB-BB70-BA55943D1B69}';
  else
    Result := '';
  end;
end;

{ Add-in CLSIDs of the stand-alone versions (each one created its own ribbon tab): the four above, VendorGrid, Change Log. }
function OldAddInGuid(I: Integer): String;
begin
  case I of
    0: Result := '{21b069be-77c6-466c-bc9c-bac979b64a61}';
    1: Result := '{d854378b-d7f6-4a6a-99bc-211591e946e7}';
    2: Result := '{04fdfd9a-a04c-4264-824e-5dcf03a2d73d}';
    3: Result := '{C7234107-72C4-4EA4-A000-8D998EE70C15}';
    4: Result := '{101df681-3a3a-4ce5-a370-4bef5a8ed4be}';
    5: Result := '{D4C1A7E2-6B3F-4E58-9A21-7C0E5F3B8A64}';
  else
    Result := '';
  end;
end;

function OldProgId(I: Integer): String;
begin
  case I of
    0: Result := 'TEW.SweDrawingParameters.AddIn';
    1: Result := 'TEW.SweLineStyles.AddIn';
    2: Result := 'TEW.SwePdfSchematic.AddIn';
    3: Result := 'DataSheetMfgPartsImport.Addin';
    4: Result := 'VendorGrid.AddIn';
    5: Result := 'ChangeLog.SWE.AddIn';
  else
    Result := '';
  end;
end;

{ Runs a stand-alone tool's uninstaller silently. The Inno uninstaller hands over to a temporary copy of itself and
  returns at once, so wait (up to 60 s) for its uninstall key to disappear. }
procedure UninstallOld(const AppId: String);
var
  Key, Cmd: String;
  Root, Code, Waited: Integer;
begin
  Key := UninstallRoot + AppId + '_is1';
  if RegQueryStringValue(HKLM64, Key, 'UninstallString', Cmd) then Root := HKLM64
  else if RegQueryStringValue(HKLM32, Key, 'UninstallString', Cmd) then Root := HKLM32
  else exit;
  Log('Removing stand-alone install ' + AppId + ': ' + Cmd);
  if Exec(RemoveQuotes(Cmd), '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART', '', SW_HIDE, ewWaitUntilTerminated, Code) then
  begin
    Waited := 0;
    while RegKeyExists(Root, Key) and (Waited < 120) do
    begin
      Sleep(500);
      Waited := Waited + 1;
    end;
  end
  else
    Log('Could not start the uninstaller: ' + SysErrorMessage(Code));
end;

{ Whatever the uninstallers left (or a copy registered by hand / RegAsm): SWE must not load the old add-ins. }
procedure RemoveOldAddIn(const Guid, ProgId: String);
begin
  RegDeleteKeyIncludingSubkeys(HKLM64, 'SOFTWARE\Trace Software\Electrical\Addins\' + Guid);
  RegDeleteKeyIncludingSubkeys(HKLM64, 'SOFTWARE\SolidWorks\SOLIDWORKS Electrical\AddIns\' + Guid);
  RegDeleteKeyIncludingSubkeys(HKLM64, 'SOFTWARE\Trace Software\SOLIDWORKS Electrical\Addins\' + Guid);
  RegDeleteKeyIncludingSubkeys(HKCU, 'Software\SolidWorks\SOLIDWORKS Electrical\AddIns\' + Guid);
  RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Trace Software\Electrical\AddIns\' + Guid);
  RegDeleteKeyIncludingSubkeys(HKLM64, 'SOFTWARE\Classes\CLSID\' + Guid);
  RegDeleteKeyIncludingSubkeys(HKLM64, 'SOFTWARE\Classes\' + ProgId);
end;

{ Stand-alone VendorGrid (its own PowerShell installer): Program Files\VendorGrid, COM-registered with RegAsm, and a
  Programs and Features entry named VendorGrid. Unregister, remove the entry and the folder. Settings and saved vendor API
  keys in %AppData%\VendorGrid are kept: VendorGrid inside SWEETs uses them. }
procedure RemoveStandaloneVendorGrid;
var
  Dir, Dll: String;
  Code: Integer;
begin
  Dir := ExpandConstant('{commonpf64}\VendorGrid');
  Dll := Dir + '\VendorGrid.dll';
  if FileExists(Dll) then
  begin
    Log('Removing stand-alone VendorGrid: ' + Dir);
    if not Exec(ExpandConstant('{win}\Microsoft.NET\Framework64\v4.0.30319\regasm.exe'), '"' + Dll + '" /unregister /nologo',
                '', SW_HIDE, ewWaitUntilTerminated, Code) then
      Log('regasm /unregister could not start: ' + SysErrorMessage(Code));
  end;
  RegDeleteKeyIncludingSubkeys(HKLM64, UninstallRoot + 'VendorGrid');
  if DirExists(Dir) and not DelTree(Dir, True, True, True) then
    Log('Could not delete ' + Dir + ' (files in use?); delete it after closing SOLIDWORKS Electrical.');
end;

{ Stand-alone Change Log: a WiX MSI ("Change Log for SOLIDWORKS Electrical") installed in SWE's bin\AddIns\ChangeLog.
  Uninstall every product with that name silently. Its logs (%LocalAppData%\SWE ChangeLog), settings (%AppData%\SWE ChangeLog)
  and the change logs stored with each project (<project>\ChangeLog) are kept: Change Log inside SWEETs uses them. }
procedure RemoveStandaloneChangeLog;
var
  Names: TArrayOfString;
  I, Code: Integer;
  Name: String;
begin
  if not RegGetSubkeyNames(HKLM64, UninstallRoot, Names) then exit;
  for I := 0 to GetArrayLength(Names) - 1 do
    if RegQueryStringValue(HKLM64, UninstallRoot + Names[I], 'DisplayName', Name) and
       (Name = 'Change Log for SOLIDWORKS Electrical') and (Copy(Names[I], 1, 1) = '{') then
    begin
      Log('Removing stand-alone Change Log MSI ' + Names[I]);
      if not Exec(ExpandConstant('{sys}\msiexec.exe'), '/x ' + Names[I] + ' /qn /norestart', '', SW_HIDE, ewWaitUntilTerminated, Code) then
        Log('msiexec could not start: ' + SysErrorMessage(Code))
      else if (Code <> 0) and (Code <> 3010) then
        Log('msiexec /x returned ' + IntToStr(Code));
    end;
end;

{ A SWEETs build from before the TEW -> SWEETs rename lives in Program Files\TEW\SWEETs. Same AppId: uninstall it first
  so its files and its TEW-named registry entries go away; this setup then installs to Program Files\SWEETs. }
procedure RemoveTewEraSweets;
var
  Key, Dir: String;
begin
  Key := UninstallRoot + '{$UpgradeCode}' + '_is1';
  if RegQueryStringValue(HKLM64, Key, 'Inno Setup: App Path', Dir) and (Pos('\TEW\', Dir) > 0) then
    UninstallOld('{$UpgradeCode}');
  RegDeleteKeyIncludingSubkeys(HKLM64, 'SOFTWARE\Classes\TEW.SWEETs.AddIn');   { ProgId of those builds }
end;

{ Shared PDF to Schematic data (learned mappings, symbol catalog) moved from ProgramData\TEW\SwePdfSchematic to
  ProgramData\SWEETs\PdfSchematic. Copies only files the new folder does not have yet; the old folder is left as is.
  Per-user data (settings, logs, backups) is copied by SWEETs itself the first time it starts for each user. }
procedure MigrateSharedData;
var
  Code: Integer;
  OldDir, NewDir: String;
begin
  OldDir := ExpandConstant('{commonappdata}\TEW\SwePdfSchematic');
  NewDir := ExpandConstant('{commonappdata}\SWEETs\PdfSchematic');
  if not DirExists(OldDir) then exit;
  Log('Copying ' + OldDir + ' to ' + NewDir);
  if not Exec(ExpandConstant('{sys}\robocopy.exe'), '"' + OldDir + '" "' + NewDir + '" /E /XC /XN /XO /R:0 /W:0 /NJH /NJS /NP',
              '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Log('robocopy could not start: ' + SysErrorMessage(Code))
  else if Code >= 8 then
    Log('robocopy reported errors (exit ' + IntToStr(Code) + ')');
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  I: Integer;
begin
  if CurStep = ssInstall then
  begin
    RemoveTewEraSweets;
    for I := 0 to 3 do UninstallOld(OldAppId(I));
    RemoveStandaloneVendorGrid;
    RemoveStandaloneChangeLog;
    for I := 0 to 5 do RemoveOldAddIn(OldAddInGuid(I), OldProgId(I));
    RegDeleteKeyIncludingSubkeys(HKLM64, StaleKey);
    MigrateSharedData;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then RegDeleteKeyIncludingSubkeys(HKLM64, StaleKey);
end;

function InitializeSetup(): Boolean;
begin
  Result := IsDotNetInstalled(net48, 0);
  if not Result then
    MsgBox('{#AppName} requires Microsoft .NET Framework 4.8.' + #13#10 +
           'Install it from https://dotnet.microsoft.com/download/dotnet-framework/net48 and run this setup again.',
           mbCriticalError, MB_OK);
end;

"@
}

Step 'Building setup EXE with Inno Setup 6'
$issPath = Join-Path $artifacts 'SWEETs.iss'
[System.IO.File]::WriteAllText($issPath, (Write-InnoScript), (New-Object System.Text.UTF8Encoding($true)))
Done $issPath
& $iscc /Q $issPath
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compile failed (exit $LASTEXITCODE). Open '$issPath' in the Inno Setup IDE to see the error line." }
Done "Installer written to $outDir"
Get-ChildItem $outDir -Filter *.exe | Format-Table Name, Length, LastWriteTime -AutoSize
