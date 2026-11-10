# SWEETs for SOLIDWORKS Electrical

SOLIDWORKS Electrical Efficiency Tools: one SOLIDWORKS Electrical 2026 add-in that puts every tool on a single
**SWEETs** ribbon tab.

| Group | Button | Tool |
|---|---|---|
| Drawing Tools | Drawing Parameters | Updates grid, snap, grid zone and ortho on the drawings of the open project |
| Drawing Tools | Line Style Editor | Edits line styles (`linestyle.xlin`) and the shapes they use (`ltypeshp.shp/.shx`) |
| Import Tools | PDF to Schematic | Builds a SOLIDWORKS Electrical project from a vector PDF schematic |
| Import Tools | Datasheet Importer | Reads a manufacturer data sheet and adds its parts to the manufacturer parts manager |
| Export Tools | Vendor Search (VendorGrid) | Compares availability, vendor SKU and price by quantity across vendors for a part number |
| Project Tools | Change Log, Compare, Capture Changes, Export to Excel, Verify, Settings (Change Log) | Keeps a tamper-evident log of every change to the open project; compares captured project states; exports the log to Excel |
| Settings | Settings | Turns each tool on or off |

## Settings

**SWEETs › Settings** lists the tools by ribbon group, with a switch each, and shows whether each one loaded. Saving writes
`%AppData%\SWEETs\settings.ini` (per Windows user) and rebuilds the tab: a tool that is off has no button and its
command is not registered. A group whose tools are all off is left off the tab. If a button does not appear or
disappear after saving, restart SOLIDWORKS Electrical. A tool missing from the file is on. Turning Change Log off hides all six of its buttons and stops
recording.

The Datasheet Importer, VendorGrid and Change Log keep their own settings behind the Settings button inside their
windows (Change Log: also the Settings button in Project Tools). SOLIDWORKS Electrical API keys are not entered
anywhere: SWEETs connects once for every tool, so VendorGrid's key field and Change Log's key prompt were removed.

## Install

Run `artifacts\installer\SWEETs-<version>-setup.exe` as administrator with SOLIDWORKS Electrical closed. Setup:

- removes the stand-alone installs of the tools (their uninstallers run silently; VendorGrid: unregistered and
  `Program Files\VendorGrid` deleted; Change Log: its MSI is uninstalled) and deletes their add-in registry keys, so their
  old stand-alone ribbon tabs go away. Their settings, saved vendor API keys and change logs are kept;
- removes a SWEETs build from before the TEW → SWEETs rename (installed in `Program Files\TEW\SWEETs`);
- installs to `C:\Program Files\SWEETs` and registers one add-in, **SWEETs**;
- copies the shared PDF to Schematic data (learned mappings, symbol catalog) from `%ProgramData%\TEW\SwePdfSchematic` to
  `%ProgramData%\SWEETs\PdfSchematic`, then seeds it with the symbol catalog and example rules (only files not there).

## Data folders

| Tool | Folder (under %LocalAppData%, %AppData% and %ProgramData%) | Was |
|---|---|---|
| SWEETs host | `SWEETs` (`settings.ini`, `logs`) | `TEW\SWEETs` |
| Drawing Parameters | `SWEETs\DrawingParameters` | `TEW\SweDrawingParameters` |
| Line Style Editor | `SWEETs\LineStyles` | `TEW\SweLineStyles` |
| PDF to Schematic | `SWEETs\PdfSchematic` | `TEW\SwePdfSchematic` |
| Datasheet Importer | `%AppData%\DataSheetMfgPartsImport` (unchanged) | |
| VendorGrid | `%AppData%\VendorGrid` (settings, vendor API keys, DPAPI), `%LocalAppData%\VendorGrid\vendorgrid.log` (unchanged) | |
| Change Log | `<project folder>\ChangeLog` (journals, captures), `%AppData%\SWE ChangeLog` (settings), `%LocalAppData%\SWE ChangeLog\addin.log` (unchanged) | |

