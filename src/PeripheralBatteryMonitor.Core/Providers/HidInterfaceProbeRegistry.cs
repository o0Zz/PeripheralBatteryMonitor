using System;
using System.Collections.Generic;
using PeripheralBatteryMonitor.Hid;
using PeripheralBatteryMonitor.Providers.Logitech;

namespace PeripheralBatteryMonitor.Providers
{
    /// <summary>
    /// The vendor probes the startup snapshot may run against a HID interface nothing claims.
    /// Third companion to <see cref="BatteryProviderRegistry"/> and
    /// <see cref="HidDeviceSpecRegistry"/>: those say how to read a battery and which devices
    /// exist, this says who can still get a word out of a collection the other two ignore.
    ///
    /// This is the only file that names a probe, which is what keeps <c>DiagnosticReport</c>
    /// -- a root file -- from naming a vendor.
    ///
    /// Probes are stateless and run once per session, so these are instances rather than the
    /// factories <see cref="BatteryProviderRegistry"/> keeps.
    /// </summary>
    internal static class HidInterfaceProbeRegistry
    {
        private static readonly List<IHidInterfaceProbe> probes = new List<IHidInterfaceProbe>();

        static HidInterfaceProbeRegistry()
        {
            Register(new LogitechProbe());   //HID++ and Centurion framing, for the offsets nobody could verify
        }

        /// <summary>Add a probe. Do this before the snapshot runs for it to take effect.</summary>
        public static void Register(IHidInterfaceProbe probe)
        {
            if (probe == null) throw new ArgumentNullException("probe");
            probes.Add(probe);
        }

        /// <summary>The first probe that speaks this interface's vendor protocol, or null.</summary>
        public static IHidInterfaceProbe Match(HidInterfaceInfo info)
        {
            foreach (IHidInterfaceProbe probe in probes)
            {
                if (probe.VendorId == info.VendorId)
                    return probe;
            }
            return null;
        }
    }
}
