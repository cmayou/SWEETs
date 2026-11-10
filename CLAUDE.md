# SWEETs: project notes for Claude Code

SOLIDWORKS Electrical 2026 add-in (C#, .NET Framework 4.8, x64, WPF) that hosts the four SWEETs tools on one
**SWEETs** ribbon tab. Owner: Charles. Combined on 2026-11-06 from four stand-alone add-ins; renamed from TEW to
SWEETs the same day (namespaces, assemblies, folders, commands, data folders, publisher).

## Ground rules from the owner
- Use only verified information. Ask when something is unclear rather than guessing.
- Visual Studio solution (`SWEETs.sln`), PowerShell build producing an Inno Setup 6 installer (`build\Build.ps1`).
- Minimal SaaS/enterprise UI (WPF). Write the UI as the application, with no assistant-style text.
- All interfaces look the same (owner, 2026-11-06): one shared theme, `src\SWEETs.UI\Theme.xaml`. Tool
  `UI\Theme.xaml` files only merge it (DP and PDF: nothing else; LS: adds its DataGrid behaviour; Datasheet Importer:
  maps its own keys `SurfaceBrush`, `PrimaryButton`, `TitleText`… onto the shared palette and styles, because its
  XAML and `ThemeBrush.Get` use them). Do not add colours or control templates in a tool.
- Window header (owner chose the light header bar): `Header.Bar` → `Header.Icon` (main windows only, the tool's
  ribbon icon) → `Header.Overline` "SWEETs · Drawing Tools | Import Tools" (dialogs: "SWEETs · <tool>") →
  `Header.Title` → `Header.Subtitle`; actions right-aligned in the bar.
- The SWE API keys are not in source (repo is public on GitHub, owner 2026-11-10): `src\SWEETs.AddIn\Secrets\SweApiKeys.local.cs`
  (git-ignored, copied from `SweApiKeys.template.cs`). The csproj compiles the local file instead of the template when it
  exists; `build\Build.ps1` stops if it is missing. Never commit a key; `DataSheetMfgPartsImport\EmbeddedLicense.Code` is empty.

## Ribbon (owner's spec)
One tab, **SWEETs**: group **Drawing Tools** (Drawing Parameters, Line Style Editor), group **Import Tools**
(PDF to Schematic, Datasheet Importer), group **Export Tools** (VendorGrid: Vendor Search), group **Project Tools**
(Change Log, all six of its buttons: Change Log, Compare, Capture Changes, Export to Excel, Verify, Settings),
group **Settings** (Settings button: turn each tool on/off). Owner's choices, 2026-11-07.
Turned-off tools are hidden (no button, no command registered).

## Architecture
- `src\SWEETs.AddIn`: the only COM-registered add-in (CLSID 3ed791fd-feac-4081-99b5-ef363b2fa108,
  Inno AppId 17788920-4793-4447-92EC-38A76D1BC778: never change).
  - `Entry\SweetsAddIn.cs`: connect, API keys, tool connect/disconnect, ribbon build, ribbon retry timer
    (the ribbon can be missing during SWE startup: seen by the Datasheet Importer), COM registration.
  - `Entry\ToolCatalog.cs`: the tools, their group, captions, icons and connect delegates.
  - `Entry\SettingsCommand.cs`: opens `UI\SettingsWindow`; applying is queued on the dispatcher so the tab is rebuilt
    after the command has returned to SWE (the Settings button lives on that tab).
  - `Settings\SweetsSettings.cs`: `%AppData%\SWEETs\settings.ini`.
  - `Interop\SweApi.cs`: reflection bridge copied from Drawing Parameters. `Interop\SweContract.cs`: members used,
    each already used by one of the tools.
- Tools are class libraries. Their former `SweAddIn`/`Addin` entry classes are static modules with
  `Connect(object app) → ribbon command` and `Disconnect()`. Commands, windows and logic unchanged.
  Change Log's `Connect` returns its six buttons (`ToolDefinition.ConnectButtons`); turning it off hides all six.
- VendorGrid and Change Log use the SWEETs connection (owner, 2026-11-07): no API key of their own. VendorGrid's key
  field and `Defaults.SolidWorksLicenseKey` were removed (`AppSettings.SolidWorksLicenseKey` stays only so old settings
  files load); Change Log's key prompt and key settings were removed (`LicenseStore.cs`, `LicenseDialog`, `LicenseKeyBox`
  are excluded from compile in `ChangeLog.AddIn.csproj`).
- VendorGrid compiles against the same `Interop.EwAPI.dll` as the other tools (never copied). It used to load its theme
  into `Application.Current.Resources` (still does) and translated every WPF element in the process: `Loc` now only
  translates elements in VendorGrid's own windows. Its TabControl/TabItem styles moved to SWEETs.UI so Change Log's
  Compare tabs look the same whichever tool opened first.
