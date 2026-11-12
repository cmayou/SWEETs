# SWEETs Drawing Parameters: project notes for Claude Code

> **Part of SWEETs.** This tool is a class library hosted by the SWEETs add-in (`..\src\SWEETs.AddIn`): ribbon
> **SWEETs › Drawing Tools › Drawing Parameters**. Build and install it with `..\SWEETs.sln` and `..\build\Build.ps1`; its own
> stand-alone build scripts, registration and ribbon tab were retired. Data lives under `SWEETs\DrawingParameters`
> (settings in %AppData%, logs and drawing backups in %LocalAppData%); data from the former `TEW\` folders is copied there the first time SWEETs starts.
> Sections below that describe the stand-alone add-in are kept for history.

SOLIDWORKS Electrical 2026 add-in (C#, .NET Framework 4.8, x64, WPF). It adds **SWEETs › Drawing Tools ›
Drawing Parameters**, which sets snap, grid, grid zone and ortho on the drawings of the open project.
Owner: Charles. Started in a claude.ai chat on 2026-10-28 and moved here on 2026-11-04.

## Ground rules from the owner
- Use only verified information. Ask when something is unclear rather than guessing.
- Visual Studio project, with a PowerShell build that produces an Inno Setup 6 installer (`build\Build.ps1`).
- Minimal SaaS/enterprise UI (WPF, `UI\Theme.xaml`). Write the UI as the application, with no assistant-style filler text.
- The SWE API key is hard-coded in `Entry\SweAddIn.cs`, as the owner asked.
- Snap and grid each take **one** spacing value, written to both X and Y, as in SWE's own Drawing parameters dialog.
  The grid zone keeps separate X/Y corners.

## Current state (version 1.1.1)
- The add-in and `tools\SWEETs.DwgPatch.Tests` both build with 0 errors and 0 warnings (build check 2026-11-04; all six
  reference cases pass). Version 1.1.1 changed only the X/Y spacing UI.
- Installer: `artifacts\installer\SWEETs.SweDrawingParameters-1.1.1-setup.exe` (built 2026-11-04).
- **Tested in SOLIDWORKS Electrical on 2026-11-04:** the owner installed 1.1.1 and confirmed it works as expected.

## What was learned (all verified)
- `.ewg` drawings are DWG files: AC1027 (2013) for SWE 2026 drawings, AC1024 (2010) for older ones. Some title
  blocks are AC1032 (2018); those are **not supported** and are shown as such.
- Where the values live: `*Active` VPORT record → SNAPMODE, SNAPUNIT, GRIDMODE, GRIDUNIT; header → ORTHOMODE,
  LIMMIN/LIMMAX (the grid zone). **OSMODE (object snap) is not stored in DWG files from R15 on**: it's an
  application setting, so the tool doesn't set it.
- The SWE API has no call for grid or snap. `IEwApplicationX.runCommand("SETVAR …")` returns EW_NO_ERROR but
  changes nothing, and `runCommand("SNAPUNIT …")` returns EW_BAD_INPUTS (tested in versions 1.0.0 and 1.0.1;
  logs are in `%LocalAppData%\SWEETs\DrawingParameters\logs`).
- So the add-in **patches the DWG file directly** (`src\…\Dwg\`):
  - It decompresses `AcDb:AcDbObjects` and `AcDb:Header`, overwrites the fixed-width bits in place and recomputes
    the CRC16 values (seed 0xC0C1).
  - The LZ77 compressor (a port of ACadSharp/LibreDWG) plus two zero bytes reproduces SWE's pages **byte for byte**.
  - Only the pages that changed are rewritten. If a page no longer fits its slot, the slot grows by 32-byte blocks
    and the later pages, page map, section info and file header (CRC32, plus the copy at the end of the file) are
    moved and updated.
  - The page checksum is the Adler-style one from ODA spec section 4.3. For data pages:
    `data_crc = cs(0, data)` and `hdr_crc = cs(data_crc, header32 with hdr_crc = 0)`. The header is XORed with
    `0x4164536B ^ address`.
  - The header-section CRC covers `[16, 24 + size)` when R2010+ `bitsize_hi` is present; the code detects this by
    checking the CRC. `has_ds_data` exists only in AC1027+. Header REQUIREDVERSIONS (BLL) exists only in AC1027+.
- Each drawing is updated as follows (`Services\DrawingUpdater.cs`):
  1. Skip drawings that are open or locked (COM calls, made on SWE's UI thread).
  2. Patch in memory.
  3. Verify twice: with the patcher's own reader, then with ACadSharp 3.8.0 (independent).
  4. Back up the original, then swap the new file in with `File.Replace`.

## Verification evidence
- `tools\run-dwg-tests.cmd`: six reference cases whose C# output SHA-256 must equal the Python prototype's (all
  pass), plus a read-only scan of `E:\SOLIDWORKS Electrical Data\Projects` (24 OK; 4 AC1032 title blocks
  unsupported). Patch-then-restore is byte-identical whenever no page grew.
- The prototype's output was also decoded with LibreDWG: every object and header variable was identical except
  the requested values (and the file-layout addresses when a page grew).
- `tools\prototype\`: the Python prototype (`dwg2004.py`, `verify.py`, `verify2.py`, `probe.py`) and
  `expected.json`. The LibreDWG checks need `dwgread` from https://github.com/LibreDWG/libredwg (build it with
  `cmake`; run `git submodule update --init jsmn` first).

## Layout
- `src\SWEETs.SweDrawingParameters.AddIn\`
  - `Entry\`: add-in entry, ribbon, and the command, which opens `MainWindow` modally and then the `RunWindow`
    (modeless).
  - `Interop\`: `SweApi.cs` (reflection bridge), `SweContract.cs` (every EwAPI member used, each checked against the
    SWE 2026 interop).
  - `Dwg\`: `DwgR2004File` (container), `Lz77Ac18`, `DwgBits` (bits, checksums, CRCs), `DwgParameterPatcher`.
  - `Services\`: `ProjectDrawings`, `DrawingUpdater`, `DwgParameterReader`.
  - `UI\`: `MainWindow`, `RunWindow`, view models, `Theme.xaml`.
- `build\Build.ps1` (Inno Setup 6 or PackageForge), `build\Register-Dev.ps1`.
- `tools\`: `build-check.cmd`, `run-dwg-tests.cmd`, `SWEETs.DwgPatch.Tests\`, `prototype\`.

## Related projects (same machine)
`E:\AI Applicaations\SWEETs App\SWEETs.SwePdfSchematic` is the PDF-to-schematic add-in. This project reuses its `SweApi`,
registration and Build.ps1 patterns. The Electrical API 2019 HTML help is in
`E:\AI Applicaations\Electrical API-2019.0.0-ActiveX\html`.
