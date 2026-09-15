using System;
using System.Diagnostics;
using PeripheralBatteryMonitor.Diagnostics;
using PeripheralBatteryMonitor.Hid;

namespace PeripheralBatteryMonitor.Providers.Logitech
{
    /// <summary>
    /// Logitech HID++ 2.0 over the long-report framing: LIGHTSPEED mice, keyboards and the
    /// PRO X Wireless headset. <see cref="CenturionTransport"/> is the other framing.
    ///
    /// <c>[reportId][deviceIndex][featureIndex][functionId&lt;&lt;4 | softwareId][params...]</c>, and
    /// the answer echoes the first four bytes so it can be told apart from the unsolicited
    /// notifications arriving on the same interface. That is already the layout
    /// <see cref="IHidppTransport"/> promises, so this normalises nothing.
    /// </summary>
    internal class HidppTransport : IHidppTransport
    {
        private const int MAX_FRAMES_PER_REQUEST = 8;

        private const int LOG_BYTES = 20;

        private HidDevice device;

        private HidppTransport(HidDevice device)
        {
            this.device = device;
        }

        public static HidppTransport Open(HidInterfaceInfo info)
        {
            if (info == null)
                return null;

                //The 7-byte short report (0x10) is deliberately not a fallback. On Windows a
                //receiver's short and long collections are separate device paths, so a reply to
                //a short request arrives on the *other* handle -- supporting it is a two-handle
                //transport, not a smaller buffer. Nothing needs it: 2.0 feature calls work over
                //the long collection at every device index, and the PRO X collection rejects
                //report 0x10 outright.
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

                //Both directions, always: once the app is on someone else's machine this is the
                //only record of the conversation that exists.
            Log.WriteHex("HID++", "->", request, Math.Min(request.Length, LOG_BYTES));

            if (!device.Write(request, timeoutMs))
                return null;

                //Bound the whole exchange, not each read: a chatty device could otherwise keep
                //us here for MAX_FRAMES_PER_REQUEST * timeoutMs.
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
                    //connect/wake/sleep report carries our device index but a protocol byte
                    //where the feature index would be. Keep waiting.
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
