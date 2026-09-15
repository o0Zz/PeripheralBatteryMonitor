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

- **.NET Framework 4.8**: ships with Windows 10 1903+ and Windows 11, so the exe runs on a clean machine. It is also the only target that consumes `Windows.winmd` the way this app does — see **WinRT bridging**.
- **A single executable**, `PeripheralBatteryMonitor.exe`. The README tells users to download one file and the release workflow uploads the bare exe, so that has to stay true now that Core is a separate assembly: net48 has no `PublishSingleFile`, so the `EmbedReferencedAssemblies` target in `PeripheralBatteryMonitor.App.csproj` embeds every copy-local DLL as a manifest resource and `EmbeddedAssemblies` serves them back through `AppDomain.AssemblyResolve`. The same items are removed from the copy-local set, so `bin/` holds the exe alone — which means an F5 run exercises exactly the path a release uses. `Program.Main` installs the resolver before calling `Run`, which is `[MethodImpl(NoInlining)]` for that reason: `Run` mentions `Settings`, whose fields are Core types.
- **WinForms.** Never add `PublishAot` or `PublishTrimmed`. On net48 `UseWindowsForms` does not apply — `PeripheralBatteryMonitor.App` uses classic `<Reference>` items.
- CI patches the version with `-p:AssemblyVersion=` / `-p:FileVersion=` / `-p:Version=`. There is no `AssemblyInfo.cs` to edit — the SDK generates the attributes.

## Layout

```
src/PeripheralBatteryMonitor.Core/   net48 — discovery and battery reading. ZERO UI dependencies.
  IDeviceNotification.cs             the UI callback contract
  DiagnosticReport.cs                the snapshot every session's log opens with
  BluetoothRadio.cs                  the radio off/on toggle behind the tray's Restart Bluetooth
  DeviceManager.cs                   the two Bluetooth watchers + the HID reconcile
  HidDeviceSource.cs                 the non-Bluetooth discovery snapshot (internal)
  BatteryDevice.cs                   one device's state, bound to one provider
  Contracts/                         the surface providers see; the vocabulary both sides speak
  Diagnostics/                       the log, always on — the bottom of the dependency graph
  Hid/                               vendor-neutral raw HID plumbing
  Providers/                         one folder per device family + the two registries
src/PeripheralBatteryMonitor.App/    net48 — WinForms tray icon and three windows
  Program.cs, Settings.cs, Info.cs, AboutForm.cs, TrayTooltip.cs, EmbeddedAssemblies.cs, Strings.cs
  Languages/                         one key = value file per interface language
  app.manifest                       DPI awareness and the execution level
  Properties/Resources.resx          the five tray icons — the only .resx with content
  Resources/                         the .ico files those entries point at
```

`PeripheralBatteryMonitor.Core` referencing `System.Windows.Forms`, `System.Drawing` or any UI package is a **build-breaking error**, enforced by `GuardCoreHasNoUiDependencies` in `PeripheralBatteryMonitor.Core.csproj` (`PBM0001`). Core also sets `DisableImplicitFrameworkReferences` and lists what it needs, so the boundary is real rather than nominal. A battery level crosses as an `int`; a tray `Icon` is picked in `Settings.GetIconForBatteryLevel` and never travels the other way.

Both projects use root namespace `PeripheralBatteryMonitor`. The files at Core's root are in that namespace, which is why the App does not `using` anything to reach Core: everything it touches lives there. The root is the assembly's public API and the compiler now says so — App references exactly `BatteryDevice`, `DeviceManager`, `IDeviceNotification`, `DiagnosticReport` and `Diagnostics.Log`, while `HidDeviceSource` is `internal` because only `DeviceManager` drives it. The folders below carry `PeripheralBatteryMonitor.Contracts`, `.Diagnostics`, `.Hid` and `.Providers` (plus `.Providers.<Vendor>`).

## DPI

**`app.manifest` is the only thing that makes this process DPI-aware, and shipping without one is what made the window fonts fuzzy.** A process that declares nothing is DPI-*unaware*: Windows lays it out at 96 DPI and bitmap-stretches the result to the display scale, resampling every glyph. It looks fine at 100% and blurs at 125% or 150% — which is why it only showed up on some monitors.

- The declaration is **system** DPI awareness, not per-monitor v2. On .NET Framework the manifest is only half of per-monitor support: WinForms also needs `DpiAwareness=PerMonitorV2` in `<exe>.config`, and a config file is a second file next to the exe. Claiming per-monitor here without the WinForms half is *worse* than not claiming it — Windows stops scaling the window and WinForms does not start, leaving the UI undersized.
- What system awareness still costs: a window dragged to a monitor at a different scale than the primary is stretched again. Fixing that means restoring `App.config` and giving up the single file.
- **Every form needs `AutoScaleMode.Font` and a matching `AutoScaleDimensions`.** `Info` had neither, which meant `AutoScaleMode.None`: it kept its design-time pixel size while the font grew with the scale, so the rows were clipped. Both forms now declare the 96 DPI baseline `(6F, 13F)`.
- **Two controls need their geometry repaired after scaling, in `Settings.FitInputRows`.** Both are captioned input rows where the caption carries `Anchor = None` so a left-to-right `FlowLayoutPanel` centres it on the control — which only works if the control's own geometry is honest, and for these two it is not.
  - **`NumericUpDown` is a `ContainerControl`**, so it runs its own auto-scale pass on top of the form's and its margin is scaled more than once: a declared top margin of `3` arrived as **28** at 150%, dropping the spinner nine pixels below "Refresh period:" and stretching the row from 33 to 54 px. Copying the caption's margin puts it back on the one value scaled exactly once. Do not "fix" this by tuning the declared margin — that is what caused it.
  - **`ComboBox` takes its height from its font once**, at construction, and never revisits it, so anything assigned to `Size` is pinned for the life of the form. Left at a designer-written `21`, it was shorter than the 20 px caption beside it while the row sized itself to the 28 px the box actually wanted. It is sized from `PreferredHeight` and from measuring its own widest entry, so a long translation of "same as Windows" cannot clip either.
- **`ListView` column widths do not auto-scale, and docking does not resize them either** — they are plain integers the control never revisits, on both counts. `Info.LayoutColumns` gives the two fixed columns a scaled width and lets the device name absorb the remainder, and is wired to `ClientSizeChanged` so it covers the initial layout and every drag of the window border. It clamps the device column to a floor, because a window dragged narrow would otherwise compute a negative width and throw. — they are plain integers the control never revisits. Columns are built in `OnLoad` rather than the constructor, because auto-scaling has not happened yet when the constructor runs.

## Windows

Three, plus the tray icon and its context menu (Settings / Open log folder / Restart Bluetooth / Refresh / Exit).

