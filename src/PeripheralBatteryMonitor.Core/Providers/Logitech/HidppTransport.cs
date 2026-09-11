using System;
using System.Diagnostics;
using PeripheralBatteryMonitor.Diagnostics;
using PeripheralBatteryMonitor.Hid;

namespace PeripheralBatteryMonitor.Providers.Logitech
{
    /// <summary>
    /// Logitech HID++ 2.0 request/response framing over one HID interface -- the long-report
    /// framing, which is what LIGHTSPEED mice, keyboards and the PRO X Wireless headset use.
    /// <see cref="CenturionTransport"/> is the other framing, for the newer headsets.
    ///
    /// A request is an output report laid out as
    /// <c>[reportId][deviceIndex][featureIndex][functionId&lt;&lt;4 | softwareId][params...]</c>
    /// and the answer arrives as an input report echoing deviceIndex / featureIndex /
    /// functionId, so a reply can be told apart from the unsolicited notifications the device
    /// also sends on the same interface. That layout is already what
    /// <see cref="IHidppTransport"/> promises its callers, so this transport normalises
    /// nothing -- it hands the wire frame straight back.
    ///
    /// Features are addressed by *index*, and the index of a given feature id differs per
    /// device, so it must be looked up at runtime through the root feature
    /// (<see cref="GetFeatureIndex"/>) rather than hardcoded.
    /// </summary>
    internal class HidppTransport : IHidppTransport
    {
        private const int MAX_FRAMES_PER_REQUEST = 8;

            //How much of a frame to hex-log. A long frame is 20 bytes and the payload every
            //decoder reads ends well inside that.
        private const int LOG_BYTES = 20;

        private HidDevice device;

        private HidppTransport(HidDevice device)
        {
            this.device = device;
        }

        /// <summary>
        /// Open a HID++ conversation on the given interface. Null when the interface can't be
        /// opened or is too small to carry a long report.
        /// </summary>
        public static HidppTransport Open(HidInterfaceInfo info)
        {
            if (info == null)
                return null;

                //Frame size is a property of the framing, not of the handle, so this states
                //its own minimum rather than deferring to a shared guard: 20 here, 64 for a
                //Centurion frame. A collection too small for the framing cannot carry it.
                //
                //The 7-byte short report (0x10) is deliberately not a fallback. On Windows a
                //receiver's short and long collections are separate device paths and a reply
                //to a short request arrives on the *other* handle, so supporting it is a
                //two-handle transport rather than a smaller buffer -- and nothing here needs
                //it: HID++ 2.0 feature calls work over the long collection at every device
                //index. Also, the PRO X collection rejects report 0x10 outright.
            if (info.OutputReportByteLength < Hidpp.LONG_FRAME_SIZE || info.InputReportByteLength < Hidpp.LONG_FRAME_SIZE)
            {
                Log.Write("HID++", "refusing " + info + ": reports smaller than a "
                    + Hidpp.LONG_FRAME_SIZE + "-byte long frame");
                return null;
            }

            HidDevice hid = HidDevice.Open(info);
            if (hid == null)
                return null;

            return new HidppTransport(hid);
        }

        /// <summary>
        /// Run one transaction. Returns the raw reply frame -- byte 4 onwards is the payload --
        /// or null on write failure, timeout or a device-reported error.
        /// </summary>
        public byte[] Request(byte deviceIndex, byte featureIndex, byte functionId, byte[] parameters, int timeoutMs)
        {
            byte[] request = new byte[device.OutputReportByteLength];
            request[0] = Hidpp.REPORT_LONG;
            request[1] = deviceIndex;
            request[2] = featureIndex;
            request[3] = (byte)((functionId << 4) | Hidpp.SOFTWARE_ID);
            if (parameters != null)
            {
                for (int i = 0; i < parameters.Length && (4 + i) < request.Length; i++)
                    request[4 + i] = parameters[i];
            }

                //Both directions, always. This is the only record of a vendor conversation
                //that exists once the app is on someone else's machine -- the person with the
                //hardware is not the person who can read the code. LOG_BYTES rather than the
                //whole frame because the rest is zero padding.
            Log.WriteHex("HID++", "->", request, Math.Min(request.Length, LOG_BYTES));

            if (!device.Write(request, timeoutMs))
                return null;

                //Bound the whole exchange, not just each individual read: a chatty device
                //could otherwise keep us here for MAX_FRAMES_PER_REQUEST * timeoutMs.
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

                Log.WriteHex("HID++", "<-", reply, Math.Min(read, LOG_BYTES));

                if (read < 5)
                    continue;
                if (reply[1] != deviceIndex)
                    continue;   //notification about some other device on this receiver

                if (reply[2] == Hidpp.ERROR_HIDPP20 || reply[2] == Hidpp.ERROR_HIDPP10)
                {
                    if (Hidpp.IsErrorFor(reply, read, featureIndex, functionId, "HID++"))
                        return null;
                    continue;   //an error about some other feature
                }

                if (reply[2] == featureIndex && reply[3] == request[3])
                    return reply;

                    //Anything else is an unsolicited notification -- a receiver's 0x41
                    //connect/wake/sleep report among them, which carries our device index but a
                    //protocol byte where the feature index would be. Keep waiting for our reply.
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
