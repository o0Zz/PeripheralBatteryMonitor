using System;
using System.Diagnostics;
using PeripheralBatteryMonitor.Contracts;
using PeripheralBatteryMonitor.Diagnostics;
using PeripheralBatteryMonitor.Hid;

namespace PeripheralBatteryMonitor.Providers.SteelSeries
{
    /// <summary>
    /// Battery for SteelSeries Arctis Nova wireless headsets, which reach the PC on their own
    /// USB dongle and so are discovered through <c>HidDeviceSource</c> rather than by the
    /// Bluetooth watchers -- the same shape as the Logitech LIGHTSPEED case.
    ///
    /// The protocol is far simpler than HID++: write one command byte to the vendor
    /// collection at usage page <c>0xFFC0</c> / usage <c>0x0001</c> and the dongle answers with
    /// a status report carrying the battery level and whether the headset is switched on. There
    /// is no feature discovery and nothing to cache between polls, so unlike
    /// <c>LogitechBatteryProvider</c> this type holds no per-device state -- the per-model
    /// differences are all static data in <see cref="Models"/>.
    ///
    /// <para>
    /// Device ids, report layout and the discrete/percentage split are taken from the
    /// HeadsetControl project's published device descriptions (GPL-3.0). Only those facts were
    /// used; no code was copied. **None of it has been verified against hardware here** -- see
    /// the note on <see cref="Models"/>.
    /// </para>
    /// </summary>
    public class SteelSeriesBatteryProvider : IBatteryProvider
    {
        private const ushort STEELSERIES_VENDOR_ID = 0x1038;

            //The vendor collection the dongle answers status requests on. A Nova dongle also
            //publishes audio-control collections; those do not answer 0xB0.
        private const ushort NOVA_USAGE_PAGE = 0xFFC0;
        private const ushort NOVA_USAGE = 0x0001;

            //Request is the single command byte 0xB0 ("report device status") preceded by
            //report id 0, then zero-padded to the collection's output report length. These
            //collections declare no report ids, so byte 0 is always 0 and is not part of the
            //message -- see the framing note on ReplyIndex in NovaModel.
        private const byte REPORT_ID_NONE = 0x00;
        private const byte COMMAND_DEVICE_STATUS = 0xB0;

            //Same budget as the Logitech provider: this runs on the UI thread inside the poll
            //tick, once per tracked device.
        private const int TIMEOUT_MS = 500;

            //The status report carries no echo of the request, so a frame cannot be matched to
            //it the way a HID++ reply can. The handle is opened per transaction, which makes
            //the first frame almost always the answer; a couple of spares cover a notification
            //(volume, chatmix) that happened to be queued first.
        private const int MAX_FRAMES_PER_REQUEST = 3;

            //Discrete models report 0..4 rather than a percentage. Four steps over the full
            //range, so each step is 25 points: 0/25/50/75/100. Coarser than it looks -- a
            //headset reading 25% may be anywhere from a fifth to nearly half full.
        private const int DISCRETE_MAX = 4;

        /// <summary>
        /// The HID interface that stands for one of these headsets, for the discovery layer.
        ///
        /// Restricted to an explicit product id list rather than "any SteelSeries device on
        /// this usage page", for the same reason <c>LogitechBatteryProvider.HidSpec</c> is:
        /// a dongle that is matched but cannot answer becomes a permanent "unknown battery"
        /// entry in the tray, which is worse than not listing it.
        /// </summary>
        public static readonly HidDeviceSpec HidSpec;

            //Assigned here rather than by a field initializer, and that is load-bearing:
            //initializers run in declaration order, so building the spec inline would read
            //Models -- declared at the bottom of this file -- while it was still null, and
            //HidDeviceSpecRegistry's static constructor would die taking the app with it.
            //A static constructor body runs after every field initializer, whatever the order.
        static SteelSeriesBatteryProvider()
        {
            HidSpec = new HidDeviceSpec(
                STEELSERIES_VENDOR_ID,
                ProductIds(),
                NOVA_USAGE_PAGE,
                NOVA_USAGE,
                "SteelSeries Arctis Wireless");
        }