The first time SWEETs starts for a Windows user it copies everything from the old `TEW\…` folders that the new
folders do not have yet (settings, logs, drawing and Support-file backups, learned mappings), writes
`%LocalAppData%\SWEETs\.migrated-from-tew` with what it copied, and never touches the TEW folders. Delete them once
you are happy with SWEETs.

## Build

Requirements: Visual Studio 2022 (or the .NET 8 SDK) with the .NET Framework 4.8 targeting pack, SOLIDWORKS
Electrical 2026 installed (for `bin\Interop.EwAPI.dll`), Inno Setup 6.

### Get the source

```powershell
git clone https://github.com/cmayou/SWEETs.git
```

### API key

The SOLIDWORKS Electrical API key is not in the repository. Before the first build, copy
`src\SWEETs.AddIn\Secrets\SweApiKeys.template.cs` to `SweApiKeys.local.cs` in the same folder and put your key(s)
in `Values` (tried in order). git ignores `*.local.cs`. Without that file the add-in project builds with a warning
and SOLIDWORKS Electrical refuses the connection; `build\Build.ps1` stops before building.

### Not in the repository

| Left out | Where it comes from |
|---|---|
| `Interop.EwAPI.dll`, `EwAPIX.dll` (Dassault) | The SOLIDWORKS Electrical 2026 install (`bin` folder); copy them into the tool `lib\` folders as before |
| Vendor data sheet PDFs (`DataSheetMfgPartsImport\samples`) | Kept locally; download from the manufacturers |
| Test drawings and SWE line style files (`...DwgPatch.Tests\testdata`, `...LineStyles.Tests\testdata`) | Kept locally; those tests need them |
| Stand-alone installers of VendorGrid, Change Log and the Datasheet Importer | Superseded by the SWEETs installer |
| Built setup EXE | GitHub Releases |

```powershell
.\build\Build.ps1                 # build, stage, write artifacts\SWEETs.iss, compile the setup EXE
.\build\Build.ps1 -Version 1.0.1
.\build\Build.ps1 -NoPackage      # build and stage only
.\build\Register-Dev.ps1          # elevated: register the local build for debugging
```

Open `SWEETs.sln` in Visual Studio and build **x64**. `src\SWEETs.AddIn` references the six tool projects, so
its `bin\x64\<Configuration>` folder holds the whole payload.

## Layout

```
SWEETs.sln                            host, six tools, and their test projects (Tests folder)
src\SWEETs.AddIn\                     host add-in: ribbon tab, Settings window, tool catalog, data migration
src\SWEETs.UI\                        shared WPF theme (colours, fonts, buttons, header bar) used by every window
SWEETs.SweDrawingParameters\           Drawing Parameters (class library) + tools\SWEETs.DwgPatch.Tests
SWEETs.SweLineStyles\                  Line Style Editor (class library) + tools\SWEETs.LineStyles.Tests
SWEETs.SwePdfSchematic\                PDF to Schematic (class library + Core) + tests, harness
DataSheetMfgPartsImport\               Datasheet Importer (class library)
VendorGrid\                            VendorGrid (class library); installer\ = its old stand-alone installer, not used
SWE-ChangeLog\                         Change Log (AddIn + Core class libraries) + tests\ChangeLog.Core.Tests;
                                       build\, installer\ = its old stand-alone build and MSI, not used
