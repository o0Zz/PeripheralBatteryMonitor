using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PeripheralBatteryMonitor.Diagnostics;

namespace PeripheralBatteryMonitor.Hid
{
    /// <summary>
    /// An open HID interface, in one of three modes -- <see cref="Open"/>,
    /// <see cref="OpenForReportRequests"/> and <see cref="OpenForFeatureReports"/>. The
    /// distinction is not cosmetic: it decides whether the handle can wait for traffic the
    /// device sends on its own, and what access rights it had to ask for to exist at all.
    ///
    /// Open one only for the duration of a transaction. The driver keeps a per-handle queue
    /// of input reports, so a short-lived handle guarantees the first report read is a
    /// response to what was just written rather than something stale.
    /// </summary>
    public class HidDevice : IDisposable
    {
        private SafeFileHandle handle;

            //Whether the handle was opened FILE_FLAG_OVERLAPPED, which is what Read/Write need
            //to be able to time out.
        private readonly bool overlapped;

        public int InputReportByteLength { get; private set; }
        public int OutputReportByteLength { get; private set; }
        public int FeatureReportByteLength { get; private set; }

        private HidDevice(SafeFileHandle handle, HidInterfaceInfo info, bool overlapped)
        {
            this.handle = handle;
            this.InputReportByteLength = info.InputReportByteLength;
            this.OutputReportByteLength = info.OutputReportByteLength;
            this.FeatureReportByteLength = info.FeatureReportByteLength;
            this.overlapped = overlapped;
        }

        /// <summary>
        /// Open for <see cref="Read"/>/<see cref="Write"/>, i.e. for a request/response
        /// conversation where the answer arrives as an input report the device sends.
        ///
        /// Uses FILE_FLAG_OVERLAPPED, because a HID read blocks until the device sends
        /// something and a silent device (headset switched off, dongle asleep) would otherwise
        /// hang the caller forever. Since the poll loop runs on the UI thread, every operation
        /// has to be bounded: the I/O is issued asynchronously, waited on for at most
        /// <c>timeoutMs</c>, then cancelled. Null when the interface can't be opened.
        /// </summary>
        public static HidDevice Open(HidInterfaceInfo info)
        {
            return OpenInternal(info, true, HidNative.GENERIC_READ | HidNative.GENERIC_WRITE);
        }

        /// <summary>
        /// Open for <see cref="GetInputReport"/> only -- asking the device for a report by id,
        /// rather than waiting for one.
        ///
        /// Deliberately *not* overlapped. <c>HidD_GetInputReport</c> issues a synchronous
        /// DeviceIoControl with a NULL OVERLAPPED, which Windows documents as unreliable on a
        /// handle opened FILE_FLAG_OVERLAPPED ("can incorrectly report that the operation is
        /// complete"). There is no timeout to lose by dropping it: this call goes out on the
        /// control pipe and returns, it never waits on device traffic.
        /// Null when the interface can't be opened.
        /// </summary>
        public static HidDevice OpenForReportRequests(HidInterfaceInfo info)
        {
            return OpenInternal(info, false, HidNative.GENERIC_READ | HidNative.GENERIC_WRITE);
        }

        /// <summary>
        /// Open for <see cref="GetFeature"/>/<see cref="SetFeature"/> only -- a vendor protocol
        /// carried on the control pipe rather than in the report streams.
        ///
        /// Opened with desired access **0**, and that is the whole point of this mode rather
        /// than an optimisation. A vendor protocol often lives on a collection Windows opens
        /// exclusively for itself -- Razer's sits behind the mouse and consumer-control
        /// collections -- where CreateFile with GENERIC_READ|GENERIC_WRITE fails outright with
        /// ERROR_ACCESS_DENIED. The IOCTLs behind HidD_GetFeature/HidD_SetFeature are declared
        /// FILE_ANY_ACCESS, so a query-only handle drives them perfectly well; it just cannot
        /// do ReadFile/WriteFile, which this mode does not offer. Not overlapped, for the same
        /// reason <see cref="OpenForReportRequests"/> is not: these calls go out on the control
        /// pipe and return, they never wait on device traffic.
        /// Null when the interface can't be opened.
        /// </summary>
        public static HidDevice OpenForFeatureReports(HidInterfaceInfo info)
        {
            return OpenInternal(info, false, 0);
        }

