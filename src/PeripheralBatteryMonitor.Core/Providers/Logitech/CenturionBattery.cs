using System;
using System.Diagnostics;
using PeripheralBatteryMonitor.Diagnostics;
using PeripheralBatteryMonitor.Hid;

namespace PeripheralBatteryMonitor.Providers.Logitech
{
    /// <summary>
    /// The battery command Logitech's Centurion headsets actually answer, which is *not* a
    /// HID++ 2.0 feature call.
    ///
    /// The PRO X 2 LIGHTSPEED (0x046D/0x0AF7, usage page 0xFFA0) implements no battery feature:
    /// a root ping and a getFeature for 0x1004 both go unanswered, which is what the app used
    /// to do here and why it reported the headset as switched off while it was on. It answers
    /// exactly one fixed vendor request instead, and volunteers a frame whenever it is switched
    /// on or off.
    ///
    /// <code>
    /// request : 51 08 00 03 1A 00 03 00 04 0A  [00 * 54]          (64 bytes)
    /// battery : 51 0B .. .. .. .. .. .. 04 .. PCT .. CHG ...
    ///              │                    │     │      └ [12] 0x02 = charging
    ///              │                    │     └ [10] percent, 0..100
    ///              │                    └ [8]  tag, must be 0x04
    ///              └ [1] frame kind: 0x03 ack, 0x05 power event, 0x0B battery
    /// power   : 51 05 00 03 00 00 XX 00        [6] 0x00 = off, 0x01 = on
    /// </code>
    ///
    /// Offsets are raw-wire, report id included -- the same bytes <see cref="HidDevice.Read"/>
    /// hands back, with none of the hidapi off-by-one that <c>SteelSeriesBatteryProvider</c>
    /// has to correct for: this collection uses numbered reports, so hidapi keeps byte 0 too
    /// (the published captures all start <c>51</c>).
    ///
    /// Sources, three independent implementations agreeing byte for byte:
    /// <list type="bullet">
    ///   <item>https://github.com/LiamBateman/logibar -- logibar-headset-monitor, and the
    ///         "Headset: a vendor protocol" section of its README</item>
    ///   <item>https://ratatoskr.run/linux-input/2026/09/17552386/t -- "[PATCH] HID:
    ///         logitech-headset: add a battery", proposed Linux driver</item>
    ///   <item>https://github.com/Latency404/Logitech-G-Tray-Tool -- where the name
    ///         "Centurion" comes from</item>
    /// </list>
    ///
    /// <b>Still unverified against hardware here</b>, as nobody on this side owns a PRO X 2 --
    /// but no longer a guess either, which is what the feature-layer attempt was.
    /// </summary>
    internal static class CenturionBattery
    {
            //Sent verbatim, padded with zeros to the frame size. It decomposes into the
            //Centurion envelope -- report 0x51, cplLength 0x08, flags 0x00, feature index
            //0x03, function 1 with software id 0xA, parameters 00 03 00 04 0A. That it
            //decomposes so cleanly is what confirms Solaar's description of the envelope. It
            //is written out rather than assembled because the five parameter bytes have never
            //been decoded, so there is nothing to name them after.
        private static readonly byte[] BATTERY_REQUEST =
            { 0x51, 0x08, 0x00, 0x03, 0x1A, 0x00, 0x03, 0x00, 0x04, 0x0A };

        public const byte REPORT_CENTURION = 0x51;

            //Fixed, and not derived from the collection's declared report length: the frame is
            //64 bytes including the report id, which is what Solaar writes and what the 63-byte
            //maximum payload it documents implies.
        public const int FRAME_SIZE = 64;

        private const byte KIND_ACK = 0x03;
        private const byte KIND_POWER = 0x05;
        private const byte KIND_BATTERY = 0x0B;

        private const int OFFSET_KIND = 1;
        private const int OFFSET_BATTERY_TAG = 8;
        private const byte BATTERY_TAG = 0x04;
        private const int OFFSET_BATTERY_LEVEL = 10;
        private const int OFFSET_CHARGING_STATE = 12;
        private const byte CHARGING = 0x02;
        private const int OFFSET_POWER_STATE = 6;

            //Its own traffic arrives on this collection, so the answer is not necessarily the
            //next frame: an ack precedes it and a power event can land beside it.
        private const int MAX_FRAMES_PER_REQUEST = 8;

        private const int LOG_BYTES = 16;

        public static int? Read(HidInterfaceInfo info, int timeoutMs)
        {
            if (info.OutputReportByteLength < FRAME_SIZE
                || info.InputReportByteLength < FRAME_SIZE)
            {
                Log.Write("Centurion", "refusing " + info + ": reports smaller than a "
                    + FRAME_SIZE + "-byte frame");
                return null;
            }

            using (HidDevice hid = HidDevice.Open(info))
            {
                if (hid == null)
                    return null;

                byte[] request = new byte[hid.OutputReportByteLength];
                Array.Copy(BATTERY_REQUEST, request, BATTERY_REQUEST.Length);

                Log.WriteHex("Centurion", "->", request, BATTERY_REQUEST.Length);
                if (!hid.Write(request, timeoutMs))
                    return null;

                    //Bound the whole exchange rather than each read, so a chatty device cannot
                    //hold the UI thread for MAX_FRAMES_PER_REQUEST timeouts.
                Stopwatch budget = Stopwatch.StartNew();

                for (int frame = 0; frame < MAX_FRAMES_PER_REQUEST; frame++)
                {
                    int remaining = timeoutMs - (int)budget.ElapsedMilliseconds;
                    if (remaining <= 0)
                        return null;

                    byte[] reply = new byte[hid.InputReportByteLength];
                    int read;
                    if (!hid.Read(reply, remaining, out read))
                        return null;

                    int? level = Decode(reply, read);
                    if (level.HasValue)
                        return level;
                }
            }

            return null;
        }

        /// <summary>
        /// A value only for the battery frame. Null means "keep reading" for everything else,
        /// including the power event that says the headset is off -- the caller's contract
        /// already reads a null as "cannot read right now", and the poll after it will time
        /// out with nothing, which is the same answer one frame later.
        /// </summary>
        private static int? Decode(byte[] reply, int read)
        {
            bool ours = read > OFFSET_CHARGING_STATE && reply[0] == REPORT_CENTURION;
            Log.WriteHex("Centurion", ours ? "<-" : "<- (ignored)", reply, Math.Min(read, LOG_BYTES));
            if (!ours)
                return null;

            switch (reply[OFFSET_KIND])
            {
                case KIND_BATTERY:
                    if (reply[OFFSET_BATTERY_TAG] != BATTERY_TAG)
                        return null;

                    int level = reply[OFFSET_BATTERY_LEVEL];
                    if (level > 100)
                        return null;

                    Log.Write("Centurion", "battery " + level + "%"
                        + (reply[OFFSET_CHARGING_STATE] == CHARGING ? " (charging)" : ""));
                    return level;

                case KIND_POWER:
                    Log.Write("Centurion", "power event: headset "
                        + (reply[OFFSET_POWER_STATE] == 0x00 ? "off" : "on"));
                    return null;

                case KIND_ACK:
                default:
                    return null;
            }
        }
    }
}
