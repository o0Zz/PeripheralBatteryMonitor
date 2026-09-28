using System;
using System.Diagnostics;
using PeripheralBatteryMonitor.Contracts;
using PeripheralBatteryMonitor.Diagnostics;
using PeripheralBatteryMonitor.Hid;

namespace PeripheralBatteryMonitor.Providers.EightBitDo
{
    /// <summary>
    /// Battery for 8BitDo controllers in **DInput mode**, over their 2.4 GHz dongle or a cable.
    ///
    /// Nothing is asked: the controller streams its input report continuously, and byte 14 of
    /// it carries the battery -- bit 7 charging, bits 0..6 a percentage. Layout and ids come
    /// from SDL's <c>SDL_hidapi_8bitdo.c</c> (zlib) and agree with rigbat's
    /// <c>eightbitdo.rs</c> (MIT); facts only, no code.
    ///
    /// <para>
    /// XInput mode has no battery to find, and those product ids are deliberately absent: the
    /// dongle presents itself to XInput as a wired pad, and the vendor channel on
    /// <c>0xFF7A</c> is answered by the receiver itself, never forwarded to the controller.
    /// Listing one would only be a permanent "unknown battery" entry.
    /// </para>
    /// </summary>
    public class EightBitDoBatteryProvider : IBatteryProvider
    {
        private const ushort EIGHTBITDO_VENDOR_ID = 0x2DC8;

        private const ushort GENERIC_DESKTOP_PAGE = 0x0001;
        private const ushort USAGE_JOYSTICK = 0x0004;
        private const ushort USAGE_GAMEPAD = 0x0005;

            //Offsets are raw-wire, report id included. The DInput collection uses numbered
            //reports, so hidapi keeps byte 0 as well and SDL's data[14] is this byte 14.
        private const int BATTERY_OFFSET = 14;
        private const byte CHARGING_BIT = 0x80;
        private const byte LEVEL_MASK = 0x7F;

            //The two ids SDL treats as carrying the extended layout: 0x01 is what the Ultimate 2
            //Wireless streams, 0x04 what the older models stream once enhanced mode is on. SDL's
            //0x03 means "firmware without enhanced mode" and has no battery byte.
        private const byte REPORT_ID_EXTENDED = 0x01;
        private const byte REPORT_ID_ENHANCED_USB = 0x04;

            //Feature report whose GET switches the SF30 / SN30 / Pro 2 / Pro 3 into the
            //enhanced report. A read with a side effect, which is why only those models get it.
        private const byte FEATURE_ENABLE_ENHANCED = 0x06;

            //Measured on the Ultimate 2 Wireless dongle: first report 9 ms after opening, at
            //1000 Hz. A wired pad streams at 100-200 Hz. This bounds the UI stall when the
            //controller is off and nothing arrives.
        private const int READ_BUDGET_MS = 200;

        public static readonly HidDeviceSpec GamepadHidSpec;
        public static readonly HidDeviceSpec JoystickHidSpec;

            //Static constructor for the same reason as SteelSeries: a field initializer reading
            //Models would run before Models is assigned.
        static EightBitDoBatteryProvider()
        {
            GamepadHidSpec = new HidDeviceSpec(EIGHTBITDO_VENDOR_ID, ProductIds(),
                GENERIC_DESKTOP_PAGE, USAGE_GAMEPAD, "8BitDo controller");
            JoystickHidSpec = new HidDeviceSpec(EIGHTBITDO_VENDOR_ID, ProductIds(),
                GENERIC_DESKTOP_PAGE, USAGE_JOYSTICK, "8BitDo controller");
        }

        public int? ReadBattery(IBatteryDeviceContext ctx)
        {
            if (ctx == null || ctx.Transport != DeviceTransport.UsbHid)
                return null;

            int vendorId;
            if (!ProviderHid.TryGetInt(ctx, DeviceProperties.PROP_HID_VENDOR_ID, out vendorId) ||
                vendorId != EIGHTBITDO_VENDOR_ID)
                return null;

            int productId;
            if (!ProviderHid.TryGetInt(ctx, DeviceProperties.PROP_HID_PRODUCT_ID, out productId))
                return null;

            Model model = ModelFor((ushort)productId);
            if (model == null)
                return null;

            HidInterfaceInfo info = ProviderHid.DescribeFromProperties(ctx);
            if (info == null)
                return null;

                //Firmware v1.02 of the Ultimate 2 Wireless streams 12-byte reports with no
                //battery byte; say so rather than looking like a controller switched off.
            if (info.InputReportByteLength <= BATTERY_OFFSET)
            {
                Log.Write("8BitDo", "input report is " + info.InputReportByteLength
                    + " bytes, too short to carry a battery; update the firmware");
                return null;
            }

            try
            {
                    //Every poll rather than once: a power cycle drops the controller back to its
                    //plain report, and a provider that remembered having enabled it would then
                    //read nothing until the app restarted.
                if (model.NeedsEnhancedMode && !EnableEnhancedMode(info))
                    return null;

                using (HidDevice hid = HidDevice.Open(info))
                {
                    if (hid == null)
                        return null;
                    return ReadStream(hid);
                }
            }
            catch (Exception e)
            {
                Log.Write("8BitDo", "read failed: " + e.Message);
                return null;
            }
        }