        public int? ReadBattery(IBatteryDeviceContext ctx)
        {
                //Cheap rejections first: this runs against every tracked device, including
                //every Bluetooth one, on every poll.
            if (ctx == null || ctx.Transport != DeviceTransport.UsbHid)
                return null;

            int vendorId;
            if (!ProviderHid.TryGetInt(ctx, DeviceProperties.PROP_HID_VENDOR_ID, out vendorId) ||
                vendorId != STEELSERIES_VENDOR_ID)
                return null;

            int productId;
            if (!ProviderHid.TryGetInt(ctx, DeviceProperties.PROP_HID_PRODUCT_ID, out productId))
                return null;

            NovaModel model = ModelFor((ushort)productId);
            if (model == null)
                return null;

            HidInterfaceInfo info = ProviderHid.DescribeFromProperties(ctx);
            if (info == null)
                return null;

            try
            {
                using (HidDevice hid = HidDevice.Open(info))
                {
                    if (hid == null)
                        return null;

                    return ReadStatus(hid, model);
                }
            }
            catch (Exception e)
            {
                    //Raw HID access fails for plenty of benign reasons (dongle yanked
                    //mid-transaction, another process holding the collection). No reading.
                Log.Write("SteelSeries", "read failed on '" + ctx.DeviceName + "': " + e.Message);
                return null;
            }
        }

        private static int? ReadStatus(HidDevice hid, NovaModel model)
        {
            if (hid.OutputReportByteLength < 2)
                return null;

            byte[] request = new byte[hid.OutputReportByteLength];
            request[0] = REPORT_ID_NONE;
            request[1] = COMMAND_DEVICE_STATUS;

            if (!hid.Write(request, TIMEOUT_MS))
                return null;

                //Bound the whole exchange, not just each read, so a chatty dongle cannot hold
                //the poll tick for MAX_FRAMES_PER_REQUEST * TIMEOUT_MS.
            Stopwatch budget = Stopwatch.StartNew();

            for (int frame = 0; frame < MAX_FRAMES_PER_REQUEST; frame++)
            {
                int remaining = TIMEOUT_MS - (int)budget.ElapsedMilliseconds;
                if (remaining <= 0)
                    return null;

                byte[] reply = new byte[hid.InputReportByteLength];
                int read;
                if (!hid.Read(reply, remaining, out read))
                    return null;

                Verdict verdict;
                int? level = Parse(reply, read, model, out verdict);
                if (verdict != Verdict.NotAStatusFrame)
                    return level;
            }

            return null;
        }

        private enum Verdict
        {
            /// <summary>The frame was a status report and carried a usable level.</summary>
            Level,

            /// <summary>A status report saying the headset is off; stop reading, keep the previous value.</summary>
            Offline,

            /// <summary>Something else on this collection -- a notification. Keep waiting.</summary>
            NotAStatusFrame,
        }

        private static int? Parse(byte[] reply, int read, NovaModel model, out Verdict verdict)
        {
            verdict = Verdict.NotAStatusFrame;

            if (read <= model.LevelIndex || read <= model.StateIndex)
                return null;

            if (reply[model.StateIndex] == model.OfflineValue)
            {
                    //Headset switched off or out of range. A null return leaves the last known
                    //level on screen, which is the right answer for a device that is merely
                    //asleep -- the contract IBatteryProvider documents for null.
                verdict = Verdict.Offline;
                return null;
            }

            int raw = reply[model.LevelIndex];

                //The only discriminator available: a status frame's level byte is in range and
                //a notification's very likely is not. Weak, which is why the handle is opened
                //per transaction so the first frame is nearly always the answer.
            if (raw > model.RawMax)
                return null;

            verdict = Verdict.Level;
            return model.Discrete ? raw * (100 / DISCRETE_MAX) : raw;
        }

        /* ============================ model table ============================ */

        /// <summary>
        /// One entry per product id, because the reply layout is not uniform across the range
        /// and neither is the meaning of the level byte.
        /// </summary>
        private sealed class NovaModel
        {
            public readonly ushort ProductId;

