# CLAUDE.md — Data Sheet MFG Parts Import

> **Part of SWEETs.** Hosted by the SWEETs add-in (`..\src\SWEETs.AddIn`): ribbon **SWEETs › Import Tools › Datasheet Importer**.
> Build and install with `..\SWEETs.sln` and `..\build\Build.ps1`; the stand-alone ribbon tab, installer and `build\` scripts were retired.

This file tells Claude Code everything it needs to work on this project.

---

## What this project is

**Data Sheet MFG Parts Import** is a SOLIDWORKS Electrical COM add-in (.NET Framework 4.8, C# 10, WPF). It reads a
manufacturer data sheet (PDF or photo), extracts part information, shows it in a review dialog, and saves each part —
with its circuits and terminals — to the SOLIDWORKS Electrical manufacturer parts manager under a named library.

Nothing leaves the machine. No cloud calls are made by the add-in itself. The optional Ollama engine runs locally.

---

## Repository layout

```
DataSheetMfgPartsImport.csproj   main project (net48, x64, WPF, C# 10)
Addin.cs                         COM entry point; registry hooks; dependency resolver; ribbon retry
AppIcons.cs                      finds Icons\ next to the installed DLL
AssemblyAttributes.cs            ComVisible(false); assembly-level attributes
EmbeddedLicense.cs               SOLIDWORKS Electrical API license key (plain text in the binary)
ExtractCommand.cs                IEwCommandX impl; opens ImporterWindow via ShowDialog()
Log.cs                           append-only log → %APPDATA%\DataSheetMfgPartsImport\addin.log
RibbonUi.cs                      adds the "Data Sheet MFG Parts Import" ribbon tab + button

Archive/
  ClassTree.cs                   loads archive languages and component class tree

Extraction/
  Types.cs                       IExtractor, Extractors.Create(), ExtractionResult, PartRecord
  RulesExtractor.cs              offline regex/pattern engine (no network); main extraction path
  GridTables.cs                  position-aware price-list / selection-guide table reader
  CircuitDetector.cs             finds terminal marks and infers circuits from PDF text
  CircuitPatterns.cs             compiled regexes used by CircuitDetector
  CategoryRules.cs               maps printed component type to a category name + ClassCandidate
  OllamaExtractor.cs             local AI via Ollama REST API (optional)
  PartConverter.cs               turns ExtractionResult → ExtractedFile for the UI
  PdfText.cs                     PdfPig wrapper; returns List<TextLine> with geometry
  ResultParser.cs                parses AI JSON response → PartRecord
  Prompt.cs                      shared AI prompt text
  ImageUtil.cs                   image resize / EXIF strip for Ollama vision input
  Units.cs                       unit parsing helpers (mm, kg, V, A, Hz …)

Interop/
  EwInterop.cs                   COM declarations generated from EwAPIX.dll type library
                                 DO NOT edit by hand — regenerate with tools/generate_interop.py
  Com.cs                         late-binding helpers; EwApiException

Model/
  Schema.cs                      PartRecord (string key-value store + Flags dict)
  CircuitSpec.cs                 one circuit: Kind, Terminals[], CircuitTypeKeyCode
  ExtractedPart.cs               one part as the UI sees it; holds List<CircuitSpec>
  ExtractedPart.cs               also contains ExtractedFile (the full sheet result)
  FieldValue.cs                  (value, Confidence, source) triple for one field
  PartField.cs                   PartField enum + PartFieldInfo (label, unit, group)
  PartKeys.cs                    maps PartField ↔ PartRecord string key

Services/
  ICatalogService.cs             interface: GetComponentClasses, GetCircuitTypes, FindExisting, Build
  ElectricalCatalogService.cs    live implementation; all SOLIDWORKS Electrical API writes
  CircuitTypeDatabase.cs         reads circuit type key codes from tew_app_data SQL when API is thin

Settings/
  AppSettings.cs                 JSON settings; Targets list; MapTarget(); Load() / Save()

UI/
  ImporterWindow.xaml/.cs        main WPF dialog (SWEETs-style card layout)
  SettingsWindow.xaml/.cs        settings dialog
  Themes/Theme.xaml              all colours, brushes and control styles
  Converters/Converters.cs       IValueConverter implementations incl. ConfidenceToShape/Brush/Tip
  ViewModels/
    Mvvm.cs                      RelayCommand, AsyncRelayCommand, ObservableObject, Set<T>
    ImporterViewModel.cs         top-level VM: file loading, extraction, bulk-class, save
    PartCardViewModel.cs         one part card: fields, circuits, class picker, duplicate handling
    FieldViewModel.cs            one editable field row
    CircuitViewModel.cs          one circuit row; circuit-type picker; KindBrush

Icons/
  import16.png, import32.png, import64.png   ribbon + window + installer icons

installer/
  Setup/Program.cs               self-contained setup EXE (no Inno Setup needed)
  Setup/Setup.csproj
  Setup/app.ico                  built from the three PNGs
  DataSheetMfgPartsImport.iss    optional Inno Setup 6 script
  Build-Setup.ps1                builds add-in then setup EXE → installer\output\
  Build-Installer.ps1            builds add-in then Inno Setup installer

build/
  Build.ps1                      dotnet build → VS MSBuild fallback
  BuildTools.ps1                 shared Invoke-AddinBuild function
  Install.ps1                    xcopy + regasm (PowerShell, elevated)
  Uninstall.ps1                  regasm /unregister + rd (PowerShell, elevated)

Install.cmd                      cmd-only installer  (xcopy + regasm + reg) — no 3rd-party tools
Uninstall.cmd                    cmd-only uninstaller (regasm /unregister + reg delete + rd)

tools/
  generate_interop.py            regenerates Interop/EwInterop.cs from any EwAPIX.dll
                                 Usage: python generate_interop.py path\to\EwAPIX.dll Interop\EwInterop.cs

samples/
  430250400.pdf + .expected.txt          Molex Micro-Fit 3.0 housing
  520-td001_-en-e.pdf + .expected.txt    Allen-Bradley PowerFlex 520 drive catalog
  gladiatormccbgcb100.pdf + .expected.txt AutomationDirect Gladiator MCCB price list

README.md                        end-user documentation
CLAUDE.md                        this file
```

---

## Key identifiers (do not change without re-installing)

| Item | Value |
|---|---|
| Add-in CLSID | `{C7234107-72C4-4EA4-A000-8D998EE70C15}` |
| Add-in ProgId | `DataSheetMfgPartsImport.Addin` |
| Command | `DATASHEETIMPORT` |
| Ribbon command string | `^C^CDATASHEETIMPORT` (in settings.json) |
| Install folder | `C:\Program Files\DataSheetMfgPartsImport` |
| Settings + log | `%APPDATA%\DataSheetMfgPartsImport\` |
| Uninstall registry key | `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\DataSheetMfgPartsImport` |
| SWE add-in keys (HKLM + HKCU) | `SOFTWARE\SolidWorks\SOLIDWORKS Electrical\AddIns\{GUID}` |
| SWE add-in keys (legacy) | `SOFTWARE\Trace Software\Electrical\AddIns\{GUID}` |
| Product GUID (installer) | `{75AF52BB-1286-43AB-BB70-BA55943D1B69}` |

---

## Build

Requirements: Visual Studio 2022 with ".NET desktop development" workload **or** .NET SDK 6+ with .NET Framework 4.8
targeting pack. PdfPig is restored from NuGet automatically.

```powershell
# From the project root — tries dotnet build, falls back to VS MSBuild:
.\build\Build.ps1

# Or open DataSheetMfgPartsImport.sln in Visual Studio → Build → Release / x64
```

Output: `bin\Release\net48\DataSheetMfgPartsImport.dll` (and its dependencies).

The project file already excludes the old WinForms files if they exist on disk from an earlier checkout:
`Archive\ArchiveWriter.cs`, `Archive\CircuitTypes.cs`, `Archive\TerminalPlan.cs`, `UI\MainForm.cs`, `UI\SettingsForm.cs`.

---

## Install / uninstall (no third-party tools)

```cmd
:: Install — right-click → Run as administrator
Install.cmd

:: Install from a custom build output folder
Install.cmd "E:\path\to\bin\Release\net48"

:: Uninstall — right-click → Run as administrator, or via Apps and Features
Uninstall.cmd
```

Both scripts use only `xcopy`, `regasm`, `reg` and `rd` — nothing from NuGet or Inno Setup.

---

## Regenerating the COM interop

When you upgrade SOLIDWORKS Electrical or get a new `EwAPIX.dll`:

```powershell
python tools\generate_interop.py "C:\path\to\EwAPIX.dll" Interop\EwInterop.cs
```

The script reads the MSFT type library embedded in the DLL directly (no Windows COM registration needed). It only
emits the members listed in the `USED` dict near the top of the script. Add a member name there, then re-run, to
expose a new API method.

**Never hand-edit `Interop\EwInterop.cs`** — regenerate it instead.

The DLL the interop was generated from: `EwAPIX.dll` version **2026.3.2.0003**.

---

## Architecture decisions and constraints

### Threading
Every SOLIDWORKS Electrical API call must happen on the SWE UI thread. `ExtractCommand.execute()` is called on that
thread; `ImporterWindow.ShowDialog()` runs modally on the same thread. The extraction itself is dispatched to
`Task.Run()` inside the view model and results are marshalled back via `Dispatcher`.

### No save call before this project
Older versions of this add-in never called `insert()` or `update()` on a part — parts were built in memory and lost.
The correct order, now implemented in `ElectricalCatalogService.Build()`, is:
1. `setReference`, `setManufacturer`, field setters, `setClassID`, `setLibraryCode`
2. `insert()` for new parts, `update()` for existing
3. `addEwManufacturerPartCircuit` × N, `addEwManufacturerPartTerminal` × M per circuit, then `update()` again
4. Read the part back with `findByManufacturerAndReference` to confirm it was kept

### Circuit types
On SOLIDWORKS Electrical 2026, `getEwCircuitTypeArray` returns integer IDs, not `IEwCircuitTypeX` objects. Each ID
is resolved with `getEwCircuitTypeByID`. Where descriptions are missing, `CircuitTypeDatabase` reads them directly
from `[tew_app_data].[tew].[tew_element]` and `[tew_translatedtext]` using the connection string the API provides.

### Weight units
SOLIDWORKS Electrical stores weight in **grams**. The extraction engines report it in **kilograms** (from the data
sheet). `PartConverter` multiplies by 1000 before the value reaches the UI or the writer.

### Interop dual-dispatch
`ElectricalCatalogService` calls some methods via the typed `IEw*X` interface and falls back to late-binding through
`Com.CallWithError` / `Com.CallInt` when the array element is a raw COM object rather than a typed one. This handles
both Electrical versions without a version check.

### Colour-blind accessible confidence indicators
Field confidence is shown as a Unicode shape + colour (not colour alone):

| Confidence | Shape | Colour |
|---|---|---|
| High (found with label) | ● | Teal `#0E7490` |
| Medium (inferred) | ◐ | Dark amber `#92400E` |
| Low (guess) | ▲ | Violet `#7C3AED` |
| Edited | ✎ | Blue `#1F5FBF` |

Colours avoid the red-green axis. All dots also carry a plain-English tooltip for screen readers.

---

## Settings file (`%APPDATA%\DataSheetMfgPartsImport\settings.json`)

Key fields:

```jsonc
{
  "Engine": "rules",                      // "rules" or "ollama"
  "OllamaUrl": "http://localhost:11434",
  "OllamaModel": "qwen2.5vl:7b",
  "LibraryCode": "DSI",                   // library in manufacturer parts manager; created if missing
  "Language": "",                         // archive language code; blank = auto
  "CopyDatasheetToFolder": "",            // copy source PDF here and link the copy
  "ImportNoteSlot": 3,                    // user data slot for "Imported from …" note; 0 = off
  "CircuitTypeKeyCodes": {               // pin a circuit kind to a circuit type key code
    "Coil": "COIL",
    "AuxNO": "NO"
  },
  "Mapping": {                           // PartRecord key → archive field target
    "description": "description",        // targets: description, commercialRef, serie, stock,
    "series": "serie",                   //   article, supplier, useVoltage, useFrequency,
    "supplierRef": "commercialRef",      //   controlVoltage, controlFrequency, width, height,
    "ratedVoltage": "useVoltage",        //   depth, weight, value1–7, user1–20, none
    "frequency": "useFrequency",
    "coilVoltage": "controlVoltage",
    "width": "width",
    "height": "height",
    "depth": "depth",
    "weight": "weight",
    "ratedCurrent": "user1",
    "ratedPower": "user2"
  },
  "RibbonCommand": "^C^CDATASHEETIMPORT",
  "LicenseCode": ""                       // overrides the built-in key; leave blank normally
}
```

---

## Extraction engines

### Offline rules (`RulesExtractor.cs`)
- Reads PDFs with a selectable text layer via PdfPig
- Two table layouts: **ratings table** (Allen-Bradley style header detection, up to 300 variants/sheet) and
  **price-list / selection-guide** (`GridTables.cs`, position-aware column grouping)
- Patterns for voltage, current, power, frequency, dimensions, weight, IP rating, temperature, EAN/GTIN, etc.
- 50+ manufacturer name aliases; category from weighted keyword scoring
- Tested against `samples/` — see `.expected.txt` files for expected output

### Ollama (`OllamaExtractor.cs`)
- POST to `http://localhost:11434/api/chat`, `format:"json"`, temperature 0
- Sends PDF text + base64 image blobs (vision models only need images)
- Max 30,000 characters of PDF text; max 24 parts per sheet
- Recommended model: `qwen2.5vl:7b` (reads photos and scanned PDFs)
- `ListModelsAsync()` is used by the Settings dialog "Test and list models" button

---

## Running the tests (manual)

There is no automated test runner. To verify an extraction change:

```powershell
# From project root
python tools\generate_interop.py EwAPIX.dll Interop\EwInterop.cs   # if DLL changed
.\build\Build.ps1
.\Install.cmd bin\Release\net48
# Open SOLIDWORKS Electrical, run DATASHEETIMPORT, drop samples\430250400.pdf
# Compare result to samples\430250400.expected.txt
```

The `.expected.txt` files list the specific field values and flags the rules engine should produce for each sample
sheet. If you change `RulesExtractor.cs` or `GridTables.cs`, re-check all three samples.

---

## Common tasks for Claude Code

### Add a new extracted field
1. Add the enum value to `Model\PartField.cs` (PartField enum + PartFieldInfo.All entry with label, unit, group)
2. Add the mapping pair to `Model\PartKeys.cs` (PartField ↔ PartRecord string key)
3. Add extraction logic to `Extraction\RulesExtractor.cs` (rules) and update `Extraction\Prompt.cs` (AI)
4. Add the target to `Settings\AppSettings.cs` Targets.Build() if it needs a new archive setter
5. Add the setter case to `Services\ElectricalCatalogService.Apply()` if it's a new archive target

### Add a new API method
1. Add the method name to the `USED` dict in `tools\generate_interop.py` under its interface
2. Run `python tools\generate_interop.py EwAPIX.dll Interop\EwInterop.cs`
3. Use the new member in the relevant service or writer class

### Change the library name default
Edit `Settings\AppSettings.cs`: `LibraryCode = "DSI"` in `Defaults()`.

### Change the import note slot
Edit `Settings\AppSettings.cs`: `ImportNoteSlot = 3` in `Defaults()`.

### Add a new circuit kind
1. Add the enum value to `Model\CircuitSpec.cs` (CircuitKind enum + KindLabel())
2. Add keyword hints to `UI\ViewModels\CircuitViewModel.cs` SuggestType() keywords dict
3. Add detection logic to `Extraction\CircuitDetector.cs`

### Upgrade SOLIDWORKS Electrical
1. Copy the new `EwAPIX.dll` somewhere accessible
2. Run `python tools\generate_interop.py path\to\EwAPIX.dll Interop\EwInterop.cs`
3. Build and install; check `addin.log` for any new API errors

---

## Known gaps and pending work

- **No OCR**: scanned PDFs and photos need the Ollama engine with a vision model
- **Terminal designations**: only standard IEC marks are written (1, 2, A1, A2, 1/L1 …); data-sheet-specific
  designations from the AI engine are not written
- **Coil circuit**: not separately distinguished from contact circuits for relay/contactor parts; a relay saved from a
  data sheet may need its coil circuit type corrected by hand in the archive
- **Catalog size**: the AI engine reads at most 24 parts and 30,000 characters per sheet
- **Circuit detection from text**: only used when the sheet has ≤ 12 parts (to avoid cross-contamination in catalogs)
- **Build not yet verified**: the project has not been compiled and run end-to-end since the WPF migration; expect one
  or two compile fixes on first build
- **`setText` field**: the terminal number is set via `IEwManufacturerPartTerminalX.setText()`; the archive field
  this populates has not been confirmed in the UI

---

## Dependencies

| Package / Assembly | Purpose |
|---|---|
| `PdfPig` 0.1.8 (NuGet) | PDF text + geometry extraction |
| `System.Web.Extensions` | JSON (JavaScriptSerializer) for settings and Ollama |
| `System.Web`, `System.Configuration` | Required by XAML compiler when referencing System.Web.Extensions |
| `System.Windows.Forms` | FolderBrowserDialog in SettingsWindow; WinForms message boxes in ExtractCommand |
| `System.Net.Http` | Ollama HTTP client |
| `System.Data` | SQL connection in CircuitTypeDatabase |
| `System.Drawing` | Icon helper |
| WPF (UseWPF=true) | ImporterWindow, SettingsWindow, Theme, ViewModels |
| .NET Framework 4.8 | Required by SOLIDWORKS Electrical host process |

---

## EwAPIX.dll version

The COM interop was generated from `EwAPIX.dll` **2026.3.2.0003**. If you work with a different version, regenerate
`Interop\EwInterop.cs` using `tools\generate_interop.py` before making API calls.
