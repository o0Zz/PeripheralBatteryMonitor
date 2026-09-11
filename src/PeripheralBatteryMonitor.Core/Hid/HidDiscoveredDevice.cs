using System.Collections.Generic;

namespace PeripheralBatteryMonitor.Hid
{
    /// <summary>
    /// One device that HID discovery produced.
    ///
    /// Usually one per interface, which is what discovery assumed for as long as every
    /// supported device was a peripheral with its own dongle. A *receiver* breaks that: it is
    /// one HID interface carrying up to six paired peripherals, addressed by HID++ device
    /// index 1..6. So discovery now yields descriptors rather than raw interfaces, and an
    /// interface may produce none, one, or several.
    ///
    /// <see cref="Id"/> stays the bare interface path for a one-to-one device, byte for byte
    /// what it was before receivers existed -- the tray's per-device icons and the low-battery
    /// latch are keyed on it, and there was no reason to churn every existing device's
    /// identity to add a new kind.
    /// </summary>
    public class HidDiscoveredDevice
    {
        public HidInterfaceInfo Interface;
        public string Id;
        public string Name;
        public Dictionary<string, object> Properties;
    }
}
