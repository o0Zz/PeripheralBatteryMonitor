using System;
using System.Collections.Generic;
using System.Text;
using PeripheralBatteryMonitor.Contracts;
using PeripheralBatteryMonitor.Hid;

namespace PeripheralBatteryMonitor.Providers
{
    /// <summary>
    /// Reads battery for Apple "Magic" devices. These do **not** expose battery through the
    /// Bluetooth property bag, so no other provider sees them; they report it through a vendor
    /// HID input report (id 0x90) whose byte[2] is a plain 0..100 percentage. Verified against
    /// the Linux hid-magicmouse driver and the WinMagicBattery implementation.
    ///
    /// They are still *discovered* over Bluetooth like any other paired device -- only the
    /// reading goes over HID -- so unlike the Logitech provider this one registers no
    /// <see cref="HidDeviceSpec"/>: doing so would list the device twice.
    /// </summary>
    public class AppleBatteryProvider : IBatteryProvider
    {
            //0x004C over Bluetooth, 0x05AC over USB. The device path spells the BT one
            //"vid&0002004c", which is why the enumerator pre-filters on bare hex digits.
        private const ushort APPLE_VID_BT = 0x004C;
        private const ushort APPLE_VID_USB = 0x05AC;

        private static readonly ushort[] appleVendorIds = new ushort[] { APPLE_VID_BT, APPLE_VID_USB };

        private const byte BATTERY_REPORT_ID = 0x90;   //Apple vendor battery input report

            //A GET_REPORT right after the device wakes can fail or answer with stale data.
        private const int READ_ATTEMPTS = 3;

        public int? ReadBattery(IBatteryDeviceContext ctx)
        {
            object addr;
            ctx.TryGetProperty(DeviceProperties.PROP_AEP_DEVICE_ADDRESS, out addr);
            int level;
            return TryGetBatteryLevel(addr == null ? null : addr.ToString(), ctx.DeviceName, out level)
                ? (int?)level : null;
        }

        /* ===================== Apple HID battery read ====================== */

        /// <summary>
        /// Matched by Bluetooth MAC, which Apple HID devices report as their serial number.
        /// The single-device fallback below covers systems where the serial is not the MAC.
        /// </summary>
        private static bool TryGetBatteryLevel(string bluetoothAddress, string deviceName, out int level)
        {
            level = -1;
            try
            {
                    //macNormalized -> level, deduped across the device's HID collections
                Dictionary<string, int> readings = ReadAllAppleHidBatteries();
                if (readings.Count == 0)
                    return false;

                string target = NormalizeMac(bluetoothAddress);
                if (!String.IsNullOrEmpty(target) && readings.TryGetValue(target, out level))
                    return true;

                    //Fallback: a single Apple battery + an Apple-looking device name.
                    //With two Magic devices both produce readings, so this never fires and
                    //the MAC match above does the disambiguation.
                if (readings.Count == 1 && LooksLikeAppleDevice(deviceName))
                {
                    foreach (int only in readings.Values) { level = only; return true; }
                }
            }
            catch
            {
                    //Device asleep, access denied on a claimed collection. No reading.
            }

            level = -1;
            return false;
        }

        private static Dictionary<string, int> ReadAllAppleHidBatteries()
        {
            Dictionary<string, int> result = new Dictionary<string, int>();

            foreach (HidInterfaceInfo info in HidInterfaceEnumerator.Enumerate(appleVendorIds))
                ReadDeviceBattery(info, result);

            return result;
        }

        private static void ReadDeviceBattery(HidInterfaceInfo info, Dictionary<string, int> result)
        {
                //Asks for report 0x90 rather than waiting for one, so the handle must not be
                //overlapped.
            using (HidDevice device = HidDevice.OpenForReportRequests(info))
            {
                if (device == null)
                    return;

                    //Apple reports the MAC as the HID serial number for BT devices.
                string mac = NormalizeMac(device.GetSerialNumber());

                for (int attempt = 0; attempt < READ_ATTEMPTS; attempt++)
                {
                        //report id in byte[0]; on success byte[2] is the 0..100 level.
                    byte[] buffer = new byte[3];
                    buffer[0] = BATTERY_REPORT_ID;

                    if (device.GetInputReport(buffer) && buffer[0] == BATTERY_REPORT_ID)
                    {
                        int level = buffer[2];
                        if (level >= 0 && level <= 100)
                        {
                            result[mac] = level;   //keyed by MAC ("" if unknown) to dedupe collections
                            return;
                        }
                    }
                }
            }
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

        private static bool LooksLikeAppleDevice(string name)
        {
            if (String.IsNullOrEmpty(name))
                return false;
            string n = name.ToLowerInvariant();
            return n.Contains("magic") || n.Contains("apple") || n.Contains("trackpad");
        }
    }
}
