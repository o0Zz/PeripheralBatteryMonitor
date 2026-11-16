using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using PeripheralBatteryMonitor.Contracts;
using PeripheralBatteryMonitor.Diagnostics;
using PeripheralBatteryMonitor.Hid;

namespace PeripheralBatteryMonitor.Providers.Sony
{
    /// <summary>
    /// Battery for PlayStation controllers: DualSense and DualSense Edge (PS5), DualShock 4 (PS4).
    ///
    /// Windows publishes no battery level for them, over USB or Bluetooth. The level rides in
    /// the input report the controller streams unasked. Report ids, offsets and the decoding of
    /// the status byte come from Linux <c>hid-playstation.c</c>; facts only, no code.
    ///
    /// Over USB the controller is found by <see cref="HidSpec"/>. Over Bluetooth the watchers
    /// find it like any paired device and only the reading goes over HID -- the Apple shape.
    /// </summary>
    public class SonyBatteryProvider : IBatteryProvider
    {
        private const ushort SONY_VENDOR_ID = 0x054C;
        private static readonly ushort[] sonyVendorIds = { SONY_VENDOR_ID };

        private const ushort GENERIC_DESKTOP_PAGE = 0x0001;
        private const ushort USAGE_GAMEPAD = 0x0005;

            //Over Bluetooth both families start in a short report 0x01 that carries no battery,
            //and switch to the full report once this feature report (calibration) is read --
            //Linux, SDL and Steam all rely on that. The switch lasts until the controller powers
            //off, and a DirectInput game written for report 0x01 can lose input, so it is sent
            //only when no full report arrives on its own.
        private const byte FEATURE_ENABLE_FULL_REPORT = 0x05;

            //Both families stream at about 250 Hz over USB and, once switched, over Bluetooth.
            //This bounds the UI stall when nothing arrives.
        private const int READ_BUDGET_MS = 200;

        public static readonly HidDeviceSpec HidSpec;

            //Static constructor for the same reason as 8BitDo: a field initializer reading
            //Models would run before Models is assigned.
        static SonyBatteryProvider()
        {
            HidSpec = new HidDeviceSpec(SONY_VENDOR_ID, ProductIds(),
                GENERIC_DESKTOP_PAGE, USAGE_GAMEPAD, "PlayStation controller");
        }

            //Logged only when it changes; see AppleBatteryProvider.
        private string lastDiagnosis;

        public int? ReadBattery(IBatteryDeviceContext ctx)
        {
            if (ctx == null)
                return null;

            try
            {
                if (ctx.Transport == DeviceTransport.UsbHid)
                    return ReadUsb(ctx);
                if (ctx.Transport == DeviceTransport.BluetoothClassic)
                    return ReadBluetooth(ctx);
            }
            catch (Exception e)
            {
                Log.Write("Sony", "read failed: " + e.Message);
            }
            return null;
        }

        private static int? ReadUsb(IBatteryDeviceContext ctx)
        {
            int vendorId;
            if (!ProviderHid.TryGetInt(ctx, DeviceProperties.PROP_HID_VENDOR_ID, out vendorId) ||
                vendorId != SONY_VENDOR_ID)
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

            Layout layout = model.UsbLayout;
            byte? status = ReadStatus(info, layout);
            if (!status.HasValue)
            {
                Log.Write("Sony", "no report 0x" + layout.ReportId.ToString("X2") + " within " + READ_BUDGET_MS + " ms");
                return null;
            }
            return Decode(model, layout, status.Value);
        }

        private int? ReadBluetooth(IBatteryDeviceContext ctx)
        {
                //The HID collection of a switched-off controller can outlive its connection, and
                //reading feature 0x05 from it would only wait for nothing.
            object connected;
            if (ctx.TryGetProperty(DeviceProperties.PROP_AEP_IS_CONNECTED, out connected) &&
                connected is bool && !(bool)connected)
                return null;

            Model model;
            HidInterfaceInfo info = FindBluetoothCollection(ctx, out model);
            if (info == null)
                return null;

            Layout layout = model.BluetoothLayout;
            byte? status = ReadStatus(info, layout);
            if (!status.HasValue && EnableFullReport(info))
                status = ReadStatus(info, layout);

            if (!status.HasValue)
            {
                Log.Write("Sony", "no report 0x" + layout.ReportId.ToString("X2") + " within " + READ_BUDGET_MS
                    + " ms, even after feature 0x05; another program may hold the controller exclusively");
                return null;
            }
            return Decode(model, layout, status.Value);
        }

