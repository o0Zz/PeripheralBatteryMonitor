using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PeripheralBatteryMonitor.Diagnostics;

namespace PeripheralBatteryMonitor.Hid
{
    /// <summary>
    /// Lists the HID interfaces currently present on the machine.
    ///
    /// Interfaces are opened with a desired access of <b>0</b> (query-only). That is the
    /// documented way to read a HID device's attributes and report descriptor without
    /// asking for I/O rights: Windows opens keyboards and mice exclusively, so asking for
    /// GENERIC_READ|GENERIC_WRITE here would fail on those and can disturb devices we have
    /// no interest in. Actual I/O happens later, per device, through <see cref="HidDevice"/>.
    /// </summary>
    public static class HidInterfaceEnumerator
    {
        /// <summary>
        /// Null or empty <paramref name="vendorIds"/> considers every device, which is slower:
        /// every HID interface on the machine gets opened.
        /// </summary>
        public static List<HidInterfaceInfo> Enumerate(ICollection<ushort> vendorIds)
        {
            List<HidInterfaceInfo> result = new List<HidInterfaceInfo>();

                //Cheap pre-filter on the device path so unrelated HID devices are never opened.
                //USB paths spell the vendor as "vid_046d", Bluetooth ones as "vid&0002004c",
                //so match on the bare hex digits which both forms contain.
            List<string> pathHints = new List<string>();
            if (vendorIds != null)
            {
                foreach (ushort vid in vendorIds)
                    pathHints.Add(vid.ToString("x4", CultureInfo.InvariantCulture));
            }

            Guid hidGuid;
            HidNative.HidD_GetHidGuid(out hidGuid);

            IntPtr deviceInfoSet = HidNative.SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero,
                HidNative.DIGCF_PRESENT | HidNative.DIGCF_DEVICEINTERFACE);
            if (deviceInfoSet == HidNative.INVALID_HANDLE_VALUE)
                return result;

            try
            {
                HidNative.SP_DEVICE_INTERFACE_DATA interfaceData = new HidNative.SP_DEVICE_INTERFACE_DATA();
                interfaceData.cbSize = Marshal.SizeOf(typeof(HidNative.SP_DEVICE_INTERFACE_DATA));

                for (uint index = 0;
                     HidNative.SetupDiEnumDeviceInterfaces(deviceInfoSet, IntPtr.Zero, ref hidGuid, index, ref interfaceData);
                     index++)
                {
                    string path = GetDevicePath(deviceInfoSet, ref interfaceData);
                    if (path == null)
                        continue;

                    if (pathHints.Count > 0 && !MatchesAnyHint(path, pathHints))
                        continue;

                    HidInterfaceInfo info = Describe(path);
                    if (info == null)
                        continue;

                    if (vendorIds != null && vendorIds.Count > 0 && !vendorIds.Contains(info.VendorId))
                        continue;   //path hint matched by coincidence

                    result.Add(info);
                }
            }
            finally
            {
                HidNative.SetupDiDestroyDeviceInfoList(deviceInfoSet);
            }

            return result;
        }

        private static bool MatchesAnyHint(string path, List<string> hints)
        {
            string lower = path.ToLowerInvariant();
            foreach (string hint in hints)
            {
                if (lower.Contains(hint))
                    return true;
            }
            return false;
        }

