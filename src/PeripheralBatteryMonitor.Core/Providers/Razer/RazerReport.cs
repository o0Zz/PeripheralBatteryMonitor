using System;
using System.Threading;
using PeripheralBatteryMonitor.Diagnostics;
using PeripheralBatteryMonitor.Hid;

namespace PeripheralBatteryMonitor.Providers.Razer
{
    /// <summary>
    /// Razer's vendor request/response framing, carried on HID **feature reports** rather than
    /// in the report streams -- which is why <see cref="HidDevice.OpenForFeatureReports"/>
    /// exists.
    ///
    /// One transaction is SET_REPORT with the request, a pause, then GET_REPORT to collect the
    /// answer out of the same 90-byte structure. There is no separate response channel: the
    /// device overwrites the buffer in place, so the reply echoes the request's transaction id,
    /// command class and command id, which is what makes it verifiable.
    ///
    /// <code>
    /// offset  0  status
    ///         1  transaction id          (per-device, echoed)
    ///       2-3  remaining packets       (big endian, always 0 here)
    ///         4  protocol type           (always 0)
    ///         5  data size
    ///         6  command class           (echoed)
    ///         7  command id              (echoed)
    ///      8-87  arguments (80 bytes)
    ///        88  crc = XOR of bytes 2..87
    ///        89  reserved
    /// </code>
    ///
    /// The wire buffer is 91 bytes: the 90 above prefixed with report id 0.
    /// </summary>
    internal static class RazerReport
    {
        /// <summary>90-byte payload plus the leading report id.</summary>
        internal const int WIRE_LENGTH = 91;

        private const int REPORT_ID = 0;          //index of the report id in the wire buffer
        private const int PAYLOAD = 1;            //where the 90-byte payload starts

            //Offsets within the payload, not the wire buffer. Add PAYLOAD to index the latter.
        private const int OFF_STATUS = 0;
        private const int OFF_TRANSACTION_ID = 1;
        private const int OFF_DATA_SIZE = 5;
        private const int OFF_COMMAND_CLASS = 6;
        private const int OFF_COMMAND_ID = 7;
        private const int OFF_ARGUMENTS = 8;
        private const int OFF_CRC = 88;

            //The CRC covers the payload from the remaining-packets field up to, but not
            //including, the CRC byte itself.
        private const int CRC_FIRST = 2;
        private const int CRC_LAST = 87;

        /* ---- status byte, as the device sets it in the reply ---- */
        private const byte STATUS_NEW_COMMAND = 0x00;   //what we send
        private const byte STATUS_BUSY = 0x01;
        private const byte STATUS_SUCCESSFUL = 0x02;
        private const byte STATUS_FAILURE = 0x03;
        private const byte STATUS_TIMEOUT = 0x04;
        private const byte STATUS_NOT_SUPPORTED = 0x05;

            //The device needs a moment between SET and GET; asking immediately reads back the
            //request rather than the answer. Reference implementations use 60 ms.
        private const int SETTLE_MS = 60;

            //Two attempts, not the ten a standalone tool can afford: this runs on the UI thread
            //inside the poll tick. Worst case here is ~2 * (SETTLE_MS + RETRY_MS), a fifth of a
            //second, against the 5 s a 10 x 500 ms retry loop would cost per device.
        private const int MAX_ATTEMPTS = 2;
        private const int RETRY_MS = 40;

        /// <summary>
        /// Run one command and return its 80-byte argument block, or null if the device did not
        /// answer, answered something else, or reported a failure.
        /// </summary>
        internal static byte[] Request(HidDevice device, byte transactionId, byte commandClass,
                                       byte commandId, byte dataSize)
        {
            if (device == null || device.FeatureReportByteLength != WIRE_LENGTH)
                return null;

            for (int attempt = 0; attempt < MAX_ATTEMPTS; attempt++)
            {
                if (attempt > 0)
                    Thread.Sleep(RETRY_MS);

                byte[] wire = Build(transactionId, commandClass, commandId, dataSize);
                if (!device.SetFeature(wire))
                    return null;

                Thread.Sleep(SETTLE_MS);

                byte[] reply = new byte[WIRE_LENGTH];
                reply[REPORT_ID] = 0;
                if (!device.GetFeature(reply))
                    return null;

                byte status = reply[PAYLOAD + OFF_STATUS];

                    //Busy means "ask again", which is the only case worth a second attempt.
                if (status == STATUS_BUSY)
                    continue;

                if (status != STATUS_SUCCESSFUL)
                {
                    Log.Write("Razer", "class 0x" + commandClass.ToString("X2") + " id 0x"
                        + commandId.ToString("X2") + " -> status 0x" + status.ToString("X2")
                        + " (" + StatusName(status) + ")");
                    return null;
                }

                    //The reply lands in the same buffer the request occupied, so a device that
                    //answered nothing at all would hand back our own bytes. Checking the echo
                    //and the CRC is what tells the two apart.
                if (reply[PAYLOAD + OFF_TRANSACTION_ID] != transactionId ||
                    reply[PAYLOAD + OFF_COMMAND_CLASS] != commandClass ||
                    reply[PAYLOAD + OFF_COMMAND_ID] != commandId)
                {
                    Log.Write("Razer", "reply did not echo the request -- ignored");
                    return null;
                }

                if (reply[PAYLOAD + OFF_CRC] != Checksum(reply))
                {
                    Log.Write("Razer", "reply failed its checksum -- ignored");
                    return null;
                }

                byte[] arguments = new byte[OFF_CRC - OFF_ARGUMENTS];
                Array.Copy(reply, PAYLOAD + OFF_ARGUMENTS, arguments, 0, arguments.Length);
                return arguments;
            }

            return null;
        }

        private static byte[] Build(byte transactionId, byte commandClass, byte commandId, byte dataSize)
        {
            byte[] wire = new byte[WIRE_LENGTH];
            wire[REPORT_ID] = 0;
            wire[PAYLOAD + OFF_STATUS] = STATUS_NEW_COMMAND;
            wire[PAYLOAD + OFF_TRANSACTION_ID] = transactionId;
            wire[PAYLOAD + OFF_DATA_SIZE] = dataSize;
            wire[PAYLOAD + OFF_COMMAND_CLASS] = commandClass;
            wire[PAYLOAD + OFF_COMMAND_ID] = commandId;
            wire[PAYLOAD + OFF_CRC] = Checksum(wire);
            return wire;
        }

        /// <summary>XOR of payload bytes 2..87. Takes the wire buffer, not the payload.</summary>
        private static byte Checksum(byte[] wire)
        {
            byte crc = 0;
            for (int i = CRC_FIRST; i <= CRC_LAST; i++)
                crc ^= wire[PAYLOAD + i];
            return crc;
        }

        private static string StatusName(byte status)
        {
            switch (status)
            {
                case STATUS_NEW_COMMAND: return "unanswered";
                case STATUS_BUSY: return "busy";
                case STATUS_FAILURE: return "failure";
                case STATUS_TIMEOUT: return "timeout";
                case STATUS_NOT_SUPPORTED: return "not supported";
                default: return "unknown";
            }
        }
    }
}
