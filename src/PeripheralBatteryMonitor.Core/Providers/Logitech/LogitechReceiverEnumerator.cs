using System;
using System.Collections.Generic;
using System.Text;
using PeripheralBatteryMonitor.Diagnostics;
using PeripheralBatteryMonitor.Hid;

namespace PeripheralBatteryMonitor.Providers.Logitech
{
    /// <summary>
    /// Turns one Logitech receiver into the peripherals actually paired to it: a single HID
    /// interface carrying up to six devices at HID++ index 1..6. Index 0xFF addresses the
    /// receiver itself, which speaks HID++ 1.0 registers and implements no 2.0 battery feature
    /// -- reading every collection at 0xFF found nothing and surfaced a phantom "unknown
    /// battery" entry, which is why the provider's id allowlist existed.
    ///
    /// <b>A child device exists here only because something answered a root-feature ping at
    /// its index *and* implements a battery feature we can read.</b> The receiver is never
    /// surfaced. That makes a receiver id shared across products harmless -- 0xC547 ships with
    /// the G915 X TKL, the PRO X Superlight and the G502 X alike -- because nothing here names
    /// the peripheral from the receiver's id. It asks the device.
    ///
    /// <b>Unverified against hardware.</b> The ping gate is what makes that safe to ship: a
    /// wrong id costs one sweep that finds nothing, not a wrong reading.
    /// </summary>
    internal class LogitechReceiverEnumerator : IHidDeviceExpander
    {
            //Slots a Unifying-style receiver can pair. LIGHTSPEED dongles usually hold one.
        private const byte FIRST_INDEX = 1;
        private const byte LAST_INDEX = 6;

            //An empty slot says nothing and costs the whole timeout, so six of them is the
            //worst case: 6 x 300 ms of UI-thread stall. Deliberately several times the 80 ms
            //this started at -- a receiver has to forward the ping over the air first, and a
            //timeout chosen to bound the stall is worthless if it bounds out the answer too.
        private const int PING_TIMEOUT_MS = 300;

            //Real exchanges with a device that has already proved it is listening.
        private const int QUERY_TIMEOUT_MS = 500;

            //Long enough to amortise the cost above; short enough that pairing a new
            //peripheral shows up without restarting the app.
        private static readonly TimeSpan ReprobeInterval = TimeSpan.FromMinutes(15);

            //The usual cause is another process holding the collection, which clears on its
            //own -- but not so short that we retry an exclusive open every tick.
        private static readonly TimeSpan RetryAfterOpenFailure = TimeSpan.FromSeconds(60);

            //The expensive case -- six pings each running to the full timeout -- so it is asked
            //less often than an open failure. The interval can be this long only because the
            //tray's Refresh forces a re-sweep past it; see the <c>force</c> argument.
        private static readonly TimeSpan RetryAfterEmptySweep = TimeSpan.FromMinutes(5);

            //0x0005 DEVICE_NAME: func 0 gives the length, func 1 fetches 16 characters at a
            //time. Worth the two or three transactions -- the alternative in the tray is
            //"USB Receiver device 1".
        private const ushort FEATURE_DEVICE_NAME = 0x0005;
        private const int DEVICE_NAME_CHUNK = 16;

            //A slot must implement one of these to be worth surfacing: a paired device with no
            //readable battery is exactly the phantom entry this design exists to prevent.
        private static readonly ushort[] batteryFeatures = new ushort[] { 0x1004, 0x1000, 0x1001, 0x1F20 };

        private class Sweep
        {
            public DateTime When;
            public List<HidDiscoveredDevice> Devices;
            public bool OpenFailed;

            public TimeSpan ValidFor
            {
                get
                {
                    if (OpenFailed)
                        return RetryAfterOpenFailure;
                    return Devices.Count == 0 ? RetryAfterEmptySweep : ReprobeInterval;
                }
            }
        }

            //Keyed on the interface path, which identifies one receiver in one USB port. Static
            //because HidDeviceSpec holds a single expander instance shared by every interface
            //the spec matches.
        private static readonly Dictionary<string, Sweep> sweeps = new Dictionary<string, Sweep>();

