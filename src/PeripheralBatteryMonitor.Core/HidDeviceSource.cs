using System;
using System.Collections.Generic;
using PeripheralBatteryMonitor.Diagnostics;
using PeripheralBatteryMonitor.Hid;
using PeripheralBatteryMonitor.Providers;

namespace PeripheralBatteryMonitor
{
    /// <summary>
    /// Discovery source for devices that reach the PC over raw USB HID instead of Bluetooth --
    /// typically a wireless peripheral with its own vendor dongle, which has no Bluetooth
    /// association endpoint and so is invisible to the <c>DeviceWatcher</c> pair in
    /// <see cref="DeviceManager"/>.
    ///
    /// Unlike Bluetooth discovery there is nothing to subscribe to: this is a plain snapshot
    /// of what is plugged in right now, cheap enough (a setupapi walk filtered to a handful of
    /// vendor ids) to re-run on every poll tick, which is also what gives plug/unplug
    /// handling for free.
    ///
    /// One interface is *usually* one device, and was always one device until receivers were
    /// supported. A spec may now carry an <see cref="IHidDeviceExpander"/>, in which case the
    /// interface stands for however many peripherals are actually paired to it -- see
    /// <see cref="HidDiscoveredDevice"/>.
    ///
    /// <b>internal, unlike the other three types at the project root.</b> Those three --
    /// <see cref="BatteryDevice"/>, <see cref="DeviceManager"/> and
    /// <see cref="IDeviceNotification"/> -- are exactly what the App project references, so
    /// the root doubles as this assembly's public API. This one is a helper
    /// <see cref="DeviceManager"/> drives and nothing outside Core touches; keeping it
    /// internal makes that boundary the compiler's business rather than a convention.
    /// </summary>
    internal static class HidDeviceSource
    {
        /// <summary>
        /// Every device behind a present HID interface that a registered spec claims. May be
        /// empty.
        ///
        /// <paramref name="force"/> passes the user's explicit Refresh down to the specs that
        /// cache -- the receiver expanders. The enumeration itself is a setupapi walk that is
        /// never cached, so nothing else here reads it.
        /// </summary>
        public static List<HidDiscoveredDevice> Discover(bool force)
        {
            List<HidDiscoveredDevice> found = new List<HidDiscoveredDevice>();

            ICollection<ushort> vendorIds = HidDeviceSpecRegistry.GetVendorIds();
            if (vendorIds.Count == 0)
                return found;

            foreach (HidInterfaceInfo info in HidInterfaceEnumerator.Enumerate(vendorIds))
            {
                HidDeviceSpec spec = HidDeviceSpecRegistry.Match(info);

                LogDecisionOnce(info, spec);

                if (spec == null)
                    continue;

                if (spec.Expander != null)
                {
                        //A receiver: the interface stands for whatever is paired to it, and
                        //possibly for nothing at all.
                    List<HidDiscoveredDevice> children = spec.Expander.Expand(info, spec, force);
                    if (children != null)
                        found.AddRange(children);
                    continue;
                }

                found.Add(ProviderHid.Describe(info, GetDeviceName(info, spec), null));
            }

            return found;
        }

            //Interfaces this has already had its say about, so a permanently unclaimed
            //collection writes one line rather than one line every poll tick for as long as
            //the app runs. Bounded by the number of HID interfaces of the registered vendors,
            //which is tens.
        private static readonly HashSet<string> loggedInterfaces = new HashSet<string>();

        /// <summary>
        /// Name what discovery decided about one interface, once.
        ///
        /// An interface no spec claims is invisible to the entire app: no
        /// <see cref="BatteryDevice"/>, nothing to refresh, nothing in the tray and nothing
        /// in the Info window. That is the exact shape of every "my device does not show up"
        /// report, and until this line existed there was no way to tell it apart from a
        /// device that was found and simply would not answer.
        ///
        /// This costs nothing extra to collect: <c>Enumerate</c> is pre-filtered by the
        /// *vendor* ids the specs registered, not by the specs themselves, so the rejected
        /// interfaces of a registered vendor are already in hand and being thrown away.
        /// </summary>
        private static void LogDecisionOnce(HidInterfaceInfo info, HidDeviceSpec spec)
        {
            lock (loggedInterfaces)
            {
                if (!loggedInterfaces.Add(info.Path))
                    return;
            }

            Log.Write("Discovery", (spec != null ? "claimed by '" + spec.FallbackName + "': " : "claimed by no spec:  ")
                + info + "  " + info.Path);
        }

        private static string GetDeviceName(HidInterfaceInfo info, HidDeviceSpec spec)
        {
            if (spec != null)
                return spec.NameFor(info);
            return String.IsNullOrWhiteSpace(info.Product) ? "Unknown HID device" : info.Product.Trim();
        }
    }
}
