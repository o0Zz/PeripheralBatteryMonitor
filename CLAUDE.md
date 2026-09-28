# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Working agreement

**Never run `git commit` or `git push`. The author does that.** Leave every change in
the working tree and say which files you touched — review here happens by reading the
diff, and a commit made on the author's behalf turns that diff into a clean
`git status` with the work one command further away.

This outranks any workflow or skill that says to commit between steps — the `/cleanup`
skill does, and that is the mistake this rule exists to stop. Carry out the steps, keep
the build green between them, and stop short of the commit.

Also not without being asked, in the same message: `git push`, `git reset --hard`,
`git checkout --` over live edits, branch or tag creation, or anything else that
rewrites history or discards work.

## Build / run

Windows-only WinForms app targeting **.NET Framework 4.8** (`WinExe`, AnyCPU running 64-bit). Both projects are SDK-style and `Microsoft.NETFramework.ReferenceAssemblies` supplies the net48 reference assemblies, so the plain .NET SDK builds the repo — no Visual Studio, no targeting pack, no `nuget.exe`.

```sh
dotnet build PeripheralBatteryMonitor.sln -c Release

# Output: src/PeripheralBatteryMonitor.App/bin/Release/net48/PeripheralBatteryMonitor.exe
```

There is no test project and no linter. Validation is by running the produced `.exe` on a Windows machine that has paired BLE devices.

## Target and deployment

- **.NET Framework 4.8**: ships with Windows 10 1903+ and Windows 11, so the exe runs on a clean machine. It is also the only target that consumes `Windows.winmd` the way this app does — see **WinRT bridging** in `src/PeripheralBatteryMonitor.Core/CLAUDE.md`.
- **A single executable**, `PeripheralBatteryMonitor.exe`. The README tells users to download one file and the release workflow uploads the bare exe, so that has to stay true now that Core is a separate assembly: net48 has no `PublishSingleFile`, so the `EmbedReferencedAssemblies` target in `PeripheralBatteryMonitor.App.csproj` embeds every copy-local DLL as a manifest resource and `EmbeddedAssemblies` serves them back through `AppDomain.AssemblyResolve`. The same items are removed from the copy-local set, so `bin/` holds the exe alone — which means an F5 run exercises exactly the path a release uses. `Program.Main` installs the resolver before calling `Run`, which is `[MethodImpl(NoInlining)]` for that reason: `Run` mentions `Settings`, whose fields are Core types.
- **WinForms.** Never add `PublishAot` or `PublishTrimmed`. On net48 `UseWindowsForms` does not apply — `PeripheralBatteryMonitor.App` uses classic `<Reference>` items.
- CI patches the version with `-p:AssemblyVersion=` / `-p:FileVersion=` / `-p:Version=`. There is no `AssemblyInfo.cs` to edit — the SDK generates the attributes.

## Layout

`PeripheralBatteryMonitor.Core` referencing `System.Windows.Forms`, `System.Drawing` or any UI package is a **build-breaking error**, enforced by `GuardCoreHasNoUiDependencies` in `PeripheralBatteryMonitor.Core.csproj` (`PBM0001`). Core also sets `DisableImplicitFrameworkReferences` and lists what it needs, so the boundary is real rather than nominal. A battery level crosses as an `int`; a tray `Icon` is picked in `Settings.GetIconForBatteryLevel` and never travels the other way.

Both projects use root namespace `PeripheralBatteryMonitor`. The files at Core's root are in that namespace, which is why the App does not `using` anything to reach Core: everything it touches lives there. The root is the assembly's public API and the compiler now says so — App references exactly `BatteryDevice`, `DeviceManager`, `IDeviceNotification`, `DiagnosticReport` and `Diagnostics.Log`, while `HidDeviceSource` is `internal` because only `DeviceManager` drives it. The folders below carry `PeripheralBatteryMonitor.Contracts`, `.Diagnostics`, `.Hid` and `.Providers` (plus `.Providers.<Vendor>`).

## Architecture

