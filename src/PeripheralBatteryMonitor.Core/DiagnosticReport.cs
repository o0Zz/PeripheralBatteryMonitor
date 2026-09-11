using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using PeripheralBatteryMonitor.Diagnostics;
using PeripheralBatteryMonitor.Hid;
using PeripheralBatteryMonitor.Providers;

namespace PeripheralBatteryMonitor
{
    /// <summary>
    /// Writes the opening pages of every session's log: what machine this is, and every HID
    /// interface present on it.
    ///
    /// It exists because of one gap the continuous log cannot close on its own. Discovery's
    /// enumeration is pre-filtered to the *vendor ids* some spec registered
    /// (<c>HidDeviceSpecRegistry.GetVendorIds</c>), so the poll tick never so much as looks
    /// at an interface from an unregistered vendor -- and "the app cannot see my device at
    /// all" is the shape of almost every report. Enumerating with no filter, once, is what
    /// puts that interface in the file with its VID/PID/usage page, which is what adding
    /// support for it starts from.
    ///
    /// Nothing here is per-device: the continuous log records every read, every provider
    /// decision and every failed handle as they happen.
    ///
    /// At the project root, not under <c>Diagnostics/</c>: the App calls this, and the root
    /// is this assembly's public API. It also depends on <c>Hid/</c> and <c>Providers/</c>,
    /// which a file under <c>Diagnostics/</c> deliberately may not.
    /// </summary>
    public static class DiagnosticReport
    {
        /// <summary>
        /// Real I/O -- one query-only <c>CreateFile</c> per HID interface on the machine, plus
        /// the vendor probe -- so this is <b>not</b> for the startup path. Call it once the
        /// message loop is running (<c>BeginInvoke</c> from the <c>Settings</c> constructor --
        /// not from its <c>OnLoad</c>, which <c>SetVisibleCore</c> keeps from ever running
        /// unless the user opens that window): running it
        /// before <c>Application.Run</c> is seconds of no tray icon at all on a machine with
        /// sixty HID interfaces and a device in selective suspend, which is precisely the
        /// machine whose owner already suspects the app is broken.
        ///
        /// It must stay on the UI thread rather than a worker, for the reason CLAUDE.md gives
        /// about HID handles: two concurrent conversations on one collection can land a reply
        /// on the wrong handle. The poll tick runs on the UI thread, so sharing it is what
        /// keeps this snapshot and a poll from ever overlapping.
        /// </summary>
        public static void WriteStartupSnapshot()
        {
            try
            {
                Log.Write("Report", "================ startup snapshot ================");
                WriteHeader();

                    //null means "no vendor pre-filter" -- the whole point is the interfaces the
                    //poll tick never even looks at. Enumerated once and handed to both sections,
                    //because each interface here costs a CreateFile.
                List<HidInterfaceInfo> all = HidInterfaceEnumerator.Enumerate(null);

                WriteHidInterfaces(all);
                ProbeUnclaimedVendorCollections(all);
                Log.Write("Report", "================ end of snapshot ================");
            }
            catch (Exception e)
            {
                    //A snapshot that half-wrote is still worth having, and the exception that
                    //stopped it is worth more than most of the rest of it.
                Log.Write("Report", "snapshot aborted: " + e);
            }
        }

        private static void WriteHeader()
        {
            Assembly entry = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();

            Log.Write("Report", "version    " + entry.GetName().Version + "  (" + entry.GetName().Name + ")");
            Log.Write("Report", "os         " + Environment.OSVersion + ", 64-bit process: " + Environment.Is64BitProcess
                + ", 64-bit os: " + Environment.Is64BitOperatingSystem);
            Log.Write("Report", "clr        " + Environment.Version);
            Log.Write("Report", "culture    ui=" + CultureInfo.CurrentUICulture.Name
                + " formatting=" + CultureInfo.CurrentCulture.Name);
            Log.Write("Report", "time       " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                + " local, " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " utc");
            Log.Write("Report", "log        " + Log.FilePath);
        }

        /// <summary>
        /// Every HID interface present, and for each one whether a spec claims it. An
        /// interface listed here as claimed by no spec is one the app cannot see at all.
        /// </summary>
        private static void WriteHidInterfaces(List<HidInterfaceInfo> all)
        {
            Log.Write("Report", "---- HID interfaces (all vendors) ----");
            Log.Write("Report", all.Count + " interface(s)");

            foreach (HidInterfaceInfo info in all)
            {
                HidDeviceSpec spec = HidDeviceSpecRegistry.Match(info);
                Log.Write("Report", (spec != null ? "  claimed by '" + spec.FallbackName + "'" : "  claimed by no spec")
                    + "  " + info);
                Log.Write("Report", "      " + info.Path);
            }
        }

        /// <summary>
        /// Every vendor-defined collection no spec claims, and whatever the vendor's own probe
        /// can get out of it. This is the case nothing else in the log covers: a claimed
        /// collection is already traced by the read path on every poll, in both directions.
        ///
        /// Which collections qualify is decided here and the vendor conversation is not -- see
        /// <c>IHidInterfaceProbe</c>. A collection with no probe registered is still worth its
        /// line: that is the app saying, in as many words, that it can see the device and has
        /// nothing to say to it.
        /// </summary>
        private static void ProbeUnclaimedVendorCollections(List<HidInterfaceInfo> all)
        {
            Log.Write("Report", "---- unclaimed vendor collections ----");

            foreach (HidInterfaceInfo info in all)
            {
                    //Vendor-defined pages only. The mouse, keyboard and consumer-control
                    //collections beside them speak no vendor protocol and Windows often holds
                    //them exclusively, so probing them would produce a page of access-denied.
                if (info.UsagePage < 0xFF00)
                    continue;

                if (HidDeviceSpecRegistry.Match(info) != null)
                    continue;   //the read path already traces this one, every poll

                Log.Write("Report", "collection " + info);
                Log.Write("Report", "    " + info.Path);

                IHidInterfaceProbe probe = HidInterfaceProbeRegistry.Match(info);
                if (probe == null)
                {
                    Log.Write("Report", "    no probe registered for VID_" + info.VendorId.ToString("X4"));
                    continue;
                }

                try
                {
                    probe.Probe(info);
                }
                catch (Exception e)
                {
                    Log.Write("Report", "    probe threw: " + e.Message);
                }
            }
        }
    }
}
