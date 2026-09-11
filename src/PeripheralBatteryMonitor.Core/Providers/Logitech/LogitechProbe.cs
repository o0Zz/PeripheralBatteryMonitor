using System;
using PeripheralBatteryMonitor.Diagnostics;
using PeripheralBatteryMonitor.Hid;

namespace PeripheralBatteryMonitor.Providers.Logitech
{
    /// <summary>
    /// Talks to every Logitech vendor collection on the machine and hex-logs what comes back,
    /// for <see cref="DiagnosticReport"/>.
    ///
    /// This exists because the Centurion support was written blind. Solaar's framing and an
    /// independently observed battery exchange agree on the request header exactly, and
    /// disagree on where the answer's payload begins -- and that is one byte offset nobody
    /// can settle without the hardware. Rather than guess it and read a plausible neighbouring
    /// byte, the snapshot asks the device: a root-feature ping and a root <c>getFeature</c>
    /// are the two most benign transactions the protocol has, and their replies pin the
    /// offsets.
    ///
    /// <b>It runs only against collections no spec claims.</b> A claimed one is already
    /// exercised by the normal read path on every poll, and both transports hex-log each
    /// frame in both directions -- so probing it here would open a second handle to the same
    /// hardware to learn what the log already says. What is left is exactly the case nothing
    /// else covers: a Logitech vendor collection the app cannot currently see at all.
    /// </summary>
    internal static class LogitechProbe
    {
        private const ushort LOGITECH_VENDOR_ID = 0x046D;

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

        /// <summary>
        /// Probe the Logitech vendor collections no spec claims. Writes to the log; returns
        /// nothing, because nothing here decides anything -- it only records.
        /// </summary>
        public static void ProbeUnclaimed()
        {
            Log.Write("Probe", "---- unclaimed Logitech vendor collections ----");

            foreach (HidInterfaceInfo info in HidInterfaceEnumerator.Enumerate(new ushort[] { LOGITECH_VENDOR_ID }))
            {
                    //Vendor-defined pages only. The mouse, keyboard and consumer-control
                    //collections beside them speak no vendor protocol and Windows often holds
                    //them exclusively, so probing them would produce a page of access-denied.
                if (info.UsagePage < 0xFF00)
                    continue;

                if (HidDeviceSpecRegistry.Match(info) != null)
                    continue;   //the read path already traces this one, every poll

                Log.Write("Probe", "collection " + info);
                Log.Write("Probe", "    " + info.Path);

                try
                {
                    ProbeOne(info);
                }
                catch (Exception e)
                {
                    Log.Write("Probe", "    threw: " + e.Message);
                }
            }
        }

        private static void ProbeOne(HidInterfaceInfo info)
        {
                //Both framings, not whichever the usage page suggests. The probe is looking at
                //collections nobody has classified yet -- that is the whole point of it -- and
                //a 64-byte collection can be opened by either transport, so guessing here
                //would be the same guess the report exists to replace. A framing the device
                //does not speak simply stays silent, and the silence is itself an answer.
            ProbeFraming(info, "Centurion", CenturionTransport.Open(info));
            ProbeFraming(info, "HID++", HidppTransport.Open(info));
        }

        private static void ProbeFraming(HidInterfaceInfo info, string framing, IHidppTransport hidpp)
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
