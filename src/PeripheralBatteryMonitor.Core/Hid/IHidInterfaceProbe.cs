namespace PeripheralBatteryMonitor.Hid
{
    /// <summary>
    /// Asks one HID interface a question only its vendor's protocol can express, and logs the
    /// answer. Which collections are worth asking is the caller's decision and vendor-neutral;
    /// what to say to one is not, and lives here. A throw is the caller's to swallow.
    /// </summary>
    internal interface IHidInterfaceProbe
    {
        ushort VendorId { get; }

        void Probe(HidInterfaceInfo info);
    }
}
