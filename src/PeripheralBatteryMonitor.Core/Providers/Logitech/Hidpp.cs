using PeripheralBatteryMonitor.Diagnostics;

namespace PeripheralBatteryMonitor.Providers.Logitech
{
    /// <summary>
    /// The HID++ 2.0 vocabulary, and the two root-feature calls every transport needs.
    ///
    /// It lives apart from <see cref="HidppTransport"/> because there is now more than one
    /// framing that carries HID++ -- see <see cref="CenturionTransport"/> -- and neither
    /// should have to name the other to say "device index 0xFF" or "resolve this feature".
    /// The root feature is at index 0 by definition on every device and in every framing,
    /// so <see cref="Ping"/> and <see cref="GetFeatureIndex"/> are written once here and
    /// each transport delegates to them.
    /// </summary>
    internal static class Hidpp
    {
            //Long report: 20 bytes total (1 id + 19). Modern Logitech gaming gear exposes only
            //this one; the 7-byte short report (0x10) is a Unifying-era thing and writing it to
            //a collection that doesn't declare it fails outright.
        public const byte REPORT_LONG = 0x11;
        public const int LONG_FRAME_SIZE = 20;

            //Addresses the device sitting behind its own receiver, as opposed to indexes
            //1..6 which address devices paired to a multi-device Unifying receiver.
        public const byte DEVICE_INDEX_DIRECT = 0xFF;

        public const byte FEATURE_ROOT = 0x00;

            //Any nonzero value; it is echoed back and distinguishes our traffic from
            //another application's (G HUB may be talking to the same device). Nonzero is
            //load-bearing rather than conventional: a device's own unsolicited notifications
            //carry software id 0, so a zero here would make them indistinguishable from
            //replies to us.
        public const byte SOFTWARE_ID = 0x0E;

            //A feature index of 0xFF in a reply marks a HID++ 2.0 error; 0x8F is the
            //HID++ 1.0 equivalent. Both mean "no value", never "index 255".
        public const byte ERROR_HIDPP20 = 0xFF;
        public const byte ERROR_HIDPP10 = 0x8F;

            //Arbitrary byte echoed back by the root feature's ping, which is what makes a
            //pong tell-apart-able from any other reply.
        public const byte PING_MAGIC = 0xAA;

        /// <summary>
        /// Root feature ping. Cheap way to find out whether anything is actually listening
        /// before spending a timeout per feature probing what it supports -- a device that is
        /// switched off answers nothing at all.
        /// </summary>
        public static bool Ping(IHidppTransport transport, byte deviceIndex, int timeoutMs)
        {
            byte[] reply = transport.Request(deviceIndex, FEATURE_ROOT, 0x01,
                new byte[] { 0x00, 0x00, PING_MAGIC }, timeoutMs);
            return reply != null && reply.Length > 6 && reply[6] == PING_MAGIC;
        }

        /// <summary>
        /// Resolve a feature id to this device's feature index via the root feature.
        /// Returns 0 when the device does not implement it (index 0 is always the root
        /// feature itself, so it is never a valid answer here).
        /// </summary>
        public static byte GetFeatureIndex(IHidppTransport transport, byte deviceIndex, ushort featureId, int timeoutMs)
        {
            byte[] reply = transport.Request(deviceIndex, FEATURE_ROOT, 0x00,
                new byte[] { (byte)(featureId >> 8), (byte)(featureId & 0xFF), 0x00 }, timeoutMs);

            if (reply == null || reply.Length < 5)
                return 0;
            return reply[4];
        }

        /// <summary>
        /// Is this reply an error frame for the feature we asked about? Layout is
        /// <c>[id][devIdx][0xFF][echoed featureIndex][echoed funcSw][code]</c>, and it is the
        /// same layout in both framings once a reply has been normalised.
        /// </summary>
        public static bool IsErrorFor(byte[] reply, int read, byte featureIndex, byte functionId, string category)
        {
            if (reply[2] != ERROR_HIDPP20 && reply[2] != ERROR_HIDPP10)
                return false;

            if (read <= 5 || reply[3] != featureIndex)
                return false;   //an error about some other feature; not ours

            Log.Write(category, "error on feature 0x" + featureIndex.ToString("X2")
                + " func 0x" + functionId.ToString("X2") + ": code 0x" + reply[5].ToString("X2"));
            return true;
        }
    }
}