        private static HidInterfaceInfo Describe(string path)
        {
            using (SafeFileHandle handle = HidNative.CreateFile(path, 0,
                       HidNative.FILE_SHARE_READ | HidNative.FILE_SHARE_WRITE,
                       IntPtr.Zero, HidNative.OPEN_EXISTING, 0, IntPtr.Zero))
            {
                if (handle.IsInvalid)
                {
                    DescribeFailedOnce(path, "CreateFile failed (error " + Marshal.GetLastWin32Error() + ")");
                    return null;
                }

                HidNative.HIDD_ATTRIBUTES attributes = new HidNative.HIDD_ATTRIBUTES();
                attributes.Size = Marshal.SizeOf(typeof(HidNative.HIDD_ATTRIBUTES));
                if (!HidNative.HidD_GetAttributes(handle, ref attributes))
                {
                    DescribeFailedOnce(path, "HidD_GetAttributes failed");
                    return null;
                }

                HidInterfaceInfo info = new HidInterfaceInfo();
                info.Path = path;
                info.VendorId = attributes.VendorID;
                info.ProductId = attributes.ProductID;
                info.Product = ReadString(handle);

                    //Capabilities are best-effort: a caller that needs them (matching on usage
                    //page, sizing a report) checks them, but one that only needs the path and
                    //VID/PID -- asking for a known report id by number -- does not. Dropping the
                    //interface here would hide it from that caller for no reason.
                IntPtr preparsed;
                if (HidNative.HidD_GetPreparsedData(handle, out preparsed))
                {
                    try
                    {
                        HidNative.HIDP_CAPS caps = new HidNative.HIDP_CAPS();
                        if (HidNative.HidP_GetCaps(preparsed, ref caps) == HidNative.HIDP_STATUS_SUCCESS)
                        {
                            info.UsagePage = caps.UsagePage;
                            info.Usage = caps.Usage;
                            info.InputReportByteLength = caps.InputReportByteLength;
                            info.OutputReportByteLength = caps.OutputReportByteLength;
                            info.FeatureReportByteLength = caps.FeatureReportByteLength;
                        }
                        else
                        {
                                //Worth a line of its own: the interface still comes back, but
                                //with usage page and both report lengths left at 0, which makes
                                //every spec that matches on them silently miss it.
                            DescribeFailedOnce(path, "HidP_GetCaps failed, capabilities unknown");
                        }
                    }
                    finally
                    {
                        HidNative.HidD_FreePreparsedData(preparsed);
                    }
                }

                DescribeSucceeded(path);
                return info;
            }
        }

            //What each path last failed with. Enumeration runs on every poll tick, and a
            //collection Windows opens exclusively for itself fails identically for as long as
            //the machine is on -- that is one fact, not 288 events a day. A *different* reason
            //is worth a line, and so is the interface starting to describe properly.
        private static readonly Dictionary<string, string> lastDescribeFailure = new Dictionary<string, string>();

        private static void DescribeFailedOnce(string path, string reason)
        {
            lock (lastDescribeFailure)
            {
                string previous;
                if (lastDescribeFailure.TryGetValue(path, out previous) && previous == reason)
                    return;
                lastDescribeFailure[path] = reason;
            }

            Log.Write("Hid", "describe: " + reason + " on " + path);
        }

        private static void DescribeSucceeded(string path)
        {
            lock (lastDescribeFailure)
            {
                if (lastDescribeFailure.Remove(path))
                    Log.Write("Hid", "describe: succeeding again on " + path);
            }
        }

        private static string ReadString(SafeFileHandle handle)
        {
            byte[] buffer = new byte[HidNative.STRING_BYTES];
            if (!HidNative.HidD_GetProductString(handle, buffer, buffer.Length))
                return "";
            return HidNative.DecodeString(buffer).Trim();
        }

        private static string GetDevicePath(IntPtr deviceInfoSet, ref HidNative.SP_DEVICE_INTERFACE_DATA interfaceData)
        {
            int requiredSize = 0;
            HidNative.SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, IntPtr.Zero, 0, ref requiredSize, IntPtr.Zero);
            if (requiredSize <= 0)
                return null;

            IntPtr detailBuffer = Marshal.AllocHGlobal(requiredSize);
            try
            {
                    //cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA: 8 on 64-bit, 6 on 32-bit (4 + sizeof(WCHAR)).
                Marshal.WriteInt32(detailBuffer, (IntPtr.Size == 8) ? 8 : 6);

                if (!HidNative.SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, detailBuffer, requiredSize, ref requiredSize, IntPtr.Zero))
                    return null;

                    //The DevicePath string starts right after the cbSize DWORD.
                return Marshal.PtrToStringAuto(new IntPtr(detailBuffer.ToInt64() + 4));
            }
            finally
            {
                Marshal.FreeHGlobal(detailBuffer);
            }
        }
    }
}
