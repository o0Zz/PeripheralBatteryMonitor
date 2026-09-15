using System;
using PeripheralBatteryMonitor.Contracts;
using PeripheralBatteryMonitor.Diagnostics;
using PeripheralBatteryMonitor.Hid;

namespace PeripheralBatteryMonitor.Providers.Logitech
{
    /// <summary>
    /// Reads battery for Logitech LIGHTSPEED and Unifying devices. Not Bluetooth at all, so
    /// they are discovered through the HID layer, and Windows exposes no battery for them
    /// either: PROP_BATTERY_LEVEL is absent from every one of the device's nodes and there is
    /// no HID-battery node.
    ///
    /// Which *feature* carries the battery varies per device, so <see cref="batteryFeatures"/>
    /// is probed in order and the first that yields a value is remembered. Percentage features
    /// come first; the two that report a raw cell voltage are last, because converting volts
    /// costs accuracy.
    ///
    /// Two things vary underneath and neither reaches the decoders: the **framing**, chosen by
    /// usage page in <see cref="OpenTransport"/>, and the **device index**, from the property
    /// bag -- 0xFF behind its own dongle, 1..6 behind a receiver.
    ///
    /// Verified against a PRO X Wireless (VID 0x046D / PID 0x0ABA), which is the awkward case:
    /// it implements *none* of the three standard battery features, only 0x1F20. HID++ 4.2
    /// answers on its 0xFF43/0x0202 collection at device index 0xFF, and 0x1F20 function 0
    /// returns [voltage_hi][voltage_lo][flags], e.g. 0x0F54 = 3924 mV.
    /// </summary>
    public class LogitechBatteryProvider : IBatteryProvider
    {
        private const ushort LOGITECH_VENDOR_ID = 0x046D;

            //A device connected directly: its own dongle, or Bluetooth.
        private const ushort HIDPP_USAGE_PAGE = 0xFF43;
        private const ushort HIDPP_USAGE = 0x0202;

            //A *receiver* carries the same traffic on the generic vendor page instead, short
            //reports at usage 0x0001 and long at 0x0002. Not a variant spelling of the pair
            //above: a receiver never publishes 0xFF43 and a direct device never publishes
            //0xFF00. Pointing ReceiverHidSpec at 0xFF43 made every receiver unmatchable and
            //the whole sweep unreachable.
        private const ushort RECEIVER_USAGE_PAGE = 0xFF00;
        private const ushort RECEIVER_USAGE_LONG = 0x0002;

            //The newer headsets, same feature layer inside Centurion framing. A device
            //publishes one or the other, never both, so the usage page picks the transport.
        private const ushort CENTURION_USAGE_PAGE = 0xFFA0;
        private const ushort CENTURION_USAGE = 0x0001;

        /* ---- HID++ battery features, most to least precise ---- */

            //0x1004 UNIFIED_BATTERY: func 1 getStatus -> [stateOfCharge][levels][charging][ext].
            //State of charge is a straight percentage when the device supports it.
        private const ushort FEATURE_UNIFIED_BATTERY = 0x1004;
            //0x1000 BATTERY_UNIFIED_LEVEL_STATUS: func 0 -> [level][nextLevel][status], level
            //being a percentage, or 0 when the device only reports discrete levels.
        private const ushort FEATURE_BATTERY_LEVEL_STATUS = 0x1000;
            //0x1001 BATTERY_VOLTAGE: func 0 -> [mV_hi][mV_lo][flags].
        private const ushort FEATURE_BATTERY_VOLTAGE = 0x1001;
            //0x1F20 ADC_MEASUREMENT: func 0 -> [mV_hi][mV_lo][flags]. What the PRO X uses.
        private const ushort FEATURE_ADC_MEASUREMENT = 0x1F20;

        private static readonly ushort[] batteryFeatures = new ushort[]
        {
            FEATURE_UNIFIED_BATTERY,
            FEATURE_BATTERY_LEVEL_STATUS,
            FEATURE_BATTERY_VOLTAGE,
            FEATURE_ADC_MEASUREMENT,
        };

            //The dongle answers in under a millisecond when awake; this only covers the case
            //where the device is off and says nothing.
        private const int TIMEOUT_MS = 500;

        /// <summary>
        /// Restricted to verified product ids: matching every Logitech HID++ collection would
        /// also match receivers, where index 0xFF addresses the receiver itself and no battery
        /// feature answers -- a phantom "unknown battery" entry in the tray. To add a device
        /// here it must sit on this collection and answer at index 0xFF.
        /// </summary>
        public static readonly HidDeviceSpec HidSpec;

        /// <summary>
        /// Its own spec because a <see cref="HidDeviceSpec"/> matches one usage page, and its
        /// own id list because these ids are **unverified** -- from Solaar and HeadsetControl,
        /// not hardware. A precise usage page plus an explicit id is what keeps that safe:
        /// nothing else on the machine can match one by accident.
        /// </summary>
        public static readonly HidDeviceSpec CenturionHidSpec;

