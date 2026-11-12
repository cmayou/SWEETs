# Change Log for SOLIDWORKS Electrical

> **Part of SWEETs.** Change Log now ships inside the SWEETs add-in (`..\README.md`): its six buttons are in the
> **Project Tools** group of the SWEETs tab, SWEETs connects to SOLIDWORKS Electrical for it (no license key prompt or
> key setting) and the SWEETs installer removes the stand-alone MSI. Build `..\SWEETs.sln`; `build\build.ps1`,
> `build\Register-Dev.ps1`, `build\Set-LicenseKey.ps1` and `installer\` below describe the old stand-alone add-in and
> are no longer used. Storage, integrity model and tests are unchanged.


A SOLIDWORKS Electrical (SWE) add-in that keeps a tamper-evident log of every change to a project
(pages, symbols, wires, cables, manufacturer parts, components, texts, locations, functions, harnesses).
It can export the log to Excel and visually compare two captured project states ("captures").

Built against SOLIDWORKS Electrical **2026 SP2.1** (`EwAPI` 1.0, `Interop.EwAPI` 2026.4), .NET Framework 4.8, WPF and WiX 5.

---

## What you get

| Area | Behaviour |
|---|---|
| **Ribbon** | A new **Change Log** tab with these groups: *Review* (Change Log, Compare), *Capture* (Capture Changes), *Export* (Export to Excel), *Integrity* (Verify, Settings). |
| **Change Log pane** | Docked SWE pane showing:<br>• project status (recording, indexing, failed) and storage health<br>• an integrity banner<br>• KPI cards and a 14-day activity chart<br>• a breakdown by object type<br>• chips for type/user/action, a search box, a time range and an inferred on/off toggle<br>• a virtualized change grid with a detail drawer (field before → after, attribution, integrity, journal reference, related batch)<br>Below 720 px the pane switches to a compact card layout. |
| **Each change records** | UTC timestamp, action (Added / Edited / Removed), object type, label and ID, page (label and ID), user (SWE login, or Windows identity if SWE user rights are off), Windows account, machine, field-level before/after, batch ID (one operation such as a macro insertion), source (Live / Reconciled) and attribution (Confirmed / Inferred). |
| **Compare** | Pick two captures (or the live project). The comparison shows: summary cards, a per-type table, changed pages ranked by impact, and a **page canvas** drawn from captured geometry. On the canvas, added items are green, removed items dashed red, and edited items amber with a dashed ghost at the old position; unchanged items are grey. Zoom with the mouse wheel, pan by dragging, double-click to fit, click to inspect. |
| **Export** | Writes an `.xlsx` with three sheets: *Changes* (frozen header and auto-filter), *Summary* (by type and by user) and *Integrity* (findings). The export respects the pane's current filter. No Excel installation is needed. |
| **License key** | Prompted on first start and stored with DPAPI (per user). The Settings dialog has a masked field with a show/hide toggle. It is never written to source, the MSI, or plain-text config. |

---

## Build

Prerequisites (Windows): SOLIDWORKS Electrical installed (the add-in compiles against its `Interop.EwAPI.dll`,
embedding the interop types like Dassault's own add-ins), and Visual Studio 2022/2026 with the
*.NET desktop development* workload (.NET SDK + .NET Framework 4.8 targeting).

```powershell
powershell -ExecutionPolicy Bypass -File .\build\build.ps1 -Version 1.0.0                          # build + test + MSI
powershell -ExecutionPolicy Bypass -File .\build\build.ps1 -Version 1.0.0 -SignCertThumbprint <tp>  # signed (recommended)
```

Output: `out\ChangeLog-1.0.0-x64.msi`. The installer project needs the *HeatWave* extension to load in
Visual Studio; command-line builds do not.

### Sharing / distribution
The build also produces **`out\ChangeLog-<ver>-x64.zip`**, which is the file to hand out. It contains:
the MSI, `INSTALL.txt` (end-user and IT instructions), `Set-LicenseKey.ps1` and `SHA256SUMS.txt`.
Before sharing outside your own team:
- **Sign it** (`-SignCertThumbprint`). Unsigned DLLs loaded into SWE trigger SmartScreen, and EDR or AppLocker often block them.
- **Bump `-Version`** for every release. MajorUpgrade only replaces an older version; rebuilding the same number doesn't upgrade.
- **Don't ship an API license key.** Each recipient uses their own key (check your Dassault API licence terms).

### Developer loop (no MSI)
```powershell
.\build\build.ps1 -SkipInstaller -Configuration Debug
.\build\Register-Dev.ps1                 # elevated: registers bin\Debug with SWE and enables start-up loading
# restart SWE, then Visual Studio ▸ Debug ▸ Attach to Process ▸ SOLIDWORKSElectrical.exe (Managed v4.x)
```

---

## Install and enable

1. Run the MSI (per-machine, needs elevation).
2. Restart SOLIDWORKS Electrical ▸ **Tools ▸ Add-in management** ▸ tick **Change Log** (*Activate*, and *Start up* to load it every time).
3. On first load, enter the API license key (masked, show/hide). IT can pre-provision it per machine with
   `build\Set-LicenseKey.ps1 -Scope Machine` (elevated).

### How SWE loads the add-in (verified against SWE 2026 SP2.1 `EwAddInMngCmd`)
```
HKLM\SOFTWARE\Trace Software\SOLIDWORKS Electrical\Addins\{D4C1A7E2-6B3F-4E58-9A21-7C0E5F3B8A64}
    Title, Description, Path   REG_SZ  (all three required, otherwise the add-in is not listed)
    Hidden                     REG_SZ  "1" hides it from Add-in management
