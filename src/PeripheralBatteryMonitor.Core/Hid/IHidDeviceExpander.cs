using System.Collections.Generic;

namespace PeripheralBatteryMonitor.Hid
{
    /// <summary>
    /// Turns one matched HID interface into the devices behind it -- present only on a spec
    /// describing a receiver, the one case that breaks the interface-to-device one-to-one.
    /// </summary>
    public interface IHidDeviceExpander
    {
        /// <summary>
        /// The devices behind <paramref name="info"/>, possibly none. Return a device only
        /// once something has answered at its index, never because a slot could exist: a
        /// permanent "?" in the tray is the phantom entry this hook exists to prevent.
        ///
        /// Called from the poll tick, so it must be cheap or cached -- and
        /// <paramref name="force"/> is the user having just asked for a refresh, which must
        /// re-probe rather than serve the cache, or Refresh is a button that does nothing.
        /// </summary>
        List<HidDiscoveredDevice> Expand(HidInterfaceInfo info, HidDeviceSpec spec, bool force);
    }
}