        private static HidDevice OpenInternal(HidInterfaceInfo info, bool overlapped, uint desiredAccess)
        {
            if (info == null || String.IsNullOrEmpty(info.Path))
                return null;

            SafeFileHandle h = HidNative.CreateFile(info.Path,
                desiredAccess,
                HidNative.FILE_SHARE_READ | HidNative.FILE_SHARE_WRITE,
                IntPtr.Zero, HidNative.OPEN_EXISTING,
                overlapped ? HidNative.FILE_FLAG_OVERLAPPED : 0, IntPtr.Zero);

            if (h.IsInvalid)
            {
                    //Captured before anything else can overwrite the thread's last error.
                int error = Marshal.GetLastWin32Error();
                LogOpenFailureOnce(info.Path, error, desiredAccess);
                h.Dispose();
                return null;
            }

            ForgetOpenFailure(info.Path);
            return new HidDevice(h, info, overlapped);
        }

            //The last error each path failed with, so a handle that keeps failing the same
            //way says so once instead of once per poll tick for as long as the app runs. A
            //*change* is the event worth recording -- including the change to success, which
            //is why the entry is cleared on a good open.
        private static readonly Dictionary<string, int> lastOpenError = new Dictionary<string, int>();

        private static void LogOpenFailureOnce(string path, int error, uint desiredAccess)
        {
            lock (lastOpenError)
            {
                int previous;
                if (lastOpenError.TryGetValue(path, out previous) && previous == error)
                    return;
                lastOpenError[path] = error;
            }

                //Worth naming the code rather than just failing: ERROR_ACCESS_DENIED (5) means
                //another process holds the collection exclusively -- vendor software such as
                //G HUB does this -- while ERROR_FILE_NOT_FOUND (2) means the dongle went away
                //between enumeration and here. The two need opposite advice, and neither is
                //visible from "no battery reading".
            Log.Write("Hid", "open failed (error " + error
                + ", access 0x" + desiredAccess.ToString("X") + ") on " + path);
        }

        private static void ForgetOpenFailure(string path)
        {
            lock (lastOpenError)
            {
                if (lastOpenError.Remove(path))
                    Log.Write("Hid", "open succeeded again on " + path);
            }
        }

