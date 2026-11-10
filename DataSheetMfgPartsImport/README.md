# Data Sheet MFG Parts Import: SOLIDWORKS Electrical add-in

> **Part of SWEETs.** Hosted by the SWEETs add-in (`..\src\SWEETs.AddIn`): ribbon **SWEETs › Import Tools › Datasheet Importer**.
> Build and install with `..\SWEETs.sln` and `..\build\Build.ps1`; the stand-alone ribbon tab, installer and `build\` scripts were retired.

Reads a manufacturer data sheet, shows each part on a card for review, and saves the parts, with their circuits and
terminals, to the SOLIDWORKS Electrical **manufacturer parts manager** under a library of your choice (default `DSI`).
No cloud service is used.

The importer window, the circuit model and the save sequence follow the *SWEETs Datasheet Importer* project. The reading
engines (offline rules with position-aware table reading, and Ollama), the library creation and the field mapping
table are this project's own.

## Status: not yet compiled or run

The COM layer was generated from the type library inside the `EwAPIX.dll` you supplied (2026.3.2.0003), so interface
IDs, vtable slots, method names and signatures come from your install. The rest is ordinary C# and WPF that has not been
compiled yet; expect a few compile fixes. The behaviours under "First run" must be confirmed on a test archive before
you trust it with production data.

## Two ways to read a data sheet (Settings > Reading engine)

| | Offline rules | Local AI model (Ollama) |
|---|---|---|
| Needs | nothing else | [Ollama](https://ollama.com) running, plus a downloaded model |
| Reads | PDFs with selectable text | PDFs with text, and photos (with a vision model) |
| Accuracy | good on labelled spec tables, weak on unusual layouts | better on varied layouts; depends on the model |
| Data leaves the PC | no | no |
| Speed | seconds | seconds to minutes, much faster with a GPU |

**Offline rules** looks for labels and units (rated current, coil voltage, IP rating, dimensions, weight, order
number and so on, in English, German, French and Italian), recognises about 50 common manufacturers, and guesses
the component type from keywords. It never guesses silently: anything found more than once, taken from a title,
or inferred from layout is highlighted for review. Ordering tables: when a sheet lists one part per row, each row becomes a part. Rows in
ratings tables (catalog number, normal and heavy duty HP and kW, output current, input range, watts loss, frame size)
are read in full, including a frame-size table of dimensions and weight. Price lists and selection guides (a "Part Number" header with a Price or Description column) are read by position: the
header's x-positions define the columns, wrapped descriptions are kept with their row, merged cells (shared voltages,
frequency, interrupt ratings) are treated as table-wide values, and a specification table with one column per pole count
gives each variant its weight and insulation ratings. Other table layouts give one part per row that shares the sheet's
general values, with the variant-specific columns left for you to check. Up to 300 parts per sheet.

**Local AI model** sends the sheet's text (and any photos) to Ollama on `http://localhost:11434`. Setup:

1. Install Ollama, then in a command window pull a model, for example `ollama pull qwen2.5vl:7b`.
   Any vision-capable model works for photos; a text-only model is enough for PDFs with text.
2. In the add-in, open Settings, choose **Local AI model (Ollama)**, and click **Test and list models**.

Model names change over time; use whichever vision model Ollama currently recommends.

## Requirements

- Windows x64, SOLIDWORKS Electrical 2026 (the DLL inspected was 2026.3.2.0003)
- To build: Visual Studio 2022 with the ".NET desktop development" workload (or the .NET SDK 6+ and the .NET Framework
  4.8 targeting pack), and internet access once to restore the PdfPig package. The importer window is WPF; the build
  scripts try `dotnet build` and fall back to Visual Studio's MSBuild.
- Nothing to enter for the SOLIDWORKS Electrical API license: the code is built into the add-in (`EmbeddedLicense.cs`),
  so it is visible to anyone who has the add-in file. To use a different code, put it in `settings.json` as
  `LicenseCode`; a non-empty value there overrides the built-in one.

## Build and install

**Build** (pick one):

```cmd
build\Build.ps1               :: PowerShell — tries dotnet build, falls back to VS MSBuild
```
Or open `DataSheetMfgPartsImport.sln` in Visual Studio 2022 and build in Release / x64.

**Install** (no third-party software needed — cmd prompt only):

```cmd
:: Right-click Install.cmd → Run as administrator
Install.cmd
```

`Install.cmd` uses only what Windows ships with — `xcopy`, `regasm` and `reg`. It:
- copies the built files from `bin\Release\net48` to `C:\Program Files\DataSheetMfgPartsImport`
- registers the COM add-in with `regasm /codebase`
- writes an Apps and Features entry (so Add/Remove Programs works)
- copies `Uninstall.cmd` into the install folder

You can also pass the source folder as an argument if your output path differs:

```cmd
Install.cmd "E:\ai built apps\DataSheetMfgPartsImport\bin\Release\net48"
```

