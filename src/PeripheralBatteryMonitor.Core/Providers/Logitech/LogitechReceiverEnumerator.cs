using System;
using System.Collections.Generic;
using System.Text;
using PeripheralBatteryMonitor.Diagnostics;
using PeripheralBatteryMonitor.Hid;

namespace PeripheralBatteryMonitor.Providers.Logitech
{
    /// <summary>
    /// Turns one Logitech receiver into the peripherals actually paired to it.
    ///
    /// A LIGHTSPEED or Unifying receiver is a single HID interface carrying up to six devices,
    /// addressed by HID++ device index 1..6. Index 0xFF addresses the receiver itself, which
    /// speaks HID++ 1.0 registers and implements no 2.0 battery feature at all -- so the old
    /// behaviour of reading every Logitech collection at 0xFF found nothing and surfaced a
    /// phantom "unknown battery" entry, which is why the provider's product id allowlist
    /// existed.
    ///
    /// <b>This is a better answer to that than an allowlist, and it is why the allowlist can
    /// stop carrying the weight.</b> A child device exists here only because something
    /// answered a root-feature ping at its index *and* implements a battery feature we can
    /// read. The receiver is never surfaced. That makes a receiver product id shared across
    /// several products harmless -- 0xC547 ships with the G915 X TKL, the PRO X Superlight and
    /// the G502 X LIGHTSPEED alike -- because nothing here identifies the peripheral from the
    /// receiver's id. It asks the device.
    ///
    /// <b>Unverified against hardware.</b> The receiver product ids come from published device
    /// tables, and nobody working on this has one. The ping gate is what makes that safe to
    /// ship: a wrong id costs one sweep that finds nothing, not a wrong reading.
    /// </summary>
    internal class LogitechReceiverEnumerator : IHidDeviceExpander
    {
            //Slots a Unifying-style receiver can pair. LIGHTSPEED dongles usually hold one.
        private const byte FIRST_INDEX = 1;
        private const byte LAST_INDEX = 6;

            //An occupied slot answers in well under a millisecond *once the peripheral is
            //awake*; an empty one says nothing and costs the whole timeout, and a receiver has
            //to forward the ping over the air before either can happen. Six empty slots is the
            //worst case, so this number decides how long a sweep can stall the UI thread:
            //6 x 300 ms. It is deliberately several times the 80 ms this started at -- that
            //was measured against nothing, and a value chosen to bound the stall is worthless
            //if it also bounds out the answer.
        private const int PING_TIMEOUT_MS = 300;

            //Resolving a feature and reading a name are real exchanges with a device that has
            //already proved it is listening, so they can afford the normal budget.
        private const int QUERY_TIMEOUT_MS = 500;

            //How long a sweep's result stands. Long enough that the cost above is amortised to
            //nothing against a five-minute poll; short enough that pairing a new peripheral
            //through Logitech's own software shows up without restarting this app.
        private static readonly TimeSpan ReprobeInterval = TimeSpan.FromMinutes(15);

            //How long "the collection would not open" stands. Short, because the usual cause is
            //another process holding it -- which clears on its own -- but not so short that we
            //retry an exclusive open on every single tick.
        private static readonly TimeSpan RetryAfterOpenFailure = TimeSpan.FromSeconds(60);

            //How long "the receiver answered for nobody" stands. This is the expensive case --
            //six pings that each run to the full timeout -- and the cheap thing to do with an
            //expensive question is ask it less often, so it is not retried at the open-failure
            //rate. A peripheral switched on during the gap is found by the poll after it, and
            //a user who does not want to wait has the tray's Refresh, which forces a re-sweep
            //(see the <c>force</c> argument). That pairing is the point: the interval can be
            //this long only because there is a way to skip it.
        private static readonly TimeSpan RetryAfterEmptySweep = TimeSpan.FromMinutes(5);

            //0x0005 DEVICE_NAME: func 0 gives the length, func 1 fetches 16 characters at a
            //time. Two or three transactions, run once per sweep and cached with it -- worth
            //it, because the alternative in the tray is "USB Receiver device 1".
        private const ushort FEATURE_DEVICE_NAME = 0x0005;
        private const int DEVICE_NAME_CHUNK = 16;

            //The battery features the provider knows how to decode. A slot has to implement one
            //of them to be worth surfacing; a paired device with no readable battery would
            //otherwise be exactly the phantom entry this design exists to prevent.
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

            //Keyed on the interface path, which is what identifies one receiver in one USB
            //port. Static because HidDeviceSpec holds a single expander instance shared by
            //every interface the spec matches, and because a sweep's result is a property of
            //the hardware rather than of any one device object.
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

        /// <summary>
        /// Ask each slot whether anything is there. One open handle for the whole sweep -- six
        /// separate opens would cost more than the pings do.
        /// </summary>
        private static List<HidDiscoveredDevice> Probe(HidInterfaceInfo info, HidDeviceSpec spec, out bool openFailed)
        {
            List<HidDiscoveredDevice> found = new List<HidDiscoveredDevice>();
            openFailed = false;

            try
            {
                    //Say what is being swept before sweeping it. A sweep that finds nothing is
                    //the shape of every "my Logitech device is missing" report, and without
                    //this line the log of one is indistinguishable from a log where no receiver
                    //was ever claimed at all.
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
                                //Paired and awake, but nothing here can read its battery.
                                //Surfacing it would put a permanent "?" in the tray, which is
                                //the phantom entry in a different costume.
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

        /// <summary>
        /// The peripheral's own name, via feature 0x0005. Null when it does not implement it or
        /// will not answer, in which case the caller falls back to something generic.
        /// </summary>
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
