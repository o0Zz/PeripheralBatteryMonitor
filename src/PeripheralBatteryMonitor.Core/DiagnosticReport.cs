using System;
using System.Collections.Generic;
using PeripheralBatteryMonitor.Diagnostics;
using PeripheralBatteryMonitor.Hid;
using PeripheralBatteryMonitor.Providers;

namespace PeripheralBatteryMonitor
{
    public static class DiagnosticReport
    {
        private const string SUPPORTED = "[  Supported  ] ";
        private const string NOT_SUPPORTED = "[Not Supported] ";
        private const string CONTINUATION = "                ";
        private const ushort VENDOR_DEFINED_USAGE_PAGE = 0xFF00;


        public static void WriteStartupSnapshot()
        {
            try
            {
                List<HidInterfaceInfo> all = HidInterfaceEnumerator.Enumerate(null);

                Log.Write("Report", "---- HID interfaces (all vendors): " + all.Count + " present ----");
                foreach (HidInterfaceInfo info in all)
                {
                    HidDeviceSpec spec = HidDeviceSpecRegistry.Match(info);

                        //Path on the row, not under it. Everything else about an interface is
                        //already here, so a line of its own said almost nothing twice -- but it
                        //cannot be dropped: two collections of one device differ only by their
                        //mi_/col index, and their rows are otherwise byte-identical.
                    Log.Write("Report", (spec != null ? SUPPORTED : NOT_SUPPORTED) + info
                        + (spec != null ? "  -> '" + spec.FallbackName + "'" : "")
                        + "  " + HidInterfaceInfo.ShortPath(info.Path));

                    Probe(info, spec);
                }
            }
            catch (Exception e)
            {
                Log.Write("Report", "snapshot aborted: " + e);
            }
        }

        private static void Probe(HidInterfaceInfo info, HidDeviceSpec spec)
        {
            if (spec != null || info.UsagePage < VENDOR_DEFINED_USAGE_PAGE)
                return;

            IHidInterfaceProbe probe = HidInterfaceProbeRegistry.Match(info);
            if (probe == null)
                return;

            try
            {
                probe.Probe(info);
            }
            catch (Exception e)
            {
                Log.Write("Report", CONTINUATION + "probe threw: " + e.Message);
            }
        }
    }
}
