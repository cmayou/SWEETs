# Guide for AI agents and contributors

> Change Log is now a SWEETs tool (see `..\CLAUDE.md`): `ChangeLogAddIn` is a static module the SWEETs host calls
> with its EwAPI application; `RibbonBuilder` only registers the commands and SWEETs places the buttons. Invariant 5
> (`AddInIds.Clsid`) now only matters for removing old stand-alone registrations, and there is no license key to store.
> The UI merges the shared SWEETs theme (`..\src\SWEETs.UI\Theme.xaml`).


## Module map: where to change what
| Task | File(s) |
|---|---|
| Track a new SWE property or object type | `src/ChangeLog.AddIn/Ew/EwObjectReader.cs` (one `.Set(...)` per field), plus `TryIdentify` for new types and `Model/Enums.cs` (`ObjectKind`, **append only**) |
| Enumerate a new object type at project open | `Ew/StateCollector.cs` |
| Coalescing, attribution, reconcile rules | `src/ChangeLog.Core/Recording/ChangeRecorder.cs` |
| Journal format, integrity rules | `Core/Storage/*`, `Core/Integrity/JournalVerifier.cs` |
| Capture comparison (internally "baselines") | `Core/State/DiffEngine.cs`; canvas: `AddIn/UI/Controls/PageCanvas.cs` |
| Excel columns | `Core/Export/ChangeLogExporter.cs` |
| Ribbon buttons | `AddIn/Host/AddInHost.cs` → `Buttons()` |
| Pane UI | `AddIn/UI/ChangeLogView.xaml` + `ChangeLogViewModel.cs` |
| Colours and typography | `AddIn/UI/Theme.xaml` (+ `Palette` in `Mvvm.cs` for drawn controls) |

## Invariants (do not break)
1. **Never let an exception escape into SWE.** COM entry points, event handlers and commands catch, log and continue.
2. **All COM calls run on the SWE UI (STA) thread.** Only plain `ObjectState`/`ProjectState` may cross to worker threads.
3. **Journals are append-only and hash-chained.** Never rewrite or "fix" a journal file. The body is stored as a string, and hashes cover those exact bytes.
4. **Persisted enum values and JSON member names are a file format.** Append only; never renumber or rename.
5. **`AddInIds.Clsid`** must match the registry GUID in `installer/Package.wxs` and `build/Register-Dev.ps1` (SWE instantiates the class whose `[Guid]` equals it).
6. **The license key never goes into source, logs or the installer.** It is stored with DPAPI only.
7. Core stays free of COM, WPF and NuGet runtime dependencies.
8. C# 7.3 (net48 default). No newer language features.
9. **Never close or reopen the user's project** (e.g. to create a native SWE Snapshot); other tools may be attached to it. User-facing wording is "capture", never "snapshot" (SWE has its own Snapshot feature).

## Verify a change
```powershell
.\build\build.ps1 -SkipInstaller      # build + all tests
```
API signatures come from SWE's installed `Interop.EwAPI.dll` (embedded interop types). tlbimp substitutes coclass interfaces in signatures (e.g. `addCommand(EwCommandX)`), so check the Object Browser. Out-parameters marked `retval` become return values; for example, `string getTag(out EwErrorCode e)`.
