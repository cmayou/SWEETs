# SWEETs Line Style Editor for SOLIDWORKS Electrical 2026

> **Part of SWEETs.** This tool is a class library hosted by the SWEETs add-in (`..\src\SWEETs.AddIn`): ribbon
> **SWEETs › Drawing Tools › Line Style Editor**. Build and install it with `..\SWEETs.sln` and `..\build\Build.ps1`; its own
> stand-alone build scripts, registration and ribbon tab were retired. Data lives under `SWEETs\LineStyles`
> (settings in %AppData%, logs and Support-file backups in %LocalAppData%); data from the former `TEW\` folders is copied there the first time SWEETs starts.
> Sections below that describe the stand-alone add-in are kept for history.

SOLIDWORKS Electrical add-in that edits the application's **line styles** and the **shapes** they embed, in a
graphical editor. Open it from **SWEETs › Drawing Tools › Line Style Editor**.

## What it edits

All three files are in `C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS Electrical\Support`:

| File | Content |
|---|---|
| `linestyle.xlin` | The line styles (XML, schema `linestyle.xsd`): name, pattern, ASCII preview, descriptions in up to 14 languages. SWE 2026 ships 55. |
| `ltypeshp.shp` | Source of the shapes used inside patterns, e.g. `[CIRC1,ltypeshp.shx,x=-.1,s=.1]`. SWE 2026 ships 6 (TRACK1, ZIG, BOX, CIRC1, BAT, AMZIGZAG). |
| `ltypeshp.shx` | The compiled shapes, in SWE's `TraceCAD-86 shapes 1.0` format. The editor compiles it from the `.shp` on every save. |

## Using it

**Line styles tab**
- The left list shows every style with a live preview. Search by name, description or pattern, or filter Simple / Shape / Text / Unsaved.
- The editor has a zoomable preview, the raw pattern text, a row per pattern element (dash, gap, dot, shape, text with S / rotation / X / Y), the ASCII field (**Generate** rebuilds it), and a 14-language description grid.
- Editing the raw pattern updates the element rows, and editing a row updates the raw pattern.
- **+ New**, **Duplicate** and **Delete** apply to the list. Problems are shown above the editor, and saving is blocked while any style has an error.

**Shapes tab**
- Cards show each shape, its number and size, and which line styles use it. A shape that is in use cannot be deleted. Renaming a shape updates every style that refers to it.
- **+ New shape** or **Edit** opens the grid editor:
  - Line, Arc (start, end, point on arc), Circle and Select tools, snapped to a 1/n-unit grid. The dashed horizontal axis is the line; the blue circle is the insertion point.
  - **Import DXF** reads LINE, ARC, CIRCLE, LWPOLYLINE and POLYLINE from an ASCII DXF. It scales to 2 units by default and places the shape on the line.
  - The orange overlay is the shape as compiled, so any rounding shows before you save. Beside the canvas are the shape drawn on a sample line and the generated shape codes.

**Save to SOLIDWORKS Electrical** (Ctrl+S)
1. Copies the current three files to `%LocalAppData%\SWEETs\LineStyles\backups\<date-time>\`.
2. Writes the shapes first, then `linestyle.xlin`. Program Files is read-only, so Windows asks once for administrator permission (UAC). The read-only attribute on `ltypeshp.shp` is kept.
3. Reads every file back and compares it with what was intended.

Files you did not change are written back byte-for-byte as they were: shapes you didn't touch keep their original `.shp` text and comments, and the XML keeps SWE's own layout. **Backups** lists earlier backups and restores one; the files it replaces are backed up first.

## Not yet verified in SOLIDWORKS Electrical
- When SWE re-reads `linestyle.xlin` and `ltypeshp.shx`. The save message suggests a restart if changes don't appear.
- Whether drawings that already use a style pick up an edited pattern, or keep the definition stored in the drawing.

## Build

Requirements: Visual Studio 2022 or the .NET SDK 8+, the .NET Framework 4.8 targeting pack, SOLIDWORKS Electrical 2026 (for `bin\Interop.EwAPI.dll`), and Inno Setup 6 for the installer.

```
tools\build-check.cmd                 compile add-in + run format checks → tools\build-check-result.txt
.\build\Build.ps1                     build + Inno Setup installer → artifacts\installer\SWEETs.SweLineStyles-<ver>-setup.exe
.\build\Build.ps1 -Packager PackageForge
.\build\Register-Dev.ps1              (elevated) register the local build with SWE for debugging
```

`tools\SWEETs.LineStyles.Tests` checks the file-format code against copies of SWE 2026's own Support files (`testdata\`). It
also compares against the installed files when they are present. The checks:
- The `.shp` compiles to the shipped `.shx` byte for byte.
- Unchanged `.shp` and `.xlin` files are written back byte-identical.
- All 55 patterns parse and re-format stably.
- Shapes survive decode → encode → decode.
- DXF parsing works.

Logs: `%LocalAppData%\SWEETs\LineStyles\logs`. Settings: `%AppData%\SWEETs\LineStyles\settings.xml`.
