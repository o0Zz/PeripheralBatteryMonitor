# PeripheralBatteryMonitor.App

Loaded when working under `src/PeripheralBatteryMonitor.App/`. Moved out of the root `CLAUDE.md`, which still holds the working agreement, build, deployment and conventions.

## DPI

**`app.manifest` is the only thing that makes this process DPI-aware, and shipping without one is what made the window fonts fuzzy.** A process that declares nothing is DPI-*unaware*: Windows lays it out at 96 DPI and bitmap-stretches the result to the display scale, resampling every glyph. It looks fine at 100% and blurs at 125% or 150% — which is why it only showed up on some monitors.

- The declaration is **system** DPI awareness, not per-monitor v2. On .NET Framework the manifest is only half of per-monitor support: WinForms also needs `DpiAwareness=PerMonitorV2` in `<exe>.config`, and a config file is a second file next to the exe. Claiming per-monitor here without the WinForms half is *worse* than not claiming it — Windows stops scaling the window and WinForms does not start, leaving the UI undersized.
- What system awareness still costs: a window dragged to a monitor at a different scale than the primary is stretched again. Fixing that means restoring `App.config` and giving up the single file.
- **Every form needs `AutoScaleMode.Font` and a matching `AutoScaleDimensions`.** `Info` had neither, which meant `AutoScaleMode.None`: it kept its design-time pixel size while the font grew with the scale, so the rows were clipped. Both forms now declare the 96 DPI baseline `(6F, 13F)`.
- **Two controls need their geometry repaired after scaling, in `Settings.FitInputRows`.** Both are captioned input rows where the caption carries `Anchor = None` so a left-to-right `FlowLayoutPanel` centres it on the control — which only works if the control's own geometry is honest, and for these two it is not.
  - **`NumericUpDown` is a `ContainerControl`**, so it runs its own auto-scale pass on top of the form's and its margin is scaled more than once: a declared top margin of `3` arrived as **28** at 150%, dropping the spinner nine pixels below "Refresh period:" and stretching the row from 33 to 54 px. Copying the caption's margin puts it back on the one value scaled exactly once. Do not "fix" this by tuning the declared margin — that is what caused it.
  - **`ComboBox` takes its height from its font once**, at construction, and never revisits it, so anything assigned to `Size` is pinned for the life of the form. Left at a designer-written `21`, it was shorter than the 20 px caption beside it while the row sized itself to the 28 px the box actually wanted. It is sized from `PreferredHeight` and from measuring its own widest entry, so a long translation of "same as Windows" cannot clip either.
- **`ListView` column widths do not auto-scale, and docking does not resize them either** — they are plain integers the control never revisits, on both counts. `Info.LayoutColumns` gives the two fixed columns a scaled width and lets the device name absorb the remainder, and is wired to `ClientSizeChanged` so it covers the initial layout and every drag of the window border. It clamps the device column to a floor, because a window dragged narrow would otherwise compute a negative width and throw. Columns are built in `OnLoad` rather than the constructor, because auto-scaling has not happened yet when the constructor runs.

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

## Crash logging

- **A crash writes itself to the log**, via `App/CrashLog.cs`, wired from `Program.Run` after
  `EmbeddedAssemblies.Install` — naming `Log` loads Core, so it cannot go any earlier, and
  `Application.SetUnhandledExceptionMode` must precede the first window. Without it a crash is
  invisible: the file just stops mid-session, which reads exactly like the user closing the app.
  The three sinks are not interchangeable.
  - **`Application.ThreadException` is survivable and is deliberately survived.** It covers the
    UI thread, which is where the poll tick and every HID transaction run, and swallowing the
    throw is what keeps a single bad tick from costing the user their tray icon and the menu
    that reaches *Exit*. The log line is then the only record it happened.
  - **`AppDomain.CurrentDomain.UnhandledException` cannot stop anything** — a WinRT watcher
    callback or the radio-restart worker throwing is fatal whatever this does. It exists to get
    the reason on disk first, which `Log`'s AutoFlush guarantees.
  - **`TaskScheduler.UnobservedTaskException`** is the one the synchronous
    `AsTask().Wait(timeout)` pattern throughout Core can produce silently: a call that times out
    leaves its task running, and whatever it throws afterwards lands nowhere else.
  - The mode is set explicitly rather than left at `Automatic`, which consults an `.exe.config`
    this app is forbidden to ship — so the behaviour would otherwise depend on a file that is
    never there. Exceptions are logged with `ToString()`, not `Message`: the stack trace and the
    inner exceptions are the point, and an `AggregateException`'s own `Message` names nothing.
