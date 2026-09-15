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
