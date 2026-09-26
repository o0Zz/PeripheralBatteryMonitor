using System;
using System.Collections.Generic;
using System.Diagnostics;
using PeripheralBatteryMonitor.Diagnostics;
using PeripheralBatteryMonitor.Hid;

namespace PeripheralBatteryMonitor.Providers.Logitech
{
    /// <summary>
    /// HID++ through a receiver, which needs **both** of the receiver's vendor collections --
    /// the 7-byte short one at usage 0x0001 and the 20-byte long one at usage 0x0002.
    ///
    /// <b>This is why receiver-attached devices read as switched off.</b> The app used to send
    /// everything as a long 0x11 frame on the long collection alone, and every slot of a
    /// G915 X TKL's 0xC547 receiver answered `silent` while the keyboard was in use. Two
    /// separate things break there:
    /// <list type="bullet">
    ///   <item>the root-feature traffic -- ping and getFeature -- goes out as a **short 0x10**
    ///         frame. Solaar's <c>base.write()</c> picks short for any payload that fits, and
    ///         logibar, which supports this exact receiver, sends <c>[0x10, idx, 0x00, swId,
    ///         featureId&gt;&gt;8, featureId&amp;0xFF, 0x00]</c>;</item>
    ///   <item>the **reply may come back on either collection**. logibar accepts a reply with
    ///         <c>r[0] in [0x10, 0x11]</c>, and a receiver's "that device is not connected"
    ///         answer is a HID++ 1.0 error, which is always a short frame. On Linux one hidraw
    ///         node carries both; on Windows they are two device paths, so a transport holding
    ///         only the long handle cannot see any of it -- and silence is exactly what a
    ///         missing device looks like.</item>
    /// </list>
    ///
    /// So both handles are opened and both are waited on together, via
    /// <see cref="HidDevice.ReadAny"/>. Requests are framed by feature index: the root feature
    /// short, everything above it long, which is what both references do.
    ///
    /// Replies are normalised to the long layout before they leave, per
    /// <see cref="IHidppTransport"/>, so the feature layer and the battery decoders never
    /// learn a short frame was involved.
    ///
    /// <b>Unverified against hardware</b> -- nobody here has a receiver. What is verified is
    /// the failure it replaces: log (12) of 2026-09-26, three sweeps, six silent slots each.
    /// </summary>
    internal class ReceiverTransport : IHidppTransport
    {
        private const int MAX_FRAMES_PER_REQUEST = 8;

        private const int LOG_BYTES = 20;

            //A receiver publishes its short reports at usage 0x0001 on the same vendor page as
            //the long ones. Both are collections of one USB interface, so the interface path's
            //group key is what pairs them -- the product id cannot, since one receiver is one
            //product id twice over.
        private const ushort RECEIVER_USAGE_SHORT = 0x0001;

        private HidDevice shortDevice;
        private HidDevice longDevice;

        private ReceiverTransport(HidDevice shortDevice, HidDevice longDevice)
        {
            this.shortDevice = shortDevice;
            this.longDevice = longDevice;
        }

        /// <summary>
        /// The short collection of the same interface as <paramref name="longInfo"/>, or null.
        /// Wants the whole enumeration because that is where the sibling is visible; the
        /// caller already has it.
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
                if (candidate.OutputReportByteLength < Hidpp.SHORT_FRAME_SIZE
                    || candidate.InputReportByteLength < Hidpp.SHORT_FRAME_SIZE)
                    continue;
                if (!String.Equals(group, HidInterfaceInfo.CollectionGroupKey(candidate.Path), StringComparison.OrdinalIgnoreCase))
                    continue;

                return candidate;
            }

