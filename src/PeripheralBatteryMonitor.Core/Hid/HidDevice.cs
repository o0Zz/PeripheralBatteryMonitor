using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PeripheralBatteryMonitor.Diagnostics;

namespace PeripheralBatteryMonitor.Hid
{
    /// <summary>
    /// An open HID interface, in one of three modes. The distinction is not cosmetic: it
    /// decides whether the handle can wait for traffic the device sends on its own, and what
    /// access rights it had to ask for to exist at all.
    ///
    /// Open one only for the duration of a transaction. The driver keeps a per-handle queue of
    /// input reports, so a short-lived handle guarantees the first report read answers what was
    /// just written rather than being stale.
    /// </summary>
    public class HidDevice : IDisposable
    {
        private SafeFileHandle handle;
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
        /// Open for <see cref="Read"/>/<see cref="Write"/>, where the answer arrives as an
        /// input report the device sends.
        ///
        /// FILE_FLAG_OVERLAPPED, because a HID read blocks until the device says something and
        /// a silent one (headset off, dongle asleep) would hang the UI thread forever. Each
        /// operation is issued async, waited on, then cancelled.
        /// </summary>
        public static HidDevice Open(HidInterfaceInfo info)
        {
            return OpenInternal(info, true, HidNative.GENERIC_READ | HidNative.GENERIC_WRITE);
        }

        /// <summary>
        /// Open for <see cref="GetInputReport"/> only -- asking for a report by id rather than
        /// waiting for one.
        ///
        /// Deliberately *not* overlapped: HidD_GetInputReport issues a synchronous
        /// DeviceIoControl with a NULL OVERLAPPED, which Windows documents as unreliable on an
        /// overlapped handle ("can incorrectly report that the operation is complete"). There
        /// is no timeout to lose -- the call never waits on device traffic.
        /// </summary>
        public static HidDevice OpenForReportRequests(HidInterfaceInfo info)
        {
            return OpenInternal(info, false, HidNative.GENERIC_READ | HidNative.GENERIC_WRITE);
        }

        /// <summary>
        /// Open for <see cref="GetFeature"/>/<see cref="SetFeature"/> only -- a vendor protocol
        /// on the control pipe rather than in the report streams.
        ///
        /// Desired access **0**, and that is the point of this mode rather than an
        /// optimisation. Such a protocol often sits on a collection Windows opens exclusively
        /// for itself -- Razer's sits on consumer-control -- where CreateFile with
        /// GENERIC_READ|GENERIC_WRITE fails outright with ERROR_ACCESS_DENIED. The IOCTLs
        /// behind HidD_GetFeature/HidD_SetFeature are declared FILE_ANY_ACCESS, so a
        /// query-only handle drives them; it just cannot ReadFile/WriteFile, which this mode
        /// does not offer.
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
                    //Before anything else can overwrite the thread's last error.
                int error = Marshal.GetLastWin32Error();
                LogOpenFailureOnce(info.Path, error, desiredAccess);
                h.Dispose();
                return null;
            }