- **`Settings`** — the main form, and the tray host. It owns `NotifyIcon`, `IconTimer` and the context menu, and is kept hidden by `SetVisibleCore` unless the user asked for it. Two group boxes, *General* and *Devices and tray icons*, each setting followed by a `GrayText` hint line; a bottom strip with *About…* and *Close*. **There is no OK/Cancel**: every setting is written to `HKCU` in its own `CheckedChanged` handler the moment it changes (all of them through `Settings.SaveSetting`, the one place that opens the key and is certain to close it), so there is nothing pending for a Cancel to discard, and *Close* only hides the window.
- **`Info`** — the per-device list, shown by double-clicking the tray icon. Sizable, so the `ListView` is `Dock = Fill` and the `StatusStrip` `Dock = Bottom`. It used to be neither: the list was pinned at `(0, -3)` with a hand-fitted `416x143` — the negative Y hid its top border and the height was eyeballed to clear the status strip, landing two pixels short of it. A control with no `Dock` and no `Anchor` does not move, so dragging the window border left the list stranded at its design size. `BorderStyle = None` because the list *is* the window here, which is what the `-3` was approximating.
- **`AboutForm`** — version, what the app does, and the device families it can read. Built in code rather than from a designer file, so it has no `.resx`. Shown with no owner, because the owner would be the hidden `Settings` form and `ShowDialog` refuses an invisible owner.

**Manual refresh has two entry points and one implementation.** `Settings.RefreshNow` is
the tray menu's *Refresh* entry and the `Info` window's right-aligned *Refresh* button on
its `StatusStrip`; both call it, so refreshing from the list also updates the tray icon and
tooltip. It wraps `UpdateIcon` in a wait cursor — a poll is real I/O on the UI thread, a
single GATT read alone allows itself 5 s — and then restarts `IconTimer`, so the next
automatic poll is a full period away instead of firing seconds after the user asked for one.
**It is also the only pass that passes `rediscover: true`**, which travels through
`DeviceManager.refreshHidDevices` and `HidDeviceSource.Discover` to
`IHidDeviceExpander.Expand` and there means "re-probe, don't serve the cache". Parts of
discovery must cache — a Logitech receiver sweep is six radio round trips, far too expensive
per tick — and a Refresh that returned a cached answer is a button that does nothing, which
is exactly how a device that is merely *not yet found* gets reported as one the app cannot
find at all. The interval on a fruitless sweep can be as long as it is only because this
exists to skip it.
`Info` receives it as an `Action` through its constructor rather than polling itself, the
same reason `hideUnknownBattery` arrives as a `Func<bool>`: that window renders battery
state, it does not own it. Its row-building moved out of `Info_Activated` into `Populate`,
which reads cached state only; the button polls first, then repopulates. A `ToolStripItem`
was chosen over a real `Button` because it auto-sizes to its text, so "Aktualisieren" widens
the item instead of being clipped — and because clicking one does not deactivate the form,
which matters here: `Info_Deactivate` hides the window.

**Restarting the Bluetooth stack** is the tray menu's second device-facing entry, for the
one failure polling cannot fix: a wedged stack where every provider times out until the
radio is reset. `Settings.RestartBluetooth` drives `BluetoothRadio` in Core — off, five
seconds, on — which is the same thing as the Bluetooth toggle in Windows Settings.
- **The radio, not `bthserv`.** Stopping the Bluetooth Support Service needs administrator
  rights and does not touch the radio; disabling the device node resets it but also needs
  elevation. `Windows.Devices.Radios` needs neither, which is what keeps this app
  non-elevated.
- **`BluetoothRadio.RequestAccess` runs on the UI thread and the restart does not.** The
  access request is the one call that can put a consent prompt on screen, so it wants a
  thread with a message loop; the restart then goes to a `ThreadPool` worker, because
  sleeping five seconds on the UI thread freezes the tray icon and its menu and has Windows
  declare the app hung. Completion returns through `BeginInvoke` — guarded by `IsDisposed`,
  since *Exit* is reachable during the downtime and posting to a dead form would throw on a
  thread where nothing catches it.
- **`IconTimer` is stopped for the duration.** A tick landing while the radio is down would
  wait out a 30 s GATT connect timeout per BLE device, on the UI thread, to read nothing.
- **Discovery is started over on the way back**, not left to the watchers: every Bluetooth
  device dropped off while the radio was down, and a watcher that had already finished its
  enumeration never reports them returning. HID devices need nothing — the poll tick
  re-snapshots those.
- **Progress is a balloon but a failure is a message box**, and neither goes through
  `Settings.Notify`: that honours the notifications checkbox, which is about a device
  reaching 20% rather than about feedback for something the user just clicked. A failure
  leaves the radio *off*, so it must not be droppable. `DescribeFailure` unwraps the
  `AggregateException` that a faulted WinRT task arrives as, whose own message is a sentence
  about aggregate exceptions.
- **The entry hides itself on a machine with no Bluetooth radio**, decided once in the
  `Settings` constructor rather than on `Opening`: enumerating radios is a WinRT call, and
  the moment the user reaches for this entry is the moment the stack has stopped answering —
  a check there would hang the very menu it is decorating.

**Lay these out with panels, not coordinates.** `Settings` is a `TableLayoutPanel` of two `GroupBox`es, each holding a top-down `FlowLayoutPanel`; every hint is an `AutoSize` label with `MaximumSize = (400, 0)` so it wraps and grows downwards. The form itself is `AutoSize` — the height that fits depends on where those labels wrap, which depends on the display scale, so the only version that is right at both 100% and 150% is the one that asks its own contents. A fixed `ClientSize` here clips at the bottom.

A docked child's position depends on z-order, not on the order it was written: docking is applied from the **highest** index down, so a `Dock = Fill` panel must be added to `Controls` *before* the `Dock = Bottom` button strip to end up laid out last and take what is left.

**The tray reports connected devices only, and that has two consequences worth knowing.**
`UpdateSingleIcon` and `UpdateIconPerDevice` both skip a device that fails `IsConnected()`,
because a disconnected one keeps its last reading: a mouse switched off at 20% otherwise held
the icon red and occupied a tooltip line for as long as it stayed paired, hiding whatever was
actually in use. The `Info` window deliberately still lists it — that window has a
Connected/Disconnected column and exists to show everything.
- **Per-device mode can now come up empty with devices still tracked**, so `UpdateIconPerDevice`
  returns a `bool` and `UpdateIcon` falls through to `UpdateSingleIcon` when it showed nothing.
  Without that the main icon is hidden and no per-device icon exists — the app has *no* tray
  presence and no way to reach its own menu.
- **The per-device icon cleanup is keyed on what was painted this pass, not on what the manager
  still tracks.** A disconnected device (or one that "hide unknown battery" now filters) stays in
  the dictionary, so the old `deviceDict.ContainsKey` test left its icon on the tray showing a
  stale percentage for ever. The low-battery latch is still keyed on the dictionary though —
  clearing it on a mere disconnect would re-fire the balloon every time a flat device wakes up.

