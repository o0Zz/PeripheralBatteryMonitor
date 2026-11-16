# Sony provider

Loaded when working under `Providers/Sony/`.

- `Sony/SonyBatteryProvider` covers the PlayStation controllers: **DualSense** (`054C:0CE6`) and **DualSense Edge** (`0DF2`) for PS5, **DualShock 4** (`05C4` first revision, `09CC` second) for PS4. Windows publishes no battery level for any of them, on either transport, so before this provider a paired controller sat in the tray at `-1`. The level is in the input report the controller streams unasked on its gamepad collection (`0x0001/0x0005`). Report ids, offsets and status-byte decoding come from Linux [`hid-playstation.c`](https://github.com/torvalds/linux/blob/master/drivers/hid/hid-playstation.c); facts only, no code.

  | | USB report / status byte | Bluetooth report / status byte |
  |---|---|---|
  | DualSense, Edge | `0x01` / 53 | `0x31` / 54 |
  | DualShock 4 | `0x01` / 30 | `0x11` / 32 |

  Offsets are raw-wire, report id included. DualSense status: low nibble capacity in tenths (`n*10+5`, capped at 100), high nibble `0` discharging, `1` charging, `2` full; `0xA`/`0xB`/`0xF` are charger faults, where Linux reports 0% and this provider reports **no level** — a low-battery balloon over a charger fault would be wrong. DualShock 4 status: low nibble capacity, `10`+ meaning full; bit 4 is the cable, and on cable anything above `11` is a fault.

- **Two discovery paths, one provider.** On a cable the controller has no Bluetooth endpoint, so `HidSpec` registers it with `HidDeviceSpecRegistry` (`UsbHid` transport), the 8BitDo shape. Over Bluetooth the Classic watcher already finds it, so the provider follows the Apple shape: it enumerates vendor `054C`, keeps the Bluetooth-path gamepad collections of known product ids, and matches the device by HID serial number against `System.Devices.Aep.DeviceAddress`. A single controller plus a device name containing *Wireless Controller* / *DualSense* / *DualShock* is the fallback when the serial is not the address. With several controllers and no match it writes one `Sony` line listing the serials, only when that text changes.

- **The Bluetooth full-report switch has a side effect, and is sent only when needed.** Over Bluetooth both families start in a short report `0x01` with no battery, and switch to the full report once feature report `0x05` (calibration) is read. Linux, SDL and Steam do the same. The switch lasts until the controller powers off, and a DirectInput game written for the short report can lose input. So the provider first reads for up to 200 ms; only when no full report arrives does it read feature `0x05` and try again. With Steam running the controller is usually switched already, and nothing is sent. The read is gated on `System.Devices.Aep.IsConnected` not being `false`, so a switched-off controller is never sent the feature report.

- **Not verified on hardware.** Every reading logs the raw frame (`<-` hex) and the status byte, so a first log from a user confirms or corrects the table above. Left out: the Sony USB wireless adapter for DualShock 4 (`0BA0`), which streams a report even with no controller attached, and the DualShock 3, which does not pair with Windows Bluetooth without third-party drivers.
