# SWEETs PDF → Schematic for SOLIDWORKS Electrical 2026

> **Part of SWEETs.** This tool is a class library hosted by the SWEETs add-in (`..\src\SWEETs.AddIn`): ribbon
> **SWEETs › Import Tools › PDF to Schematic**. Build and install it with `..\SWEETs.sln` and `..\build\Build.ps1`; its own
> stand-alone build scripts, registration and ribbon tab were retired. Data lives under `SWEETs\PdfSchematic`
> (settings in %AppData%, logs in %LocalAppData%, learned mappings and symbol catalog in %ProgramData%); data from the former `TEW\` folders is copied there the first time SWEETs starts.
> Sections below that describe the stand-alone add-in are kept for history.

A SOLIDWORKS Electrical add-in that reads a **vector PDF schematic** locally and builds a **new SWE project**
from it: scheme pages, symbols from your library, wires, components (which give SWE's native cross-references),
and the title-block data captured as project documentation.

Launched from **SWEETs → Import Tools → PDF to Schematic** on the SWE ribbon.

```
PDF ──► VectorPdfReader ──► PageLayoutAnalyzer ──► WireExtractor ──► SymbolExtractor ──► SymbolClassifier
         (PdfPig, local)     frame / title block    nets, T-joins,    clusters, pins,     IEC + ANSI rules
                             columns, doc fields    junction dots     stub pruning
                                                                          │
   MappingStore (learned) ◄──── Review UI (cards, canvas, modal) ◄────────┘
          │                                   │
          └──────────────► BuildPlanFactory ──► BuildRunner ──► ISchematicTarget ──► SweTarget (EwAPI)
                                                                              └────► DryRunTarget (Simulate)
```

## Solution layout

| Project | Target | Purpose |
|---|---|---|
| `src/SWEETs.SwePdfSchematic.Core` | netstandard2.0 | PDF reading, recognition, mapping store, build plan, build runner. No SWE dependency. |
| `src/SWEETs.SwePdfSchematic.AddIn` | net48 x64, WPF, COM | SWE add-in entry, ribbon command, EwAPI bridge, UI. |
| `tests/SWEETs.SwePdfSchematic.Core.Tests` | net8.0 xUnit | Unit tests + end-to-end test on `samples/sample-iec-2page.pdf`. |
| `tools/SWEETs.SwePdfSchematic.Harness` | net48 WPF exe | Runs the UI without SWE (Simulate only) for fast iteration. |
| `build/` | PowerShell | `Build.ps1` (test → build → stage → PackageForge), `Register-Dev.ps1`. |

## Requirements

- Windows, Visual Studio 2022 (17.8+) or the .NET 8 SDK — builds the net48 projects too.
- `TlbImp.exe` (Visual Studio Installer → Individual components → ".NET Framework 4.8 SDK").
- `EwAPIX.dll` from SOLIDWORKS Electrical 2026 — a copy is in `lib\` (git-ignored). On the first build the add-in
  project runs TlbImp on it to create `lib\Interop.EwAPI.dll` (namespace `EwAPI`); no COM reference needed.
- PackageForge (for MSI + setup EXE). Without it, `Build.ps1` still stages the payload and manifest.

## Build

```powershell
# full pipeline: tests, Release build, staging, packageforge.json, installer
.\build\Build.ps1 -Version 1.0.0 -PackageForgeCli "C:\Tools\PackageForge\pforge.exe"

# EwAPIX.dll somewhere else
.\build\Build.ps1 -SweTypeLibrary "D:\SOLIDWORKS Electrical\EwAPIX.dll"