        /// <summary>
        /// One interface carrying up to six peripherals rather than being one device itself --
        /// hence the expander, where the "a device exists only because it answered" rule lives.
        ///
        /// On <see cref="RECEIVER_USAGE_PAGE"/>, not <see cref="HidSpec"/>'s, so the two cannot
        /// match the same interface and registration order between them is not load-bearing.
        ///
        /// **Unverified**: the ids come from published tables and nobody here has a receiver.
        /// The ping gate is what makes that safe -- a wrong id costs one sweep that finds
        /// nothing, not a wrong reading.
        /// </summary>
        public static readonly HidDeviceSpec ReceiverHidSpec;

            //Static constructor, not field initializers: those run in declaration order, so
            //reading receiverProductIds further down the file would read it while still null
            //and take HidDeviceSpecRegistry's static constructor -- and the app -- down with it.
        static LogitechBatteryProvider()
        {
            HidSpec = new HidDeviceSpec(
                LOGITECH_VENDOR_ID,
                new ushort[] { 0x0ABA },      //PRO X Wireless Gaming Headset
                HIDPP_USAGE_PAGE,
                HIDPP_USAGE,
                "Logitech Wireless Headset");

            CenturionHidSpec = new HidDeviceSpec(
                LOGITECH_VENDOR_ID,
                new ushort[] { 0x0AF7 },      //PRO X 2 LIGHTSPEED Gaming Headset -- unverified
                CENTURION_USAGE_PAGE,
                CENTURION_USAGE,
                "Logitech Wireless Headset");

            ReceiverHidSpec = new HidDeviceSpec(
                LOGITECH_VENDOR_ID,
                receiverProductIds,
                RECEIVER_USAGE_PAGE,
                RECEIVER_USAGE_LONG,
                0,
                "Logitech Receiver",
                new LogitechReceiverEnumerator());
        }

            //The battery feature this device turned out to use, resolved once and kept: feature
            //indexes are per-device but stable for its lifetime, so steady-state polling costs
            //a single transaction. One provider instance exists per device, so nothing leaks
            //between them. Index 0 always means the root feature, hence "not resolved yet".
        private ushort boundFeatureId = 0;
        private byte boundFeatureIndex = 0;

        /// <summary>
        /// Which device on the far end of this interface. 0xFF is the device behind its own
        /// dongle -- the only case before receivers were supported, and still the common one.
        /// 1..6 address the peripherals paired to a receiver, and discovery has already proved
        /// that one answers there, so this is never a guess.
        /// </summary>
        private static byte DeviceIndexFor(IBatteryDeviceContext ctx)
        {
            return ProviderHid.DeviceIndexOf(ctx, Hidpp.DEVICE_INDEX_DIRECT);
        }

        public int? ReadBattery(IBatteryDeviceContext ctx)
        {
                //Cheap rejections first: this runs against every tracked device on every poll.
            if (ctx == null || ctx.Transport != DeviceTransport.UsbHid)
                return null;

            int vendorId;
            if (!ProviderHid.TryGetInt(ctx, DeviceProperties.PROP_HID_VENDOR_ID, out vendorId) || vendorId != LOGITECH_VENDOR_ID)
                return null;

            HidInterfaceInfo info = ProviderHid.DescribeFromProperties(ctx);
            if (info == null)
                return null;

            try
            {
                using (IHidppTransport hidpp = OpenTransport(info))
                {
                    if (hidpp == null)
                        return null;

                    if (boundFeatureIndex != 0)
                        return Read(hidpp, boundFeatureId, boundFeatureIndex, ctx);

                    return Resolve(hidpp, ctx);
                }
            }
            catch (Exception e)
            {
                Log.Write("Logitech", "read failed on '" + ctx.DeviceName + "': " + e.Message);
                return null;
            }
        }

        /// <summary>
        /// The usage page decides the framing and nothing else does: a device publishes one
        /// collection or the other, never both.
        /// </summary>
        private static IHidppTransport OpenTransport(HidInterfaceInfo info)
        {
            if (info.UsagePage == CENTURION_USAGE_PAGE)
                return CenturionTransport.Open(info);
            return HidppTransport.Open(info);
        }

        private int? Resolve(IHidppTransport hidpp, IBatteryDeviceContext ctx)
        {
                //Probing costs one timeout per feature, so check something is listening first:
                //a switched-off device would otherwise burn the whole chain on the UI thread,
                //every poll.
            if (!hidpp.Ping(DeviceIndexFor(ctx), TIMEOUT_MS))
            {
                Log.Write("Logitech", "'" + ctx.DeviceName + "' is not answering HID++ (switched off?)");
                return null;
            }

            foreach (ushort featureId in batteryFeatures)
            {
                byte featureIndex = hidpp.GetFeatureIndex(DeviceIndexFor(ctx), featureId, TIMEOUT_MS);
                if (featureIndex == 0)
                    continue;   //not implemented by this device

                int? level = Read(hidpp, featureId, featureIndex, ctx);
                if (!level.HasValue)
                    continue;   //implemented but not answering / no value to give

                boundFeatureId = featureId;
                boundFeatureIndex = featureIndex;
                Log.Write("Logitech", "'" + ctx.DeviceName + "' bound to feature 0x"
                    + featureId.ToString("X4") + " at index 0x" + featureIndex.ToString("X2"));
                return level;
            }

            Log.Write("Logitech", "'" + ctx.DeviceName + "' implements no known battery feature");
            return null;
        }

