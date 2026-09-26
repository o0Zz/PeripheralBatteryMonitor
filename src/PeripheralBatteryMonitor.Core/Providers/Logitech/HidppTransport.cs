using System;
using System.Collections.Generic;
using System.Diagnostics;
using PeripheralBatteryMonitor.Diagnostics;
using PeripheralBatteryMonitor.Hid;

namespace PeripheralBatteryMonitor.Providers.Logitech
{
    /// <summary>
    /// One HID++ 2.0 conversation: the framing, the vocabulary, and the two root-feature calls
    /// everything else is built on.
    ///
    /// <c>[reportId][deviceIndex][featureIndex][functionId&lt;&lt;4 | softwareId][params...]</c>, and
    /// a reply echoes the first four bytes, which is what tells it apart from the unsolicited
    /// notifications arriving on the same collection.
    ///
    /// <b>One or two collections, and which it is decides how requests are framed.</b>
    /// <list type="bullet">
    ///   <item>A device on <b>its own dongle</b> publishes a single vendor collection --
    ///         0xFF43/0x0202 -- and answers everything on it as a long 0x11 frame. Verified on
    ///         the PRO X Wireless, whose collection rejects report 0x10 outright.</item>
    ///   <item>A <b>receiver</b> publishes two, 0xFF00/0x0001 for 7-byte short frames and
    ///         0xFF00/0x0002 for 20-byte long ones, and needs both. Root-feature traffic goes
    ///         out short -- Solaar's <c>base.write()</c> picks short for any payload that fits,
    ///         and logibar, which supports the 0xC547 receiver and the G915 X TKL, sends
    ///         <c>[0x10, idx, 0x00, swId, featureId&gt;&gt;8, featureId&amp;0xFF, 0x00]</c> for
    ///         getFeature and long only for the battery call. And the <b>reply can come back on
    ///         either</b>: logibar accepts <c>r[0] in [0x10, 0x11]</c>, and a receiver's "that
    ///         slot is not connected" answer is a HID++ 1.0 error, which is always short. On
    ///         Linux one hidraw node carries both; on Windows they are two device paths.</item>
    /// </list>
    /// Sending everything long on the long handle alone is what made every slot of a real
    /// receiver answer `silent` while the keyboard was in use -- log of 2026-09-26, three
    /// sweeps, six silent slots each. So both handles are read together via
    /// <see cref="HidDevice.ReadAny"/>, and a short reply is republished as a long frame before
    /// it leaves, so nothing above here learns which collection answered.
    ///
    /// <b>The receiver half is unverified against hardware</b> -- nobody here has one. The
    /// direct-dongle half is verified and unchanged by it.
    /// </summary>
    internal class HidppTransport : IDisposable
    {
            //20 bytes total (1 id + 19), and 7 (1 id + 6) for the short one. Writing a short
            //report to a collection that does not declare it fails outright, which is why the
            //choice below is never a size calculation.
        public const byte REPORT_LONG = 0x11;
        public const int LONG_FRAME_SIZE = 20;
        public const byte REPORT_SHORT = 0x10;
        public const int SHORT_FRAME_SIZE = 7;

            //The device behind its own receiver, as opposed to 1..6 on a multi-device one.
        public const byte DEVICE_INDEX_DIRECT = 0xFF;

        public const byte FEATURE_ROOT = 0x00;

            //Nonzero is load-bearing, not conventional: a device's own unsolicited
            //notifications carry software id 0, so a zero here would make them
            //indistinguishable from replies to us.
        public const byte SOFTWARE_ID = 0x0E;

            //A feature index of 0xFF in a reply marks a HID++ 2.0 error, 0x8F the 1.0 one.
            //Both mean "no value", never "index 255".
        private const byte ERROR_HIDPP20 = 0xFF;
        private const byte ERROR_HIDPP10 = 0x8F;

            //Sent in the root ping's third parameter. A device that follows the specification
            //echoes it back; not every one does -- see Ping.
        private const byte PING_MAGIC = 0xAA;

