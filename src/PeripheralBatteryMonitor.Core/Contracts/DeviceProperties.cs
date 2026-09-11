namespace PeripheralBatteryMonitor.Contracts
{
    /// <summary>
    /// Canonical keys into a device's property bag. Centralised in <c>Contracts/</c> so both
    /// the discovery layer (which requests them) and the providers (which read them) depend
    /// on this, not on each other.
    /// </summary>
    public static class DeviceProperties
    {
        /* ---- Keys delivered by WinRT (DeviceInformation.Properties) ---- */

            //DEVPROPKEY string-form: "{guid} pid". A byte 0..100 -- a **percentage**.
            //
            //Windows surfaces this same property under two names, and the property bag carries
            //both spellings side by side: this raw DEVPROPKEY, and the canonical name
            //"System.Devices.BatteryLife" (documented with exactly this formatID and propID 2).
            //They are aliases of one value, not two sources -- so read it once, through this key.
            //There is no coarse Critical/Low/Average/Full enum behind either spelling; a
            //provider that switched on 1..4 here would silently turn a nearly-flat battery
            //(3%) into a healthy-looking one (60%).
            //
            //Both spellings are delivered as null while the device is disconnected.
        public const string PROP_BATTERY_LEVEL = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
        public const string PROP_AEP_IS_CONNECTED = "System.Devices.Aep.IsConnected";
        public const string PROP_AEP_IS_PAIRED = "System.Devices.Aep.IsPaired";
        public const string PROP_AEP_DEVICE_ADDRESS = "System.Devices.Aep.DeviceAddress";
        public const string PROP_AEP_CONTAINER_ID = "System.Devices.Aep.ContainerId";
        public const string PROP_AEP_CATEGORY = "System.Devices.Aep.Category";

        /* ---- Keys this app synthesises for HID-discovered devices ---- */

            //Set by the HID discovery source, not by WinRT. The app-name prefix keeps them from
            //ever colliding with a canonical Windows property name (which are all "System.*").
            //They let a provider reopen the exact interface a device was found on without
            //re-enumerating the HID stack. Purely in-memory -- nothing here is persisted, so
            //renaming a key is safe.
        public const string PROP_HID_PATH = "PeripheralBatteryMonitor.Hid.DevicePath";
        public const string PROP_HID_VENDOR_ID = "PeripheralBatteryMonitor.Hid.VendorId";
        public const string PROP_HID_PRODUCT_ID = "PeripheralBatteryMonitor.Hid.ProductId";
        public const string PROP_HID_USAGE_PAGE = "PeripheralBatteryMonitor.Hid.UsagePage";
        public const string PROP_HID_USAGE = "PeripheralBatteryMonitor.Hid.Usage";
        public const string PROP_HID_INPUT_REPORT_LENGTH = "PeripheralBatteryMonitor.Hid.InputReportByteLength";
        public const string PROP_HID_OUTPUT_REPORT_LENGTH = "PeripheralBatteryMonitor.Hid.OutputReportByteLength";
        public const string PROP_HID_FEATURE_REPORT_LENGTH = "PeripheralBatteryMonitor.Hid.FeatureReportByteLength";

            //Which device on the far end of that interface. Absent means 0xFF, the device
            //behind its own dongle, which is every HID device the app supported before
            //receivers -- so a provider that does not know about this key keeps behaving
            //exactly as it did.
        public const string PROP_HID_DEVICE_INDEX = "PeripheralBatteryMonitor.Hid.DeviceIndex";
    }
}
