# SWEETs Line Style Editor: project notes for Claude Code

> **Part of SWEETs.** This tool is a class library hosted by the SWEETs add-in (`..\src\SWEETs.AddIn`): ribbon
> **SWEETs › Drawing Tools › Line Style Editor**. Build and install it with `..\SWEETs.sln` and `..\build\Build.ps1`; its own
> stand-alone build scripts, registration and ribbon tab were retired. Data lives under `SWEETs\LineStyles`
> (settings in %AppData%, logs and Support-file backups in %LocalAppData%); data from the former `TEW\` folders is copied there the first time SWEETs starts.
> Sections below that describe the stand-alone add-in are kept for history.

SOLIDWORKS Electrical 2026 add-in (C#, .NET Framework 4.8, x64, WPF). It adds **SWEETs › Drawing Tools › Line Style
Editor**, a graphical editor for SWE's line styles and their embedded shapes. Owner: Charles. Started 2026-11-06.

## Ground rules from the owner
- Use only verified information. Ask when something is unclear; never use made-up data.
- Visual Studio project, with a PowerShell build that produces an Inno Setup 6 installer (`build\Build.ps1`). PackageForge is optional.
- Minimal SaaS/enterprise UI (WPF, `UI\Theme.xaml`, copied from SWEETs Drawing Parameters). Write the UI as the
  application, with no assistant-style filler text.
- The SWE API key is hard-coded in `Entry\SweAddIn.cs`, as the owner asked.
- Decisions made on 2026-11-06:
  - Save in place to Program Files\…\Support, with a backup and UAC elevation.
  - Shapes are made in a grid vector editor or imported from DXF.
  - Descriptions are edited in a full 14-language grid.
  - The stack and build match the other SWEETs add-ins.

## Current state (version 1.0.0)
- Written in a cloud session that had no .NET compiler, so it has **not been compiled yet**. First step: run
  `tools\build-check.cmd` and fix any compile errors (they should be minor).
- Not yet run inside SOLIDWORKS Electrical.

## What was learned (verified against SWE 2026 files on the owner's machine)
- The `Styles` folder holds UI skin DLLs only. Line styles are in `Support\`: `linestyle.xlin` + `linestyle.xsd`,
  and the shapes are `ltypeshp.shp` / `ltypeshp.shx`. `ltypeshp.shp` and `linestyle.xsd` have the read-only attribute.
- `linestyle.xlin`:
  - Layout: UTF-8 without BOM, CRLF line endings, tab indentation.
  - Structure: `SECTIONS / EW_HEADER(VERSION 1.0.0.0) / lineStyles / lineStyle(pattern, name, ascii, description/LANGUAGE xml:lang)`.
  - 55 styles: 38 simple, 5 with shapes, 12 with text. 45 have all 14 languages; 10 (GROUND … ELECTRO_SIG1) are English only.
  - Pattern text contains literal `"` characters: escape only `& < >`.
  - The writer in `Core\XlinFile.cs` reproduces the file byte for byte (48,023 bytes).
- Patterns use AutoCAD linetype syntax:
  - Lengths: positive = dash, negative = gap, 0 = dot.
  - Shapes: `[NAME,ltypeshp.shx,s=,r=|a=,x=,y=]`. Text: `["TXT",STANDARD,S=,R=,X=,Y=]`.
  - Parameter key case varies (shapes use lower case, text upper case); it is kept as found.
- `ltypeshp.shx` uses the "-86 shapes 1.0" layout with the signature `TraceCAD-86 shapes 1.0\r\n\x1A` (not AutoCAD-86):
  - Header: uint16 first number, last number, count.
  - Index: (number, length) for each shape.
  - Data: name + NUL + bytes for each shape, then `EOF`.
  - Compiling the shipped `.shp` reproduces the shipped `.shx` byte for byte (131 bytes).
- How `.shp` values encode:
  - A leading `0` means hex.
  - Negative values are two's complement, **except** the octant byte of codes 10 and 11, where the sign sets bit 7
    (`-044` → `0xC4`; BAT proves this).
- Python reference used for these checks: `ShpFile`, `ShxFile`, the interpreter and the encoder were prototyped in Python first.
  Every shipped style was rendered and matched the standard AutoCAD appearance (fenceline circle and square, tracks,
  batting, zigzag, HW / GAS / GND / COM text).

## Layout
- `src\SWEETs.SweLineStyles.AddIn\`
  - `Core\`: no WPF or EwAPI, so the tests link these files.
    - `Geometry` (Pt, LinePrim, ArcPrim), `LinePattern` (parse/format/rules/ASCII), `XlinFile` (+ `Languages`, `LineStyle`).
    - `ShapeDefinition`, `ShpFile` (parse/write .shp), `ShxFile` (compile/read .shx).
    - `ShapeInterpreter` (bytes → geometry, all codes 0-14), `ShapeEncoder` (geometry → bytes), `DxfReader`.
  - `Services\LineStyleLibrary.cs`: locates Support, loads, saves (backup, staging, elevated PowerShell copy, verify), restores.
  - `Entry\`: add-in, ribbon and command (pattern and API key from SWEETs Drawing Parameters).
  - `Interop\`: `SweApi` (reflection bridge) and `SweContract` (command manager and ribbon only).
  - `UI\`:
    - `MainWindow` (Line styles and Shapes tabs), `ShapeEditorWindow`, `BackupsWindow`.
    - `Controls\`: `LinePreview`, `ShapePreview`, `ShapeCanvas`, `ShapeCatalog`.
    - `ViewModels\`.
- `tools\SWEETs.LineStyles.Tests\`: format checks against `testdata\` (copies of SWE 2026's Support files) and the installed folder.
- `build\Build.ps1`, `build\Register-Dev.ps1`. GUIDs: add-in d854378b-…, command 66f5514f-…, upgrade code AC257B4A-….

## Open questions to verify in SWE
- When SWE reloads `linestyle.xlin` / `ltypeshp.shx` (at startup only?), and whether existing drawings keep the old definitions.
- Whether SWE accepts the A= (absolute rotation) parameter and patterns longer than 12 values. The editor allows both and warns about the second.

## Related projects (same machine)
`E:\AI Applicaations\SWEETs App\SWEETs.SweDrawingParameters` provided the add-in, ribbon, theme and build patterns.
The Electrical API 2019 HTML help is in `E:\AI Applicaations\Electrical API-2019.0.0-ActiveX\html`.