            //HID++ 1.0: sub id 0x81 reads a short register, and register 0x02 is the connection
            //state, whose first byte is the receiver's connected-device bitmap.
        private const byte SUB_ID_GET_REGISTER = 0x81;
        private const byte REGISTER_CONNECTION_STATE = 0x02;

            //A receiver publishes its short reports at usage 0x0001 on the same vendor page as
            //its long ones. The product id cannot pair them -- one receiver is that id twice
            //over -- so the interface path's group key does.
        private const ushort RECEIVER_USAGE_SHORT = 0x0001;

        private const int MAX_FRAMES_PER_REQUEST = 8;

        private const int LOG_BYTES = 20;

        private HidDevice longDevice;
        private HidDevice shortDevice;      //null for a device on its own dongle

        private HidppTransport(HidDevice longDevice, HidDevice shortDevice)
        {
            this.longDevice = longDevice;
            this.shortDevice = shortDevice;
        }

        /// <summary>
        /// The short collection beside <paramref name="longInfo"/>, or null. Wants the whole
        /// enumeration because that is where the sibling is visible, and the caller already
        /// has it -- looking again later costs a second setupapi walk per poll tick.
        /// </summary>
        public static HidInterfaceInfo FindShortCollection(HidInterfaceInfo longInfo, IList<HidInterfaceInfo> present)
        {
            if (longInfo == null || present == null)
                return null;

            string group = HidInterfaceInfo.CollectionGroupKey(longInfo.Path);
            if (group == null)
                return null;

            foreach (HidInterfaceInfo candidate in present)
            {
                if (candidate == longInfo || candidate.UsagePage != longInfo.UsagePage)
                    continue;
                if (candidate.Usage != RECEIVER_USAGE_SHORT)
                    continue;
                if (candidate.OutputReportByteLength < SHORT_FRAME_SIZE
                    || candidate.InputReportByteLength < SHORT_FRAME_SIZE)
                    continue;
                if (!String.Equals(group, HidInterfaceInfo.CollectionGroupKey(candidate.Path), StringComparison.OrdinalIgnoreCase))
                    continue;

                return candidate;
            }

            return null;
        }

        /// <summary>
        /// <paramref name="shortInfo"/> is null for a device on its own dongle, and a receiver
        /// whose short collection will not open degrades to the same thing -- which is the
        /// behaviour every verified device was tested against. Null only when the long
        /// collection itself is unusable.
        /// </summary>
        public static HidppTransport Open(HidInterfaceInfo longInfo, HidInterfaceInfo shortInfo)
        {
            if (longInfo == null)
                return null;

            if (longInfo.OutputReportByteLength < LONG_FRAME_SIZE || longInfo.InputReportByteLength < LONG_FRAME_SIZE)
            {
                Log.Write("HID++", "refusing " + longInfo + ": reports smaller than a "
                    + LONG_FRAME_SIZE + "-byte long frame");
                return null;
            }

            HidDevice longHid = HidDevice.Open(longInfo);
            if (longHid == null)
                return null;

            HidDevice shortHid = shortInfo == null ? null : HidDevice.Open(shortInfo);
            if (shortInfo != null && shortHid == null)
            {
                    //Every time, not once: this is the difference between a sweep that can hear
                    //an answer and one that cannot, and the usual cause -- vendor software
                    //holding the collection -- comes and goes.
                Log.Write("HID++", "short collection would not open beside "
                    + HidInterfaceInfo.ShortPath(longInfo.Path) + " -- replies arriving there will be missed");
            }

            return new HidppTransport(longHid, shortHid);
        }