build\Build.ps1, Register-Dev.ps1      SWEETs build, installer and dev registration
build\Remove-TewFolders.ps1            archives the pre-rename TEW.* source folders (run once)
_standalone-backup\                    archived files from before SWEETs and before the rename
```

Project and namespace names keep the tools' own part after the prefix (`SWEETs.SweDrawingParameters`, not
`SWEETs.DrawingParameters`): a namespace `SWEETs.DrawingParameters` would hide the tool's `DrawingParameters` class
and break the build.

Names that keep "tew" on purpose: `.tewzip` archives and the SOLIDWORKS Electrical database tables
(`tew_app_data`, `[tew]`, `tew_element`, `tew_translatedtext`) are SOLIDWORKS Electrical's own names; `TEW-1001` is
text inside a PDF to Schematic test fixture; the installer and `Register-Dev.ps1` still name the old `TEW.*` ProgIds,
install folder and data folders so they can find and remove or migrate them.

## Look and feel

All windows use one theme: `src\SWEETs.UI\Theme.xaml` (assembly `SWEETs.UI.dll`). Each tool's own `Theme.xaml`
merges it (the Datasheet Importer's also maps its older resource names onto it), so colours, fonts and controls
are changed in one place. VendorGrid's and Change Log's own style names (`PrimaryButton`, `GhostButton`, `H1`, `Btn`,
`Chip`, `Input`, …) are defined on top of the SWEETs styles, so their buttons, text, cards, chips and inputs are the
SWEETs ones. VendorGrid keeps its colour-blind-safe status colours and shapes, and Change Log shows Added / Edited /
Removed in the SWEETs Success / Warning / Danger colours.

Ribbon icons are white glyphs on the SWEETs blue tile (VendorGrid, Change Log's six buttons and the Datasheet
Importer were redrawn to match; Change Log's Settings button uses the SWEETs gear).

Every window starts with the same white header bar (`Header.Bar`): the tool's ribbon icon on main windows, an
overline `SWEETs · <ribbon group>` (dialogs: the tool that opened them), the title and a one-line subtitle, with the
window's actions on the right. Page background `#F5F6F8`, accent `#1D4ED8`.

## How the tools plug in

Each tool's former add-in entry is now a static module with the same class name, so the tool's commands and windows
are unchanged:

| Tool | Module | Connect returns |
|---|---|---|
| Drawing Parameters | `SWEETs.SweDrawingParameters.AddIn.Entry.SweAddIn` | `SweetsUpdateProjectDrawingParametersCommand` |
| Line Style Editor | `SWEETs.SweLineStyles.AddIn.Entry.SweAddIn` | `SweetsLineStyleEditorCommand` |
| PDF to Schematic | `SWEETs.SwePdfSchematic.AddIn.Entry.SweAddIn` | `SweetsImportPdfSchematicCommand` |
| Datasheet Importer | `DataSheetMfgPartsImport.Addin` | `^C^CDATASHEETIMPORT` (its `RibbonCommand` setting) |
| VendorGrid | `VendorGrid.Addin.VendorGridAddIn` | `VendorGridSearch` |
| Change Log | `ChangeLog.AddIn.ChangeLogAddIn` | six buttons: `changelogShow`, `changelogCompare`, `changelogCapture`, `changelogExport`, `changelogVerify`, `changelogSettings` |

`Connect(application)` registers the tool's command with SWE's command manager and returns the command string the
ribbon button runs; `Disconnect()` removes it. The host gets the EwAPI application once and adds the buttons.
The Datasheet Importer casts the application to its own interop interfaces (`Interop\EwInterop.cs`, same IIDs as
SWE's type library) and registers its command through them, as it did stand-alone. Change Log compiles against SWE's
`Interop.EwAPI.dll` with embedded interop types and casts the application the same way; its `ConnectButtons` entry
in `ToolCatalog.cs` turns its six commands into six buttons in Project Tools.

To add a tool: give it a module with `Connect(object)`/`Disconnect()` (or, for several buttons, a `Connect` that
returns the buttons, wired through `ConnectButtons`), reference its project from
`SWEETs.AddIn.csproj`, link its icon under `Resources\`, and add a `ToolDefinition` to `Entry\ToolCatalog.cs`
(the `Id` is the settings key; do not change it once released).

## Logs

- SWEETs host: `%LocalAppData%\SWEETs\logs` (also from **Settings › Open log folder**)
- Drawing Parameters: `%LocalAppData%\SWEETs\DrawingParameters\logs`
- Line Style Editor: `%LocalAppData%\SWEETs\LineStyles\logs`
- PDF to Schematic: `%LocalAppData%\SWEETs\PdfSchematic\logs`
- Datasheet Importer: `%AppData%\DataSheetMfgPartsImport\addin.log`
