namespace PeripheralBatteryMonitor.Hid
{
    /// <summary>
    /// Asks one HID interface a question only its vendor's protocol can express, and logs the
    /// answer.
    ///
    /// Present only for the diagnostic snapshot, and only for collections no
    /// <see cref="HidDeviceSpec"/> claims -- a claimed one is already traced by the read path
    /// on every poll. Deciding *which* collections those are is vendor-neutral and belongs to
    /// the caller; what to say to one is not, and belongs here. Same split, and the same
    /// reason, as <see cref="IHidDeviceExpander"/>: the knowledge lives with the vendor in
    /// <c>Providers/</c> while <c>DiagnosticReport</c> stays free of vendor names.
    ///
    /// An implementation only records. It returns nothing and decides nothing, so a throw is
    /// the caller's to swallow.
    /// </summary>
    internal interface IHidInterfaceProbe
    {
            //Whose collections this understands. The caller has already narrowed the
            //candidates to unclaimed vendor-defined collections, so the vendor id is all that
            //is left to match on -- the same field a spec matches first.
        ushort VendorId { get; }

        void Probe(HidInterfaceInfo info);
    }
}
