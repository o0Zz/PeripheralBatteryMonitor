using System;
using System.Diagnostics;
using PeripheralBatteryMonitor.Diagnostics;
using PeripheralBatteryMonitor.Hid;

namespace PeripheralBatteryMonitor.Providers.Logitech
{
    /// <summary>
    /// Centurion: the framing Logitech's newer headsets use to carry HID++ 2.0, in place of
    /// the long-report framing in <see cref="HidppTransport"/>. The PRO X 2 LIGHTSPEED
    /// (VID 0x046D / PID 0x0AF7) is the reason this exists -- it answers on its own vendor
    /// collection at usage page 0xFFA0 / usage 0x0001, in 64-byte frames with report id 0x51,
    /// and does not speak the 0x11 framing at all.
    ///
    /// <code>
    /// out: [0x51][cplLength][flags][featureIndex][functionId&lt;&lt;4|swId][params...]  padded to 64
    /// in : same shape;  layer3 = frame[3 .. 2 + cplLength)
    /// cplLength = 1 (the flags byte) + the length of everything from featureIndex onwards
    /// </code>
    ///
    /// <b>The feature layer above this is completely unchanged.</b> Everything from
    /// featureIndex onwards is byte-for-byte the tail of a HID++ long report, which is why
    /// <see cref="Request"/> hands back a normalised <c>[0x11][0xFF]...</c> frame and the
    /// battery decoders never learn which transport produced them. Solaar does the same
    /// thing -- it rewrites a received Centurion frame as a standard long message and then
    /// runs its ordinary HID++ 2.0 machinery over it -- and its own address probe payload,
    /// <c>00 10 00 00 00</c>, is a plain root-feature ping, which is what confirms the layout.
    ///
    /// The <c>0x50</c> "addressed" variant (the G522 headset) inserts a device address byte at
    /// [1] and shifts everything after it by one. It is deliberately not implemented: the
    /// address has to be brute-forced across 0x00-0xFF, which costs up to 1.3 s on the UI
    /// thread, and there is no such headset to test against. The <c>deviceIndex</c> argument
    /// is where that address would go if it ever is.
    ///
    /// <b>Unverified against hardware.</b> The framing comes from Solaar's implementation and
    /// the product id from Solaar and HeadsetControl; nobody working on this has a PRO X 2.
    /// Every transaction is hex-logged for that reason -- see <see cref="LogitechProbe"/>.
    /// </summary>
    internal class CenturionTransport : IHidppTransport
    {
            //Report id of the unaddressed variant. 0x50 is the addressed one; see the remarks.
        public const byte REPORT_CENTURION = 0x51;

            //Fixed, and not derived from the collection's declared report length: the frame is
            //64 bytes including the report id, which is what Solaar writes and what the 63-byte
            //maximum payload it documents implies.
        public const int FRAME_SIZE = 64;

            //Where layer 3 -- the HID++ tail, starting at the feature index -- begins in a
            //frame. [0] report id, [1] cplLength, [2] flags.
        private const int LAYER3_OFFSET = 3;

            //Fragmentation: (fragmentIndex << 1) | moreFragments. Everything here is a single
            //frame, so anything else in this byte means the reply is not what we assume.
        private const byte FLAGS_SINGLE_FRAME = 0x00;

        private const int MAX_FRAMES_PER_REQUEST = 8;

            //How much of a frame to hex-log. The rest of a 64-byte frame is padding, and the
            //payload every decoder here reads ends well inside this. Wider than strictly
            //needed on purpose: this protocol is unverified, so the log has to carry a few
            //bytes past where we believe the answer stops.
        private const int LOG_BYTES = 24;

        private HidDevice device;

        private CenturionTransport(HidDevice device)
        {
            this.device = device;
        }

        /// <summary>
        /// Open a Centurion conversation on the given interface. Null when the interface
        /// can't be opened or is too small to carry a frame.
        ///
        /// <see cref="HidDevice.Open"/>, i.e. overlapped and read/write, for the same reason
        /// HID++ needs it: the answer arrives as an input report the device sends, so this has
        /// to be able to wait for one -- with a timeout, on the UI thread.
        /// </summary>
        public static CenturionTransport Open(HidInterfaceInfo info)
        {
            if (info == null)
                return null;

                //Frame size is a property of the framing, not of the handle, so this states
                //its own minimum rather than sharing HidppTransport's: 64 here, 20 there.
            if (info.OutputReportByteLength < FRAME_SIZE || info.InputReportByteLength < FRAME_SIZE)
            {
                Log.Write("Centurion", "refusing " + info + ": reports smaller than a " + FRAME_SIZE + "-byte frame");
                return null;
            }

            HidDevice hid = HidDevice.Open(info);
            if (hid == null)
                return null;

            return new CenturionTransport(hid);
        }