# dev loop: build + register the Debug add-in (elevated shell, SWE closed)
dotnet build src\SWEETs.SwePdfSchematic.AddIn -c Debug -p:Platform=x64
.\build\Register-Dev.ps1
```

Outputs land in `artifacts\`: `stage\` (payload), `packageforge.json`, `*-payload.zip`, `installer\`.

`packageforge.json` describes the files, RegAsm `/codebase` x64 registration, the SWE add-in key
(`HKLM\SOFTWARE\SolidWorks\SOLIDWORKS Electrical\AddIns\{04fdfd9a-a04c-4264-824e-5dcf03a2d73d}`),
the shared data folder (`%ProgramData%\SWEETs\PdfSchematic`, Users: Modify), prerequisites (.NET 4.8, SWE 2026)
and outputs. If your PackageForge schema or CLI verbs differ, adjust the manifest block or pass
`-PackageForgeArgs` (default: `build --manifest "{manifest}" --output "{out}"`).

## First run — teaching it your library

No SWE library symbol names are assumed. On the first import every symbol is **Unmapped**:

1. Review → select a card → **Change symbol…** (Ctrl+M). The picker reads the **SOLIDWORKS Electrical symbol
   library** of the connected environment (name, description, standard, class, circuit count, DWG preview) — filter
   by standard and class, search, and pick. Results are ranked for the detected kind and pole count.
   **Reload library** re-reads it after symbols are added.
2. "Also apply to N identical shapes" maps every identical shape in the PDF at once.
3. The choice is saved to `%ProgramData%\SWEETs\PdfSchematic\mappings.json` both for that exact shape
   (signature) and as the default for its kind (e.g. IEC NO contact). The next PDF is mostly pre-confirmed.

### Multi-pole devices (combine)

Poles drawn separately (4-pole fuse disconnector, 3-pole breaker, contactor main poles) are detected one per pole.
To place them as **one** multi-circuit library symbol:

- On a pole's card choose **Combine N poles** (offered when poles share a tag and are linked or stacked), or
- Ctrl/Shift-click several cards on one page → **Combine N selected**.

Then map the combined symbol (the picker can filter to symbols with N circuits). The combined shape is learned:
the same pole group is combined and mapped automatically on later imports. **Split poles** undoes it.
Kind defaults are never set from combined symbols, so single fuses keep their own default.

Names outside the library can still be typed, or listed in `%ProgramData%\SWEETs\PdfSchematic\symbol-catalog.txt`
(`name|description` per line). Point `MappingStorePath` in `%AppData%\SWEETs\PdfSchematic\settings.json` at a
share to learn as a team.

## What gets built

| From the PDF | In SOLIDWORKS Electrical |
|---|---|
| Each vector page | A scheme page in the template's first book, scaled into the drawing area, grid-snapped |
| Confirmed symbols | Library symbols at the original position/rotation, re-aligned so library pins land on the wire ends |
| Device tags (`-K1`, `CR1`…) | One component per tag; every symbol of the tag is attached → SWE generates cross-references |
| Wire nets | Wire lines snapped to the inserted symbols' real pins |
| Title block | Project name/description + build log; source PDF attached to the project |
| Wire lines | Drawn with the project wire style "Imported" (created on first use) |
| Wire numbers | New wires are numbered, then every wire of a numbered PDF net gets the PDF number in manual mode (matched on component circuit + terminal at both ends); unmatched numbers go to `…-deferred.csv` |
| Origin-destination pairs | Written to `…-deferred.csv` in the log folder (see API notes) |
| Bill-of-material reports (Mark/Tag + Reference/Part number/Article columns) | Manufacturer parts assigned to each component (`assignManufacturerPart`, × quantity); parts missing from the catalog are created first |
| BOM marks without a symbol | Cables (cable reference created in the catalog), rails/ducts (components), enclosure items (component in the location); undrawn terminals are reported only |

### Manufacturer parts

`BomReader` reads every parts table (header found from its column titles, wrapped headers and cells handled), merges the
rows of all reports per mark and part reference (manufacturer filled from any report that has it), and classifies marks
as component, terminal, cable, location item or wire (wires are skipped: SWE manages them through wire styles).
Each review card shows its component's parts with catalog status; **Parts…** (Ctrl+P) edits them. Items without a
symbol are listed on the Build step. Catalog matching: manufacturer + reference, else reference alone.

Circuits and terminals: one part per component is the *base* part (ticked "Circuits" in Parts…; by default the first
part with quantity 1, so a fuse holder's fuse links become accessories). When that catalog part has no circuits, the
build gives it one circuit per circuit of the component's inserted symbols (same circuit type, symbol order) and names
each terminal with the designation printed at that pin in the PDF ("L1"/"T1", "13"/"14", "A1"/"A2"). Catalog parts
that already define circuits are not changed.

Components take the classification of the library symbol mapped to them (the coil, else the first symbol).
Terminal strips: the strip tag ("X1") becomes a terminal-strip component and every drawn terminal a terminal component
under it, marked with the number printed beside it ("1" … "12"; unnumbered terminals are numbered in drawing order).
Terminals listed in the BOM but not drawn are created under their strip. Dash-dot centre lines through the terminals of
a strip (or any dashed line crossing two symbols) are not converted to wires.

## EwAPI compatibility

Every EwAPI member the add-in calls is listed in `src/…AddIn/Interop/SweContract.cs` and was verified against
the type library embedded in SWE 2026's `EwAPIX.dll` (names, argument lists, return types). Highlights:

- Entry: `EwAddIn.connectToEwAPI(factory)` / `disconnectFromEwApi()`; command: `EwCommandX` with
  `getName`, `getDescription`, `getFlags`, `execute(EwCommandContextX)`.
- Pages: `IEwProjectFileManagerX.newProjectFile()`; symbols and wire lines are created *from the page*:
  `IEwProjectFileX.newEwProjectSymbol()` and `newEwProjectLine(kLineSchematic)` + start/end X/Y setters.
- Ribbon: `addTab` → `addGroup` → `addControl(kControlButton, command)` → `setCaption` / `setIconFromImage`.
- `IInteropFactoryX.getEwApplication(key)` is called with the SWE API key hard-coded in `Entry/SweAddIn.cs` (`SweApiKey`).

`SweApi.CheckContract()` re-checks the list at startup (Environment card + log), so a future SWE release that
renames something is reported, not a crash. Fix = put the new name first in that member's candidate list.

Not available in EwAPI 2026, so exported to `…-deferred.csv` instead: per-wire marks (wire numbers come from SWE's
wire numbering) and individual origin-destination arrow placement (EwAPI only exposes the project-wide
automatic-arrows command).

## Recognition notes

- **Vector PDFs only.** Raster/scanned pages are detected, flagged and skipped.
- Standards: IEC (`-K1`, `=A1+B2-Q3`, `/3.4` cross-refs) and ANSI/NFPA (`CR1`, `PB2`, rung lists), detected per page.
- Kinds recognised from geometry: NO/NC contact, pushbutton NO/NC, coil, breaker, fuse, lamp, motor, terminal,
  transformer, black box, off-page arrow. Unknown shapes still map via learned signatures.
- Multi-pole devices drawn as separate poles inherit the tag of the tagged pole in the same row.
- All thresholds are in `RecognitionOptions` (mm) and persisted in settings.json.

## Iterating

- `samples/make_sample.py` regenerates the 2-page IEC fixture (requires `reportlab`).
- `dotnet test tests\SWEETs.SwePdfSchematic.Core.Tests` covers tags, geometry, layout/title block, symbols & poles,
  components across pages, link pairing and a full Simulate build after learning mappings.
- Run `tools\SWEETs.SwePdfSchematic.Harness` with a PDF path argument to work on the UI without SWE.
- Logs: `%LocalAppData%\SWEETs\PdfSchematic\logs` (also "Open log folder" in the app).

## Keyboard

Ctrl+O open · Ctrl+Enter confirm · Ctrl+M change symbol · Ctrl+P parts · Del ignore/restore · F fit page · wheel zoom · drag pan
