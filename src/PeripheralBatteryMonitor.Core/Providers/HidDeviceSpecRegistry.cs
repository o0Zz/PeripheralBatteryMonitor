using System;
using System.Collections.Generic;
using PeripheralBatteryMonitor.Hid;
using PeripheralBatteryMonitor.Providers.Logitech;
using PeripheralBatteryMonitor.Providers.Razer;
using PeripheralBatteryMonitor.Providers.SteelSeries;

namespace PeripheralBatteryMonitor.Providers
{
    /// <summary>
    /// The HID interfaces discovery should surface as tracked devices.
    /// <see cref="BatteryProviderRegistry"/> says *how* to read a battery; this says which
    /// non-Bluetooth devices exist at all.
    ///
    /// **Only devices with no Bluetooth association endpoint belong here** -- a Bluetooth one is
    /// already found by the watchers and would be listed twice. That is why the Apple Magic
    /// devices, also read over raw HID, register nothing.
    /// </summary>
    public static class HidDeviceSpecRegistry
    {
        private static readonly List<HidDeviceSpec> specs = new List<HidDeviceSpec>();

        static HidDeviceSpecRegistry()
        {
                //Receivers first: if either id list is ever widened to overlap, this order is
                //what stops a receiver being taken for a single device -- which would hide
                //everything paired to it behind one unreadable entry.
            Register(LogitechBatteryProvider.ReceiverHidSpec);   //Logitech LIGHTSPEED / Unifying receivers
            Register(LogitechBatteryProvider.HidSpec);           //Logitech LIGHTSPEED, HID++ framing (PRO X Wireless headset)
            Register(LogitechBatteryProvider.CenturionHidSpec);  //Logitech LIGHTSPEED, Centurion framing (PRO X 2 headset)
            Register(SteelSeriesBatteryProvider.HidSpec);        //SteelSeries Arctis Nova 5 / 7 dongles
            Register(RazerBatteryProvider.HidSpec);              //Razer wireless mice (matched by the 91-byte feature report)
        }

        public static void Register(HidDeviceSpec spec)
        {
            if (spec == null) throw new ArgumentNullException("spec");
            specs.Add(spec);
        }

        /// <summary>
        /// So enumeration can skip opening HID interfaces that could never match.
        /// </summary>
        public static ICollection<ushort> GetVendorIds()
        {
            List<ushort> vendorIds = new List<ushort>();
            foreach (HidDeviceSpec spec in specs)
            {
                if (!vendorIds.Contains(spec.VendorId))
                    vendorIds.Add(spec.VendorId);
            }
            return vendorIds;
        }

        public static HidDeviceSpec Match(HidInterfaceInfo info)
        {
            foreach (HidDeviceSpec spec in specs)
            {
                if (spec.Matches(info))
                    return spec;
            }
            return null;
        }
    }
}