        /// <summary>
        /// The reply, normalised to the long-report layout -- byte 4 onwards is the payload --
        /// or null on write failure, timeout or a device-reported error.
        /// </summary>
        public byte[] Request(byte deviceIndex, byte featureIndex, byte functionId, byte[] parameters, int timeoutMs)
        {
                //Short for the root feature, long above it. Not a size calculation: logibar
                //asks the root over 0x10 and reads the battery over 0x11 with the same three
                //spare parameter bytes, so the frame is chosen by who is being addressed.
            bool useShort = featureIndex == FEATURE_ROOT && shortDevice != null;
            HidDevice writer = useShort ? shortDevice : longDevice;

            byte[] request = new byte[writer.OutputReportByteLength];
            request[0] = useShort ? REPORT_SHORT : REPORT_LONG;
            request[1] = deviceIndex;
            request[2] = featureIndex;
            request[3] = (byte)((functionId << 4) | SOFTWARE_ID);
            if (parameters != null)
            {
                for (int i = 0; i < parameters.Length && (4 + i) < request.Length; i++)
                    request[4 + i] = parameters[i];
            }

            return Exchange(writer, request, deviceIndex, featureIndex, timeoutMs);
        }

        /// <summary>
        /// HID++ 1.0 register 0x02: which slots the receiver currently has a live link to, as a
        /// bitmap where bit <c>n-1</c> is slot <c>n</c>, or -1 when it does not answer.
        ///
        /// Asked for the log and nothing else. Six silent pings cannot tell "no device paired
        /// here" from "we asked the wrong way"; one short transaction can, and it is the first
        /// line worth reading in the next report.
        /// </summary>
        public int ReadConnectedSlots(int timeoutMs)
        {
            if (shortDevice == null)
                return -1;

            byte[] request = new byte[shortDevice.OutputReportByteLength];
            request[0] = REPORT_SHORT;
            request[1] = DEVICE_INDEX_DIRECT;       //the receiver itself, not a peripheral
            request[2] = SUB_ID_GET_REGISTER;
            request[3] = REGISTER_CONNECTION_STATE;

                //A 1.0 register reply echoes bytes 2 and 3 exactly as a 2.0 feature reply does,
                //so one exchange serves both -- the sub id stands in for the feature index and
                //the register for the function byte. Byte 4 is then the bitmap.
            byte[] reply = Exchange(shortDevice, request, DEVICE_INDEX_DIRECT, SUB_ID_GET_REGISTER, timeoutMs);

            return reply == null ? -1 : reply[4];
        }

        /// <summary>
        /// Write, then read until the reply echoing this request turns up. Both handles are
        /// listened to, because a receiver may answer on either.
        ///
        /// <paramref name="echoByte2"/> is what byte 2 must come back as: the feature index for
        /// a 2.0 call, the sub id for a 1.0 register read.
        /// </summary>
        private byte[] Exchange(HidDevice writer, byte[] request, byte deviceIndex, byte echoByte2, int timeoutMs)
        {
                //Both directions, always: once the app is on someone else's machine this is the
                //only record of the conversation that exists.
            Log.WriteHex("HID++", "->", request, Math.Min(request.Length, LOG_BYTES));

            if (!writer.Write(request, timeoutMs))
                return null;

            HidDevice[] listeners = shortDevice == null
                ? new HidDevice[] { longDevice }
                : new HidDevice[] { shortDevice, longDevice };

            byte[][] buffers = new byte[listeners.Length][];
            for (int i = 0; i < listeners.Length; i++)
                buffers[i] = new byte[listeners[i].InputReportByteLength];

                //Bound the whole exchange, not each read: a receiver forwards its peripherals'
                //notifications onto these same collections, so a busy one could otherwise hold
                //the UI thread for MAX_FRAMES_PER_REQUEST timeouts.
            Stopwatch budget = Stopwatch.StartNew();

            for (int frame = 0; frame < MAX_FRAMES_PER_REQUEST; frame++)
            {
                int remaining = timeoutMs - (int)budget.ElapsedMilliseconds;
                if (remaining <= 0)
                    return null;

                int read;
                int which = HidDevice.ReadAny(listeners, buffers, remaining, out read);
                if (which < 0)
                    return null;

                byte[] reply = buffers[which];
                Log.WriteHex("HID++", "<-", reply, Math.Min(read, LOG_BYTES));

                if (read < 5)
                    continue;
                if (reply[1] != deviceIndex)
                    continue;   //a notification about another device on this receiver

                if (reply[2] == ERROR_HIDPP20 || reply[2] == ERROR_HIDPP10)
                {
                        //The receiver's own "that slot is not connected" lands here, and it is
                        //the answer a long-only transport could never hear.
                    if (IsErrorFor(reply, read, echoByte2))
                        return null;
                    continue;   //an error about something else; keep waiting
                }

                if (reply[2] == echoByte2 && reply[3] == request[3])
                    return Normalise(reply, read, deviceIndex);

                    //Anything else is unsolicited -- a receiver's 0x41 connect/wake/sleep report
                    //carries our device index but a protocol byte where the feature index is.
            }

            return null;
        }

