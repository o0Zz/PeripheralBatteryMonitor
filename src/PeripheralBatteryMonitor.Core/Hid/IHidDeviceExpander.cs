using System.Collections.Generic;

namespace PeripheralBatteryMonitor.Hid
{
    /// <summary>
    /// Turns one matched HID interface into the devices behind it.
    ///
    /// Present only on a spec that describes a *receiver* -- the one case that breaks the
    /// interface-to-device one-to-one every other spec relies on. A <see cref="HidDeviceSpec"/>
    /// is a pure predicate over interface metadata and cannot enumerate anything, so this is
    /// the hook that lets the knowledge of *how* to enumerate live with the vendor, in
    /// <c>Providers/</c>, while <c>HidDeviceSource</c> stays vendor-neutral.
    ///
    /// Implementations are called from the poll tick, so they must be cheap or cached. They
    /// are also the place the phantom-entry rule is enforced: return a device only once
    /// something has actually answered at that index, never merely because a slot could exist.
    /// </summary>
    public interface IHidDeviceExpander
    {
        List<HidDiscoveredDevice> Expand(HidInterfaceInfo info, HidDeviceSpec spec);
    }
}