HKCU\...same key...
    StartUp, StartShowMsg      REG_SZ  "1"/"0" (written by the dialog's "Start up" checkbox)
```
SWE creates a dedicated AppDomain with `ApplicationBase` = the add-in folder, loads the assembly named by
`Path`, instantiates the type whose `[Guid]` equals the key GUID and invokes `connectToEwAPI(factory)` by
reflection. No COM registration is used.

---

## Architecture

```
SWE process (STA UI thread)
 ├─ ChangeLogAddIn : IEwAddIn ─────────── COM entry point (never throws into SWE)
 ├─ AddInHost ─ ribbon, commands, identity, signing key, app events
 │   └─ ProjectMonitor (one per open project)
 │        ├─ _IEwProjectXEvents add/update/delete ─► EwObjectReader (COM → ObjectState)
 │        ├─ StateCollector: time-sliced project index (40 ms slices; SWE stays responsive)
 │        └─ ChangeLogSession (Core) ◄─ Pump() every 500 ms
 │             ├─ ChangeRecorder: coalesce, drop no-ops, field diff, batches, reconcile
 │             ├─ JournalWriter: hash-chained, signed (worker thread, one write per burst)
 │             ├─ JournalTailer: reads other users' journals live
 │             ├─ BaselineStore: gzip captures, hash pinned in the journal
 │             └─ JournalVerifier: integrity report
 └─ WpfUiService ─ docked pane (HwndSource in IEwDockingPaneX), Compare, Settings
```

* **`ChangeLog.Core`**: all logic, with no COM, no WPF and no NuGet runtime dependencies. It is fully unit-tested.
* **`ChangeLog.AddIn`**:
  * `Ew\` converts COM objects to plain state. **To track a new SWE property, add one `.Set(...)` line in `EwObjectReader`.**
  * `Host\` handles lifecycle.
  * `UI\` contains the MVVM views.

### Storage
All storage lives in `<project folder>\ChangeLog\`, so the log travels with the project and is shared in collaborative environments:
- `journals\*.cljl`: one append-only file **per user session**. One writer per file means there is no multi-writer corruption on SMB shares (a real risk with SQLite on network drives).
- `baselines\*.json.gz`: captured project states.
- `project.json`: a stable project GUID.

### Captures vs. SOLIDWORKS Electrical Snapshots
*Capture Changes* stores a comparable copy of the project state. It is deliberately separate from SWE's own
**Snapshot** feature, which:
- cannot be read through the API (`IEwProjectSnapshotX` exposes create, restore and metadata only), so it cannot be compared, and
- can only be created on a **closed** project (`EW_PROJECT_OPENED`). Closing a project from an add-in could break other tools attached to it (PDM, SOLIDWORKS 3D, other add-ins), so the add-in never does that.

---

## Integrity model (tamper evidence)

| Attack | Detected by |
|---|---|
| Edit, insert or delete an entry | SHA-256 hash chain per journal |
| Rewrite a journal and recompute all hashes | ECDSA P-256 checkpoint signatures. The key is a **non-exportable** CNG key in the author's Windows profile, so other users cannot forge or re-sign entries. |
| Delete or truncate an earlier session | Each session header links to the same user+machine's previous session head. |
| Truncate or rewrite another user's journal | Witness entries: every session records the heads of all other journals it has seen. |
| Modify a baseline file | Its SHA-256 is recorded in the signed journal and checked on load. |
| **The sole author drops their own newest, unwitnessed entries** | **Only with an external anchor.** Configure *Settings ▸ External anchor folder* to point at an IT-managed share whose ACL grants users *Create files / Append data / Read* only. Every checkpoint head is appended there. Without this, a single-user project is tamper-evident against everyone except its own author. |

Records from a journal with findings are flagged **UNTRUSTED** in the grid and in the export. Entries written after the last signed checkpoint (for example after a crash) are shown as *Unsigned*, which is a warning rather than tampering.

This is tamper-**evident**, not tamper-**proof**. A local administrator controls the machine. For non-repudiation, point the anchor at infrastructure the users don't administer.

---

## Known limitations and things to verify on a real installation

These cannot be verified without SOLIDWORKS Electrical running. Check them before rollout:

1. **Remote events in collaborative mode.** It's unknown whether SWE raises `onProjectObjectUpdate` on user A's client for user B's changes. The recorder applies B's journal records first, so A does not log them, but there is a timing window of about 2 s. *Test:* two users edit the same wire and you check that each change appears once, attributed to its author.
2. **Symbol geometry anchor.** SWE gives an insertion point plus width and height. The canvas treats the insertion point as the bottom-left corner. If overlays look offset, adjust `EwObjectReader.SymbolGeometry` (one function).
3. **Wires have no page.** `IEwProjectWireX` exposes no page ID, so wire changes appear under *Not on a page*. Their drawn segments (`IEwProjectLineX`) are page-bound and appear on the canvas.
4. **Macros** are library objects. Inserting a macro shows up as one **batch** of the symbols and wires it created ("Show related changes").
5. **Keyboard input in the docked pane.** WPF inside an MFC docking pane can lose keys. `WM_GETDLGCODE` is claimed to prevent this; verify typing in the search box.
6. **Attribution of changes made without the add-in** (another user without it installed, or edits while it was disabled) is *Inferred*. It uses the project's "modified by" value, which is a best guess.
7. **Clock skew** between workstations affects ordering across users. Keep NTP healthy.
8. **First-open indexing** reads the whole project through COM (time-sliced). Very large projects take noticeably long the first time.
9. **The WPF/XAML layer was not compiled in the authoring environment.** The add-in's C# (COM layer, host, view models) compiles against SWE's shipped `Interop.EwAPI.dll`, and Core is compiled and tested; the first Visual Studio build is the first full XAML compile.

---

## Tests

`tests\ChangeLog.Core.Tests` is a dependency-free console runner, run by `build.ps1`. It covers:
- **Recorder:** coalescing; no-op suppression; transient add+delete; delete using cached label and page; field diffs; batches; external records preventing misattribution; silent kinds; max latency.
- **Session:** a full round trip ends *Verified*, and changes made without the add-in are reconciled as *Inferred*.
- **Tamper detection:** an edited body; a deleted line; a re-chained forgery without the key; a deleted earlier session; a truncation witnessed by another user; a sole-author truncation caught by the anchor (and the fact that it is *not* caught without one); a crash tail reported as a warning; a modified baseline file.
- **Diff, query and export:** diff grouping by page, the query filters and the `.xlsx` export.
- **CNG:** a real CNG ECDSA sign/verify (Windows only).