        /// <summary>
        /// A reply that arrived short is republished as a long frame, so nothing above this has
        /// to know which collection it came from. Copied rather than handed over, because a
        /// short frame is three parameter bytes in a 20-byte world and a decoder indexing past
        /// them must read zeros, not whatever the buffer held before.
        /// </summary>
        private static byte[] Normalise(byte[] reply, int read, byte deviceIndex)
        {
            if (reply[0] == REPORT_LONG && read >= LONG_FRAME_SIZE)
                return reply;

            byte[] normalised = new byte[LONG_FRAME_SIZE];
            normalised[0] = REPORT_LONG;
            normalised[1] = deviceIndex;
            int copy = Math.Min(read, normalised.Length) - 2;
            if (copy > 0)
                Array.Copy(reply, 2, normalised, 2, copy);
            return normalised;
        }

        /// <summary>
        /// Error frame layout: <c>[id][devIdx][0xFF][echoed byte 2][echoed byte 3][code]</c>.
        /// </summary>
        private static bool IsErrorFor(byte[] reply, int read, byte echoByte2)
        {
            if (read <= 5 || reply[3] != echoByte2)
                return false;   //an error about something else; not ours

            Log.Write("HID++", "error on 0x" + echoByte2.ToString("X2")
                + ": code 0x" + reply[5].ToString("X2"));
            return true;
        }

        /// <summary>
        /// Root feature ping, so a switched-off device costs one timeout rather than one per
        /// feature probed.
        ///
        /// <b>Answering at all is the test; the echoed magic byte is not.</b> A reply only gets
        /// here after <see cref="Exchange"/> matched it against this exact request -- device
        /// index, feature index, and the function byte carrying our nonzero software id -- so
        /// it is an answer to this ping by construction, which is the job the echo was doing a
        /// second time. Insisting on it cost a real device: the PRO X 2 LIGHTSPEED answers with
        /// parameters <c>01 10 00</c> and no echo anywhere, so every poll declared a headset
        /// that had just replied to be switched off.
        /// </summary>
        public bool Ping(byte deviceIndex, int timeoutMs)
        {
            byte[] reply = Request(deviceIndex, FEATURE_ROOT, 0x01,
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
        /// Returns 0 when the device does not implement the feature: index 0 is always the root
        /// feature itself, so it is never a valid answer here.
        /// </summary>
        public byte GetFeatureIndex(byte deviceIndex, ushort featureId, int timeoutMs)
        {
            byte[] reply = Request(deviceIndex, FEATURE_ROOT, 0x00,
                new byte[] { (byte)(featureId >> 8), (byte)(featureId & 0xFF), 0x00 }, timeoutMs);

            if (reply == null || reply.Length < 5)
                return 0;
            return reply[4];
        }

        public void Dispose()
        {
            if (longDevice != null)
            {
                longDevice.Dispose();
                longDevice = null;
            }
            if (shortDevice != null)
            {
                shortDevice.Dispose();
                shortDevice = null;
            }
        }
    }
}