        /* ========================= Bluetooth matching ========================= */

        /// <summary>
        /// Matched by Bluetooth address, which the controller reports as its HID serial number.
        /// A single controller plus a controller-looking device name is the fallback for a
        /// system where the serial is not the address.
        /// </summary>
        private HidInterfaceInfo FindBluetoothCollection(IBatteryDeviceContext ctx, out Model model)
        {
            model = null;

            List<HidInterfaceInfo> pads = new List<HidInterfaceInfo>();
            foreach (HidInterfaceInfo info in HidInterfaceEnumerator.Enumerate(sonyVendorIds))
            {
                if (HidInterfaceInfo.IsBluetoothPath(info.Path) &&
                    info.UsagePage == GENERIC_DESKTOP_PAGE && info.Usage == USAGE_GAMEPAD &&
                    ModelFor(info.ProductId) != null)
                    pads.Add(info);
            }
            if (pads.Count == 0)
                return null;

            object address;
            ctx.TryGetProperty(DeviceProperties.PROP_AEP_DEVICE_ADDRESS, out address);
            string target = NormalizeMac(address == null ? null : address.ToString());

            List<string> serials = new List<string>();
            foreach (HidInterfaceInfo pad in pads)
            {
                string serial = NormalizeMac(HidInterfaceEnumerator.ReadSerialNumber(pad));
                serials.Add(serial);
                if (target.Length > 0 && serial == target)
                {
                    lastDiagnosis = null;
                    model = ModelFor(pad.ProductId);
                    return pad;
                }
            }

            if (!LooksLikeController(ctx.DeviceName))
                return null;

            if (pads.Count == 1)
            {
                lastDiagnosis = null;
                model = ModelFor(pads[0].ProductId);
                return pads[0];
            }

            LogOnChange("no battery: " + pads.Count + " PlayStation controller collection(s) present, none carries address '"
                + target + "' (serials: " + String.Join(", ", serials) + ")");
            return null;
        }

        private void LogOnChange(string diagnosis)
        {
            if (diagnosis == lastDiagnosis)
                return;
            lastDiagnosis = diagnosis;
            Log.Write("Sony", diagnosis);
        }