The app is a **single-form WinForms tray application**. The form is created but kept hidden — `Settings.SetVisibleCore` suppresses visibility unless the user explicitly opens it from the tray menu. The form's job is to host the `NotifyIcon` and a `Timer` that drives polling.

`Program.Main` owns the session-local `Local\o0Zz.PeripheralBatteryMonitor` mutex for the full message-loop lifetime. A later launch exits before constructing a form, so repeatedly opening the executable cannot create duplicate tray hosts. Keep this before `EmbeddedAssemblies.Install` / `Run`: it depends only on the framework and must reject the duplicate before application state is initialised.

Three layers, all in namespace `PeripheralBatteryMonitor`; the first two are the App project, the third is Core:

1. **`Program.cs`** — entry point; installs `EmbeddedAssemblies` and then runs `Application.Run(new Settings())`.
2. **UI** — see `src/PeripheralBatteryMonitor.App/CLAUDE.md`. `Settings.UpdateIcon()` is the polling tick: it calls `deviceManager.refreshHidDevices()` (the HID sources have no watcher, so the tick is what picks up a plugged/unplugged dongle), then `device.UpdateBatteryLevel()` on every tracked device, **drops every device that is not `IsConnected()`**, picks the lowest battery level across the ones left, maps that to one of five tray icons (`Icon_Battery_20/40/60/80/100`), updates the tooltip, and fires a balloon notification once per low-battery transition (`lowBatteryNotificationDone` latch resets when level rises above 20%). An `updatingIcon` latch makes it non-re-entrant: HID discovery reports new devices *synchronously*, and `OnNewDevice` calls back into `UpdateIcon`.
3. **`PeripheralBatteryMonitor.Core`** — discovery and battery reading, one type per file:
   - `IDeviceNotification.cs` — the UI callback contract; exposes `OnNewDevice` and `OnDeviceRemoved`.
   - `BluetoothRadio.cs` (`BluetoothRadio`, static) — turns every `RadioKind.Bluetooth` radio off, waits, and turns it back on, via WinRT `Windows.Devices.Radios`. Nothing else in Core uses it; it exists for the tray's **Restart Bluetooth** entry. See **Restarting the Bluetooth stack** in `src/PeripheralBatteryMonitor.App/CLAUDE.md`.
   - `DeviceManager.cs` (`DeviceManager`) runs **two** `DeviceInformation.CreateWatcher` instances in parallel: one for BLE (protocol GUID `{bb7bb05e-5972-42b5-94fc-76eaa7084d49}`) and one for Bluetooth Classic / BR-EDR (`{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}`). Both feed the same `ConcurrentDictionary<string, BatteryDevice>` keyed by `devInfo.Id`, each device tagged with a `DeviceTransport` (`BluetoothLowEnergy` / `BluetoothClassic`). Only **paired** devices (`devInfo.Pairing.IsPaired`) are tracked. A phone may appear once on each transport, and `ReconcileSiblingNames` copies the Classic endpoint's display name onto its BLE sibling, which fixes opaque iOS BLE local names without guessing from the text. **The `System.Devices.Aep.ContainerId` is what proves the two endpoints are one physical device** — for a *paired* device it is the real PnP container id (verified: a paired device's AEP bag and its nodes in the device tree carry the same GUID). An *unpaired* endpoint instead gets a container synthesised per protocol and address, so the two transports of one unpaired device do **not** share it — irrelevant here, since only paired devices are tracked. The `Communication.Phone` category only narrows the scope and is required on **either** endpoint, not on both: the container already establishes same-device, and the BLE endpoint of a phone is not reliably categorised, so demanding it there is enough on its own to silently disable the whole reconcile. The watcher's `Updated` handler does two things: removes the device if `System.Devices.Aep.IsPaired` flips to false, and forwards property bag changes into the cached `BatteryDevice` via `UpdateProperties`. `Removed` removes the device. `scanForEver` restarts each watcher in its `Stopped` handler for continuous discovery. `refreshHidDevices()` is the **second, non-Bluetooth source**: it reconciles `DeviceTransport.UsbHid` entries against a fresh `HidDeviceSource.Discover()` snapshot (add newly present, remove vanished, never touch Bluetooth entries). `scan()` deliberately does *not* call it — see the re-entrancy note in `UpdateIcon` above; the poll tick is the single driver.
   - **Known limitation: this makes the two entries read alike, it does not merge them.** A dual-mode phone still occupies two rows in `Info` and two tray tooltip lines, now under the same name. The fix at the root would be to track one device per container and pick the endpoint that can actually report a battery; that was left alone deliberately, because choosing the wrong endpoint loses the reading and no dual-mode device was available to verify against.
   - `HidDeviceSource.cs` (`HidDeviceSource`, static) — discovery for devices that reach the PC over raw USB HID and so have **no** association endpoint and no pairing (a peripheral on its own vendor dongle). There is nothing to subscribe to, so this is a plain snapshot, cheap enough to re-run every tick. It surfaces only interfaces claimed by a registered `HidDeviceSpec`, derives the device id from the HID interface path (the analogue of `DeviceInformation.Id`; moving the dongle to another USB port therefore reads as a different device), and seeds the property bag with the `PeripheralBatteryMonitor.Hid.*` keys so a provider can reopen that exact interface without re-enumerating. It returns `HidDiscoveredDevice` descriptors rather than raw interfaces, because **one interface is only *usually* one device**: a spec may carry an `IHidDeviceExpander`, and a receiver is one interface holding up to six peripherals. A device that *is* its interface keeps the bare path as its id, byte for byte what it was before receivers existed; a receiver child gets `#01` appended and a `PROP_HID_DEVICE_INDEX` in its bag. Constructing a descriptor lives in `Providers/ProviderHid`, not here, because an expander lives under `Providers/` and must not name a root type.
   - `BatteryDevice.cs` (`BatteryDevice`) owns one device's battery state — a thin state holder + `IBatteryDeviceContext`. Two constructors: one from a WinRT `DeviceInformation` (Bluetooth), one from `(id, name, transport, properties)` for devices with no `DeviceInformation` behind them (HID). Each device **binds to one `IBatteryProvider`** and remembers it (see `Providers/`). `UpdateBatteryLevel()` first re-reads the bound provider as a fast path; if that yields nothing it probes the other priority-ordered providers and binds to the first whose `ReadBattery(this)` returns a value. Re-probing on an empty read (rather than staying stuck on a provider that went quiet) lets a device recover from a transient failure and lets a **higher-priority** provider preempt when it comes online (e.g. GATT once the device connects). A `null` reading means "can't read right now" and leaves the previous `batteryLevel`; `-1` means never read, which `Settings.UpdateIcon` filters out. `IsConnected()` is three sources, most authoritative first, and is **not** a switch on transport: the **bound** provider's `IDeviceLinkState` if it has one (only GATT does), else `System.Devices.Aep.IsConnected` from the property bag (both Bluetooth transports publish it), else `lastReadSucceeded`. Each step exists for a reason. It must be the *bound* provider and not the first candidate implementing the interface — the candidate list holds every registered provider, so for any BLE device that always found `BluetoothLEBatteryProvider`, whose link is legitimately down when it never opened one, and a BLE device with no GATT battery service reading its level from the Windows property bag reported "disconnected" for its entire life. The AEP property must be consulted *before* the read test, because a battery reading outlives the connection that produced it: Windows nulls `PROP_BATTERY_LEVEL` on disconnect and `CacheProperties` drops nulls, so the last percentage stays in the bag and `BluetoothBatteryProvider` keeps handing it back. And `lastReadSucceeded` is the last resort for `UsbHid`, where no OS-level connection state exists for a device behind a dongle — the dongle stays plugged in with the peripheral switched off, so answering *is* the liveness test.
   - **The battery layer** — three folders side by side at the project root, one type per file. There is no wrapper folder over them: the whole assembly *is* the battery layer, so a `Battery/` segment would only have repeated the product name inside itself, and a folder called `Core/` inside `PeripheralBatteryMonitor.Core` meant two different things one level apart. Dependencies run one way — `Providers/` and the root files depend on `Contracts/`, `Providers/` and the discovery code depend on `Hid/`, and nothing depends back — verified: no file under `Providers/` or `Hid/` names a root type. **`Diagnostics/` is the exception that keeps the rule a rule: it is the bottom of the graph — it depends on nothing, not even `Contracts/`, and every other folder may depend on it.** `Hid/HidDevice` has to be able to say *why* a `CreateFile` failed, so `Hid/` does now carry one `using PeripheralBatteryMonitor.Diagnostics`; a `Log` at the root instead would have made the graph circular, since the root already depends on `Hid/`. That one-way rule is why `Contracts/` is a folder rather than five files loose at the root: `BatteryDevice` implements `IBatteryDeviceContext`, which `IBatteryProvider` consumes, which `BatteryDevice` calls — keeping the contract separate from the implementations is what stops that from being a cycle between folders.
