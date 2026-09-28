# PeripheralBatteryMonitor.Core

Loaded when working under `src/PeripheralBatteryMonitor.Core/`. Moved out of the root `CLAUDE.md`, which keeps the architecture overview; per-vendor detail lives in each `Providers/<Vendor>/CLAUDE.md`.

## Contracts and Hid

- **`Contracts/`** — the surface a provider sees plus the vocabulary discovery and providers both speak, so the two depend on this rather than on each other. **The folder is named for that role, and must not be renamed after the form of its contents** — call it `Abstractions/` or `Interfaces/` and it advertises "interfaces live here", a rule it does not follow: `DeviceTransport` and `DeviceProperties` then read as intruders when they are the shared vocabulary itself, and `DeviceTransport` could not move anyway, since it appears in `IBatteryDeviceContext`'s own signature. The mirror image of the same trap: `IDeviceNotification` is an interface that stays at the **root**, because it hands out a concrete `BatteryDevice` (filing it here would invert the one-way rule) and because it is the Core-to-App contract, a different axis from this one. `IBatteryDeviceContext` (read-only view `BatteryDevice` exposes: `DeviceId`, mutable `DeviceName`, `Transport`, `TryGetProperty`); `IBatteryProvider` (a single `ReadBattery(ctx)` → `int?` that may do I/O and cache what it establishes — a `null` return means "can't read this device right now", covering both "doesn't apply" and "momentarily unavailable", so it doubles as the capability check); `DeviceTransport` (`BluetoothLowEnergy` / `BluetoothClassic` / `UsbHid`, consulted by providers that apply to only one transport — GATT is BLE-only, Logitech is `UsbHid`-only); `IDeviceLinkState` (optional; a cheap no-I/O `IsLinkUp(ctx)` that `BatteryDevice.IsConnected()` uses for BLE — implemented only by `BluetoothLEBatteryProvider`, and found via `provider as IDeviceLinkState` rather than by type name); `DeviceProperties` (the `PROP_*` property-bag keys, referenced by both the discovery layer's `requestedProperties` and the providers — canonical Windows names plus the app's own `PeripheralBatteryMonitor.Hid.*` keys, which are synthesised by `HidDeviceSource` and prefixed so they can never collide with a real Windows property). **`PROP_BATTERY_LEVEL` is a 0–100 percentage, and Windows publishes it under two names** — the raw DEVPROPKEY `{104EA319-…} 2` *and* the canonical `System.Devices.BatteryLife`, which is documented with that same formatID and propID. The property bag carries both spellings side by side (verified by dumping a paired device's AEP bag), so they are aliases of one value, not two sources: read it once, through `PROP_BATTERY_LEVEL`. There is no coarse Critical/Low/Average/Full enum behind either spelling — a provider that switched on `1..4` here (as a deleted `CoarseBatteryProvider` once did) would report a 3% battery as 60%. Both spellings arrive as null while the device is disconnected, and `BatteryDevice.CacheProperties` drops nulls, so absence means "asleep".
- **`Hid/`** — vendor-neutral raw HID plumbing, so more than one provider family can talk HID without duplicating P/Invoke. `HidNative` (the whole setupapi + hid.dll + overlapped-I/O P/Invoke surface; keep P/Invoke confined here); `HidInterfaceInfo` (one top-level collection: path, VID/PID, usage page/usage, report lengths — note a single physical device publishes **several** collections, so the usage page/usage pair is what identifies the one worth talking to); `HidInterfaceEnumerator` (lists present interfaces, pre-filtered by vendor id on the path string — matching bare hex digits so it works for both USB `vid_046d` and Bluetooth `vid&0002004c` forms — and opens each with desired access **0**, the documented query-only mode, because Windows opens keyboards and mice exclusively); `HidDevice` (an open interface, in **one of three modes**); `HidDeviceSpec` (which interface stands for a trackable device, matched on VID + optional PID list + usage page/usage + an optional **exact feature report length**). That last one exists because usage page and usage are only a good discriminator when the protocol rides on a *vendor-defined* page. Razer's does not -- it answers on the consumer-control collection, next to the volume keys -- so the 91-byte feature report is what actually identifies it.
  `HidDevice`'s two open modes are not interchangeable. `Open()` is FILE_FLAG_OVERLAPPED and supports `Write`/`Read`, for a conversation where the answer arrives as an input report the device sends — a HID read blocks until the device says something, which on the UI thread would hang forever, so each operation is issued async, waited on, then `CancelIo`'d. `OpenForReportRequests()` is deliberately **not** overlapped and supports only `GetInputReport`, which asks for a report by id instead of waiting: `HidD_GetInputReport` issues a synchronous DeviceIoControl with a NULL OVERLAPPED, which Windows documents as unreliable on an overlapped handle, and there is no timeout to lose because the call never waits on device traffic. `Read`/`Write` refuse a non-overlapped handle outright (`EnsureOverlapped`) rather than silently running unbounded. `OpenForFeatureReports()` is the third mode and the only one opened with **desired access 0**, which is the point of it rather than an optimisation: a vendor protocol carried on feature reports often sits on a collection Windows opens exclusively for itself, where `CreateFile` with `GENERIC_READ|GENERIC_WRITE` fails outright with `ERROR_ACCESS_DENIED` -- measured on this machine against every generic-desktop mouse and keyboard collection. The IOCTLs behind `HidD_GetFeature`/`HidD_SetFeature` are declared `FILE_ANY_ACCESS`, so a query-only handle drives them perfectly well; it just cannot `ReadFile`/`WriteFile`, which that mode does not offer.
  Either way, open one only for the duration of a transaction, so the driver's per-handle report queue can't hand back a stale frame.