        private static bool EnableEnhancedMode(HidInterfaceInfo info)
        {
            if (info.FeatureReportByteLength <= 0)
            {
                Log.Write("8BitDo", "no feature report to enable enhanced mode; firmware too old?");
                return false;
            }

            using (HidDevice hid = HidDevice.OpenForFeatureReports(info))
            {
                if (hid == null)
                    return false;

                byte[] report = new byte[info.FeatureReportByteLength];
                report[0] = FEATURE_ENABLE_ENHANCED;
                if (hid.GetFeature(report))
                    return true;

                Log.Write("8BitDo", "enable enhanced mode (feature 0x06) failed");
                return false;
            }
        }

            //The handle is opened per poll, so its queue starts empty and the first report is
            //current. Held open between polls it would fill with a stale backlog at 1000 Hz.
        private static int? ReadStream(HidDevice hid)
        {
            Stopwatch budget = Stopwatch.StartNew();
            byte[] report = new byte[hid.InputReportByteLength];

            while (true)
            {
                int remaining = READ_BUDGET_MS - (int)budget.ElapsedMilliseconds;
                int read;
                if (remaining <= 0 || !hid.Read(report, remaining, out read))
                {
                    Log.Write("8BitDo", "no battery report within " + READ_BUDGET_MS + " ms; controller off?");
                    return null;
                }

                byte id = report[0];
                if (read <= BATTERY_OFFSET || (id != REPORT_ID_EXTENDED && id != REPORT_ID_ENHANCED_USB))
                    continue;

                byte raw = report[BATTERY_OFFSET];
                int level = raw & LEVEL_MASK;
                if (level > 100)
                {
                    Log.Write("8BitDo", "battery byte 0x" + raw.ToString("X2") + " out of range");
                    return null;
                }

                Log.Write("8BitDo", "report 0x" + id.ToString("X2") + " battery byte 0x" + raw.ToString("X2")
                    + " level=" + level + "% charging=" + ((raw & CHARGING_BIT) != 0));
                return level;
            }
        }

        /* ============================ model table ============================ */

        private sealed class Model
        {
            public readonly ushort ProductId;
            public readonly bool NeedsEnhancedMode;

            public Model(ushort productId, bool needsEnhancedMode)
            {
                this.ProductId = productId;
                this.NeedsEnhancedMode = needsEnhancedMode;
            }
        }

        /// <summary>
        /// DInput product ids as they appear over USB -- dongle or cable. The Bluetooth-only ids
        /// (<c>0x6100</c>, <c>0x6101</c>) are left out: a Bluetooth controller is the watchers'
        /// to find, and <see cref="HidDeviceSpec"/> refuses Bluetooth paths anyway.
        ///
        /// **Only <c>0x6012</c> is verified on hardware** (Ultimate 2 Wireless, dongle, DInput,
        /// 2026-09-28: byte 14 = <c>0x5B</c>, 91%). The rest are SDL's table and as good as its
        /// testing; a wrong reading from one of them is unproven, not a transport bug.
        /// </summary>
        private static readonly Model[] Models =
        {
            new Model(0x6000, true),    //SF30 Pro
            new Model(0x6001, true),    //SN30 Pro
            new Model(0x6003, true),    //Pro 2
            new Model(0x6006, true),    //Pro 2, the id SDL labels "BT" but that also shows wired
            new Model(0x6009, true),    //Pro 3
            new Model(0x6012, false),   //Ultimate 2 Wireless -- streams the battery without asking
            new Model(0x202F, false),   //Ultimate 3
        };

        private static Model ModelFor(ushort productId)
        {
            foreach (Model model in Models)
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
