using PeripheralBatteryMonitor.Diagnostics;
using PeripheralBatteryMonitor.Hid;

namespace PeripheralBatteryMonitor.Providers.Logitech
{
    /// <summary>
    /// Asks a Logitech vendor collection nobody has classified yet the two questions worth
    /// asking, and hex-logs what comes back, for the startup snapshot.
    ///
    /// It exists because discovery only ever opens collections a registered spec claims, so a
    /// device the app does not recognise is invisible to every other line in the log -- and
    /// that is the shape of almost every report. A framing the collection does not speak stays
    /// silent, which is itself an answer.
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
            ProbeHidpp(info);

                //The one command the Centurion headsets answer. A newer headset under an
                //unknown product id reports its battery straight into the snapshot, which is
                //the whole point of this file. The size test is here rather than in
                //CenturionBattery so a collection too small for the framing does not log the
                //same refusal twice.
            if (info.InputReportByteLength >= CenturionBattery.FRAME_SIZE
                && info.OutputReportByteLength >= CenturionBattery.FRAME_SIZE)
            {
                int? level = CenturionBattery.Read(info, TIMEOUT_MS);
                Log.Write("Probe", "    Centurion vendor battery: "
                    + (level.HasValue ? level.Value + "%" : "silent"));
            }
        }

        private static void ProbeHidpp(HidInterfaceInfo info)
        {
                //No companion collection: this is a lone unclassified interface, and pairing it
                //with a sibling is discovery's job, not a diagnostic's.
            using (HidppTransport hidpp = HidppTransport.Open(info, null))
            {
                if (hidpp == null)
                    return;

                    //0xFF is a device behind its own dongle, 0x01 the first slot of a receiver.
                foreach (byte deviceIndex in new byte[] { HidppTransport.DEVICE_INDEX_DIRECT, 0x01 })
                {
                    string who = "    HID++ index 0x" + deviceIndex.ToString("X2");

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

                    byte[] reply = hidpp.Request(deviceIndex, featureIndex, 0x01, null, TIMEOUT_MS);
                    Log.WriteHex("Probe", who + " 0x1004 getStatus:", reply, reply == null ? 0 : reply.Length);
                }
            }
        }
    }
}
