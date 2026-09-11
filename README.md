# 🔋Peripheral Battery Monitor

![Banner Gif of Tray](doc/img/bat_banner.png)

## Introduction
Peripheral Battery Monitor adds a small and simple battery display to your system tray, so you can see the remaining life of any wireless devices connected to your PC at a glance.

It supports :
  - **Bluetooth Low Energy (BLE)** devices (earbuds, fitness bands, modern headsets, BLE mice/keyboards)
  - **Bluetooth Classic (BR/EDR)** devices (older headsets, gaming headsets, AirPods on Windows)
  - **Apple magic mice/trackpads/keyboards** devices
  - **Logitech LIGHTSPEED** devices (PRO X Wireless headset, ...)
  - **Razer** wireless mice (Viper Ultimate, DeathAdder V2/V3/V4 Pro, Basilisk V3 Pro, Orochi V2, ...)
  - **SteelSeries Arctis Nova** wireless headsets (Nova 5/5X, Nova 7/7P/7X and editions) — *device ids taken from [HeadsetControl](https://github.com/Sapd/HeadsetControl)'s tables, not yet confirmed on hardware; please [open an issue](https://github.com/o0Zz/PeripheralBatteryMonitor/issues) if yours reads a wrong level.*

**Notifications**
Whenever a device drops below 20% a balloon notification fires (once per device, per low-battery transition).

**Auto Startup**
When activated, the application starts automatically at Windows logon.

**Adjustable Refresh Rate**
Configure how often the app polls battery levels (1 min – 24 h).

**One tray icon per device**
Optionally show a separate tray icon for every paired device instead of one aggregate icon for the lowest battery. Each icon displays its own battery glyph and tooltip.

**Multiple languages**
English, French, German, Italian, Spanish and Simplified Chinese. The app follows Windows by default; pick a language explicitly in Settings and it applies straight away.

**Hide unknown-battery devices**
Optionally hide devices whose battery cannot be read from the tray, balloon tooltip and Info popup.

## Quick Start Guide
Follow the directions below to get started!

**Step 1.** Download `PeripheralBatteryMonitor.exe` from the [latest release](https://github.com/o0Zz/PeripheralBatteryMonitor/releases).

**Step 2.** Double-click `PeripheralBatteryMonitor.exe` to start it.

**Step 3.** Open the system tray overflow popup and locate the battery icon. Drag it to the visible tray area if you want it always shown.

**Step 4.** Double-click the icon to see the per-device list, or right-click for *Settings* / *About* / *Exit*.

*Auto-start is off by default — enable it in Settings if you want the app to launch with Windows.*

## ⚙️Settings and Configuration⚙️
![Settings Banner Photo](doc/img/bat_set_banner.png)
### How to Access Settings
When the application is running, right-click the battery icon in the system tray and select *Settings*. All changes apply immediately — no restart required.

### Settings Breakdown

**General**

- [ ] **Launch application on startup**
  - *[Off by default]* Boot the application automatically when Windows starts.

- [x] **Enable notifications**
  - When a device drops below 20%, a balloon notification is shown (once per low-battery transition).

- [x] **Refresh period [5 min default]**
  - How often the application polls battery levels for every tracked device, from 1 minute to 24 hours.

- [x] **Language [same as Windows by default]**
  - English, French, German, Italian, Spanish or Simplified Chinese. Applies immediately — no restart.

**Devices and tray icons**

- [x] **Automatically detect new devices**
  - When checked, the app keeps watching for newly paired Bluetooth devices. When unchecked, it only enumerates devices that were already paired when the app started. Devices on a USB dongle are picked up either way.

- [ ] **Show one tray icon per device**
  - *[Off by default]* When checked, each paired device gets its own tray icon with its own battery glyph and tooltip. When unchecked, a single tray icon shows the lowest battery across all devices and a multi-line tooltip listing each device.

- [ ] **Hide devices with unknown battery level**
  - *[Off by default]* When checked, devices whose battery can't be read (showing `?` / `-1%`) are hidden from the tray icons, the balloon tooltip and the Info popup.

## 🔧Troubleshooting🔧

- **Battery icon not in system tray**
  - Check the system tray overflow / pop-up area
  - Manually start the application again
    > On first startup the icon usually appears in the overflow flyout, not the main tray. Drag it to the main tray to keep it visible. If it's nowhere to be found, the application may have failed to start — try launching it again.

- **Battery shows `-1%` or `?`**
  - Wait for the next refresh tick and check again
  - Some devices simply don't report battery to Windows
    > The app polls every 5 min by default. A newly paired device will show as unknown until the first poll completes. If a device still reports nothing after several refreshes, it likely doesn't expose battery via any API Windows can read — enable **Hide devices with unknown battery level** in Settings to suppress it from the UI.

- **Bluetooth Classic device not appearing**
  - Make sure the device is paired in Windows Settings → Bluetooth & devices
  - Make sure the device is currently connected (powered on)
    > Only paired devices are tracked. Unpaired devices won't be picked up. Battery for Classic devices depends on Windows itself surfacing the value through device properties, which not every driver does.

- **Not auto-starting on system startup**
  - Confirm *Launch application on startup* is checked in Settings
  - Verify the entry is enabled in Task Manager → Startup tab
    > Auto-start is off by default. Enable it in Settings. If it's still not running on logon, Task Manager may have disabled the startup entry — re-enable it there.

- **Not sending low-battery notifications**
  - Confirm *Enable notifications* is checked in Settings
  - Make sure Windows Focus Assist / Do Not Disturb isn't suppressing balloons
  - Exit and restart the application if the problem persists
    > A notification fires once per low-battery transition (when a device drops to ≤ 20% after being above it). It won't repeat every refresh while the device stays low.

- **Multiple tray icons**
  - Check whether *Show one tray icon per device* is enabled
    > The application allows only one running instance. Multiple live icons are expected only when the per-device icon setting is enabled; after an abnormal exit, a stale icon disappears when Windows refreshes the tray area.

- **Your device isn't listed, or reads the wrong level**
  - Open the log: tray icon → right-click → *Open log folder*
  - Attach `log.txt` to your [issue](https://github.com/o0Zz/PeripheralBatteryMonitor/issues)
    > The application always writes `%LOCALAPPDATA%\PeripheralBatteryMonitor\log.txt`, so the log for the session you just had is already there — there's nothing to switch on first. It records every HID interface on the machine and which ones the application recognised, which is exactly what's needed to add support for a device it can't see. The file rolls at 1 MB and keeps one previous copy. It contains device names and hardware ids, so skim it before posting; you can delete it at any time, including while the application is running.

## Build from source

Requires only the [.NET SDK](https://dotnet.microsoft.com/download) (8.0 or newer). The solution targets .NET Framework 4.8, but both projects are SDK-style and the reference assemblies come from a NuGet package, so no Visual Studio, no targeting pack and no `nuget.exe` are needed.

```sh
dotnet build PeripheralBatteryMonitor.sln -c Release
```

The built executable is `src/PeripheralBatteryMonitor.App/bin/Release/net48/PeripheralBatteryMonitor.exe` — still a single file: `PeripheralBatteryMonitor.Core.dll` is embedded inside it.

### Layout

```
src/PeripheralBatteryMonitor.Core/   device discovery and battery reading. No UI.
src/PeripheralBatteryMonitor.App/    WinForms tray icon, Settings and Info windows.
```
