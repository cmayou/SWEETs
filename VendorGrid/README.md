# VendorGrid 2.0 — SOLIDWORKS Electrical add-in

> **Part of SWEETs.** VendorGrid now ships inside the SWEETs add-in (`..\README.md`): its **Vendor Search** button is in
> the **Export Tools** group of the SWEETs tab, SWEETs connects to SOLIDWORKS Electrical for it (no API key setting) and
> the SWEETs installer replaces the stand-alone install. Build `..\SWEETs.sln`; `installer\` below is the old
> stand-alone installer and is no longer used. Settings, vendor keys and logs stay in the folders listed below.


Type a manufacturer part number (or click a part from the open project) and VendorGrid queries every enabled vendor in parallel.
Each vendor reports live availability, vendor SKU and cost per quantity. A **VendorGrid** tab is added to the ribbon (Command Manager).

2.0 combines VendorGrid 1.1 (look, real vendor APIs, installer) with the workflow of the PartGrid prototype
(matrix/cards views, KPI strip, status chips, cost calculator, cost curve, favourites, BOM staging, export quote).
PartGrid's prices were synthetic; **every price shown here comes from a vendor API**. Websites without an API are "link-only" rows that open the vendor's search page.

## Build and install
Prerequisites: Visual Studio 2022 (.NET desktop development) + .NET Framework 4.8 SDK and targeting pack (provides `tlbimp.exe`). Build from a short path without spaces (e.g. `C:\Dev\VendorGrid`).

    powershell -ExecutionPolicy Bypass -File installer\Build-Installer.ps1

Produces `dist\VendorGrid-Setup.exe`. Run it (elevates), start SOLIDWORKS Electrical, then Tools > Add-in management > Activate + Start up VendorGrid. Logs: `%LOCALAPPDATA%\VendorGrid\vendorgrid.log`.

Vendor logos live in `src/VendorGrid/Resources/Vendors/{vendor id}.png` (96 px, ids: digikey, mouser, newark, arrow, rs, te, automationdirect, mcmaster). Drop a PNG named after a vendor id to give it a logo; vendors without one show a coloured letter badge.

## Colour-blind accessibility
Colour is never the only signal. Each state has a colour-blind-safe colour, a distinct shape and a text label: teal ● good/ready/best, dark amber ◐ partial/check, violet ▲ alternate/uncertain, rose ✕ out of stock/error, blue ◆ info/link. Red/green pairs are not used. Chart curves use the Okabe-Ito palette and also differ by marker shape (● ■ ◆ ▲ ✚) and line style (solid, dashed, dotted); the volume matrix marks each column's lowest price with ● as well as bold. A symbol key sits under the results. Colours and shapes are defined in `UI/Tones.cs` and `UI/Theme.xaml`.

## Language
VendorGrid follows the language of SOLIDWORKS Electrical. The Electrical API has no "interface language" call, so the add-in uses, in order: your choice in Settings > General > Language; the language code of the open project; the host process language; the Windows display language. What it chose and why is written to `vendorgrid.log` ("Language: using ..."). Translations are in `src/VendorGrid/Resources/Localization.tsv` (English, French, German, Spanish, Italian, Portuguese, Japanese, Simplified Chinese). A language without a column falls back to English. To add or correct a language, add a column (header = ISO code, e.g. `ko`) or edit cells, then rebuild, or place the edited file as `Localization.tsv` next to `VendorGrid.dll` to override the built-in one without rebuilding. Machine-written translations: have a native speaker review technical terms before wide release.

## Publisher name in the Windows prompt
The name in the elevation prompt comes from the file's digital signature; unsigned files always show "Unknown publisher".
* Public use: sign with a code-signing certificate from a certificate authority: `installer\Build-Installer.ps1 -Publisher "Your Company" -CertThumbprint <thumbprint>` (needs the Windows SDK for `signtool.exe`).
* Your own PC(s): run `installer\New-SigningCert.ps1 -Publisher "Your Name"` (elevated) once, then build with the printed thumbprint.
Without `-CertThumbprint` the build is unsigned; `-Publisher` still sets the name shown in Apps > Installed apps.

## Using it
* **Settings > Vendors**: one entry per vendor. Select a vendor to set what it needs: Mouser / Digi-Key / Newark show their API credentials (with Show/Hide); every other vendor has a website search page plus an optional "pull live price and stock from a data API" switch (API URL, auth header, JSON field mapping, **Test connection**). Tick vendors to include them, star favourites (listed first), **+ Add vendor** for new ones, **Reset to default** on built-ins.
* **Settings > General**: SOLIDWORKS Electrical license key (Show/Hide); country sets currency, Digi-Key locale, Newark/Farnell store and RS portal.
* **Project parts**: with a project open, its manufacturer parts appear as tiles; click one to search (quantity = number of components using it). Pick another open project from the drop-down.

## Code map
| Area | Files |
|---|---|
| Add-in / ribbon | `Addin/VendorGridAddIn.cs`, `RibbonBuilder.cs` (typed, late-bound fallback), `EwLate.cs` |
| Project reader | `Addin/ProjectReader.cs` (getEwProjectCurrent → manufacturer part manager → parts; component usage counts) |
| Search | `Core/SearchService.cs`, `Providers/*` (add a vendor = one class + one case in `BuildProviders`) |
| Settings | `Settings/*` (vendors, countries, DPAPI store) |
| UI | `UI/MainViewModel.cs` + `ResultViewModels.cs`, `ProjectViewModels.cs`, `BomViewModels.cs`, `CurveViewModels.cs`, `MainWindow.xaml`, `Theme.xaml`, `PriceCurveChart.cs` |

## Status
Written without access to a .NET compiler or SOLIDWORKS Electrical: first build may need small fixes. Unverified: how the Electrical API reports language codes (the reader tries the project's current language, the configured languages, then "en-US"/"en"/blank, and logs what worked in `vendorgrid.log`), Newark response fields/store hosts, and the late-bound ribbon path.
The supplied license key is compiled into `Settings/Defaults.cs`; remove it before distributing outside your organisation.
