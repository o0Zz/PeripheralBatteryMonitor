# SteelSeries provider

Loaded when working under `Providers/SteelSeries/`. Moved out of the root `CLAUDE.md`.

- `SteelSeries/SteelSeriesBatteryProvider` covers **Arctis Nova** wireless headsets on their own USB dongle — the same discovery shape as Logitech, a far simpler protocol. Write the single command byte `0xB0` to the vendor collection at usage page `0xFFC0` / usage `0x0001` and the dongle answers with a status report. There is no feature discovery and nothing worth caching, so unlike the Logitech provider this one holds no per-device state: every per-model difference is static data in its `Models` table.

  **The reply layout is not uniform and neither is the level byte.** Nova 7 (and the 7P/7X/Diablo/WoW variants that share its firmware) put the level at documented `data[2]` with the on/off state at `data[3]`, where `0x00` means the headset is off; Nova 5 base stations put the state at `data[1]` (`0x02` = off) and the percentage at `data[3]`. Worse, whether the level *is* a percentage depends on the product id: the pre-2026 ids report a discrete `0..4`, which maps to 0/25/50/75/100 and is much coarser than the number suggests.

  **Every published description of this protocol is written against hidapi, which is off by one from what this code sees.** hidapi strips the leading report-id byte when a collection declares no report ids; `HidDevice.Read` goes through `ReadFile`, which always returns it. So `data[2]` in any reference is `reply[3]` here, and `NovaModel` stores the already-shifted index. Get it wrong and you read a plausible neighbouring byte rather than failing — check `LevelIndex` first if a model reports nonsense.

  `HidSpec` is assigned in a **static constructor, not a field initializer**, and that is load-bearing: initializers run in declaration order, so building the spec inline would read the `Models` table at the bottom of the file while it was still null and take `HidDeviceSpecRegistry`'s static constructor — and the app — down with it.

  **Its product ids are not verified against hardware**, unlike every other id in this build. They come from the HeadsetControl project's device tables. Treat a model reporting a wrong level as unproven rather than as a transport bug.