**`NotifyIcon.Text` holds 63 characters, not 64.** The buffer is 64 including its terminator and WinForms throws `ArgumentOutOfRangeException` at 64 — on the polling tick, which makes it an unhandled exception that kills the tray app. `TrayTooltip.Fit` is the only thing that may assign it, and it lives in its own file rather than in `Settings` because none of it touches WinForms — it is string fitting, and `Settings.cs` is already the largest file in the project. It first preserves every device and reading by sharing the available name space across lines; every shortened name ends in `…`, so it cannot masquerade as a different full device name. Only when even those shortest marked lines cannot all fit does it fall back to the caller's priority order: real readings first, lowest battery first (that is the one the tray icon is showing), then `?` devices, followed by a final ellipsis line.

Both forms take their icon from `Properties.Resources.Icon_Battery_100` rather than from a per-form `$this.Icon` blob in their `.resx`. The `.resx` copies were the same artwork three times over, ~12 KB of base64 each, and lacked the hand-tuned 16 px frame the `.ico` carries — so the title bar was downsampling a 256 px PNG. `Settings.resx` and `Info.resx` now hold no resources at all, only designer metadata.

## Interface language

`Strings` serves every piece of UI text from one `key = value` file per language in
`src/PeripheralBatteryMonitor.App/Languages/` (`en`, `fr`, `de`, `it`, `es`, `zh`), embedded in the
exe. `en.lang` is the master and the per-key fallback, so a half-finished translation shows
English rather than raw key names, and an unknown key renders as itself rather than throwing.

- **Not .resx.** Satellite assemblies are DLLs in per-culture subfolders and this app is one
  file. A plain text file is also something a translator can open.
- **Adding a language is adding a file.** The picker is built from what is embedded and the
  csproj globs `Languages\*.lang`. `WithCulture=false` on that item is load-bearing — without
  it MSBuild sees `de.lang` as a culture-specific resource and builds a satellite assembly.
- **`Strings.Use` sets `CurrentUICulture`, never `CurrentCulture`.** This app writes registry
  DWORDs and talks to devices over binary protocols; none of that may change because the window
  is in German.
- **`Program.Main` picks the language before the first window exists.** Every form reads its
  text in its constructor, and `Settings` is built once and kept alive for the whole session, so
  a language chosen later would arrive too late for it. That is also why `Program` reads
  `Language` from the registry itself instead of leaving it to the form.
- **The designer files keep English literals.** They keep the WinForms designer rendering a sane
  form and double as the last-resort fallback; `ApplyStrings()` overwrites every caption right
  after `InitializeComponent` and again whenever the picker changes.
- **Changing language applies live, and that only works because of the panel layout.** Every
  label is `AutoSize` with a `MaximumSize` wrap width inside an `AutoSize` form, so the window
  re-measures itself around the new text. `Settings.ApplyStrings` also has to poke `Info` — it is
  long-lived too — and re-run `UpdateIcon`, since the tray tooltip is built from translated text.
  `AboutForm` is built fresh each time it opens and needs nothing.
- **Only `@name` is untranslated on purpose**: each language names itself, because someone
  hunting for their language in an interface they cannot read is looking for the word
  "Français".
- Device names come from the hardware and are never translated. Neither are brand names.

## Architecture

The app is a **single-form WinForms tray application**. The form is created but kept hidden — `Settings.SetVisibleCore` suppresses visibility unless the user explicitly opens it from the tray menu. The form's job is to host the `NotifyIcon` and a `Timer` that drives polling.

`Program.Main` owns the session-local `Local\o0Zz.PeripheralBatteryMonitor` mutex for the full message-loop lifetime. A later launch exits before constructing a form, so repeatedly opening the executable cannot create duplicate tray hosts. Keep this before `EmbeddedAssemblies.Install` / `Run`: it depends only on the framework and must reject the duplicate before application state is initialised.

Three layers, all in namespace `PeripheralBatteryMonitor`; the first two are the App project, the third is Core:

1. **`Program.cs`** — entry point; installs `EmbeddedAssemblies` and then runs `Application.Run(new Settings())`.
2. **UI** — see **Windows** above. `Settings.UpdateIcon()` is the polling tick: it calls `deviceManager.refreshHidDevices()` (the HID sources have no watcher, so the tick is what picks up a plugged/unplugged dongle), then `device.UpdateBatteryLevel()` on every tracked device, **drops every device that is not `IsConnected()`**, picks the lowest battery level across the ones left, maps that to one of five tray icons (`Icon_Battery_20/40/60/80/100`), updates the tooltip, and fires a balloon notification once per low-battery transition (`lowBatteryNotificationDone` latch resets when level rises above 20%). An `updatingIcon` latch makes it non-re-entrant: HID discovery reports new devices *synchronously*, and `OnNewDevice` calls back into `UpdateIcon`.
3. **`PeripheralBatteryMonitor.Core`** — discovery and battery reading, one type per file:
   - `IDeviceNotification.cs` — the UI callback contract; exposes `OnNewDevice` and `OnDeviceRemoved`.
   - `BluetoothRadio.cs` (`BluetoothRadio`, static) — turns every `RadioKind.Bluetooth` radio off, waits, and turns it back on, via WinRT `Windows.Devices.Radios`. Nothing else in Core uses it; it exists for the tray's **Restart Bluetooth** entry. See **Restarting the Bluetooth stack**.
   - `DeviceManager.cs` (`DeviceManager`) runs **two** `DeviceInformation.CreateWatcher` instances in parallel: one for BLE (protocol GUID `{bb7bb05e-5972-42b5-94fc-76eaa7084d49}`) and one for Bluetooth Classic / BR-EDR (`{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}`). Both feed the same `ConcurrentDictionary<string, BatteryDevice>` keyed by `devInfo.Id`, each device tagged with a `DeviceTransport` (`BluetoothLowEnergy` / `BluetoothClassic`). Only **paired** devices (`devInfo.Pairing.IsPaired`) are tracked. A phone may appear once on each transport, and `ReconcileSiblingNames` copies the Classic endpoint's display name onto its BLE sibling, which fixes opaque iOS BLE local names without guessing from the text. **The `System.Devices.Aep.ContainerId` is what proves the two endpoints are one physical device** — for a *paired* device it is the real PnP container id (verified: a paired device's AEP bag and its nodes in the device tree carry the same GUID). An *unpaired* endpoint instead gets a container synthesised per protocol and address, so the two transports of one unpaired device do **not** share it — irrelevant here, since only paired devices are tracked. The `Communication.Phone` category only narrows the scope and is required on **either** endpoint, not on both: the container already establishes same-device, and the BLE endpoint of a phone is not reliably categorised, so demanding it there is enough on its own to silently disable the whole reconcile. The watcher's `Updated` handler does two things: removes the device if `System.Devices.Aep.IsPaired` flips to false, and forwards property bag changes into the cached `BatteryDevice` via `UpdateProperties`. `Removed` removes the device. `scanForEver` restarts each watcher in its `Stopped` handler for continuous discovery. `refreshHidDevices()` is the **second, non-Bluetooth source**: it reconciles `DeviceTransport.UsbHid` entries against a fresh `HidDeviceSource.Discover()` snapshot (add newly present, remove vanished, never touch Bluetooth entries). `scan()` deliberately does *not* call it — see the re-entrancy note in `UpdateIcon` above; the poll tick is the single driver.
   - **Known limitation: this makes the two entries read alike, it does not merge them.** A dual-mode phone still occupies two rows in `Info` and two tray tooltip lines, now under the same name. The fix at the root would be to track one device per container and pick the endpoint that can actually report a battery; that was left alone deliberately, because choosing the wrong endpoint loses the reading and no dual-mode device was available to verify against.
   - `HidDeviceSource.cs` (`HidDeviceSource`, static) — discovery for devices that reach the PC over raw USB HID and so have **no** association endpoint and no pairing (a peripheral on its own vendor dongle). There is nothing to subscribe to, so this is a plain snapshot, cheap enough to re-run every tick. It surfaces only interfaces claimed by a registered `HidDeviceSpec`, derives the device id from the HID interface path (the analogue of `DeviceInformation.Id`; moving the dongle to another USB port therefore reads as a different device), and seeds the property bag with the `PeripheralBatteryMonitor.Hid.*` keys so a provider can reopen that exact interface without re-enumerating. It returns `HidDiscoveredDevice` descriptors rather than raw interfaces, because **one interface is only *usually* one device**: a spec may carry an `IHidDeviceExpander`, and a receiver is one interface holding up to six peripherals. A device that *is* its interface keeps the bare path as its id, byte for byte what it was before receivers existed; a receiver child gets `#01` appended and a `PROP_HID_DEVICE_INDEX` in its bag. Constructing a descriptor lives in `Providers/ProviderHid`, not here, because an expander lives under `Providers/` and must not name a root type.
   - `BatteryDevice.cs` (`BatteryDevice`) owns one device's battery state — a thin state holder + `IBatteryDeviceContext`. Two constructors: one from a WinRT `DeviceInformation` (Bluetooth), one from `(id, name, transport, properties)` for devices with no `DeviceInformation` behind them (HID). Each device **binds to one `IBatteryProvider`** and remembers it (see `Providers/`). `UpdateBatteryLevel()` first re-reads the bound provider as a fast path; if that yields nothing it probes the other priority-ordered providers and binds to the first whose `ReadBattery(this)` returns a value. Re-probing on an empty read (rather than staying stuck on a provider that went quiet) lets a device recover from a transient failure and lets a **higher-priority** provider preempt when it comes online (e.g. GATT once the device connects). A `null` reading means "can't read right now" and leaves the previous `batteryLevel`; `-1` means never read, which `Settings.UpdateIcon` filters out. `IsConnected()` is three sources, most authoritative first, and is **not** a switch on transport: the **bound** provider's `IDeviceLinkState` if it has one (only GATT does), else `System.Devices.Aep.IsConnected` from the property bag (both Bluetooth transports publish it), else `lastReadSucceeded`. Each step exists for a reason. It must be the *bound* provider and not the first candidate implementing the interface — the candidate list holds every registered provider, so for any BLE device that always found `BluetoothLEBatteryProvider`, whose link is legitimately down when it never opened one, and a BLE device with no GATT battery service reading its level from the Windows property bag reported "disconnected" for its entire life. The AEP property must be consulted *before* the read test, because a battery reading outlives the connection that produced it: Windows nulls `PROP_BATTERY_LEVEL` on disconnect and `CacheProperties` drops nulls, so the last percentage stays in the bag and `BluetoothBatteryProvider` keeps handing it back. And `lastReadSucceeded` is the last resort for `UsbHid`, where no OS-level connection state exists for a device behind a dongle — the dongle stays plugged in with the peripheral switched off, so answering *is* the liveness test.
   - **The battery layer** — three folders side by side at the project root, one type per file. There is no wrapper folder over them: the whole assembly *is* the battery layer, so a `Battery/` segment would only have repeated the product name inside itself, and a folder called `Core/` inside `PeripheralBatteryMonitor.Core` meant two different things one level apart. Dependencies run one way — `Providers/` and the root files depend on `Contracts/`, `Providers/` and the discovery code depend on `Hid/`, and nothing depends back — verified: no file under `Providers/` or `Hid/` names a root type. **`Diagnostics/` is the exception that keeps the rule a rule: it is the bottom of the graph — it depends on nothing, not even `Contracts/`, and every other folder may depend on it.** `Hid/HidDevice` has to be able to say *why* a `CreateFile` failed, so `Hid/` does now carry one `using PeripheralBatteryMonitor.Diagnostics`; a `Log` at the root instead would have made the graph circular, since the root already depends on `Hid/`. That one-way rule is why `Contracts/` is a folder rather than five files loose at the root: `BatteryDevice` implements `IBatteryDeviceContext`, which `IBatteryProvider` consumes, which `BatteryDevice` calls — keeping the contract separate from the implementations is what stops that from being a cycle between folders.
     - **`Contracts/`** — the surface a provider sees plus the vocabulary discovery and providers both speak, so the two depend on this rather than on each other. **The folder is named for that role, and must not be renamed after the form of its contents** — call it `Abstractions/` or `Interfaces/` and it advertises "interfaces live here", a rule it does not follow: `DeviceTransport` and `DeviceProperties` then read as intruders when they are the shared vocabulary itself, and `DeviceTransport` could not move anyway, since it appears in `IBatteryDeviceContext`'s own signature. The mirror image of the same trap: `IDeviceNotification` is an interface that stays at the **root**, because it hands out a concrete `BatteryDevice` (filing it here would invert the one-way rule) and because it is the Core-to-App contract, a different axis from this one. `IBatteryDeviceContext` (read-only view `BatteryDevice` exposes: `DeviceId`, mutable `DeviceName`, `Transport`, `TryGetProperty`); `IBatteryProvider` (a single `ReadBattery(ctx)` → `int?` that may do I/O and cache what it establishes — a `null` return means "can't read this device right now", covering both "doesn't apply" and "momentarily unavailable", so it doubles as the capability check); `DeviceTransport` (`BluetoothLowEnergy` / `BluetoothClassic` / `UsbHid`, consulted by providers that apply to only one transport — GATT is BLE-only, Logitech is `UsbHid`-only); `IDeviceLinkState` (optional; a cheap no-I/O `IsLinkUp(ctx)` that `BatteryDevice.IsConnected()` uses for BLE — implemented only by `BluetoothLEBatteryProvider`, and found via `provider as IDeviceLinkState` rather than by type name); `DeviceProperties` (the `PROP_*` property-bag keys, referenced by both the discovery layer's `requestedProperties` and the providers — canonical Windows names plus the app's own `PeripheralBatteryMonitor.Hid.*` keys, which are synthesised by `HidDeviceSource` and prefixed so they can never collide with a real Windows property). **`PROP_BATTERY_LEVEL` is a 0–100 percentage, and Windows publishes it under two names** — the raw DEVPROPKEY `{104EA319-…} 2` *and* the canonical `System.Devices.BatteryLife`, which is documented with that same formatID and propID. The property bag carries both spellings side by side (verified by dumping a paired device's AEP bag), so they are aliases of one value, not two sources: read it once, through `PROP_BATTERY_LEVEL`. There is no coarse Critical/Low/Average/Full enum behind either spelling — a provider that switched on `1..4` here (as a deleted `CoarseBatteryProvider` once did) would report a 3% battery as 60%. Both spellings arrive as null while the device is disconnected, and `BatteryDevice.CacheProperties` drops nulls, so absence means "asleep".
     - **`Hid/`** — vendor-neutral raw HID plumbing, so more than one provider family can talk HID without duplicating P/Invoke. `HidNative` (the whole setupapi + hid.dll + overlapped-I/O P/Invoke surface; keep P/Invoke confined here); `HidInterfaceInfo` (one top-level collection: path, VID/PID, usage page/usage, report lengths — note a single physical device publishes **several** collections, so the usage page/usage pair is what identifies the one worth talking to); `HidInterfaceEnumerator` (lists present interfaces, pre-filtered by vendor id on the path string — matching bare hex digits so it works for both USB `vid_046d` and Bluetooth `vid&0002004c` forms — and opens each with desired access **0**, the documented query-only mode, because Windows opens keyboards and mice exclusively); `HidDevice` (an open interface, in **one of three modes**); `HidDeviceSpec` (which interface stands for a trackable device, matched on VID + optional PID list + usage page/usage + an optional **exact feature report length**). That last one exists because usage page and usage are only a good discriminator when the protocol rides on a *vendor-defined* page. Razer's does not -- it answers on the consumer-control collection, next to the volume keys -- so the 91-byte feature report is what actually identifies it.
       `HidDevice`'s two open modes are not interchangeable. `Open()` is FILE_FLAG_OVERLAPPED and supports `Write`/`Read`, for a conversation where the answer arrives as an input report the device sends — a HID read blocks until the device says something, which on the UI thread would hang forever, so each operation is issued async, waited on, then `CancelIo`'d. `OpenForReportRequests()` is deliberately **not** overlapped and supports only `GetInputReport`, which asks for a report by id instead of waiting: `HidD_GetInputReport` issues a synchronous DeviceIoControl with a NULL OVERLAPPED, which Windows documents as unreliable on an overlapped handle, and there is no timeout to lose because the call never waits on device traffic. `Read`/`Write` refuse a non-overlapped handle outright (`EnsureOverlapped`) rather than silently running unbounded. `OpenForFeatureReports()` is the third mode and the only one opened with **desired access 0**, which is the point of it rather than an optimisation: a vendor protocol carried on feature reports often sits on a collection Windows opens exclusively for itself, where `CreateFile` with `GENERIC_READ|GENERIC_WRITE` fails outright with `ERROR_ACCESS_DENIED` -- measured on this machine against every generic-desktop mouse and keyboard collection. The IOCTLs behind `HidD_GetFeature`/`HidD_SetFeature` are declared `FILE_ANY_ACCESS`, so a query-only handle drives them perfectly well; it just cannot `ReadFile`/`WriteFile`, which that mode does not offer.
       Either way, open one only for the duration of a transaction, so the driver's per-handle report queue can't hand back a stale frame.
     - **`Providers/`** — `BatteryProviderRegistry` (ordered `Register(Func<IBatteryProvider>)` + `CreateProviders()`; registration order **is** priority order; the static ctor wires the six built-ins) plus one folder per device family. **To add a new source, implement `IBatteryProvider` and `Register` a factory** (before the first `BatteryDevice` is created) — no changes to `BatteryDevice`. One instance is created per device, so a provider may cache per-device state. Providers are named after the **transport** they serve, since that is what decides whether one applies at all; each rejects the transports it doesn't serve as its first act, so priority order only decides who wins where several could answer. The families, in priority order: `BluetoothLE/BluetoothLEBatteryProvider` (GATT Battery Service `0x180F` / char `0x2A19`, BLE-only, `supportGattBattery` latch, 30 s connect / 5 s read timeouts, also implements `IDeviceLinkState`); `Bluetooth/BluetoothBatteryProvider` (the level Windows itself publishes, `PROP_BATTERY_LEVEL`, 0–100, either Bluetooth transport — it covers devices with no GATT battery service); `Apple/AppleBatteryProvider`; `Logitech/LogitechBatteryProvider`; `SteelSeries/SteelSeriesBatteryProvider`; `Razer/RazerBatteryProvider`.
       `ProviderHid` sits beside them, holding the two helpers a HID-discovered provider needs to reopen its own interface from the property bag. It lives in `Providers/` and not in `Hid/` or `Contracts/` because it is the only layer allowed to depend on both — putting it in either would make one depend on the other and collapse the split those folders exist for.
       Note the two Bluetooth providers are *not* two sources for the same thing: the BLE one reads the device directly over GATT, the other reads what Windows already knows. A former fifth provider, `CoarseBatteryProvider`, was deleted because it was a *third* reading of the second one's property (via the `System.Devices.BatteryLife` alias) and misinterpreted it as a 1–4 enum — unreachable in practice, and wrong if reached.
       Alongside it, `HidDeviceSpecRegistry` is the discovery-side companion: `BatteryProviderRegistry` says *how* to read a battery, this says which non-Bluetooth devices exist at all. **Only register a spec for a device with no Bluetooth association endpoint** — a Bluetooth device is already found by the watchers, so registering it here would list it twice. That is exactly why `AppleBatteryProvider`, which also reads over raw HID, registers nothing.

     - `SteelSeries/SteelSeriesBatteryProvider` covers **Arctis Nova** wireless headsets on their own USB dongle — the same discovery shape as Logitech, a far simpler protocol. Write the single command byte `0xB0` to the vendor collection at usage page `0xFFC0` / usage `0x0001` and the dongle answers with a status report. There is no feature discovery and nothing worth caching, so unlike the Logitech provider this one holds no per-device state: every per-model difference is static data in its `Models` table.

       **The reply layout is not uniform and neither is the level byte.** Nova 7 (and the 7P/7X/Diablo/WoW variants that share its firmware) put the level at documented `data[2]` with the on/off state at `data[3]`, where `0x00` means the headset is off; Nova 5 base stations put the state at `data[1]` (`0x02` = off) and the percentage at `data[3]`. Worse, whether the level *is* a percentage depends on the product id: the pre-2026 ids report a discrete `0..4`, which maps to 0/25/50/75/100 and is much coarser than the number suggests.

       **Every published description of this protocol is written against hidapi, which is off by one from what this code sees.** hidapi strips the leading report-id byte when a collection declares no report ids; `HidDevice.Read` goes through `ReadFile`, which always returns it. So `data[2]` in any reference is `reply[3]` here, and `NovaModel` stores the already-shifted index. Get it wrong and you read a plausible neighbouring byte rather than failing — check `LevelIndex` first if a model reports nonsense.

       `HidSpec` is assigned in a **static constructor, not a field initializer**, and that is load-bearing: initializers run in declaration order, so building the spec inline would read the `Models` table at the bottom of the file while it was still null and take `HidDeviceSpecRegistry`'s static constructor — and the app — down with it.

       **Its product ids are not verified against hardware**, unlike every other id in this build. They come from the HeadsetControl project's device tables. Treat a model reporting a wrong level as unproven rather than as a transport bug.

     - `Razer/RazerBatteryProvider` + `Razer/RazerReport` cover Razer wireless mice, and are the only place a vendor conversation runs on **feature reports** instead of the report streams. One transaction is `SetFeature` with a 90-byte request, a 60 ms pause, then `GetFeature` reading the answer out of the same structure -- there is no separate response channel, so the reply echoes the transaction id, command class and command id, and carries a CRC. Checking all four is what tells a real answer apart from the request bytes being handed straight back. Battery is command class `0x07` id `0x80`, and the level arrives in argument byte 1 as **0..255, not a percentage**.

       The **transaction id is per-model and load-bearing** on a dongle: `0x1F` for the V3/V4 generation and the Orochi V2, `0x3F` for the V2 generation. Send the wrong one to a device behind a receiver and it stays silent.

       A raw level of `0` is treated as "can't read right now" rather than a flat battery. A mouse that is switched off answers 0, and reporting that as 0% would fire the low-battery balloon on every poll for a mouse sitting in a drawer.

       **The transport is verified; the battery path is not.** A wired Basilisk V3 (`0x0099`, no battery, deliberately absent from the model table) was used to prove the whole chain on real hardware: firmware-version and serial-number commands both returned `status=0x02` with a valid CRC and correct echo -- the serial decoded to readable ASCII -- while the battery command returned `status=0x05`, *not supported*, exactly as a mains-powered mouse should. So the framing, CRC, offsets, access-0 handle and 60 ms settle are all confirmed correct; what remains unproven is only that a battery-carrying model answers the battery command as the table says. Ids and transaction ids come from the MIT-licensed `xzeldon/razer-battery-report`.

       Two attempts per read, not the ten a standalone tool uses: this runs on the UI thread and a measured transaction costs ~72 ms.
     - `Logitech/LogitechBatteryProvider` covers Logitech **LIGHTSPEED** devices that talk to their own USB dongle (verified on the PRO X Wireless headset, VID `0x046D` / PID `0x0ABA`). These are not Bluetooth devices at all — hence the HID discovery path — and Windows exposes no battery for them whatsoever: `PROP_BATTERY_LEVEL` is absent from every one of the device's nodes, and there is no HID-battery node either (both checked directly). Battery comes from **HID++ 2.0** over the vendor collection at usage page `0xFF43` / usage `0x0202` (20-byte reports), at device index `0xFF` (the device behind its own receiver, as opposed to indexes 1–6 on a multi-device Unifying receiver). `Logitech/HidppTransport` does the framing — `[reportId][deviceIndex][featureIndex][functionId<<4 | swId][params…]`, replies echo the first four bytes so they can be told apart from the unsolicited notifications arriving on the same interface, and feature **indexes are per-device** so they must be resolved at runtime through the root feature rather than hardcoded. It also exposes `Ping` (root func 1). **The ping's test is that a reply came back, not that the magic byte was echoed.** The transport has already matched the reply to that exact request — device index, feature index, and the function byte carrying our nonzero software id — so a frame reaching `Hidpp.Ping` is an answer to it by construction, and the echo was the same check a second time. Insisting on it cost a real device: the PRO X 2 LIGHTSPEED answers a root ping with parameters `01 10 00` and no echo anywhere, so every poll declared a headset that had just replied to be switched off and never read it. A missing echo is logged, because it says something about a framing nobody has decoded; it no longer decides anything.
       Which feature carries the battery **varies per device**, so the provider probes a chain and binds to the first that both exists and yields a value, caching `(featureId, featureIndex)` so steady-state polling is one transaction (measured: ~42 ms per poll, ~147 ms for the first resolving read). Order is most-to-least precise: `0x1004` UNIFIED_BATTERY (func 1 → state-of-charge percentage, falling back to its discrete level bitfield — genuinely a coarse four-way enum, so each level maps to a representative percentage: 10/30/60/90), `0x1000` BATTERY_UNIFIED_LEVEL_STATUS (func 0 → percentage, `0` meaning unknown), then the two voltage-reporting ones last because converting volts to a percentage costs accuracy: `0x1001` BATTERY_VOLTAGE and `0x1F20` ADC_MEASUREMENT (both func 0 → `[mV_hi][mV_lo][flags]`, sanity-bounded to 2000–5000 mV) via `Logitech/LogitechVoltageCurve`. That curve is an **approximation** (Solaar's Li-Po discharge table): it is flat between ~3.7 V and ~4.0 V, so a couple of millivolts of noise there moves the result by a percent or two. `Resolve` pings before probing, because otherwise a device that is simply switched off would burn one timeout *per feature* on the UI thread every poll.
       The PRO X is the awkward case that motivated the chain: it implements **none** of the three standard battery features — its feature set is root / featureSet / fwVersion / deviceName / equalizer / sidetone / **`0x1F20`** — so it falls all the way through and binds to `0x1F20` at index `0x06` (`0x0F54` = 3924 mV → 70%).
       **There are three Logitech specs, built in a static constructor, and which one a device belongs to is decided by its framing and by whether it is a receiver.** `HidSpec` is the direct HID++ case above. `CenturionHidSpec` is the newer headsets. `ReceiverHidSpec` is a dongle holding other people's devices. All three are still restricted to verified — or at least published — product ids, but the reason has changed and so has what a wrong id costs.
       - **`CenturionHidSpec` — Centurion framing.** The PRO X 2 LIGHTSPEED (PID `0x0AF7`) answers on usage page **`0xFFA0`** / usage `0x0001`, in 64-byte frames with report id **`0x51`**, and does not speak the `0x11` framing at all. `CenturionTransport` does that envelope: `[0x51][cplLength][flags][featureIndex][functionId<<4|swId][params…]` padded to 64, with `cplLength = 3 + params.Length`, and `layer3 = frame[3 .. 2+cplLength)` coming back. **Everything above the envelope is unchanged** — the feature layer, the four battery decoders, the ping, the feature resolve. That works because of the one rule the whole design rests on: *a transport returns a reply normalised to the HID++ long-report layout, so the decoders never learn which framing produced them* (`IHidppTransport`). Solaar does exactly the same thing. Note the software id must stay **nonzero**: the device's own power events arrive on this collection carrying feature index 3 like a real answer, and software id 0 is the only thing that separates them. The `0x50` addressed variant (G522) is deliberately not implemented — its device address has to be brute-forced across 256 values, and an untested branch reads as supported.
       - **`ReceiverHidSpec` — devices behind a receiver, which *are* now supported.** A LIGHTSPEED or Unifying receiver is one interface carrying up to six peripherals at device indexes 1–6; index `0xFF` addresses the receiver itself, which speaks HID++ 1.0 registers and implements no 2.0 battery feature. That is what used to produce the phantom "unknown battery" entry, and it is why the PID allowlist carried the whole weight. **`LogitechReceiverEnumerator` is a better answer at the right layer: a child device exists only because something answered a root ping at its index *and* implements one of the four battery features.** The receiver is never surfaced. So a receiver id shared across products (`0xC547` ships with the G915 X TKL, the PRO X Superlight *and* the G502 X) stops mattering — nothing names the peripheral from the receiver's id, it asks the device, via feature `0x0005` DEVICE_NAME. **A receiver's collection is `0xFF00/0x0002`, not `HidSpec`'s `0xFF43/0x0202`** — the generic vendor page, long reports at usage `0x0002` and short ones at `0x0001`. That is not a variant spelling: a receiver never publishes `0xFF43` and a directly connected device never publishes `0xFF00`, so the two specs cannot match one interface and registration order between them is not load-bearing (receivers stay first anyway). `ReceiverHidSpec` shipped pointed at `0xFF43/0x0202`, which no receiver publishes, so the whole sweep was unreachable and every receiver-attached device was invisible — confirmed against a reporter's log, where a `0xC547` receiver's `0xFF00/0x0002` collection was "claimed by no spec".
         The sweep is cached per interface path, and the three cases are cached for different lengths because they cost different things: 15 minutes when it found devices, 60 s when the collection would not **open** (the usual cause is vendor software holding it, which clears on its own), and 5 minutes when it opened and **nobody answered** — the expensive case, six pings each running to the full timeout. It uses one handle for all six pings at a 300 ms timeout, so the worst case is ~1.8 s once per interval. The 80 ms this started with was measured against nothing, and a timeout chosen to bound a UI stall is worthless if it also bounds out the answer — a receiver has to forward the ping over the air before an awake peripheral can reply. Every slot's outcome is logged, silence included: a sweep that finds nothing is the shape of every "my Logitech device is missing" report, and it must not look like a sweep that never ran.
       - **To add another LIGHTSPEED device, add its PID to the spec that matches its framing.** For `HidSpec` it must still sit on the `0xFF43/0x0202` collection and answer at index `0xFF`; for `CenturionHidSpec`, on `0xFFA0/0x0001`; a receiver id goes in `receiverProductIds` and nothing else is needed, because the peripheral behind it is discovered rather than declared. The startup snapshot prints each collection's usage page and usage, so a report tells you which of the three a device belongs to without owning one.
       - The older `0xFF00/0x0001` short-report (`0x10`) collections are still **not** supported, and receivers did not change that. Two reasons: HID++ 2.0 feature calls work perfectly well over the *long* collection at indexes 1–6, so nothing needs the short frame; and on Windows a receiver's short and long collections are **separate device paths**, so a reply to a short request arrives on the other handle — supporting it is a two-handle transport, not a smaller buffer. (The PRO X collection also rejects report `0x10` outright.) The one thing short would buy is the receiver's HID++ 1.0 register `0x02` connected-device bitmap: one transaction instead of six pings, an optimisation if the sweep ever proves too slow.
       - **None of the Centurion or receiver work is verified against hardware** — it was written from Solaar's implementation and published device tables, for a relayed bug report. Solaar's framing and an independently observed battery exchange agree on the request header exactly and *disagree* on where the reply's payload starts, which is why `LogitechProbe` hex-logs a ping and a root `getFeature` from the startup snapshot rather than guessing that offset. Treat a wrong reading here as unproven, not as a transport bug.
     - `Apple/AppleBatteryProvider`: Apple "Magic" devices (Mouse/Trackpad/Keyboard) report battery only through a vendor HID **input report id `0x90`** (byte[2] = 0–100), invisible to the WinRT Bluetooth property bag. It enumerates via `HidInterfaceEnumerator.Enumerate` on Apple's vendor ids (`0x004C` over Bluetooth, `0x05AC` over USB), then `HidDevice.OpenForReportRequests` + `GetInputReport` — so it holds **no P/Invoke of its own**; the shared `Hid/` layer is the only place SetupAPI and hid.dll are touched. Devices are matched by Bluetooth MAC (Apple reports the MAC as the HID serial number, hence `HidDevice.GetSerialNumber`); a single-Apple-device + Apple-looking-name fallback covers systems where the serial isn't the MAC. Retries the report up to 3× because a GET_REPORT right after wake can fail or return stale data. No extra NuGet dependency.
       Unlike Logitech, these devices are **discovered over Bluetooth** like any other paired device — only the battery *reading* goes over HID — so this provider registers no `HidDeviceSpec`. Note this provider is why `HidInterfaceEnumerator` treats report capabilities as best-effort: it addresses a report by id and never needs the descriptor, so a `HidP_GetCaps` failure must not make the interface vanish from enumeration.

### WinRT bridging

Core consumes WinRT APIs (`Windows.Devices.Bluetooth.*`, `Windows.Devices.Enumeration`, `Windows.Devices.Radios`, `Windows.Storage.Streams`) from .NET Framework 4.8 via the `DirectWindowsWinmd.Net 10.0.15063.0` NuGet package, which provides `Windows.winmd` and `System.Runtime.WindowsRuntime.dll` references. When adding new WinRT calls, expect `IAsyncOperation<T>` — convert with `.AsTask()` and use `Task.Wait(timeoutMs)` rather than `await` (the codebase is synchronous on the UI thread inside the timer tick).

The `PackageReference` is `ExcludeAssets=runtime`, the equivalent of the old `<Private>False</Private>`: the `System.Runtime.WindowsRuntime` facade is part of .NET Framework 4.5+ and resolves from the framework directory at run time, and a `.winmd` is a compile-time contract with nothing to copy. **It is declared on Core, not App, and reaches App transitively** — which App needs, because `BatteryDevice` has a public constructor taking a WinRT `DeviceInformation`.

`DeviceInformation.Properties` is a string-keyed bag that surfaces both canonical names (`System.Devices.Aep.IsConnected`) and raw `DEVPROPKEY`s in the form `"{guid} pid"` (e.g. `"{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2"`). To populate them, the keys must be passed in the `requestedProperties` array to `CreateWatcher` — they're not delivered otherwise. **A property can have both forms, and then both appear as separate entries holding the same value** — `System.Devices.BatteryLife` and `{104EA319-…} 2` are one property under two keys. Requesting both spellings therefore gains nothing and invites reading one value twice, which is exactly the bug `CoarseBatteryProvider` was. `Updated` events carry only the changed keys, so `BatteryDevice` merges them into a `ConcurrentDictionary<string, object>` cache rather than replacing it.

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
- **Opening is lazy and never throws.** Not a static constructor and not a field
  initializer: a throw there poisons the type, and every one of the several dozen unguarded
  call sites — on the poll tick and on WinRT callback threads — would then raise
  `TypeInitializationException`. A full disk must not be why the tray icon disappears.
- **Rotation closes the writer first.** Renaming a file this process holds open is a
  sharing violation; getting that wrong stops the log dead at 1 MB, silently. The byte
  counter is seeded from the file's existing length on open, so appending to a 900 KB log
  rolls after 100 KB and not after another megabyte. If the rename fails anyway the file is
  truncated rather than reopened for append, so the cap is never merely advisory.
- **Every file opens with a banner, written by `Log.Open` itself**: a separator, then
  `PeripheralBatteryMonitor <version> (build date: …) - <repository url>`, the OS, the CLR
  and the culture, and when the file was opened with its UTC offset. Those lines carry no
  timestamp/thread/category prefix — the block is about the file, not an event in it — and
  `WriteRaw` deliberately does not roll, since `Roll` is what calls `Open` and rolling there
  would recurse.
  - **It is in `Log`, not in `DiagnosticReport`, and that placement is the point.** The old
    header was written by the startup snapshot: several lines *into* the file, under the
    traffic that had already opened it, and **once per process** — so a log that rolled
    during a long session arrived with nothing in it naming the build, the OS or the version
    that produced any of it, and long sessions are the ones worth reading. Writing it from
    `Open` makes it the first thing in *every* generation, whatever logs first.
  - **`BuildDate` and `RepositoryUrl` reach the code as `AssemblyMetadata`** from
    `Directory.Build.props`. There is no `AssemblyInfo.cs` to put them in and the exe ships
    alone, so there is no file beside it to read either. `RepositoryUrl` needs the explicit
    item: on its own it is a NuGet packaging property that never reaches an attribute.
    **`BuildDate` is a date and not a timestamp on purpose** — the generated `AssemblyInfo.cs`
    is rewritten whenever a value changes, so a time of day would make every no-op
    `dotnet build` a real recompile of both projects. CI can pin it with `-p:BuildDate=`.
- **Every line carries the managed thread id**, because WinRT `DeviceWatcher` callbacks log
  from arbitrary threads and the file interleaves with no other way to see that it has.
- **Two call sites dedupe on (path, error code)**: the `CreateFile` failure in `HidDevice`
  and the describe failures in `HidInterfaceEnumerator`. Those are the only two that scale
  with (interfaces × ticks), and vendor software holding a collection open makes them repeat
  one unchanging fact hundreds of times a day. A *change* — including back to success — is
  the event worth a line. Everything else logs every tick, on purpose.
- **The single writer is guaranteed by the `Local\` mutex** in `Program.Main`, which returns
  before `EmbeddedAssemblies.Install` and so before any Core type loads. Two Windows users
  get separate sessions *and* separate `%LOCALAPPDATA%`. Changing that mutex to `Global\`
  would mean revisiting this.
- **`DiagnosticReport.WriteStartupSnapshot`** covers the one gap the continuous log cannot:
  discovery's enumeration is pre-filtered to *registered vendor ids*, so the poll tick never
  sees the interface nobody claims — which is the shape of almost every report. It
  enumerates with no filter, once, and writes **one table in one walk** — a fixed-width
  `[  Supported  ]` / `[Not Supported]` marker, the interface, then its path indented to the
  same width, with the claiming spec's name appended to the right where it cannot disturb the
  columns. The marker is fixed-width so the question every report comes down to is both
  scannable and greppable.
  - **The marker says Supported / Not Supported, by the author's decision** — not `used` or
    `claimed`, so don't narrow it back. What it actually reports is whether a registered
    `HidDeviceSpec` claims the collection, and the gap between the two is per *collection*,
    not per device: one device publishes several and at most one is ever claimed, so a
    headset that works perfectly still shows several `Not Supported` rows. Anyone reading a
    pasted log needs to know that; it is recorded here rather than in the file.
  - **Capitalised, which is also what keeps it greppable.** `CenturionTransport` logs a
    multi-fragment reply as lowercase `-- not supported`, so a case-sensitive grep for
    `Not Supported` finds this table and nothing else. `HidDeviceSource`'s per-interface
    `Discovery` line uses the same two capitalised words, so one grep covers both places a
    collection is mentioned.
  - **The probe happens inside that walk**, on the rows that are vendor-defined *and*
    unclaimed (a claimed collection is already traced by the read path on every poll, and a
    generic page has no vendor conversation to have), handing each to whichever probe
    `HidInterfaceProbeRegistry` has for its vendor id — or logging that nobody has one. This
    was a second loop over the same list, which re-matched every interface and re-printed the
    description and path already on the row above, so reading a probe result meant matching a
    device path across two sections by eye. A row and its conversation now sit together, at
    the cost of the table no longer being one contiguous block when a probe fires — which is
    the better trade.
  - `Probe` stays its own method rather than four more lines of that loop **because of its
    try/catch**: one vendor's probe throwing must cost that row, not the rest of the table.
  - **That selection is the root file's and the conversation is the vendor's**:
    `Hid/IHidInterfaceProbe` is the hook, the same split as `IHidDeviceExpander`, and
    `Providers/HidInterfaceProbeRegistry` is the only file naming a probe, so
    `DiagnosticReport` names no vendor. `Providers/Logitech/LogitechProbe` is the one
    built-in. **It writes no header of its own** — the banner above replaced that.
  - It is `BeginInvoke`d from the `Settings` **constructor**, not `OnLoad` — `SetVisibleCore`
    keeps that form hidden, so `OnLoad` does not run until the user first opens the window —
    and it stays on the UI thread because that is where the poll tick runs, so a snapshot and
    a poll can never talk to one HID collection at once.

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