            ForgetOpenFailure(info.Path);
            return new HidDevice(h, info, overlapped);
        }

            //The last error each path failed with, so a handle that keeps failing the same way
            //says so once instead of once per poll tick for the life of the app. A *change* is
            //the event worth recording, including the change back to success.
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

                //The code matters: ERROR_ACCESS_DENIED (5) means another process holds the
                //collection exclusively (G HUB and friends), ERROR_FILE_NOT_FOUND (2) that the
                //dongle went away between enumeration and here. Opposite advice, and neither is
                //visible from "no battery reading".
            Log.Write("Hid", "open failed (error " + error
                + ", access 0x" + desiredAccess.ToString("X") + ") on " + HidInterfaceInfo.ShortPath(path));
        }

        private static void ForgetOpenFailure(string path)
        {
            lock (lastOpenError)
            {
                if (lastOpenError.Remove(path))
                    Log.Write("Hid", "open succeeded again on " + HidInterfaceInfo.ShortPath(path));
            }
        }

        /// <summary>
        /// Send an output report. Must be exactly <see cref="OutputReportByteLength"/> bytes
        /// with the report id in byte 0 -- the class driver rejects anything shorter and trims
        /// the padding itself.
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
                    CancelAndDrain(handle, overlapped);
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
        /// Wait for one input report. False on timeout, which for a HID device is a normal
        /// outcome -- nothing to say -- rather than an error.
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
                    CancelAndDrain(handle, overlapped);
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
        /// Wait for one input report across several handles at once, returning the index of
        /// the one that spoke, or -1 on timeout.
        ///
        /// A device whose protocol is split across two top-level collections needs this:
        /// Windows gives each collection its own path, so a reply can arrive on either handle
        /// and waiting on them in turn either misses it or multiplies the timeout. A Logitech
        /// receiver is exactly that -- see <c>HidppTransport</c>.
        ///
        /// Reads are issued on every handle, waited on together, and the losers are cancelled,
        /// which is why the handles must not be read concurrently from elsewhere: CancelIo
        /// cancels this thread's I/O on the handle, not this one operation.
        /// </summary>
        public static int ReadAny(HidDevice[] devices, byte[][] buffers, int timeoutMs, out int bytesRead)
        {
            bytesRead = 0;
            if (devices == null || buffers == null || devices.Length != buffers.Length || devices.Length == 0)
                return -1;

            IntPtr[] events = new IntPtr[devices.Length];
            IntPtr[] overlapped = new IntPtr[devices.Length];
            bool[] pending = new bool[devices.Length];
            int winner = -1;

            try
            {
                for (int i = 0; i < devices.Length; i++)
                {
                    HidDevice device = devices[i];
                    if (device == null || !device.overlapped
                        || buffers[i] == null || buffers[i].Length < device.InputReportByteLength)
                        continue;

                    events[i] = HidNative.CreateEvent(IntPtr.Zero, true, false, null);
                    overlapped[i] = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(HidNative.OVERLAPPED)));

                    HidNative.OVERLAPPED o = new HidNative.OVERLAPPED();
                    o.hEvent = events[i];
                    Marshal.StructureToPtr(o, overlapped[i], false);

                    uint read;
                    if (HidNative.ReadFile(device.handle, buffers[i], (uint)device.InputReportByteLength, out read, overlapped[i]))
                    {
                            //Already queued in the driver. Take it and cancel the rest.
                        bytesRead = (int)read;
                        winner = i;
                        break;
                    }

                    if (Marshal.GetLastWin32Error() == HidNative.ERROR_IO_PENDING)
                        pending[i] = true;
                }

                if (winner < 0)
                    winner = WaitForOne(devices, events, overlapped, pending, timeoutMs, out bytesRead);

                return winner;
            }
            finally
            {
                for (int i = 0; i < devices.Length; i++)
                {
                    if (pending[i] && i != winner)
                        CancelAndDrain(devices[i].handle, overlapped[i]);
                    if (overlapped[i] != IntPtr.Zero)
                        Marshal.FreeHGlobal(overlapped[i]);
                    if (events[i] != IntPtr.Zero)
                        HidNative.CloseHandle(events[i]);
                }
            }
        }

        private static int WaitForOne(HidDevice[] devices, IntPtr[] events, IntPtr[] overlapped,
                                      bool[] pending, int timeoutMs, out int bytesRead)
        {
            bytesRead = 0;

                //WaitForMultipleObjects wants the handles packed, so keep the mapping back to
                //the caller's slot -- a handle whose ReadFile failed outright is not waited on.
            List<IntPtr> waitOn = new List<IntPtr>();
            List<int> slotOf = new List<int>();
            for (int i = 0; i < devices.Length; i++)
            {
                if (!pending[i])
                    continue;
                waitOn.Add(events[i]);
                slotOf.Add(i);
            }

            if (waitOn.Count == 0)
                return -1;

            uint signalled = HidNative.WaitForMultipleObjects((uint)waitOn.Count, waitOn.ToArray(), false, (uint)timeoutMs);
            if (signalled < HidNative.WAIT_OBJECT_0 || signalled >= HidNative.WAIT_OBJECT_0 + waitOn.Count)
                return -1;      //timed out, abandoned, or failed; the finally cancels them all

            int slot = slotOf[(int)(signalled - HidNative.WAIT_OBJECT_0)];

            uint read;
            if (!HidNative.GetOverlappedResult(devices[slot].handle, overlapped[slot], out read, false))
                return -1;

            bytesRead = (int)read;
            return slot;
        }

        /// <summary>
        /// Cancel an operation and wait for the driver to be finished with it.
        ///
        /// <b>The wait is the point, not the cancel.</b> CancelIo only *requests* cancellation;
        /// until the operation actually completes the driver still owns the OVERLAPPED and the
        /// buffer, and freeing them underneath it corrupts the heap at a moment that has
        /// nothing to do with the code that caused it. GetOverlappedResult with bWait is what
        /// makes that safe, and it returns as soon as the cancel lands.
        /// </summary>
        private static void CancelAndDrain(SafeFileHandle handle, IntPtr overlapped)
        {
            if (!HidNative.CancelIo(handle))
                return;     //nothing outstanding; the caller may free straight away

            uint transferred;
            HidNative.GetOverlappedResult(handle, overlapped, out transferred, true);
        }

        /// <summary>
        /// GET_REPORT on the control pipe -- byte 0 carries the id being asked for. Unlike
        /// <see cref="Read"/> this never blocks on device traffic.
        /// </summary>
        public bool GetInputReport(byte[] report)
        {
            if (report == null || report.Length == 0)
                return false;
            return HidNative.HidD_GetInputReport(handle, report, report.Length);
        }

        /// <summary>
        /// SET_REPORT on the control pipe. Must be exactly <see cref="FeatureReportByteLength"/>
        /// bytes with the report id in byte 0 -- the class driver validates the length against
        /// the report descriptor and fails the call rather than truncating.
        /// </summary>
        public bool SetFeature(byte[] report)
        {
            if (report == null || report.Length != FeatureReportByteLength)
                return false;
            return HidNative.HidD_SetFeature(handle, report, report.Length);
        }

        /// <summary>GET_REPORT for a feature report. Same size rule as <see cref="SetFeature"/>.</summary>
        public bool GetFeature(byte[] report)
        {
            if (report == null || report.Length != FeatureReportByteLength)
                return false;
            return HidNative.HidD_GetFeature(handle, report, report.Length);
        }

        public string GetSerialNumber()
        {
            byte[] buffer = new byte[HidNative.STRING_BYTES];
            if (!HidNative.HidD_GetSerialNumberString(handle, buffer, buffer.Length))
                return "";
            return HidNative.DecodeString(buffer);
        }

        /// <summary>
        /// Refuse timed I/O on a non-overlapped handle rather than doing it unbounded: without
        /// FILE_FLAG_OVERLAPPED, ReadFile never returns ERROR_IO_PENDING, so the
        /// wait-then-cancel above cannot happen and the call blocks for as long as the device
        /// stays silent. On the UI thread that is a hang, not a slow poll.
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