- VendorGrid / Change Log styles (owner, 2026-11-07: "look the same as the existing"): their keyed styles are aliases
  of the SWEETs styles (VendorGrid `PrimaryButton`→`Btn.Primary`, `GhostButton`/`HeaderButton`→`Btn.Secondary`,
  `ChipButton`→`Btn.Chip`, `IconButton`→`Btn.Icon`, `H1`/`H2`→`Text.Subtitle`, `Muted`→`Text.Muted`, `Label`→`FieldLabel`,
  `TH`→`Text.Overline`; Card/Chip/TextBox/CheckBox/ComboBox come straight from SWEETs.UI. Change Log `Btn`→`Btn.Secondary`,
  `Btn.Primary`/`Btn.Ghost`/`FocusRing` from SWEETs.UI, `Text.H1`→`Text.Subtitle`, `Text.H2`→`Text.Overline`,
  `Input`→`Input.Small`). Never define a style whose BasedOn is the same key (WPF resolves it to itself).
- Ribbon icons: white glyph on the SWEETs blue tile (#2056ED→#1144D6 gradient, radius 96/512), drawn with PIL at 512 px
  and downscaled; heavier strokes at 16 px. Change Log Settings = the SWEETs gear (settings-16/32.png).
- Change Log invariants still hold (its `AGENTS.md`): never throw into SWE, COM on the UI thread, journals append-only,
  C# 7.3, `TreatWarningsAsErrors` (its `Directory.Build.props`).
- The installer removes stand-alone VendorGrid (RegAsm unregister, `Program Files\VendorGrid`, uninstall entry) and the
  Change Log MSI (by DisplayName), plus their add-in keys {101df681-…} / {D4C1A7E2-…}; their data folders are kept.
- One process-wide `AssemblyResolve` in the host: returns an already-loaded assembly by simple name, else loads from
  the add-in folder ignoring version (tools were compiled against different System.* versions).
- PdfPig: the Datasheet Importer was moved from 0.1.8 to 0.1.9 to match PDF to Schematic Core (one folder, one copy).
- `Infrastructure\DataMigration.cs`: first start per user copies missing files from `TEW\Swe*` / `TEW\SWEETs` to
  `SWEETs\*` under LocalAppData, AppData and ProgramData; marker `%LocalAppData%\SWEETs\.migrated-from-tew`.
  The installer copies ProgramData (admin rights) with robocopy before seeding.

## Naming rules (TEW → SWEETs rename)
- Prefix swap only: `TEW.SweX` → `SWEETs.SweX`. Do not shorten to `SWEETs.DrawingParameters`: the namespace would
  shadow the `DrawingParameters` class (CS0118). Host aliases in ToolCatalog end in `Module` for the same reason.
- Keep SOLIDWORKS Electrical's own names: `.tewzip`, `tew_app_data`, `[tew]`, `tew_element`, `tew_translatedtext`.
- Keep `TEW-1001` (text inside the PDF test fixture).
- Keep the old `TEW.*` ProgIds, `Program Files\TEW\SWEETs` and `TEW\…` data paths in the installer, Register-Dev and
  DataMigration: they exist to find and clean up or migrate the old installs.
- Command names: `Sweets…Command` (were `Tew…Command`). CLSIDs and the Inno AppId did not change.

## Not yet verified (owner to test in SWE 2026)
- SWEETs.sln built on 2026-11-06 before the rename; the renamed solution has not been built yet.
- VendorGrid and Change Log inside SWEETs (2026-11-07): not compiled or run yet. Check: both load, the six Change Log
  buttons appear, the Change Log pane docks and records, VendorGrid opens and reads project parts.
- Shared theme (SWEETs.UI) and new header bars: not compiled yet. Check every window opens (a missing resource key
  throws when the window loads).
- Data migration and the installer's upgrade from a `Program Files\TEW\SWEETs` install.
- SWE accepting the second API key fallback is only needed if the first fails; the first worked for three tools.
- Live tab rebuild after Settings › Save (remove tab + add tab at runtime). Fallback: restart SWE.
- Datasheet Importer connecting through an application object obtained with the first API key (the one Drawing
  Parameters, Line Style Editor and PDF to Schematic shipped with); stand-alone it used its own key, now the fallback.

## Related
- Each tool folder keeps its own CLAUDE.md, tests and tools. Their `build\Build.ps1` scripts built the stand-alone
  add-ins and are superseded by `build\Build.ps1` here.
- `_standalone-backup\` holds the files changed for SWEETs, as they were before, and (after
  `build\Remove-TewFolders.ps1`) zips of the pre-rename `TEW.*` folders.
- Electrical API 2019 HTML help: `E:\AI Applicaations\Electrical API-2019.0.0-ActiveX\html`.