                //Index into the input report as Windows delivers it. NOTE the off-by-one
                //against every published description of this protocol: those are written
                //against hidapi, which strips the leading report-id byte when a collection
                //declares no report ids, while ReadFile -- what HidDevice.Read uses -- always
                //returns it. So an offset documented as "data[2]" is reply[3] here. Getting
                //this wrong reads a plausible-looking neighbouring byte rather than failing,
                //so it is the first thing to check if a model reports nonsense.
            public readonly int LevelIndex;
            public readonly int StateIndex;
            public readonly byte OfflineValue;

                //Discrete models report 0..4; the rest report a percentage directly.
            public readonly bool Discrete;

            public int RawMax { get { return Discrete ? DISCRETE_MAX : 100; } }

            public NovaModel(ushort productId, int levelIndex, int stateIndex, byte offlineValue, bool discrete)
            {
                this.ProductId = productId;
                this.LevelIndex = levelIndex;
                this.StateIndex = stateIndex;
                this.OfflineValue = offlineValue;
                this.Discrete = discrete;
            }
        }

            //Nova 7 layout: level at documented data[2], state at data[3] where 0x00 means the
            //headset is off. Shared by the whole 7 / 7P / 7X range.
        private static NovaModel Nova7(ushort productId, bool discrete)
        {
            return new NovaModel(productId, 3, 4, 0x00, discrete);
        }

            //Nova 5 base stations answer with a different layout: connection state at
            //documented data[1] where 0x02 means off, and the percentage at data[3].
        private static NovaModel Nova5(ushort productId)
        {
            return new NovaModel(productId, 4, 2, 0x02, false);
        }

        /// <summary>
        /// Supported product ids.
        ///
        /// <para>
        /// **Not verified on hardware.** Every other device this app supports was confirmed
        /// against the physical thing before its id went in; these came from HeadsetControl's
        /// device tables instead. They are as good as that project's testing and no better, so
        /// treat a model that reports a wrong or impossible level as unproven rather than as a
        /// bug in the transport -- and check <c>LevelIndex</c> first.
        /// </para>
        /// </summary>
        private static readonly NovaModel[] Models =
        {
                //---- Arctis Nova 7 family, discrete 0..4 (pre-2026 firmware) ----
            Nova7(0x2202, true),    //Arctis Nova 7
            Nova7(0x2206, true),    //Arctis Nova 7X
            Nova7(0x220A, true),    //Arctis Nova 7P
            Nova7(0x223A, true),    //Arctis Nova 7 Diablo IV
            Nova7(0x227A, true),    //Arctis Nova 7 WoW Edition
            Nova7(0x22A4, true),    //Arctis Nova 7X

                //---- Arctis Nova 7 family, direct percentage ----
            Nova7(0x22A1, false),   //Arctis Nova 7, 2026 firmware
            Nova7(0x227E, false),   //Arctis Nova 7 Gen 2
            Nova7(0x2258, false),   //Arctis Nova 7X v2
            Nova7(0x229E, false),   //Arctis Nova 7X v2
            Nova7(0x22AD, false),   //Arctis Nova 7X v2
            Nova7(0x22A5, false),   //Arctis Nova 7X
            Nova7(0x22A9, false),   //Arctis Nova 7 Diablo IV, 2026 firmware
            Nova7(0x22A7, false),   //Arctis Nova 7P v2
            Nova7(0x2298, false),   //Arctis Nova 7P v2

                //---- Arctis Nova 5 base stations ----
            Nova5(0x2232),          //Arctis Nova 5
            Nova5(0x2253),          //Arctis Nova 5X
        };

        private static NovaModel ModelFor(ushort productId)
        {
            foreach (NovaModel model in Models)
            {
                if (model.ProductId == productId)
                    return model;
            }
            return null;
        }

        private static ushort[] ProductIds()
        {
            ushort[] ids = new ushort[Models.Length];
            for (int i = 0; i < Models.Length; i++)
                ids[i] = Models[i].ProductId;
            return ids;
        }
    }
}
