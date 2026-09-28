# Razer provider

Loaded when working under `Providers/Razer/`. Moved out of the root `CLAUDE.md`.

- `Razer/RazerBatteryProvider` + `Razer/RazerReport` cover Razer wireless mice, and are the only place a vendor conversation runs on **feature reports** instead of the report streams. One transaction is `SetFeature` with a 90-byte request, a 60 ms pause, then `GetFeature` reading the answer out of the same structure -- there is no separate response channel, so the reply echoes the transaction id, command class and command id, and carries a CRC. Checking all four is what tells a real answer apart from the request bytes being handed straight back. Battery is command class `0x07` id `0x80`, and the level arrives in argument byte 1 as **0..255, not a percentage**.

  The **transaction id is per-model and load-bearing** on a dongle: `0x1F` for the V3/V4 generation and the Orochi V2, `0x3F` for the V2 generation. Send the wrong one to a device behind a receiver and it stays silent.

  A raw level of `0` is treated as "can't read right now" rather than a flat battery. A mouse that is switched off answers 0, and reporting that as 0% would fire the low-battery balloon on every poll for a mouse sitting in a drawer.

  **The transport is verified; the battery path is not.** A wired Basilisk V3 (`0x0099`, no battery, deliberately absent from the model table) was used to prove the whole chain on real hardware: firmware-version and serial-number commands both returned `status=0x02` with a valid CRC and correct echo -- the serial decoded to readable ASCII -- while the battery command returned `status=0x05`, *not supported*, exactly as a mains-powered mouse should. So the framing, CRC, offsets, access-0 handle and 60 ms settle are all confirmed correct; what remains unproven is only that a battery-carrying model answers the battery command as the table says. Ids and transaction ids come from the MIT-licensed `xzeldon/razer-battery-report`.

  Two attempts per read, not the ten a standalone tool uses: this runs on the UI thread and a measured transaction costs ~72 ms.
