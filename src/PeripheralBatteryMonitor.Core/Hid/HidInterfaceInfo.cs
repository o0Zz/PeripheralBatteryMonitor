using System;

namespace PeripheralBatteryMonitor.Hid
{
    /// <summary>
    /// One HID interface -- a single top-level collection. A physical device usually publishes
    /// several, so the usage page / usage pair is what identifies the one worth talking to.
    /// </summary>
    public class HidInterfaceInfo
    {
        public string Path;
        public ushort VendorId;
        public ushort ProductId;
        public ushort UsagePage;        //0xFF00-0xFFFF for vendor-defined collections
        public ushort Usage;
        public int InputReportByteLength;
        public int OutputReportByteLength;

            //Report id included, or 0 when the collection declares none. Worth matching on
            //where a vendor protocol rides on a *standard* usage page: the Razer report is
            //91 bytes and nothing else on that device is.
        public int FeatureReportByteLength;

        public string Product;

        /// <summary>
        /// A device path with the two parts that never vary removed, for logging only. Never
        /// use it to open or identify anything -- <see cref="Path"/> is the identity.
        ///
        /// <c>\\?\hid#vid_1b1c&amp;pid_2b00&amp;mi_03&amp;col01#9&amp;1fe59b30&amp;0&amp;0000#{4d1e55b2-…}\kbd</c>
        /// becomes <c>vid_1b1c&amp;pid_2b00&amp;mi_03&amp;col01#9&amp;1fe59b30&amp;0&amp;0000\kbd</c>: the
        /// <c>\\?\hid#</c> prefix is on every path ever written, and the trailing GUID is the HID
        /// class GUID, the same 38 characters on every line of every log. Together they are
        /// nearly half of a path and carry nothing.
        ///
        /// What survives is what varies and is worth reading: <c>mi_</c>/<c>col</c> say which
        /// USB interface and collection, and the instance id is what tells two identical dongles
        /// in two ports apart -- which the app treats as two devices, so a log has to be able to
        /// show it.
        /// </summary>
        public static string ShortPath(string path)
        {
            if (String.IsNullOrEmpty(path))
                return path;

            const string prefix = @"\\?\hid#";
            string shorter = path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? path.Substring(prefix.Length)
                : path;

                //Cut the class GUID but keep whatever follows it: the "\kbd" suffix on a
                //keyboard collection is part of how Windows names that path.
            int guid = shorter.IndexOf("#{", StringComparison.Ordinal);
            if (guid < 0)
                return shorter;

            int end = shorter.IndexOf('}', guid);
            return end < 0 ? shorter.Substring(0, guid)
                           : shorter.Substring(0, guid) + shorter.Substring(end + 1);
        }

            //Bluetooth HID paths open with the service class the HID stack bound to instead of
            //"vid_": HID over BR/EDR (0x1124) or HID over GATT (0x1812), e.g.
            //\\?\hid#{00001812-0000-1000-8000-00805f9b34fb}_dev_vid&02046d_pid&b02a...
        private const string BLUETOOTH_CLASSIC_HID = "{00001124-0000-1000-8000-00805f9b34fb}";
        private const string BLUETOOTH_LE_HID = "{00001812-0000-1000-8000-00805f9b34fb}";

        public static bool IsBluetoothPath(string path)
        {
            if (String.IsNullOrEmpty(path))
                return false;
            return path.IndexOf(BLUETOOTH_CLASSIC_HID, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   path.IndexOf(BLUETOOTH_LE_HID, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// What two top-level collections of the same physical USB interface have in common,
        /// or null when the path does not have that shape. Two collections match when this is
        /// equal; nothing else about them is.
        ///
        /// <c>\\?\hid#vid_046d&amp;pid_c547&amp;mi_02&amp;col02#b&amp;b78eb83&amp;0&amp;0001#{4d1e55b2-…}</c> keys on
        /// <c>vid_046d&amp;pid_c547&amp;mi_02|b&amp;b78eb83&amp;0</c>: the <c>colNN</c> suffix and the last
        /// field of the instance id are the two things that enumerate per collection, and the
        /// <c>mi_</c> index plus the rest of the instance id are what pin it to one interface of
        /// one device in one port -- so two identical dongles never collide.
        ///
        /// For logging and matching only. <see cref="Path"/> stays the identity.
        /// </summary>
        public static string CollectionGroupKey(string path)
        {
            if (String.IsNullOrEmpty(path))
                return null;

            string shorter = ShortPath(path);

            int hash = shorter.IndexOf('#');
            if (hash < 0)
                return null;

            string hardware = shorter.Substring(0, hash);
            string instance = shorter.Substring(hash + 1);

            int col = hardware.LastIndexOf("&col", StringComparison.OrdinalIgnoreCase);
            if (col < 0)
                return null;    //a device publishing a single collection has no sibling to find

            int lastField = instance.LastIndexOf('&');
            if (lastField < 0)
                return null;

            return hardware.Substring(0, col) + "|" + instance.Substring(0, lastField);
        }

            //The diagnostics wire format, so its shape is a contract rather than a debugging
            //convenience -- this line is what someone answering a "my device does not show up"
            //report reads. Both report lengths arriving as 0 is the tell that HidP_GetCaps
            //failed and the usage page above means nothing.
        public override string ToString()
        {
            return string.Format("VID_{0:X4}&PID_{1:X4} UP=0x{2:X4} U=0x{3:X4} in={4} out={5} feat={6} '{7}'",
                VendorId, ProductId, UsagePage, Usage,
                InputReportByteLength, OutputReportByteLength, FeatureReportByteLength, Product);
        }
    }
}