        private int? Read(IHidppTransport hidpp, ushort featureId, byte featureIndex, IBatteryDeviceContext ctx)
        {
            switch (featureId)
            {
                case FEATURE_UNIFIED_BATTERY:
                    return ReadUnifiedBattery(hidpp, featureIndex, ctx);

                case FEATURE_BATTERY_LEVEL_STATUS:
                    return ReadLevelStatus(hidpp, featureIndex, ctx);

                case FEATURE_BATTERY_VOLTAGE:
                case FEATURE_ADC_MEASUREMENT:
                    return ReadVoltage(hidpp, featureId, featureIndex, ctx);
            }
            return null;
        }

        /// <summary>0x1004 getStatus: a percentage when supported, else a discrete level flag.</summary>
        private static int? ReadUnifiedBattery(IHidppTransport hidpp, byte featureIndex, IBatteryDeviceContext ctx)
        {
            byte[] reply = hidpp.Request(DeviceIndexFor(ctx), featureIndex, 0x01, null, TIMEOUT_MS);
            if (reply == null || reply.Length < 7)
                return null;

            int stateOfCharge = reply[4];
            if (stateOfCharge >= 1 && stateOfCharge <= 100)
            {
                Log.Write("Logitech", "'" + ctx.DeviceName + "' unifiedBattery soc=" + stateOfCharge + "%");
                return stateOfCharge;
            }

                //Fall back to the discrete level bitfield, which really is a coarse four-way
                //enum: each level becomes a representative percentage -- a band, not a
                //measurement.
            int level = reply[5];
            if ((level & 0x08) != 0) return 90;   //full
            if ((level & 0x04) != 0) return 60;   //good
            if ((level & 0x02) != 0) return 30;   //low
            if ((level & 0x01) != 0) return 10;   //critical
            return null;
        }

        /// <summary>0x1000 getBatteryLevelStatus: percentage in byte 0, 0 meaning "unknown".</summary>
        private static int? ReadLevelStatus(IHidppTransport hidpp, byte featureIndex, IBatteryDeviceContext ctx)
        {
            byte[] reply = hidpp.Request(DeviceIndexFor(ctx), featureIndex, 0x00, null, TIMEOUT_MS);
            if (reply == null || reply.Length < 7)
                return null;

            int dischargeLevel = reply[4];
            if (dischargeLevel < 1 || dischargeLevel > 100)
                return null;

            Log.Write("Logitech", "'" + ctx.DeviceName + "' levelStatus=" + dischargeLevel
                + "% (status=0x" + reply[6].ToString("X2") + ")");
            return dischargeLevel;
        }

        /// <summary>
        /// Receiver ids, not peripheral ids -- one covers several products, which is why the
        /// peripheral behind it is discovered rather than looked up.
        /// </summary>
        private static readonly ushort[] receiverProductIds = new ushort[]
        {
            0xC539,   //LIGHTSPEED receiver (PRO Wireless generation)
            0xC53F,   //LIGHTSPEED receiver
            0xC547,   //LIGHTSPEED receiver -- G915 X TKL, PRO X Superlight, G502 X LIGHTSPEED
            0xC54D,   //LIGHTSPEED receiver -- PRO X Superlight 2
        };

        /// <summary>0x1001 / 0x1F20: raw cell voltage in millivolts, converted by the curve.</summary>
        private static int? ReadVoltage(IHidppTransport hidpp, ushort featureId, byte featureIndex, IBatteryDeviceContext ctx)
        {
            byte[] reply = hidpp.Request(DeviceIndexFor(ctx), featureIndex, 0x00, null, TIMEOUT_MS);
            if (reply == null || reply.Length < 7)
                return null;    //device switched off / out of range

            int millivolts = (reply[4] << 8) | reply[5];
                //Sanity-bound it: a single Li-Po cell that reads outside this window means the
                //payload isn't what we think it is, and guessing a percentage from it would be
                //worse than reporting nothing.
            if (millivolts < 2000 || millivolts > 5000)
            {
                Log.Write("Logitech", "'" + ctx.DeviceName + "' implausible voltage " + millivolts
                    + "mV from feature 0x" + featureId.ToString("X4") + " -- ignored");
                return null;
            }

            int percent = LogitechVoltageCurve.ToPercentage(millivolts);
            Log.Write("Logitech", "'" + ctx.DeviceName + "' " + millivolts + "mV -> " + percent
                + "% (feature 0x" + featureId.ToString("X4") + ", flags=0x" + reply[6].ToString("X2") + ")");
            return percent;
        }
    }
}
