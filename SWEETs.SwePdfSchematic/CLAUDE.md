# SWEETs.SwePdfSchematic — notes for Claude Code

> **Part of SWEETs.** This tool is a class library hosted by the SWEETs add-in (`..\src\SWEETs.AddIn`): ribbon
> **SWEETs › Import Tools › PDF to Schematic**. Build and install it with `..\SWEETs.sln` and `..\build\Build.ps1`; its own
> stand-alone build scripts, registration and ribbon tab were retired. Data lives under `SWEETs\PdfSchematic`
> (settings in %AppData%, logs in %LocalAppData%, learned mappings and symbol catalog in %ProgramData%); data from the former `TEW\` folders is copied there the first time SWEETs starts.
> Sections below that describe the stand-alone add-in are kept for history.

SOLIDWORKS Electrical 2026 add-in (ribbon: **SWEETs** › *Import Tools* › *PDF to Schematic*) that reads a vector PDF
schematic, lets the user review the recognised symbols, and builds a new SWE project: scheme pages, library symbols,
wires, components (with classification), terminal strips/terminals, manufacturer parts (catalog parts created when
missing, circuits + terminal texts), cables/rails/ducts/enclosure items, wire style "Imported" and manual wire numbers.
State: working end to end against the fixture PDF; last open item is verifying wire-number matching in a real build.

## Layout

| Path | What |
|---|---|
| `src/SWEETs.SwePdfSchematic.Core` | netstandard2.0, **no SWE dependency**. PDF vector reading (PdfPig), recognition, BOM reading, mapping store, build plan + runner. Unit-tested. |
| `src/SWEETs.SwePdfSchematic.AddIn` | net48 x64 WPF + COM add-in. EwAPI bridge (`Interop/`), entry points (`Entry/`), UI (`UI/`). |
| `tests/SWEETs.SwePdfSchematic.Core.Tests` | xUnit, net8.0. `Fixtures/swe-ansi-hyperlink-test.pdf` is a real SWE 2026 ANSI export (project PRJ-10011). |
| `tools/SWEETs.SwePdfSchematic.Harness` | Runs the UI without SWE (analysis, review, Simulate). |
| `build/Build.ps1` | Tests → Release build → staging → Inno Setup installer (`-Packager PackageForge` optional, `-SkipTests`, `-NoPackage`). |
| `build/Register-Dev.ps1` | Dev registration (elevated, SWE closed); `-Unregister`. |
| `samples/` | Small IEC sample PDF + generator. |

## Pipeline (Core)

`VectorPdfReader` → `LinetypeNormalizer` (thick-line rects → centre lines, dash pieces → dashed segments, region boxes)
→ `SchematicRecognizer` (per page: `PageLayoutAnalyzer` frame/title block, `WireExtractor` nets, `SymbolExtractor`
clusters/pins/mechanical links/centre lines, `SymbolClassifier`, `TagParser`, pin labels, pole grouping & combine)
→ `ReportReader` + `BomReader` (parts tables of report pages, merged per mark) → `CrossReferenceResolver`
(components per tag, terminal numbering, off-page link pairing) → `ImportDocument`.
Review edits the document; `BuildPlanFactory.Create` → `BuildPlan` (sheet coordinates, units, components incl.
strip/terminal roles, parts, extras) → `BuildRunner.RunAsync(plan, ISchematicTarget)`.

`ISchematicTarget` (Core/Build/BuildRunner.cs) is the only seam to SWE: `SweTarget` (AddIn) does the real work,
`DryRunTarget` (Core) logs operations for Simulate/tests. Add new build features as an interface member + both impls.

## EwAPI bridge (AddIn/Interop)

- Every EwAPI member used is declared once in `SweContract.cs` (interface + candidate names, optional flag, ✓ = verified
  against the SWE 2026 type library). Call through `SweApi.Call/TryCall(target, C.Member, args)`; trailing
  `out EwErrorCode` is appended and checked automatically; enums via `SweApi.EnumValue("EwX", "kValue")`.
- References SWE's own `bin\Interop.EwAPI.dll` (`Private=false`, never copy it next to the add-in: duplicate EwAPI
  types break every cast). `lib/` (git-ignored) may hold `EwAPIX.dll` for building without SWE installed.
- EwAPI is apartment-bound: call only on SWE's UI thread (the build yields with `Dispatcher.Yield`).
- `SweSymbolLibrary` (library symbols, classes, measurement units, previews), `SwePartCatalog` (manufacturer parts and
  cable references: find by manufacturer+reference, else reference; create when missing), `SweTemplates`.
- No API for: per-line wire marks before numbering, placing origin-destination arrows, setting a project cable's
  reference → written to `%LocalAppData%\SWEETs\PdfSchematic\logs\build-*-deferred.csv`.

## Add-in registration (must stay unique if combined)

- Add-in COM class `SweAddIn` GUID `04fdfd9a-a04c-4264-824e-5dcf03a2d73d`, ProgId `SWEETs.SwePdfSchematic.AddIn`.
- Command class `ImportPdfCommand` GUID `3a59357d-16a4-4ca4-ae85-8e94c063a6bf`, command name `SweetsImportPdfSchematicCommand`.
- Discovery keys (HKLM): `SOFTWARE\Trace Software\Electrical\Addins\{GUID}` and
  `SOFTWARE\SolidWorks\SOLIDWORKS Electrical\AddIns\{GUID}`. The old key `SOFTWARE\Trace Software\SOLIDWORKS Electrical\Addins\{GUID}`
  is always deleted — a duplicate registration made SWE load the add-in twice and crash on exit. `SweAddIn` also has a
  single-instance guard.
- The SWE API key is a constant in `Entry/SweAddIn.cs` (`SweApiKey`). Treat it as a secret when sharing the code.
- Ribbon: tab "SWEETs", group "Import Tools", built by the SWEETs host (src\SWEETs.AddIn).

## Data on the user's machine

- Settings: `%AppData%\SWEETs\PdfSchematic\settings.json` (BuildOptions incl. units, grid, parts switches).
- Learned symbol mappings: `%ProgramData%\SWEETs\PdfSchematic\mappings.json`; extra names: `symbol-catalog.txt` beside it.
- Logs: `%LocalAppData%\SWEETs\PdfSchematic\logs\yyyyMMdd.log` (build log, per-symbol pin diagnostics, parts, wire numbers).

## Build, test, run

```powershell
dotnet test tests\SWEETs.SwePdfSchematic.Core.Tests -c Release          # Core only, no SWE needed
.\build\Build.ps1 -InnoSetupCompiler "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
.\build\Register-Dev.ps1                                              # dev loop instead of the installer
```

Conventions: C# 10, nullable off, no implicit usings. Core must not reference WPF or EwAPI. Keep recognition rules
data-driven (`Rules/default-rules.json`, `RecognitionOptions`). Add a fixture test for every recognition change.
Units: Core works in mm; `UnitConversion.PerMm` converts to inch templates at plan time.

## Combining with another app

- Keep `Core` as its own project; the other app can reference it for PDF/BOM reading without the UI.
- If both apps become one add-in: keep one `SweAddIn` (one GUID, one discovery registration), register each tool as its
  own `EwCommandX` class/GUID/command name, and share `SweApi`/`SweContract` instead of duplicating them.
- If they stay separate add-ins: different add-in and command GUIDs, the same ribbon tab name, and never ship
  `Interop.EwAPI.dll` in either output folder.
- Installer: `Build.ps1` writes the COM and SWE registry entries; merge those blocks rather than running two installers
  that register the same keys.

## Open items

- Wire numbers: confirm in a real build that wires are matched to PDF nets (see "Wire numbers" line in the log);
  matching uses component circuit + terminal at both ends, falling back to unique component pairs.
- Project cable reference cannot be set through the API (linked via a project manufacturer part when SWE accepts it).
- Terminal numbers that are not plain digits ("PE") do not normalise across "X1 PE"/"X1.PE" in BOM matching.