        /// <summary>
        /// Run one transaction and hand back the reply in HID++ long-report shape. The
        /// <paramref name="deviceIndex"/> is accepted for the interface's sake and reported
        /// back in the normalised frame; Centurion has no device-index byte to put it in.
        /// </summary>
        public byte[] Request(byte deviceIndex, byte featureIndex, byte functionId, byte[] parameters, int timeoutMs)
        {
            int parameterCount = parameters == null ? 0 : parameters.Length;
            byte functionByte = (byte)((functionId << 4) | Hidpp.SOFTWARE_ID);

            byte[] request = new byte[device.OutputReportByteLength];
            request[0] = REPORT_CENTURION;
                //cplLength counts the flags byte plus all of layer 3, which is the feature
                //index, the function byte and the parameters.
            request[1] = (byte)(1 + 2 + parameterCount);
            request[2] = FLAGS_SINGLE_FRAME;
            request[LAYER3_OFFSET] = featureIndex;
            request[LAYER3_OFFSET + 1] = functionByte;
            for (int i = 0; i < parameterCount && (LAYER3_OFFSET + 2 + i) < request.Length; i++)
                request[LAYER3_OFFSET + 2 + i] = parameters[i];

            Log.WriteHex("Centurion", "->", request, LAYER3_OFFSET + 2 + parameterCount);

            if (!device.Write(request, timeoutMs))
                return null;

                //Bound the whole exchange, not just each individual read: this collection also
                //carries unsolicited power events, so a chatty device could otherwise keep us
                //here for MAX_FRAMES_PER_REQUEST * timeoutMs.
            Stopwatch budget = Stopwatch.StartNew();

            for (int frame = 0; frame < MAX_FRAMES_PER_REQUEST; frame++)
            {
                int remaining = timeoutMs - (int)budget.ElapsedMilliseconds;
                if (remaining <= 0)
                    return null;

                byte[] reply = new byte[device.InputReportByteLength];
                int read;
                if (!device.Read(reply, remaining, out read))
                    return null;

                    //This collection also carries the headset's unsolicited power events,
                    //so label what was kept and what was dropped: otherwise a reader cannot
                    //tell the answer from the traffic around it.
                bool ours = read >= LAYER3_OFFSET + 2 && reply[0] == REPORT_CENTURION;
                Log.WriteHex("Centurion", ours ? "<-" : "<- (ignored)", reply, Math.Min(read, LOG_BYTES));

                if (!ours)
                    continue;

                byte[] normalised = Normalise(reply, read, deviceIndex);
                if (normalised == null)
                    continue;

                if (IsErrorFrame(reply))
                {
                    if (Hidpp.IsErrorFor(normalised, normalised.Length, featureIndex, functionId, "Centurion"))
                        return null;    //the device says it cannot answer this: no value
                    continue;           //an error about some other feature; keep waiting
                }

                    //The device's own power events arrive on this collection too --
                    //51 05 00 03 00 00 XX 00, carrying feature index 3 exactly as a real answer
                    //might -- and what separates them is the software id in the function byte:
                    //theirs is 0, ours is SOFTWARE_ID. That is why the constant must stay
                    //nonzero, and why both halves of this test matter.
                if (normalised[2] != featureIndex || normalised[3] != functionByte)
                    continue;

                return normalised;
            }

            return null;
        }

        public bool Ping(byte deviceIndex, int timeoutMs)
        {
            return Hidpp.Ping(this, deviceIndex, timeoutMs);
        }

        public byte GetFeatureIndex(byte deviceIndex, ushort featureId, int timeoutMs)
        {
            return Hidpp.GetFeatureIndex(this, deviceIndex, featureId, timeoutMs);
        }

        /// <summary>An error reply carries 0xFF (or the 1.0 spelling) where layer 3 starts.</summary>
        private static bool IsErrorFrame(byte[] reply)
        {
            return reply[LAYER3_OFFSET] == Hidpp.ERROR_HIDPP20 || reply[LAYER3_OFFSET] == Hidpp.ERROR_HIDPP10;
        }

        /// <summary>
        /// Rebuild a Centurion frame as a HID++ long report, so every caller above sees one
        /// layout. Null when the frame does not describe itself sensibly, which is worth
        /// dropping rather than guessing at -- a wrong offset here reads a plausible
        /// neighbouring byte instead of failing.
        /// </summary>
        private static byte[] Normalise(byte[] reply, int read, byte deviceIndex)
        {
            if (reply[2] != FLAGS_SINGLE_FRAME)
            {
                    //A fragmented reply. Reassembly is not implemented, and pretending the
                    //first fragment is the whole answer would be worse than reporting nothing.
                Log.Write("Centurion", "multi-fragment reply (flags 0x" + reply[2].ToString("X2") + ") -- not supported");
                return null;
            }

            int layer3Length = reply[1] - 1;
            if (layer3Length < 2 || LAYER3_OFFSET + layer3Length > read)
            {
                    //cplLength disagrees with what actually arrived. Trust the read length,
                    //which is the one number the driver, not the device, is responsible for.
                layer3Length = read - LAYER3_OFFSET;
                if (layer3Length < 2)
                    return null;
            }

                //At least a long frame, so callers that index a fixed payload offset are safe
                //on a short reply; longer when Centurion's bigger payload actually carries more.
            int length = Math.Max(Hidpp.LONG_FRAME_SIZE, 2 + layer3Length);
            byte[] normalised = new byte[length];
            normalised[0] = Hidpp.REPORT_LONG;
            normalised[1] = deviceIndex;
            Array.Copy(reply, LAYER3_OFFSET, normalised, 2, layer3Length);
            return normalised;
        }

        public void Dispose()
        {
            if (device != null)
            {
                device.Dispose();
                device = null;
            }
        }
    }
}