### Persistence

All user settings live in the registry under **`HKCU\SOFTWARE\PeripheralBatteryMonitor`**:
- `IntervalMin` (DWORD, default 5) — polling interval in minutes; `numericUpDownRefreshPeriod` writes this and reconfigures `IconTimer.Interval` live.
**The diagnostic log is not a setting and deliberately has none.** See **Diagnostics** below; there is nothing about it in the registry, and Core still never touches the registry at all.
- `NotificationEnabled` (DWORD, default 1).
- `AutomaticDetectionEnabled` (DWORD, default 0) — drives `DeviceManager.scan(scanForEver)`. Bluetooth watchers only; HID discovery always re-runs each tick since it is a local setupapi walk, not a radio scan.
- `OneIconPerDevice` (DWORD, default 0) — one tray icon per device instead of a single lowest-battery icon.
- `HideUnknownBattery` (DWORD, default 0) — skip devices whose level is still `-1`.
- `Language` (**string**, default `""`) — two-letter interface language code; empty follows Windows. The only non-DWORD value here, and the only one `Program` reads directly rather than the Settings form.

Auto-start is implemented by writing the exe path to **`HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run\PeripheralBatteryMonitor`**.

### Diagnostics

`Diagnostics.Log` writes `%LOCALAPPDATA%\PeripheralBatteryMonitor\log.txt` **always, with no
switch anywhere** — no setting, no registry value, nothing to start. It opens itself on the
first line written (from inside the `Settings` constructor, during the first HID
enumeration) and rolls at 1 MB keeping one previous generation. **Core owns the whole
thing.** The tray's *Open log folder* entry only reveals the file; it generates nothing.

