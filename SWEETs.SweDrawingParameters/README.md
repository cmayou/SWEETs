# SWEETs Drawing Parameters (SOLIDWORKS Electrical 2026 add-in)

> **Part of SWEETs.** This tool is a class library hosted by the SWEETs add-in (`..\src\SWEETs.AddIn`): ribbon
> **SWEETs › Drawing Tools › Drawing Parameters**. Build and install it with `..\SWEETs.sln` and `..\build\Build.ps1`; its own
> stand-alone build scripts, registration and ribbon tab were retired. Data lives under `SWEETs\DrawingParameters`
> (settings in %AppData%, logs and drawing backups in %LocalAppData%); data from the former `TEW\` folders is copied there the first time SWEETs starts.
> Sections below that describe the stand-alone add-in are kept for history.

Adds **SWEETs › Drawing Tools › Drawing Parameters** to the SOLIDWORKS Electrical ribbon. The dialog
lists every drawing in the open project with its current snap, grid, grid zone and ortho values, and applies new
values to the drawings you select.

## What it changes, and where the values live

Drawings (`.ewg`) are DWG files (AutoCAD 2010 `AC1024` or 2013 `AC1027` format). These are the values that SOLIDWORKS Electrical's
Drawing parameters dialog edits (right-click the status bar):

| Dialog setting | Stored in the drawing as | System variable |
|---|---|---|
| Snap On (F9) | `*Active` VPORT record | `SNAPMODE` |
| Snap spacing | `*Active` VPORT record | `SNAPUNIT` |
| Grid On (F7) | `*Active` VPORT record | `GRIDMODE` |
| Grid spacing | `*Active` VPORT record | `GRIDUNIT` |
| Grid zone | Drawing header limits | `LIMMIN`, `LIMMAX` |
| Ortho (F8) | Drawing header | `ORTHOMODE` |
| Object snap-on modes | **Not stored in drawings**: an application setting | `OSMODE` |

Values are in drawing units (inches for ANSI templates, millimetres for metric templates). In the Frozen Drink
Dispenser demo project, for example, drawing `26.ewg` has snap 0.125 / grid 0.25 and the grid zone is 0,0 → 17,11
(an ANSI B sheet).

**Object snap** isn't part of a DWG file from AutoCAD 2000 (R15) on. It's a SOLIDWORKS Electrical setting, so this
tool doesn't change it. Set it once in Drawing parameters › Object snap-on modes.

## How a drawing is updated

The SOLIDWORKS Electrical API has no call for grid or snap, and `runCommand("SETVAR …")` is accepted but ignored
(tested in 1.0.1). So the add-in changes the values **in the drawing file itself**, with its own small DWG patcher
(`Dwg\`):

1. Drawings that are open in SOLIDWORKS Electrical, or locked by another user, are skipped. Close them first.
2. The `AcDb:AcDbObjects` and `AcDb:Header` sections are decompressed. The fixed-width fields of the `*Active`
   VPORT record (SNAPMODE, SNAPUNIT, GRIDMODE, GRIDUNIT) and the header variables (ORTHOMODE, LIMMIN, LIMMAX) are
   overwritten in place, and the object and header CRCs are recomputed. No record changes size.
3. Only the pages whose content changed are recompressed. The compressor reproduces SOLIDWORKS Electrical's own
   pages byte for byte. A page keeps its slot when the new data fits. Otherwise the slot grows by 32-byte blocks, and
   the later pages, the page map and the file header are moved and updated. Page checksums, section info, page map
   and the file header CRC32 are all recomputed.
4. The new file is checked twice before it's used: once with the patcher's own reader and once with ACadSharp
   (an independent library). The original is copied to `%LocalAppData%\SWEETs\DrawingParameters\backups\<project>\<date-time>\`,
   then `File.Replace` swaps the new file in.

Supported: DWG 2010 (AC1024) and 2013 (AC1027), which are the formats SOLIDWORKS Electrical uses for drawings.
Other versions (for example AC1032 title blocks) are listed as *Not supported* and left unchanged.

**Verification done:** on five of your drawings, the C# patcher's output is byte-identical to the prototype's. For
the prototype's output, LibreDWG (another independent DWG reader) decodes every object and header variable
identically except the requested values (and, when a page grows, the file-layout addresses). A read-only scan of
all 24 project drawings patched each one in memory and restored it; restoring gave back the original file byte for
byte. To repeat these checks, run `tools\run-dwg-tests.cmd`.

## Build

Requirements: Visual Studio 2022 (or the .NET 8 SDK) with the .NET Framework 4.8 targeting pack, SOLIDWORKS
Electrical 2026 installed (for `bin\Interop.EwAPI.dll`), and Inno Setup 6 for the installer.

```powershell
# Build, stage and create artifacts\installer\SWEETs.SweDrawingParameters-1.1.1-setup.exe
.\build\Build.ps1

# Other options
.\build\Build.ps1 -Version 1.0.2
.\build\Build.ps1 -NoPackage                 # build and stage only
.\build\Build.ps1 -Packager PackageForge     # MSI + EXE through PackageForge
```

For development without the installer, close SOLIDWORKS Electrical and run this from an elevated PowerShell:

```powershell
.\build\Register-Dev.ps1            # registers the most recent build
.\build\Register-Dev.ps1 -Unregister
```

## First run checklist

1. Make a copy of a project, or use one you can restore, and open it in SOLIDWORKS Electrical.
2. Open **SWEETs › Drawing Tools › Drawing Parameters**. The list fills with each drawing's current values.
3. Select **one** drawing, change the snap spacing and click **Apply**.
4. Check that the status says *Verified*, then open the drawing and check Drawing parameters. Use the **Backups** button to find the original copies.
5. Then run it on the whole project.

## Files

| Path | Purpose |
|---|---|
| `src\…\Entry\SweAddIn.cs` | Add-in entry point, API key, ribbon button, COM registration |
| `src\…\Entry\UpdateDrawingParametersCommand.cs` | Ribbon command; opens the dialog on SWE's UI thread |
| `src\…\Interop\SweContract.cs` | Every EwAPI member used (all checked against the SWE 2026 interop) |
| `src\…\Services\ProjectDrawings.cs` | Lists the drawings of the active project |
| `src\…\Dwg\*.cs` | DWG R2004-family container reader, LZ77 codec, in-place parameter patcher |
| `src\…\Services\DrawingUpdater.cs` | Skip open/locked → patch → verify twice → backup → replace |
| `src\…\UI\RunWindow.xaml`, `RunViewModel.cs` | Modeless run window: runs the update from SWE's message loop |
| `src\…\Services\DwgParameterReader.cs` | Current values (patcher reader, ACadSharp 3.8.0 as independent check) |
| `tools\run-dwg-tests.cmd` | Patcher reference cases and read-only scan of all project drawings |
| `src\…\UI\*` | WPF dialog (MVVM) |

Logs: `%LocalAppData%\SWEETs\DrawingParameters\logs`. Last-used values: `%AppData%\SWEETs\DrawingParameters\settings.xml`.
