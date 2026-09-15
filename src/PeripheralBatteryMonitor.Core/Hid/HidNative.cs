using System;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PeripheralBatteryMonitor.Hid
{
    /// <summary>
    /// Raw Win32 surface for the HID stack. Keep the P/Invoke confined to this file so the
    /// rest of the battery layer stays managed code.
    /// </summary>
    internal static class HidNative
    {
        internal static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        internal const uint DIGCF_PRESENT = 0x02;
        internal const uint DIGCF_DEVICEINTERFACE = 0x10;

        internal const uint GENERIC_READ = 0x80000000;
        internal const uint GENERIC_WRITE = 0x40000000;
        internal const uint FILE_SHARE_READ = 0x00000001;
        internal const uint FILE_SHARE_WRITE = 0x00000002;
        internal const uint OPEN_EXISTING = 3;
        internal const uint FILE_FLAG_OVERLAPPED = 0x40000000;

        internal const int ERROR_IO_PENDING = 997;
        internal const uint WAIT_OBJECT_0 = 0;

        internal const int HIDP_STATUS_SUCCESS = 0x00110000;

            //Every hid.dll string getter fills a caller-supplied buffer of this size: the HID
            //string cap is 126 wchars including the terminator.
        internal const int STRING_BYTES = 254;

        internal static string DecodeString(byte[] buffer)
        {
            string s = Encoding.Unicode.GetString(buffer);
            int nul = s.IndexOf('\0');
            return nul < 0 ? s : s.Substring(0, nul);
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct HIDD_ATTRIBUTES
        {
            public int Size;
            public ushort VendorID;
            public ushort ProductID;
            public ushort VersionNumber;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct OVERLAPPED
        {
            public IntPtr Internal;
            public IntPtr InternalHigh;
            public uint Offset;
            public uint OffsetHigh;
            public IntPtr hEvent;
        }

            //Only the first five fields are consumed; the rest must be declared so the struct
            //has the exact size hid.dll expects.
        [StructLayout(LayoutKind.Sequential)]
        internal struct HIDP_CAPS
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps;
            public ushort NumberInputValueCaps;
            public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps;
            public ushort NumberOutputValueCaps;
            public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps;
            public ushort NumberFeatureValueCaps;
            public ushort NumberFeatureDataIndices;
        }

        [DllImport("hid.dll")]
        internal static extern void HidD_GetHidGuid(out Guid hidGuid);

        [DllImport("hid.dll", SetLastError = true)]
        internal static extern bool HidD_GetAttributes(SafeFileHandle handle, ref HIDD_ATTRIBUTES attributes);

        [DllImport("hid.dll", SetLastError = true)]
        internal static extern bool HidD_GetProductString(SafeFileHandle handle, byte[] buffer, int bufferLength);

        [DllImport("hid.dll", SetLastError = true)]
        internal static extern bool HidD_GetSerialNumberString(SafeFileHandle handle, byte[] buffer, int bufferLength);

        [DllImport("hid.dll", SetLastError = true)]
        internal static extern bool HidD_GetInputReport(SafeFileHandle handle, byte[] reportBuffer, int reportBufferLength);

            //SET_REPORT / GET_REPORT(Feature) on the control pipe. Both IOCTLs behind these are
            //declared FILE_ANY_ACCESS, which is why they work on a handle opened with desired
            //access 0 -- the only way to reach a collection Windows holds exclusively.
        [DllImport("hid.dll", SetLastError = true)]
        internal static extern bool HidD_SetFeature(SafeFileHandle handle, byte[] reportBuffer, int reportBufferLength);

        [DllImport("hid.dll", SetLastError = true)]
        internal static extern bool HidD_GetFeature(SafeFileHandle handle, byte[] reportBuffer, int reportBufferLength);

        [DllImport("hid.dll", SetLastError = true)]
        internal static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr preparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        internal static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        internal static extern int HidP_GetCaps(IntPtr preparsedData, ref HIDP_CAPS caps);

        [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
        internal static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        internal static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

        [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
        internal static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr deviceInterfaceDetailData, int deviceInterfaceDetailDataSize, ref int requiredSize, IntPtr deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        internal static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool WriteFile(SafeFileHandle handle, byte[] buffer, uint bytesToWrite, out uint bytesWritten, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool ReadFile(SafeFileHandle handle, byte[] buffer, uint bytesToRead, out uint bytesRead, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool CancelIo(SafeFileHandle handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool GetOverlappedResult(SafeFileHandle handle, IntPtr overlapped, out uint transferred, bool wait);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        internal static extern IntPtr CreateEvent(IntPtr securityAttributes, bool manualReset, bool initialState, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    }
}