- **It was opt-in and that was wrong.** A checkbox, a registry DWORD, `Start`/`Stop`/`IsEnabled`
  and a guard at every call site are each a way for the log to be missing in exactly the
  session worth reading — and the person who needs it is being talked through it by someone
  who cannot see their screen.
- **The privacy point survives the setting.** The file records device names and HID paths,
  several of which embed hardware ids, and it is written to be pasted into a public issue.
  That mattered when it was opt-in; it matters *more* now that nobody opts in. Deleting the
  file is the only opt-out left, which is why the handle carries `FileShare.Delete` —
  without it Explorer refuses the delete while the app runs.
- **Not `Trace`, not `TraceSource`, and not `Debug`.** `Debug.WriteLine` is
  `[Conditional("DEBUG")]`, so it was compiled out of the only build anyone reporting an
  issue ever runs — that is the bug this replaced. `TRACE` by contrast *is* defined in
  Release by the SDK, but nothing calls `Trace` either, which sidesteps the whole class of
  hazard. `TraceSource` was considered and rejected: rotation is the one thing
  `System.Diagnostics` does not ship (`TextWriterTraceListener` only appends), so a custom
  listener gets written either way — and constructing a `TraceSource` calls
  `ConfigurationManager`, loading `System.Configuration.dll` to hunt for an `.exe.config`
  **this app is forbidden to ship** (CI fails on any file beside the exe). New code logs
  through `Diagnostics.Log`.