### WinRT bridging

Core consumes WinRT APIs (`Windows.Devices.Bluetooth.*`, `Windows.Devices.Enumeration`, `Windows.Devices.Radios`, `Windows.Storage.Streams`) from .NET Framework 4.8 via the `DirectWindowsWinmd.Net 10.0.15063.0` NuGet package, which provides `Windows.winmd` and `System.Runtime.WindowsRuntime.dll` references. When adding new WinRT calls, expect `IAsyncOperation<T>` — convert with `.AsTask()` and use `Task.Wait(timeoutMs)` rather than `await` (the codebase is synchronous on the UI thread inside the timer tick).

The `PackageReference` is `ExcludeAssets=runtime`, the equivalent of the old `<Private>False</Private>`: the `System.Runtime.WindowsRuntime` facade is part of .NET Framework 4.5+ and resolves from the framework directory at run time, and a `.winmd` is a compile-time contract with nothing to copy. **It is declared on Core, not App, and reaches App transitively** — which App needs, because `BatteryDevice` has a public constructor taking a WinRT `DeviceInformation`.

`DeviceInformation.Properties` is a string-keyed bag that surfaces both canonical names (`System.Devices.Aep.IsConnected`) and raw `DEVPROPKEY`s in the form `"{guid} pid"` (e.g. `"{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2"`). To populate them, the keys must be passed in the `requestedProperties` array to `CreateWatcher` — they're not delivered otherwise. **A property can have both forms, and then both appear as separate entries holding the same value** — `System.Devices.BatteryLife` and `{104EA319-…} 2` are one property under two keys. Requesting both spellings therefore gains nothing and invites reading one value twice, which is exactly the bug `CoarseBatteryProvider` was. `Updated` events carry only the changed keys, so `BatteryDevice` merges them into a `ConcurrentDictionary<string, object>` cache rather than replacing it.

## Diagnostics internals

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
- **Every line is five fixed columns**, `date time | th | category | device | message`, so the
  eye can run down one of them instead of parsing each line:

  ```
  2026-09-16 14:22:12.674 |  1 | Centurion   | PRO X 2 LIGHTSPEED       | -> [10] 51 08 00 03 1A 00 03 00 04 0A
  2026-09-16 14:22:12.690 |  3 | Battery     | Xbox Wireless Controller | transport=BluetoothLowEnergy level=none
  2026-09-16 14:22:38.079 |  1 | Logitech    |                          | receiver slot 1: silent
  ```

  `CATEGORY_WIDTH` is 11 (`SteelSeries`, the longest in the tree) and `DEVICE_WIDTH` 24.
  **A longer value is cut to fit, ending in `…`** — the columns are the point, and one ragged
  line breaks the eye's run down the file. The ellipsis is what stops a cut name reading as a
  different, shorter device. Nothing is lost by cutting: the `Discovery` line that first reports
  an interface prints the full name. The banner ends with a header row naming the columns, so
  the layout documents itself where every reader starts.
