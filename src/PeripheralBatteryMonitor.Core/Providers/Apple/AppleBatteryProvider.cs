using System;
using System.Collections.Generic;
using System.Text;
using PeripheralBatteryMonitor.Contracts;
using PeripheralBatteryMonitor.Diagnostics;
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

        private const ushort VENDOR_DEFINED_USAGE_PAGE = 0xFF00;

            //Logged only when it changes, so a device that stays unreadable says why once
            //rather than every tick. Cleared on a reading, so the next failure says it again.
        private string lastDiagnosis;

        public int? ReadBattery(IBatteryDeviceContext ctx)
        {
            ctx.TryGetProperty(DeviceProperties.PROP_AEP_DEVICE_ADDRESS, out object addr);
            string target = NormalizeMac(addr?.ToString());

            List<CollectionRead> reads;
            try
            {
                reads = ReadAllAppleCollections();
            }
            catch (Exception e)
            {
                LogOnChange("reading Apple HID collections threw: " + e.Message);
                return null;
            }

            if (TryPickLevel(reads, target, ctx.DeviceName, out int level))
            {
                lastDiagnosis = null;
                return level;
            }

            Diagnose(reads, target, ctx.DeviceName);
            return null;
        }

        /* ===================== Apple HID battery read ====================== */

        private sealed class CollectionRead
        {
            public HidInterfaceInfo Info;
            public string Mac;          //Apple reports the Bluetooth MAC as the HID serial number
            public int? Level;
            public string Failure;

            public bool IsVendorDefined
            {
                get { return Info.UsagePage >= VENDOR_DEFINED_USAGE_PAGE; }
            }
        }

        private static List<CollectionRead> ReadAllAppleCollections()
        {
            List<CollectionRead> reads = new List<CollectionRead>();
            foreach (HidInterfaceInfo info in HidInterfaceEnumerator.Enumerate(appleVendorIds))
                reads.Add(ReadCollection(info));
            return reads;
        }

        /// <summary>
        /// The serial comes from a query-only handle, not from the one opened for the read, so
        /// a collection that refuses the read still says whose it is -- which is what
        /// <see cref="Diagnose"/> needs to tell "not this device" from "this device, unreadable".
        /// </summary>
        private static CollectionRead ReadCollection(HidInterfaceInfo info)
        {
            CollectionRead read = new CollectionRead
            {
                Info = info,
                Mac = NormalizeMac(HidInterfaceEnumerator.ReadSerialNumber(info)),
            };

                //Asks for report 0x90 rather than waiting for one, so the handle must not be
                //overlapped.
            using (HidDevice device = HidDevice.OpenForReportRequests(info, out int openError))
            {
                if (device == null)
                {
                    read.Failure = "open error " + openError;
                    return read;
                }

                for (int attempt = 0; attempt < READ_ATTEMPTS; attempt++)
                {
                        //report id in byte[0]; on success byte[2] is the 0..100 level.
                    byte[] buffer = new byte[3];
                    buffer[0] = BATTERY_REPORT_ID;

                    if (!device.GetInputReport(buffer, out int readError))
                        read.Failure = "report 0x90 error " + readError;
                    else if (buffer[0] != BATTERY_REPORT_ID)
                        read.Failure = "answered report 0x" + buffer[0].ToString("X2");
                    else if (buffer[2] > 100)
                        read.Failure = "level " + buffer[2] + " out of range";
                    else
                    {
                        read.Level = buffer[2];
                        read.Failure = null;
                        return read;
                    }
                }
            }
            return read;
        }

        /// <summary>
        /// Matched by Bluetooth MAC. The single-device fallback below covers systems where the
        /// serial is not the MAC.
        /// </summary>
        private static bool TryPickLevel(List<CollectionRead> reads, string target, string deviceName, out int level)
        {
                //MAC ("" if unknown) -> level, deduped across the device's HID collections
            Dictionary<string, int> readings = new Dictionary<string, int>();
            foreach (CollectionRead read in reads)
            {
                if (read.Level.HasValue)
                    readings[read.Mac] = read.Level.Value;
            }

            if (!String.IsNullOrEmpty(target) && readings.TryGetValue(target, out level))
                return true;

                //Fallback: a single Apple battery + an Apple-looking device name.
                //With two Magic devices both produce readings, so this never fires and
                //the MAC match above does the disambiguation.
            if (readings.Count == 1 && LooksLikeAppleDevice(deviceName))
            {
                foreach (int only in readings.Values) { level = only; return true; }
            }

            level = -1;
            return false;
        }

        /// <summary>
        /// Every device without a reading falls through to this provider, so a device that
        /// neither owns an Apple collection nor is named like an Apple one stays silent.
        ///
        /// The case this exists for (issue #9): a HID filter driver -- Boot Camp's
        /// applewirelessmouse.sys -- republishes the mouse with a reduced report descriptor.
        /// The vendor collection carrying 0x90 is then gone, and without this line the log
        /// reads exactly like a device that was never supported.
        /// </summary>
        private void Diagnose(List<CollectionRead> reads, string target, string deviceName)
        {
            List<CollectionRead> own = new List<CollectionRead>();
            if (!String.IsNullOrEmpty(target))
            {
                foreach (CollectionRead read in reads)
                {
                    if (read.Mac == target)
                        own.Add(read);
                }
            }

            if (own.Count == 0)
            {
                if (LooksLikeAppleDevice(deviceName))
                    LogOnChange("no battery: " + reads.Count + " Apple HID collection(s) present, none carries this device's address");
                return;
            }

            int vendorDefined = 0;
            List<string> details = new List<string>();
            foreach (CollectionRead read in own)
            {
                if (read.IsVendorDefined)
                    vendorDefined++;
                details.Add(String.Format("UP=0x{0:X4} U=0x{1:X4} {2}", read.Info.UsagePage, read.Info.Usage, read.Failure));
            }

            string diagnosis = "no battery: " + own.Count + " HID collection(s) carry this device's address, "
                + vendorDefined + " vendor-defined: " + String.Join("; ", details);
            if (vendorDefined == 0)
                diagnosis += " -- report 0x90 lives on a vendor-defined collection; a filter driver on this device's HID stack may have replaced its report descriptor";

            LogOnChange(diagnosis);
        }

        private void LogOnChange(string diagnosis)
        {
            if (diagnosis == lastDiagnosis)
                return;
            lastDiagnosis = diagnosis;
            Log.Write("Apple", diagnosis);
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