        private static string NormalizeMac(string value)
        {
            if (String.IsNullOrEmpty(value))
                return "";
            StringBuilder sb = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                if (Uri.IsHexDigit(c))
                    sb.Append(Char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        private static bool LooksLikeController(string name)
        {
            if (String.IsNullOrEmpty(name))
                return false;
            string n = name.ToLowerInvariant();
            return n.Contains("wireless controller") || n.Contains("dualsense") || n.Contains("dualshock");
        }

        /* ============================ report read ============================ */

        private static bool EnableFullReport(HidInterfaceInfo info)
        {
            if (info.FeatureReportByteLength <= 0)
            {
                Log.Write("Sony", "no feature report to switch to the full input report");
                return false;
            }

            using (HidDevice hid = HidDevice.OpenForFeatureReports(info))
            {
                if (hid == null)
                    return false;

                byte[] report = new byte[info.FeatureReportByteLength];
                report[0] = FEATURE_ENABLE_FULL_REPORT;
                bool ok = hid.GetFeature(report);
                Log.Write("Sony", "switch to the full input report (feature 0x05) " + (ok ? "sent" : "failed"));
                return ok;
            }
        }

            //The handle is opened per poll, so its queue starts empty and the first report is
            //current, as for 8BitDo.
        private static byte? ReadStatus(HidInterfaceInfo info, Layout layout)
        {
            if (info.InputReportByteLength <= layout.StatusOffset)
            {
                Log.Write("Sony", "input report is " + info.InputReportByteLength
                    + " bytes, too short for the status byte at " + layout.StatusOffset);
                return null;
            }

            using (HidDevice hid = HidDevice.Open(info))
            {
                if (hid == null)
                    return null;

                Stopwatch budget = Stopwatch.StartNew();
                byte[] report = new byte[hid.InputReportByteLength];
                while (true)
                {
                    int remaining = READ_BUDGET_MS - (int)budget.ElapsedMilliseconds;
                    int read;
                    if (remaining <= 0 || !hid.Read(report, remaining, out read))
                        return null;

                    if (report[0] == layout.ReportId && read > layout.StatusOffset)
                    {
                        Log.WriteHex("Sony", "<-", report, read);
                        return report[layout.StatusOffset];
                    }
                }
            }
        }

        private static int? Decode(Model model, Layout layout, byte status)
        {
            int? level = model.IsDualSense ? DecodeDualSense(status) : DecodeDualShock4(status);
            Log.Write("Sony", "report 0x" + layout.ReportId.ToString("X2") + " status byte 0x" + status.ToString("X2")
                + " level=" + (level.HasValue ? level.Value + "%" : "none (charging fault)"));
            return level;
        }

            //Low nibble: capacity in tenths. High nibble: 0 discharging, 1 charging, 2 full;
            //0xA/0xB mean voltage or temperature out of range and 0xF a charging error, where
            //Linux reports 0% -- a tray alarm over a charger fault would be wrong, so no level.
        private static int? DecodeDualSense(byte status)
        {
            int capacity = status & 0x0F;
            switch (status >> 4)
            {
                case 0x0:
                case 0x1:
                    return Math.Min(capacity * 10 + 5, 100);
                case 0x2:
                    return 100;
                default:
                    return null;
            }
        }

            //Low nibble: capacity in tenths, 10 or more meaning full; 11 is "charged" on cable.
            //Bit 4: cable plugged in, where anything above 11 is a charging fault.
        private static int? DecodeDualShock4(byte status)
        {
            int capacity = status & 0x0F;
            bool cable = (status & 0x10) != 0;

            if (capacity < 10)
                return capacity * 10 + 5;
            if (!cable || capacity <= 11)
                return 100;
            return null;
        }

        /* ============================ model table ============================ */

            //Offsets are raw-wire, report id included. Linux reads the status byte at 52
            //(DualSense) or 29 (DualShock 4) into the input struct, which starts after the report
            //id over USB, after id + 1 header byte for a DualSense on Bluetooth, and after id + 2
            //for a DualShock 4 on Bluetooth.
        private static readonly Layout DualSenseUsb = new Layout(0x01, 53);
        private static readonly Layout DualSenseBluetooth = new Layout(0x31, 54);
        private static readonly Layout DualShock4Usb = new Layout(0x01, 30);
        private static readonly Layout DualShock4Bluetooth = new Layout(0x11, 32);

        private sealed class Layout
        {
            public readonly byte ReportId;
            public readonly int StatusOffset;

            public Layout(byte reportId, int statusOffset)
            {
                this.ReportId = reportId;
                this.StatusOffset = statusOffset;
            }
        }

        private sealed class Model
        {
            public readonly ushort ProductId;
            public readonly bool IsDualSense;

            public Model(ushort productId, bool isDualSense)
            {
                this.ProductId = productId;
                this.IsDualSense = isDualSense;
            }

            public Layout UsbLayout
            {
                get { return IsDualSense ? DualSenseUsb : DualShock4Usb; }
            }

            public Layout BluetoothLayout
            {
                get { return IsDualSense ? DualSenseBluetooth : DualShock4Bluetooth; }
            }
        }

        private static readonly Model[] Models =
        {
            new Model(0x0CE6, true),    //DualSense
            new Model(0x0DF2, true),    //DualSense Edge
            new Model(0x05C4, false),   //DualShock 4, first revision
            new Model(0x09CC, false),   //DualShock 4, second revision
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
