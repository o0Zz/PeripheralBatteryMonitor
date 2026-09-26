using System.Collections.Generic;
using PeripheralBatteryMonitor.Hid;
using PeripheralBatteryMonitor.Providers;

namespace PeripheralBatteryMonitor
{
    /// <summary>
    /// Discovery for devices that reach the PC over raw USB HID -- a peripheral on its own
    /// vendor dongle, which has no Bluetooth association endpoint and so is invisible to the
    /// watchers in <see cref="DeviceManager"/>.
    ///
    /// Nothing to subscribe to, so this is a plain snapshot, cheap enough to re-run every poll
    /// tick -- which is what gives plug/unplug handling for free.
    ///
    /// One interface is only *usually* one device: a spec may carry an
    /// <see cref="IHidDeviceExpander"/>, and then the interface stands for however many
    /// peripherals are actually paired to it.
    ///
    /// <b>internal</b>, unlike the other three types at the project root, which are this
    /// assembly's public API. Nothing outside Core drives this one.
    /// </summary>
    internal static class HidDeviceSource
    {
        /// <summary>
        /// <paramref name="force"/> passes the user's explicit Refresh down to the specs that
        /// cache -- the receiver expanders. The enumeration itself is never cached.
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

                if (spec == null)
                    continue;

                if (spec.Expander != null)
                {
                    List<HidDiscoveredDevice> children = spec.Expander.Expand(info, spec, force);
                    if (children != null)
                        found.AddRange(children);
                    continue;
                }

                found.Add(ProviderHid.Describe(info, GetDeviceName(info, spec), null));
            }

            return found;
        }

        private static string GetDeviceName(HidInterfaceInfo info, HidDeviceSpec spec)
        {
            return spec.NameFor(info);
        }
    }
}
