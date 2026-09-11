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

            //Short, and deliberately much shorter than the provider's read timeout. A device
            //that is present answers a ping in well under a millisecond; an absent slot says
            //nothing at all and costs the whole timeout. Six empty slots is the worst case, so
            //this number is the one that decides how long the sweep can stall the UI thread:
            //6 x 80 ms, once per re-probe interval.
        private const int PING_TIMEOUT_MS = 80;

            //Resolving a feature and reading a name are real exchanges with a device that has
            //already proved it is listening, so they can afford the normal budget.
        private const int QUERY_TIMEOUT_MS = 500;

            //How long a sweep's result stands. Long enough that the cost above is amortised to
            //nothing against a five-minute poll; short enough that pairing a new peripheral
            //through Logitech's own software shows up without restarting this app.
        private static readonly TimeSpan ReprobeInterval = TimeSpan.FromMinutes(15);

            //And how long a *failure* stands. Shorter, because the usual cause is another
            //process holding the collection open, which clears on its own -- but not so short
            //that we retry an exclusive open on every single tick.
        private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(60);

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
        }

            //Keyed on the interface path, which is what identifies one receiver in one USB
            //port. Static because HidDeviceSpec holds a single expander instance shared by
            //every interface the spec matches, and because a sweep's result is a property of
            //the hardware rather than of any one device object.
        private static readonly Dictionary<string, Sweep> sweeps = new Dictionary<string, Sweep>();

        public List<HidDiscoveredDevice> Expand(HidInterfaceInfo info, HidDeviceSpec spec)
        {
            lock (sweeps)
            {
                Sweep cached;
                if (sweeps.TryGetValue(info.Path, out cached))
                {
                    TimeSpan age = DateTime.UtcNow - cached.When;
                    TimeSpan validFor = cached.Devices.Count == 0 ? RetryAfterFailure : ReprobeInterval;
                    if (age < validFor)
                        return cached.Devices;
                }

                Sweep fresh = new Sweep();
                fresh.When = DateTime.UtcNow;
                fresh.Devices = Probe(info, spec);
                sweeps[info.Path] = fresh;
                return fresh.Devices;
            }
        }

        /// <summary>
        /// Ask each slot whether anything is there. One open handle for the whole sweep -- six
        /// separate opens would cost more than the pings do.
        /// </summary>
        private static List<HidDiscoveredDevice> Probe(HidInterfaceInfo info, HidDeviceSpec spec)
        {
            List<HidDiscoveredDevice> found = new List<HidDiscoveredDevice>();

            try
            {
                using (IHidppTransport hidpp = HidppTransport.Open(info))
                {
                    if (hidpp == null)
                        return found;   //cached as a failure, retried in a minute

                    for (byte index = FIRST_INDEX; index <= LAST_INDEX; index++)
                    {
                        if (!hidpp.Ping(index, PING_TIMEOUT_MS))
                            continue;

                        ushort featureId;
                        if (!HasBatteryFeature(hidpp, index, out featureId))
                        {
                                //Paired and awake, but nothing here can read its battery.
                                //Surfacing it would put a permanent "?" in the tray, which is
                                //the phantom entry in a different costume.
                            Log.Write("Logitech", "receiver slot " + index + " answers but implements no battery feature -- not surfaced");
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
