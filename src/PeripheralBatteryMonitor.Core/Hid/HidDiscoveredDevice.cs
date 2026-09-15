using System.Collections.Generic;

namespace PeripheralBatteryMonitor.Hid
{
    public class HidDiscoveredDevice
    {
        public HidInterfaceInfo Interface;

            //The bare interface path for a one-to-one device, byte for byte what it was before
            //receivers existed: the tray's per-device icons and the low-battery latch are keyed
            //on it, so changing it re-identifies every existing device.
        public string Id;

        public string Name;
        public Dictionary<string, object> Properties;
    }
}
