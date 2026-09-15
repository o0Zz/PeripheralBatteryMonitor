using PeripheralBatteryMonitor.Diagnostics;

namespace PeripheralBatteryMonitor.Providers.Logitech
{
    /// <summary>
    /// The HID++ 2.0 vocabulary, and the two root-feature calls every transport needs. Apart
    /// from the transports because more than one framing carries HID++, and the root feature
    /// is at index 0 by definition in all of them.
    /// </summary>
    internal static class Hidpp
    {
            //20 bytes total (1 id + 19). The 7-byte short report (0x10) is a Unifying-era
            //thing and writing it to a collection that does not declare it fails outright.
        public const byte REPORT_LONG = 0x11;
        public const int LONG_FRAME_SIZE = 20;

            //The device behind its own receiver, as opposed to 1..6 on a multi-device one.
        public const byte DEVICE_INDEX_DIRECT = 0xFF;

        public const byte FEATURE_ROOT = 0x00;

            //Nonzero is load-bearing, not conventional: a device's own unsolicited
            //notifications carry software id 0, so a zero here would make them
            //indistinguishable from replies to us.
        public const byte SOFTWARE_ID = 0x0E;

            //A feature index of 0xFF in a reply marks a HID++ 2.0 error, 0x8F the 1.0 one.
            //Both mean "no value", never "index 255".
        public const byte ERROR_HIDPP20 = 0xFF;
        public const byte ERROR_HIDPP10 = 0x8F;

            //Sent in the root ping's third parameter. A device that follows the specification
            //echoes it back; not every one does -- see Ping.
        public const byte PING_MAGIC = 0xAA;

        /// <summary>
        /// Root feature ping, so a switched-off device costs one timeout rather than one per
        /// feature probed.
        ///
        /// <b>Answering at all is the test; the echoed magic byte is not.</b> A reply only
        /// reaches here after the transport matched it against this exact request -- device
        /// index, feature index, and the function byte carrying our nonzero software id -- so
        /// it is an answer to this ping by construction, which is the job the echo was doing a
        /// second time. Insisting on it cost a real device: the PRO X 2 LIGHTSPEED answers with
        /// parameters <c>01 10 00</c> and no echo anywhere, so every poll declared a headset
        /// that had just replied to be switched off.
        /// </summary>
        public static bool Ping(IHidppTransport transport, byte deviceIndex, int timeoutMs)
        {
            byte[] reply = transport.Request(deviceIndex, FEATURE_ROOT, 0x01,
                new byte[] { 0x00, 0x00, PING_MAGIC }, timeoutMs);

            if (reply == null || reply.Length <= 6)
                return false;

            if (reply[6] != PING_MAGIC)
            {
                    //A pong's parameters are [protocolMajor][protocolMinor][echo], so a device
                    //putting something else there says something about its framing that nobody
                    //has decoded yet.
                Log.WriteHex("HID++", "ping at index 0x" + deviceIndex.ToString("X2")
                    + " answered without echoing the magic byte, accepted on the header echo:", reply, 7);
            }

            return true;
        }

        /// <summary>
        /// Returns 0 when the device does not implement the feature: index 0 is always the
        /// root feature itself, so it is never a valid answer here.
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
        /// Error frame layout, the same in both framings once normalised:
        /// <c>[id][devIdx][0xFF][echoed featureIndex][echoed funcSw][code]</c>.
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
