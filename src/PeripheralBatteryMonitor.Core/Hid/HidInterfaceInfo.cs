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