        /// <summary>
        /// Send an output report. <paramref name="report"/> must be
        /// <see cref="OutputReportByteLength"/> bytes long with the report id in byte 0 --
        /// the HID class driver rejects anything shorter and trims the padding itself.
        /// </summary>
        public bool Write(byte[] report, int timeoutMs)
        {
            if (!EnsureOverlapped("Write"))
                return false;
            if (report == null || report.Length != OutputReportByteLength)
                return false;

            IntPtr evt = HidNative.CreateEvent(IntPtr.Zero, true, false, null);
            IntPtr overlapped = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(HidNative.OVERLAPPED)));
            try
            {
                HidNative.OVERLAPPED o = new HidNative.OVERLAPPED();
                o.hEvent = evt;
                Marshal.StructureToPtr(o, overlapped, false);

                uint written;
                if (HidNative.WriteFile(handle, report, (uint)report.Length, out written, overlapped))
                    return true;

                if (Marshal.GetLastWin32Error() != HidNative.ERROR_IO_PENDING)
                    return false;

                if (HidNative.WaitForSingleObject(evt, (uint)timeoutMs) != HidNative.WAIT_OBJECT_0)
                {
                    HidNative.CancelIo(handle);
                    return false;
                }

                return HidNative.GetOverlappedResult(handle, overlapped, out _, false);
            }
            finally
            {
                Marshal.FreeHGlobal(overlapped);
                HidNative.CloseHandle(evt);
            }
        }

        /// <summary>
        /// Wait for one input report. Returns false on timeout, which for a HID device is a
        /// normal outcome (nothing to say) rather than an error.
        /// </summary>
        public bool Read(byte[] buffer, int timeoutMs, out int bytesRead)
        {
            bytesRead = 0;
            if (!EnsureOverlapped("Read"))
                return false;
            if (buffer == null || buffer.Length < InputReportByteLength)
                return false;

            IntPtr evt = HidNative.CreateEvent(IntPtr.Zero, true, false, null);
            IntPtr overlapped = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(HidNative.OVERLAPPED)));
            try
            {
                HidNative.OVERLAPPED o = new HidNative.OVERLAPPED();
                o.hEvent = evt;
                Marshal.StructureToPtr(o, overlapped, false);

                uint read;
                if (HidNative.ReadFile(handle, buffer, (uint)InputReportByteLength, out read, overlapped))
                {
                    bytesRead = (int)read;
                    return true;
                }

                if (Marshal.GetLastWin32Error() != HidNative.ERROR_IO_PENDING)
                    return false;

                if (HidNative.WaitForSingleObject(evt, (uint)timeoutMs) != HidNative.WAIT_OBJECT_0)
                {
                    HidNative.CancelIo(handle);
                    return false;
                }

                if (!HidNative.GetOverlappedResult(handle, overlapped, out read, false))
                    return false;

                bytesRead = (int)read;
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(overlapped);
                HidNative.CloseHandle(evt);
            }
        }

        /// <summary>
        /// Ask the device for an input report by id (GET_REPORT on the control pipe) instead
        /// of waiting for it to be sent. <paramref name="report"/> carries the requested id
        /// in byte 0. Unlike <see cref="Read"/> this never blocks on device traffic.
        /// </summary>
        public bool GetInputReport(byte[] report)
        {
            if (report == null || report.Length == 0)
                return false;
            return HidNative.HidD_GetInputReport(handle, report, report.Length);
        }

        /// <summary>
        /// Send a feature report (SET_REPORT on the control pipe). <paramref name="report"/>
        /// must be <see cref="FeatureReportByteLength"/> bytes with the report id in byte 0 --
        /// the class driver validates the length against the report descriptor and fails the
        /// call rather than truncating.
        /// </summary>
        public bool SetFeature(byte[] report)
        {
            if (report == null || report.Length != FeatureReportByteLength)
                return false;
            return HidNative.HidD_SetFeature(handle, report, report.Length);
        }

        /// <summary>
        /// Read a feature report back (GET_REPORT on the control pipe). Same size rule as
        /// <see cref="SetFeature"/>; byte 0 carries the report id being asked for.
        /// </summary>
        public bool GetFeature(byte[] report)
        {
            if (report == null || report.Length != FeatureReportByteLength)
                return false;
            return HidNative.HidD_GetFeature(handle, report, report.Length);
        }

        /// <summary>The HID serial number string, or "" when the device doesn't expose one.</summary>
        public string GetSerialNumber()
        {
            byte[] buffer = new byte[HidNative.STRING_BYTES];
            if (!HidNative.HidD_GetSerialNumberString(handle, buffer, buffer.Length))
                return "";
            return HidNative.DecodeString(buffer);
        }

        /// <summary>
        /// Refuse timed I/O on a non-overlapped handle rather than doing it unbounded. Without
        /// FILE_FLAG_OVERLAPPED, ReadFile never returns ERROR_IO_PENDING, so the wait-then-cancel
        /// dance below cannot happen and the call would block for as long as the device stays
        /// silent -- on the UI thread, that is a hang, not a slow poll.
        /// </summary>
        private bool EnsureOverlapped(string operation)
        {
            if (overlapped)
                return true;
            Log.Write("Hid", operation + " needs a handle from Open(), not OpenForReportRequests()/OpenForFeatureReports()");
            return false;
        }

        public void Dispose()
        {
            if (handle != null)
            {
                handle.Dispose();
                handle = null;
            }
        }
    }
}