- **A device path is logged through `HidInterfaceInfo.ShortPath`, never raw.** Nearly half of
  `\\?\hid#vid_1b1c&pid_2b00&mi_03&col01#9&1fe59b30&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}\kbd`
  carries nothing: the `\\?\hid#` prefix is on every path ever written and the trailing GUID is
  the HID *class* GUID, the same 38 characters on every line of every log. Both go; 98 chars
  become 51.
  - **What survives is what varies**: `mi_`/`col` say which USB interface and collection, and
    the instance id is what tells two identical dongles in two ports apart — which this app
    treats as two devices, so a log has to be able to show it. The `\kbd` suffix and a receiver
    child's `#01` survive too.
  - **Shortening is for logging only.** `Path` stays the identity — it is `PROP_HID_PATH`, the
    device id, the receiver sweep's cache key and the open-failure dedupe key. Never open or
    match on the short form.
  - The `vid_`/`pid_` in the short path duplicates the `VID_`/`PID_` the snapshot's row already
    prints, and stays anyway: `HidDevice`'s open-failure lines carry no row beside them, so
    there the path is the only thing naming the device.
- **The thread id is a column of its own**, because WinRT `DeviceWatcher` callbacks log from
  arbitrary threads and the file interleaves with no other way to see that it has. It is not
  made redundant by the device column: that one says *which device*, this one says *which of the
  possibly-concurrent readers*.
- **A line written during a device's poll also carries that device's name**, via `Log.Scope`.
  A transport holds a handle, not a device, so `-> [8] 51 06 …` on its own says nothing about
  which peripheral answered — unreadable the moment two devices are polled in one tick. Threading a name through every transport and provider signature to reach
  the log would be the worse trade, so the name is **ambient**: `BatteryDevice.UpdateBatteryLevel`
  is the single place that knows it, and it wraps the whole read in one `using`.
  - **`[ThreadStatic]`, and that is load-bearing**, not caution: the poll tick and a WinRT
    watcher callback can both be inside a read at once on different threads, and a shared field
    would label one device's frames with the other's name.
  - **Nothing else may repeat the name.** `BatteryDevice.Describe` and the provider lines used
    to print it themselves and now do not — inside the scope it would appear twice. A message
    written outside a scope (discovery, the receiver sweep, the startup snapshot) leaves the
    column blank, which is correct: those precede any device.
- **Two call sites dedupe on (path, error code)**: the `CreateFile` failure in `HidDevice`
  and the describe failures in `HidInterfaceEnumerator`. Those are the only two that scale
  with (interfaces × ticks), and vendor software holding a collection open makes them repeat
  one unchanging fact hundreds of times a day. A *change* — including back to success — is
  the event worth a line. Everything else logs every tick, on purpose.
- **`DiagnosticReport.WriteStartupSnapshot`** covers the one gap the continuous log cannot:
  discovery's enumeration is pre-filtered to *registered vendor ids*, so the poll tick never
  sees the interface nobody claims — which is the shape of almost every report. It
  enumerates with no filter, once, and writes **one table in one walk** — a fixed-width
  `[  Supported  ]` / `[Not Supported]` marker, the interface, the claiming spec's name, and
  the short path — all on one row. The path is not a line of its own: everything else about an
  interface is already on the row, so a second line said almost nothing twice. It cannot be
  *dropped*, though — two collections of one device differ only by their `mi_`/`col` index, and
  their rows are otherwise byte-identical. The marker is fixed-width so the question every report comes down to is both
  scannable and greppable.
  - **The marker says Supported / Not Supported, by the author's decision** — not `used` or
    `claimed`, so don't narrow it back. What it actually reports is whether a registered
    `HidDeviceSpec` claims the collection, and the gap between the two is per *collection*,
    not per device: one device publishes several and at most one is ever claimed, so a
    headset that works perfectly still shows several `Not Supported` rows. Anyone reading a
    pasted log needs to know that; it is recorded here rather than in the file.
  - **Capitalised, which is also what keeps it greppable**: a case-sensitive grep for
    `Not Supported` finds this table and nothing else, so nothing elsewhere may write those
    two words capitalised about anything but a claimed collection. `HidDeviceSource`'s
    per-interface `Discovery` line does use them, deliberately — one grep then covers both
    places a collection is mentioned.
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