- **The single writer is guaranteed by the `Local\` mutex** in `Program.Main`, which returns
  before `EmbeddedAssemblies.Install` and so before any Core type loads. Two Windows users
  get separate sessions *and* separate `%LOCALAPPDATA%`. Changing that mutex to `Global\`
  would mean revisiting this.

## Where the rest lives

Folder-level `CLAUDE.md` files load only when work touches that folder:

- `src/PeripheralBatteryMonitor.App/CLAUDE.md` — DPI, the windows and tray, interface language, crash logging.
- `src/PeripheralBatteryMonitor.Core/CLAUDE.md` — `Contracts/` and `Hid/`, WinRT bridging, diagnostics internals and the startup snapshot.
- `src/PeripheralBatteryMonitor.Core/Providers/CLAUDE.md`, plus one per vendor folder (`Apple`, `EightBitDo`, `Logitech`, `Razer`, `SteelSeries`) — the registry and each device protocol.

## Conventions worth preserving

- **Do not comment code that explains itself.** A comment earns its place only when a reader cannot recover the *why* from the code: a tricky decision, the obvious alternative and why it was rejected, a vendor quirk, an ordering that looks arbitrary and is load-bearing, a number that was measured rather than chosen. Everything else is noise — restating what the next line does, an XML doc comment that is the method name written as a sentence, a banner over a self-evident block. It costs a reader time, it has to be kept true, and it goes stale silently, which is worse than never having been written.
  - **Make the code say it instead.** If a comment can be deleted by renaming a variable, extracting a method or naming a constant, do that and delete it. A magic number gets a name, not a footnote.
  - **This file is where the durable *why* lives**, not a banner above every method. Prefer one paragraph here to the same explanation repeated across three files.
  - **The heavy commenting already in the tree is not a licence to add more.** New and edited code follows this rule; when you touch a file, leave it no more commented than you found it.
- One name throughout: `PeripheralBatteryMonitor` is the repo, the solution, the exe and the root namespace of both projects; the two csproj files add only a `.App` / `.Core` suffix. The author's old `oz` prefix (`ozBluetoothLEBatteryMonitor`, `ozPeripheralBatteryMonitor`) has been dropped — don't reintroduce it.
- **New non-UI code goes in Core, not App.** The App project is the tray icon, the two windows and the registry settings they write; everything about *finding a device and reading its battery* belongs on the other side of the boundary the csproj guard enforces. `Settings.cs` is already the largest file in App and should not grow logic.
- The app was called `BluetoothLEBatteryMonitor` until it grew past Bluetooth (Logitech LIGHTSPEED devices reach the PC over a USB dongle, no Bluetooth involved). **Do not reintroduce a transport into the product name.** Transport names are still correct *inside* the provider layer — `BluetoothLEBatteryProvider`, `BluetoothBatteryProvider`, `DeviceTransport.BluetoothLowEnergy`, `Providers/BluetoothLE/` — because those really are transport-specific; the app as a whole is not.
- The rename was a **clean break** on persistence: settings moved to `HKCU\SOFTWARE\PeripheralBatteryMonitor` and the auto-start value to `Run\PeripheralBatteryMonitor` with no migration, by decision. Anyone upgrading from a pre-rename build is reset to defaults and keeps a dead `Run\BluetoothLEBatteryMonitor` entry pointing at the old exe name, which this app will not clean up. Say so in the release notes.
- Tray-icon thresholds are baked into `Settings.UpdateIcon` as cascading `if/else` (≥90/70/50/30/else → 100/80/60/40/20). The five `.ico` resources must stay in lockstep with these buckets. **The last bucket is `else`, not `> 0`.** `UpdateSingleIcon` used to skip the assignment entirely when the lowest reading was `0`, which left the tray on the designer's `Icon_Battery_100` — a flat device showed a full battery. The sentinel for “nothing readable” is the `100` that `theLowestBattery` is initialised to, never `0`, so nothing needs guarding there. `Icon_Battery_20` already carries a pure-red fill bar, so 0% reads as critical without a sixth icon.
- New BLE features should go through `BatteryDevice` rather than reading GATT from the UI layer; `Settings` only ever talks to `DeviceManager` and `BatteryDevice` accessors.