        public List<HidDiscoveredDevice> Expand(HidInterfaceInfo info, HidDeviceSpec spec, bool force)
        {
            lock (sweeps)
            {
                Sweep cached;
                if (!force && sweeps.TryGetValue(info.Path, out cached))
                {
                    if (DateTime.UtcNow - cached.When < cached.ValidFor)
                        return cached.Devices;
                }

                Sweep fresh = new Sweep();
                fresh.When = DateTime.UtcNow;
                fresh.Devices = Probe(info, spec, out fresh.OpenFailed);
                sweeps[info.Path] = fresh;
                return fresh.Devices;
            }
        }

        /// <summary>One open handle for the whole sweep: six separate opens cost more than the
        /// pings do.</summary>
        private static List<HidDiscoveredDevice> Probe(HidInterfaceInfo info, HidDeviceSpec spec, out bool openFailed)
        {
            List<HidDiscoveredDevice> found = new List<HidDiscoveredDevice>();
            openFailed = false;

            try
            {
                    //A sweep that finds nothing is the shape of every "my Logitech device is
                    //missing" report; without this line it looks like a sweep that never ran.
                Log.Write("Logitech", "receiver sweep: " + info);

                using (IHidppTransport hidpp = HidppTransport.Open(info))
                {
                    if (hidpp == null)
                    {
                        openFailed = true;
                        return found;   //retried in a minute; the usual cause is vendor software
                    }

                    for (byte index = FIRST_INDEX; index <= LAST_INDEX; index++)
                    {
                        if (!hidpp.Ping(index, PING_TIMEOUT_MS))
                        {
                                //Silent means "no device paired here" and "paired but switched
                                //off" alike -- the receiver answers for neither, and its one
                                //reply that would tell them apart is a HID++ 1.0 error it sends
                                //on the short collection, which is a handle this does not hold.
                            Log.Write("Logitech", "receiver slot " + index + ": silent");
                            continue;
                        }

                        ushort featureId;
                        if (!HasBatteryFeature(hidpp, index, out featureId))
                        {
                                //Paired and awake, but unreadable: surfacing it would put a
                                //permanent "?" in the tray.
                            Log.Write("Logitech", "receiver slot " + index + ": answers but implements no battery feature -- not surfaced");
                            continue;
                        }

                        string name = ReadDeviceName(hidpp, index) ?? (spec.NameFor(info) + " device " + index);
                        Log.Write("Logitech", "receiver slot " + index + " = '" + name
                            + "', battery feature 0x" + featureId.ToString("X4"));

                        found.Add(ProviderHid.Describe(info, name, index));
                    }
                }
            }
            catch (Exception e)
            {
                Log.Write("Logitech", "receiver sweep failed on " + info.Path + ": " + e.Message);
            }

            Log.Write("Logitech", "receiver sweep found " + found.Count + " device(s)");
            return found;
        }

        private static bool HasBatteryFeature(IHidppTransport hidpp, byte deviceIndex, out ushort featureId)
        {
            foreach (ushort candidate in batteryFeatures)
            {
                if (hidpp.GetFeatureIndex(deviceIndex, candidate, QUERY_TIMEOUT_MS) != 0)
                {
                    featureId = candidate;
                    return true;
                }
            }
            featureId = 0;
            return false;
        }

        private static string ReadDeviceName(IHidppTransport hidpp, byte deviceIndex)
        {
            byte featureIndex = hidpp.GetFeatureIndex(deviceIndex, FEATURE_DEVICE_NAME, QUERY_TIMEOUT_MS);
            if (featureIndex == 0)
                return null;

            byte[] lengthReply = hidpp.Request(deviceIndex, featureIndex, 0x00, null, QUERY_TIMEOUT_MS);
            if (lengthReply == null || lengthReply.Length < 5)
                return null;

            int length = lengthReply[4];
            if (length <= 0 || length > 64)
                return null;

            StringBuilder name = new StringBuilder(length);
            for (int offset = 0; offset < length; offset += DEVICE_NAME_CHUNK)
            {
                byte[] chunk = hidpp.Request(deviceIndex, featureIndex, 0x01,
                    new byte[] { (byte)offset }, QUERY_TIMEOUT_MS);
                if (chunk == null)
                    break;

                for (int i = 4; i < chunk.Length && name.Length < length; i++)
                {
                    if (chunk[i] == 0)
                        break;
                    name.Append((char)chunk[i]);   //ASCII, per the feature definition
                }
            }

            string text = name.ToString().Trim();
            return String.IsNullOrEmpty(text) ? null : text;
        }
    }
}
