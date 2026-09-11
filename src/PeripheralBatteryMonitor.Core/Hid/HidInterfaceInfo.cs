namespace PeripheralBatteryMonitor.Hid
{
    /// <summary>
    /// One HID interface (a single top-level collection) as reported by the OS. A physical
    /// device usually publishes several of these -- e.g. the Logitech PRO X Wireless dongle
    /// exposes a consumer-control collection plus two vendor-defined ones -- so the usage
    /// page / usage pair is what identifies the collection worth talking to.
    /// </summary>
    public class HidInterfaceInfo
    {
        public string Path;             //\\?\hid#vid_046d&pid_0aba&mi_03&col02#... (open with HidDevice)
        public ushort VendorId;
        public ushort ProductId;
        public ushort UsagePage;        //0xFF00-0xFFFF for vendor-defined collections
        public ushort Usage;
        public int InputReportByteLength;
        public int OutputReportByteLength;

            //Length of this collection's feature report, report id included, or 0 when it
            //declares none. Worth matching on: a vendor protocol carried over feature reports
            //has a fixed size, so the length identifies the collection more reliably than the
            //usage page does -- the Razer report is 91 bytes and nothing else on the device is.
        public int FeatureReportByteLength;
        public string Product;          //HID product string; may be empty

        /// <summary>
        /// The diagnostics wire format, so its shape is a contract rather than a debugging
        /// convenience: this one line is what someone answering a "my device does not show
        /// up" report reads, and every field in it is one a spec can reject on. The report
        /// lengths are here because they decide whether a transport can open the collection
        /// at all -- and because both of them arriving as 0 is the tell that
        /// <c>HidP_GetCaps</c> failed and the usage page above is meaningless.
        /// </summary>
        public override string ToString()
        {
            return string.Format("VID_{0:X4}&PID_{1:X4} UP=0x{2:X4} U=0x{3:X4} in={4} out={5} feat={6} '{7}'",
                VendorId, ProductId, UsagePage, Usage,
                InputReportByteLength, OutputReportByteLength, FeatureReportByteLength, Product);
        }
    }
}
