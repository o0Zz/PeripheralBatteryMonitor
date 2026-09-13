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
        /// <summary>
        /// The devices behind <paramref name="info"/>, possibly none.
        ///
        /// <paramref name="force"/> is the user having just asked for a refresh, and it means
        /// "do the expensive thing now" -- an implementation that caches must re-probe rather
        /// than serve what it has. Without it a cache is indistinguishable from a broken
        /// Refresh button: a peripheral switched on a moment ago stays missing, the user
        /// clicks Refresh, and the app confidently repeats an answer it worked out minutes
        /// before anything happened.
        /// </summary>
        List<HidDiscoveredDevice> Expand(HidInterfaceInfo info, HidDeviceSpec spec, bool force);
    }
}