            return null;
        }

        /// <summary>
        /// Null only when the long collection will not open. <b>A missing short collection is
        /// not fatal</b> -- the transport then behaves exactly as the long-only one did, which
        /// is the behaviour every currently-working device was verified against.
        /// </summary>
        public static ReceiverTransport Open(HidInterfaceInfo longInfo, HidInterfaceInfo shortInfo)
        {
            if (longInfo == null)
                return null;

            if (longInfo.OutputReportByteLength < Hidpp.LONG_FRAME_SIZE
                || longInfo.InputReportByteLength < Hidpp.LONG_FRAME_SIZE)
            {
                Log.Write("Receiver", "refusing " + longInfo + ": reports smaller than a "
                    + Hidpp.LONG_FRAME_SIZE + "-byte long frame");
                return null;
            }

            HidDevice longHid = HidDevice.Open(longInfo);
            if (longHid == null)
                return null;

            HidDevice shortHid = shortInfo == null ? null : HidDevice.Open(shortInfo);
            if (shortHid == null)
            {
                    //Worth a line every time rather than once: this is the difference between
                    //a sweep that can hear an answer and one that cannot, and the reason is
                    //usually vendor software holding the collection, which comes and goes.
                Log.Write("Receiver", shortInfo == null
                    ? "no short collection found beside " + HidInterfaceInfo.ShortPath(longInfo.Path)
                      + " -- root replies arriving there will be missed"
                    : "short collection would not open -- root replies arriving there will be missed");
            }

            return new ReceiverTransport(shortHid, longHid);
        }

        public byte[] Request(byte deviceIndex, byte featureIndex, byte functionId, byte[] parameters, int timeoutMs)
        {
                //Short for the root feature, long above it. Not a size calculation: logibar
                //asks the root over 0x10 and reads the battery over 0x11 with the same three
                //spare parameter bytes, so the frame is chosen by who is being addressed.
            bool useShort = featureIndex == Hidpp.FEATURE_ROOT && shortDevice != null;
            HidDevice writer = useShort ? shortDevice : longDevice;

            byte[] request = new byte[writer.OutputReportByteLength];
            request[0] = useShort ? Hidpp.REPORT_SHORT : Hidpp.REPORT_LONG;
            request[1] = deviceIndex;
            request[2] = featureIndex;
            request[3] = (byte)((functionId << 4) | Hidpp.SOFTWARE_ID);
            if (parameters != null)
            {
                for (int i = 0; i < parameters.Length && (4 + i) < request.Length; i++)
                    request[4 + i] = parameters[i];
            }

            Log.WriteHex("Receiver", "->", request, Math.Min(request.Length, LOG_BYTES));

            if (!writer.Write(request, timeoutMs))
                return null;

            HidDevice[] listeners = shortDevice == null
                ? new HidDevice[] { longDevice }
                : new HidDevice[] { shortDevice, longDevice };

            byte[][] buffers = new byte[listeners.Length][];
            for (int i = 0; i < listeners.Length; i++)
                buffers[i] = new byte[listeners[i].InputReportByteLength];

                //Bound the whole exchange, not each read: a receiver forwards its peripherals'
                //notifications onto the same collections, so a busy one could otherwise hold
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
                Log.WriteHex("Receiver", "<-", reply, Math.Min(read, LOG_BYTES));

                if (read < 5)
                    continue;
                if (reply[1] != deviceIndex)
                    continue;   //a notification about another device on this receiver

                if (reply[2] == Hidpp.ERROR_HIDPP20 || reply[2] == Hidpp.ERROR_HIDPP10)
                {
                        //The receiver's own "that slot is not connected" lands here, and it is
                        //the answer the long-only transport could never hear.
                    if (Hidpp.IsErrorFor(reply, read, featureIndex, functionId, "Receiver"))
                        return null;
                    continue;
                }

                if (reply[2] == featureIndex && reply[3] == request[3])
                    return Normalise(reply, read, deviceIndex);

                    //Anything else is unsolicited -- a receiver's 0x41 connect/wake/sleep report
                    //carries our device index but a protocol byte where the feature index is.
            }

            return null;
        }

        /// <summary>
        /// A reply that arrived short is republished as a long frame, so nothing above this
        /// has to know which collection it came from. The payload is copied rather than the
        /// buffer reused, because a short frame is three parameter bytes in a 20-byte world
        /// and a decoder indexing past them must read zeros, not the previous reply.
        /// </summary>
        private static byte[] Normalise(byte[] reply, int read, byte deviceIndex)
        {
            if (reply[0] == Hidpp.REPORT_LONG && read >= Hidpp.LONG_FRAME_SIZE)
                return reply;

            byte[] normalised = new byte[Hidpp.LONG_FRAME_SIZE];
            normalised[0] = Hidpp.REPORT_LONG;
            normalised[1] = deviceIndex;
            int copy = Math.Min(read, normalised.Length) - 2;
            if (copy > 0)
                Array.Copy(reply, 2, normalised, 2, copy);
            return normalised;
        }

        public bool Ping(byte deviceIndex, int timeoutMs)
        {
            return Hidpp.Ping(this, deviceIndex, timeoutMs);
        }

        public byte GetFeatureIndex(byte deviceIndex, ushort featureId, int timeoutMs)
        {
            return Hidpp.GetFeatureIndex(this, deviceIndex, featureId, timeoutMs);
        }

        /// <summary>
        /// HID++ 1.0 register 0x02: which slots the receiver currently has a live link to, as a
        /// bitmap where bit <c>n-1</c> is slot <c>n</c>. Returns -1 when the receiver does not
        /// answer -- it is asked for the log, never to decide anything.
        ///
        /// It exists because a silent slot has two causes that look identical from six pings:
        /// "no device paired here" and "we asked the wrong way". This is one short transaction
        /// that says which, and it is the first line worth reading in the next report.
        /// </summary>
        public int ReadConnectedSlots(int timeoutMs)
        {
            if (shortDevice == null)
                return -1;

            byte[] request = new byte[shortDevice.OutputReportByteLength];
            request[0] = Hidpp.REPORT_SHORT;
            request[1] = Hidpp.DEVICE_INDEX_DIRECT;     //the receiver itself, not a peripheral
            request[2] = SUB_ID_GET_REGISTER;
            request[3] = REGISTER_CONNECTION_STATE;

            Log.WriteHex("Receiver", "->", request, Math.Min(request.Length, LOG_BYTES));
            if (!shortDevice.Write(request, timeoutMs))
                return -1;

            HidDevice[] listeners = new HidDevice[] { shortDevice, longDevice };
            byte[][] buffers = new byte[][]
            {
                new byte[shortDevice.InputReportByteLength],
                new byte[longDevice.InputReportByteLength],
            };

            Stopwatch budget = Stopwatch.StartNew();

            for (int frame = 0; frame < MAX_FRAMES_PER_REQUEST; frame++)
            {
                int remaining = timeoutMs - (int)budget.ElapsedMilliseconds;
                if (remaining <= 0)
                    return -1;

                int read;
                int which = HidDevice.ReadAny(listeners, buffers, remaining, out read);
                if (which < 0)
                    return -1;

                byte[] reply = buffers[which];
                Log.WriteHex("Receiver", "<-", reply, Math.Min(read, LOG_BYTES));

                if (read < 5 || reply[1] != Hidpp.DEVICE_INDEX_DIRECT)
                    continue;
                if (reply[2] != SUB_ID_GET_REGISTER || reply[3] != REGISTER_CONNECTION_STATE)
                    continue;

                return reply[4];
            }

            return -1;
        }

            //HID++ 1.0: sub id 0x81 is "read short register", and register 0x02 is the
            //connection state, whose first byte is the connected-device bitmap.
        private const byte SUB_ID_GET_REGISTER = 0x81;
        private const byte REGISTER_CONNECTION_STATE = 0x02;

        public void Dispose()
        {
            if (shortDevice != null)
            {
                shortDevice.Dispose();
                shortDevice = null;
            }
            if (longDevice != null)
            {
                longDevice.Dispose();
                longDevice = null;
            }
        }
    }
}
