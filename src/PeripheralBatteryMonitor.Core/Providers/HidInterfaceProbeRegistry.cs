using System;
using System.Collections.Generic;
using PeripheralBatteryMonitor.Hid;
using PeripheralBatteryMonitor.Providers.Logitech;

namespace PeripheralBatteryMonitor.Providers
{
    /// <summary>
    /// The vendor probes the startup snapshot may run against a HID interface nothing claims.
    ///
    /// The only file that names a probe, which is what keeps <c>DiagnosticReport</c> -- a root
    /// file -- from naming a vendor. Probes are stateless and run once per session, so these
    /// are instances rather than the factories <see cref="BatteryProviderRegistry"/> keeps.
    /// </summary>
    internal static class HidInterfaceProbeRegistry
    {
        private static readonly List<IHidInterfaceProbe> probes = new List<IHidInterfaceProbe>();

        static HidInterfaceProbeRegistry()
        {
            Register(new LogitechProbe());   //HID++ and Centurion framing, for the offsets nobody could verify
        }

        public static void Register(IHidInterfaceProbe probe)
        {
            if (probe == null) throw new ArgumentNullException("probe");
            probes.Add(probe);
        }

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
