using PeripheralBatteryMonitor.Diagnostics;
using PeripheralBatteryMonitor.Hid;

namespace PeripheralBatteryMonitor.Providers.Logitech
{
    /// <summary>
    /// Talks to a Logitech vendor collection and hex-logs what comes back, for the startup
    /// snapshot.
    ///
    /// It exists because the Centurion support was written blind: Solaar's framing and an
    /// independently observed battery exchange agree on the request header exactly and
    /// disagree on where the reply's payload begins, and that offset cannot be settled without
    /// the hardware. Rather than guess and read a plausible neighbouring byte, this asks the
    /// device -- a root ping and a root <c>getFeature</c> are the protocol's two most benign
    /// transactions, and their replies pin the offsets.
    /// </summary>
    internal class LogitechProbe : IHidInterfaceProbe
    {
            //An absent device costs the whole timeout, so this bounds how long the startup
            //snapshot takes on a machine with several unrecognised Logitech collections.
        private const int TIMEOUT_MS = 80;

            //The cheapest question producing a structured answer, and the first feature the
            //provider would probe anyway.
        private const ushort FEATURE_UNIFIED_BATTERY = 0x1004;

        public ushort VendorId
        {
            get { return 0x046D; }
        }

        public void Probe(HidInterfaceInfo info)
        {
                //Both framings, not whichever the usage page suggests: these are collections
                //nobody has classified yet, so guessing here would be the guess this exists to
                //replace. A framing the device does not speak stays silent, which is an answer.
            ProbeFraming("Centurion", CenturionTransport.Open(info));
            ProbeFraming("HID++", HidppTransport.Open(info));
        }

        private static void ProbeFraming(string framing, IHidppTransport hidpp)
        {
            if (hidpp == null)
                return;

            using (hidpp)
            {
                    //0xFF is a device behind its own dongle, 0x01 the first slot of a receiver.
                    //Centurion ignores the argument, so its duplicate line is harmless.
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

                        //The raw wire frame the transport hex-logs is the real output here --
                        //this reply is the one whose payload offset is in doubt.
                    byte[] reply = hidpp.Request(deviceIndex, featureIndex, 0x01, null, TIMEOUT_MS);
                    Log.WriteHex("Probe", who + " 0x1004 getStatus, normalised:", reply, reply == null ? 0 : reply.Length);
                }
            }
        }
    }
}
