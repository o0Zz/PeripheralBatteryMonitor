using System;
using PeripheralBatteryMonitor.Contracts;
using PeripheralBatteryMonitor.Diagnostics;
using PeripheralBatteryMonitor.Hid;

namespace PeripheralBatteryMonitor.Providers.Logitech
{
    /// <summary>
    /// Reads battery for Logitech LIGHTSPEED and Unifying devices -- a peripheral on its own
    /// USB dongle, or one paired to a receiver alongside others. These are not Bluetooth
    /// devices at all, so they are discovered through the HID layer (see <see cref="HidSpec"/>
    /// and its two siblings) and none of the Bluetooth property-bag providers can see them;
    /// Windows exposes no battery for them either
    /// (<see cref="DeviceProperties.PROP_BATTERY_LEVEL"/> is absent from every one of the
    /// device's nodes, and there is no HID-battery node).
    ///
    /// Battery comes from HID++ 2.0 over the device's vendor collection. Which *feature*
    /// carries it varies per device, so <see cref="batteryFeatures"/> is probed in order and
    /// the first one that both exists and yields a value is remembered. Features that report a
    /// percentage outright come first; the two that report a raw cell voltage are last,
    /// because turning volts into a percentage costs accuracy (see
    /// <see cref="LogitechVoltageCurve"/>).
    ///
    /// Two things vary underneath that and neither reaches this class's decoders. The
    /// **framing** is chosen by usage page in <see cref="OpenTransport"/> -- the newer headsets
    /// wrap the same feature layer in Centurion, see <see cref="CenturionTransport"/> -- and
    /// the **device index** comes from the property bag, 0xFF for a device behind its own
    /// dongle and 1..6 for one paired to a receiver, see
    /// <see cref="LogitechReceiverEnumerator"/>.
    ///
    /// Verified against a PRO X Wireless (VID 0x046D / PID 0x0ABA), which is the awkward case:
    /// it implements *none* of the three standard battery features, only 0x1F20. HID++ 4.2
    /// answers on its 0xFF43/0x0202 collection at device index 0xFF, and 0x1F20 function 0
    /// returns [voltage_hi][voltage_lo][flags], e.g. 0x0F54 = 3924 mV.
    /// </summary>
    public class LogitechBatteryProvider : IBatteryProvider
    {
        private const ushort LOGITECH_VENDOR_ID = 0x046D;

            //Logitech's HID++ collection on modern gaming gear.
        private const ushort HIDPP_USAGE_PAGE = 0xFF43;
        private const ushort HIDPP_USAGE = 0x0202;

            //The collection the newer headsets answer on instead, carrying the same HID++ 2.0
            //feature layer inside Centurion framing. A device publishes one or the other, never
            //both, so the usage page is what picks the transport -- see OpenTransport.
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

            //The dongle answers in well under a millisecond when the device is awake; this
            //only has to cover the case where it is off and says nothing at all.
        private const int TIMEOUT_MS = 500;

        /// <summary>
        /// The HID++ interface that stands for one of these devices, for the discovery layer.
        ///
        /// Deliberately restricted to product ids that have actually been verified: matching
        /// every Logitech HID++ collection would also match Unifying receivers for mice and
        /// keyboards, where device index 0xFF addresses the receiver rather than the
        /// peripheral and no battery feature answers -- surfacing a phantom "unknown battery"
        /// entry in the tray. Adding a LIGHTSPEED device that speaks this framing is a
        /// one-line change here: its battery feature no longer has to be the same one, but it
        /// does still have to sit on this collection and answer at device index 0xFF.
        /// </summary>
        public static readonly HidDeviceSpec HidSpec;

        /// <summary>
        /// The Centurion interface, for the newer headsets -- a different collection and a
        /// different envelope, but the same feature layer above it, which is why one provider
        /// serves both. See <see cref="CenturionTransport"/>.
        ///
        /// Its own spec because a <see cref="HidDeviceSpec"/> matches one usage page, and its
        /// own product id list because these ids are unverified: they come from Solaar and
        /// HeadsetControl, not from hardware anyone here has. A precise usage page plus an
        /// explicit product id is what keeps that from being a risk -- nothing else on the
        /// machine can match one by accident.
        /// </summary>
        public static readonly HidDeviceSpec CenturionHidSpec;

        /// <summary>
        /// A LIGHTSPEED or Unifying receiver, which is one interface carrying up to six
        /// peripherals rather than being one device itself. It matches the same HID++
        /// collection as <see cref="HidSpec"/> and differs only in carrying an expander --
        /// see <see cref="LogitechReceiverEnumerator"/>, which is where the "a device exists
        /// only because it answered" rule lives.
        ///
        /// Registered *before* HidSpec, because the first matching spec wins and a receiver
        /// must not be taken for a single device.
        ///
        /// Kept to verified receiver ids even though the ping gate now makes a wrong one
        /// harmless. Widening this to every Logitech receiver is defensible for the first
        /// time -- but it would change behaviour for every Logitech user, and nobody here has
        /// a receiver to check it against.
        /// </summary>
        public static readonly HidDeviceSpec ReceiverHidSpec;

            //Static constructor rather than field initializers, the same trap the SteelSeries
            //and Razer providers document: initializers run in declaration order, so anything
            //reading a table further down the file would read it while it was still null and
            //take HidDeviceSpecRegistry's static constructor -- and the app -- down with it.
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
                HIDPP_USAGE_PAGE,
                HIDPP_USAGE,
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
                //Cheap rejections first: this runs against every tracked device, including
                //every Bluetooth one, on every poll.
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
                    //Raw HID access fails for plenty of benign reasons (dongle yanked
                    //mid-transaction, another process holding the collection). No reading.
                Log.Write("Logitech", "read failed on '" + ctx.DeviceName + "': " + e.Message);
                return null;
            }
        }

        /// <summary>
        /// Pick the framing this collection speaks. The usage page decides it and nothing else
        /// does: a device publishes the HID++ collection or the Centurion one, never both, and
        /// the feature layer on top is identical either way.
        /// </summary>
        private static IHidppTransport OpenTransport(HidInterfaceInfo info)
        {
            if (info.UsagePage == CENTURION_USAGE_PAGE)
                return CenturionTransport.Open(info);
            return HidppTransport.Open(info);
        }

        /// <summary>
        /// Find which battery feature this device carries, and bind to the first that actually
        /// produces a value. Only called until one sticks.
        /// </summary>
        private int? Resolve(IHidppTransport hidpp, IBatteryDeviceContext ctx)
        {
                //Probing costs one timeout per feature, so make sure something is listening
                //first -- otherwise a device that is simply switched off would burn the whole
                //chain's worth of timeouts on the UI thread, every poll.
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

        /// <summary>Read and decode one battery feature. Null when it has nothing to report.</summary>
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

                //State of charge not supported: fall back to the discrete level bitfield. This
                //really is a coarse four-way enum, so each level becomes a representative
                //percentage -- a band, not a measurement.
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
        /// Receiver product ids. Not peripheral ids -- one of these covers several products,
        /// which is exactly why the peripheral behind it has to be discovered rather than
        /// looked up. From published device tables, unverified here.
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