**Uninstall:**

```cmd
:: From Apps and Features, or right-click → Run as administrator:
Uninstall.cmd
```

Or from Apps and Features — the entry points at the copy in the install folder.
It offers to also remove settings and the log from `%APPDATA%\DataSheetMfgPartsImport`.

**Alternatives** (if you prefer a packaged installer):
`build\Build.ps1` + `build\Install.ps1` (PowerShell), or the Inno Setup script in `installer\`.

## Use

1. Start SOLIDWORKS Electrical. The add-in registers the command `DATASHEETIMPORT` and adds a **Data Sheet MFG Parts
   Import** ribbon tab.
2. Click **Add part from data sheet**. The window has four steps: add the data sheet, extract, review, save.
3. Drop one or more data sheets on the window (or **Browse**, Ctrl+O): PDFs, or photos and screenshots when the local
   AI engine is on.
4. Review each card. The dot beside a value shows how it was found: **green** with a label, **amber** inferred,
   **orange** a guess, **blue** edited by you, grey not found. Hover a value to see where it came from.
   - Pick or **Confirm** the class. It is suggested from the component type; saving stays disabled until you confirm.
   - Check the circuits (below).
   - A part that already exists shows **Skip / Overwrite**.
   - In a catalog the main product family starts selected; accessories start unselected.
5. **Save to Library** (Ctrl+Enter). Each card then shows Saved, Updated, Skipped or Failed with any warnings.
6. **Settings** (in the window header): reading engine, library, language, where each value is saved, and more.

## Circuits and terminals

Each card lists the part's circuits as rows: a **kind**, the **terminal marks**, and the SOLIDWORKS Electrical
**circuit type**. Rows can be edited, removed or added.

| Part | Circuits |
|---|---|
| Contactor, relay | coil (A1, A2), main poles (1/L1-2/T1 and so on), NO / NC / changeover contacts |
| Breaker | one main pole per pole (1-2, 3-4, 5-6) |
| Connector | one pin per circuit or position, one terminal each |
| Drive | power input (R/L1, S/L2, T/L3 or two for single phase) and motor output (U/T1, V/T2, W/T3) |

Terminal marks printed in the data sheet (A1-A2, 13-14, 11-12-14, 1/L1) are used as written when the sheet has
twelve parts or fewer; otherwise, and when the sheet gives none, standard IEC markings are derived from the pole count,
the contact configuration and the category. Accessories get no circuits.

**Circuit type.** The type for each row is chosen, in order: the key code pinned for that kind in `settings.json`
(`CircuitTypeKeyCodes`, for example `{"Coil":"COIL","AuxNO":"NO"}`); the best match of the type's name against the
kind (coil, power, NO, NC, changeover, pin, input, motor...); **Miscellaneous**. The box is editable: pick from the list
or type a key code. A card with a circuit that has no type cannot be saved.

**Reading the circuit types.** On SOLIDWORKS Electrical 2026 the API returns circuit type IDs, not objects. Each ID is
resolved with `getEwCircuitTypeByID`. Where the key code or name is missing, they are read, read-only, from
`[tew_app_data].[tew].[tew_element]` (`ele_id`, `ele_keycode`) and `[tew_translatedtext]` (`tra_objectid` = `ele_id`),
using the connection details the API itself provides. The window's activity list and `addin.log` say how many types
were found.

## How a part is saved

1. Reference, manufacturer and part type; then each value through the table in Settings (below).
2. Class: `setClassID` with the class's ID, falling back to `setClassNodeID`.
3. Library: the library named in Settings is found or created as a manufacturer part library (leave blank for none).
4. `insert()` for a new part, `update()` for an existing one (Overwrite replaces its circuits).
5. Circuits and terminals (`addEwManufacturerPartCircuit`, `addEwManufacturerPartTerminal`, `setText`), then `update()`.
6. The part is read back, and the circuit count and library are checked.

## Where values are saved

Settings > **Where each value is saved** sets the destination of each value. Defaults:

| Value | Default destination |
|---|---|
| Manufacturer, reference, component type | always: manufacturer, reference (component type is used to suggest the class) |
| Description | Description (`setDescription(lang, text)`) |
| Series | Series |
| Supplier reference | Commercial reference |
| Use voltage, use frequency, control voltage | Use voltage, use frequency, control voltage |
| Width, height, depth | Width, height, depth in millimetres |
| Weight | Weight, in **grams** |
| Rated current, rated power | User data 1 and 2 |
| Poles, contacts, IP rating, temperature, EAN, mounting, price, other specifications | **Not written** (map to a Value 1 to 7 or User data 1 to 20 if you want them) |

An "Imported from ...pdf (date)" note is written to the user data number in Settings (default 3; 0 turns it off). The
data sheet is linked as the part's data sheet; with a folder set in Settings the file is copied there and the copy is
linked.

## First run: confirm these on a test archive

1. **Compiles.** Run `installer\Build-Setup.ps1` and fix any compiler errors.
2. **The window opens** from the ribbon button. If the button does nothing, change `RibbonCommand` in `settings.json`
   to `DATASHEETIMPORT` or `_DATASHEETIMPORT` and restart.
3. **A new part is actually stored.** Save one part, close and reopen the project, and find it in the manufacturer
   parts manager. If it is missing or the card shows a warning, send me the message.
4. **The library.** The part should be under the library you named; a library created by the add-in should look like
   your other manufacturer part libraries (name, description, version).
5. **Class.** The add-in calls `setClassID(IEwClassX.getID())` and falls back to `setClassNodeID`. Confirm the part lands
   in the class you picked.
6. **Circuits and terminals.** Open the part: the circuits should have the types shown on the card, and each terminal
   should carry its mark in the field you expect (`setText`).
7. **Circuit types.** The activity list should report the number of circuit types found. If it reports none, the add-in
   logs why; send me the log lines that start with `getEwCircuitTypeArray` or `Circuit types`.
8. **Units.** Dimensions are written in millimetres and weight in grams. Check one part.
9. **PdfPig loads inside SOLIDWORKS.** If reading fails with "could not load file or assembly", send me the message.
10. **Rules accuracy.** Tuned on three real sheets in `samples/` (a Molex connector housing, a PowerFlex drive catalog
    and an AutomationDirect Gladiator MCCB price list), each with expected values in a `.expected.txt` file. Send me
    sheets where values are missed or wrong.

Log: `%APPDATA%\DataSheetMfgPartsImport\addin.log`. Settings: `%APPDATA%\DataSheetMfgPartsImport\settings.json`.

## Known gaps

- No OCR: scanned PDFs and photos need the Ollama engine with a vision model.
- Rules engine: price lists and the ratings-table layout are read column by column; other layouts give one part per
  row, or are missed.
- The AI engine reads at most 24 parts and 30,000 characters per sheet, so it cannot cover long catalogs.
- Terminal marks are only taken from the sheet when it has twelve parts or fewer.
- Coil and contact circuits are derived from the text of the sheet; check them on relays and contactors.
- Terminal designations the AI engine reads from a sheet are no longer shown or written; the circuit rows carry the terminal marks.

## Privacy

Everything runs on this PC. The offline rules read the file locally. With Ollama, the sheet goes only to the
Ollama address in Settings (default: this machine). Change that address to a remote server and the sheet goes there.

## Regenerating the COM layer for another release

```powershell
python .\tools\generate_interop.py "C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS Electrical\EwAPIX.dll" .\Interop\EwInterop.cs
```

## Names and icons

The product is **Data Sheet MFG Parts Import**. Its technical names all follow from that: the project and assembly
`DataSheetMfgPartsImport`, the install folder `C:\Program Files\DataSheetMfgPartsImport`, the settings and log folder
`%APPDATA%\DataSheetMfgPartsImport`, the ribbon command `DATASHEETIMPORT`, and the setup file
`DataSheetMfgPartsImport-Setup-1.0.0.exe`. The add-in and the setup have their own COM and product IDs, so this version
installs cleanly next to nothing; uninstall any earlier build first, and delete the old `%APPDATA%\PartsArchiveAddin`
folder if you want its settings gone.

The icons are in `Icons\` (`import16.png`, `import32.png`, `import64.png`). The ribbon button uses all three sizes, the
windows use the 32 pixel one, and `installer\Setup\app.ico` (built from the same three files) is the icon of the setup
program and of the Apps and Features entry. They are installed to `...\DataSheetMfgPartsImport\Icons`.

## Layout

```
Addin.cs                  COM entry point, registry registration, dependency resolver
ExtractCommand.cs         the DATASHEETIMPORT command; opens the importer window
RibbonUi.cs               the ribbon tab and button
Interop/                  EwInterop.cs (generated COM declarations), Com.cs (helpers)
Services/                 ICatalogService, ElectricalCatalogService (all writes), CircuitTypeDatabase
Archive/ClassTree.cs      archive languages and component classes
Extraction/               RulesExtractor, GridTables, OllamaExtractor, PdfText, CircuitDetector, CategoryRules, PartConverter
Model/                    PartRecord and fields, the importer's part, field and circuit models
Settings/AppSettings.cs   engine, Ollama, library, language, value destinations, pinned circuit types
UI/                       ImporterWindow, SettingsWindow, Themes/Theme.xaml, ViewModels/, Converters/
Icons/                    ribbon, window and installer icons (import16, import32, import64)
AppIcons.cs               finds the installed icons
EmbeddedLicense.cs        the built-in SOLIDWORKS Electrical API license code
installer/Setup/          self-contained setup program (Build-Setup.ps1)
installer/*.iss           optional Inno Setup script
tools/generate_interop.py regenerates Interop/EwInterop.cs from any EwAPIX.dll
samples/                  data sheets used to tune the rules, with expected results
```
