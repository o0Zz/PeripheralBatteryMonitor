using PeripheralBatteryMonitor.Diagnostics;
using PeripheralBatteryMonitor.Hid;

namespace PeripheralBatteryMonitor.Providers.Logitech
{
    /// <summary>
    /// Talks to a Logitech vendor collection and hex-logs what comes back, for the startup
    /// snapshot. <c>DiagnosticReport</c> decides which collections are worth asking -- the
    /// unclaimed vendor-defined ones -- and this only answers what to say to one.
    ///
    /// It exists because the Centurion support was written blind. Solaar's framing and an
    /// independently observed battery exchange agree on the request header exactly, and
    /// disagree on where the answer's payload begins -- and that is one byte offset nobody
    /// can settle without the hardware. Rather than guess it and read a plausible neighbouring
    /// byte, the snapshot asks the device: a root-feature ping and a root <c>getFeature</c>
    /// are the two most benign transactions the protocol has, and their replies pin the
    /// offsets.
    /// </summary>
    internal class LogitechProbe : IHidInterfaceProbe
    {
            //A device that is present answers a ping in well under a millisecond; one that is
            //absent says nothing at all and costs the whole timeout. Same reasoning, and the
            //same number, as the receiver sweep in LogitechReceiverEnumerator -- and here it
            //is what bounds how long the startup snapshot can take on a machine with several
            //unrecognised Logitech collections.
        private const int TIMEOUT_MS = 80;

            //0x1004 UNIFIED_BATTERY. Asking the root feature to resolve it is the cheapest
            //question that produces a structured answer, and it is the first feature the
            //provider would probe anyway.
        private const ushort FEATURE_UNIFIED_BATTERY = 0x1004;

        public ushort VendorId
        {
            get { return 0x046D; }
        }

        public void Probe(HidInterfaceInfo info)
        {
                //Both framings, not whichever the usage page suggests. The probe is looking at
                //collections nobody has classified yet -- that is the whole point of it -- and
                //a 64-byte collection can be opened by either transport, so guessing here
                //would be the same guess the report exists to replace. A framing the device
                //does not speak simply stays silent, and the silence is itself an answer.
            ProbeFraming("Centurion", CenturionTransport.Open(info));
            ProbeFraming("HID++", HidppTransport.Open(info));
        }

        private static void ProbeFraming(string framing, IHidppTransport hidpp)
        {
            if (hidpp == null)
                return;

            using (hidpp)
            {
                    //Both device indexes worth asking about: 0xFF is a device behind its own
                    //dongle, 0x01 the first slot of a receiver. Centurion ignores the argument,
                    //so it answers the same to both and the duplicate line is harmless.
                foreach (byte deviceIndex in new byte[] { Hidpp.DEVICE_INDEX_DIRECT, 0x01 })
                {
                    string who = "    " + framing + " index 0x" + deviceIndex.ToString("X2");

                    if (!hidpp.Ping(deviceIndex, TIMEOUT_MS))
                    {
                        Log.Write("Probe", who + " ping: silent");
                        continue;
                    }
                    Log.Write("Probe", who + " ping: answered");

                    byte featureIndex = hidpp.GetFeatureIndex(deviceIndex, FEATURE_UNIFIED_BATTERY, TIMEOUT_MS);
                    Log.Write("Probe", who + " feature 0x1004 -> index 0x" + featureIndex.ToString("X2")
                        + (featureIndex == 0 ? " (not implemented)" : ""));

                    if (featureIndex == 0)
                        continue;

                        //getStatus. Its reply is the one whose payload offset is in doubt, so
                        //the raw wire frame the transport already hex-logs is the real output
                        //here; this line only says what we made of it.
                    byte[] reply = hidpp.Request(deviceIndex, featureIndex, 0x01, null, TIMEOUT_MS);
                    Log.WriteHex("Probe", who + " 0x1004 getStatus, normalised:", reply, reply == null ? 0 : reply.Length);
                }
            }
        }
    }
}
