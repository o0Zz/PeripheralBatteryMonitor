namespace PeripheralBatteryMonitor.Contracts
{
    public static class DeviceProperties
    {
        /* ---- Keys delivered by WinRT (DeviceInformation.Properties) ---- */

            //A byte 0..100 -- a percentage. Windows surfaces this same property under two
            //names and the bag carries both side by side: this raw DEVPROPKEY and the
            //canonical "System.Devices.BatteryLife". Aliases of one value, not two sources.
            //There is no coarse Critical/Low/Average/Full enum behind either spelling; a
            //provider that switched on 1..4 here would report a 3% battery as 60%.
            //Both spellings arrive as null while the device is disconnected.
        public const string PROP_BATTERY_LEVEL = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
        public const string PROP_AEP_IS_CONNECTED = "System.Devices.Aep.IsConnected";
        public const string PROP_AEP_IS_PAIRED = "System.Devices.Aep.IsPaired";
        public const string PROP_AEP_DEVICE_ADDRESS = "System.Devices.Aep.DeviceAddress";
        public const string PROP_AEP_CONTAINER_ID = "System.Devices.Aep.ContainerId";
        public const string PROP_AEP_CATEGORY = "System.Devices.Aep.Category";

        /* ---- Keys this app synthesises for HID-discovered devices ---- */

            //The app-name prefix keeps these from ever colliding with a canonical Windows
            //property, which are all "System.*".
        public const string PROP_HID_PATH = "PeripheralBatteryMonitor.Hid.DevicePath";
        public const string PROP_HID_VENDOR_ID = "PeripheralBatteryMonitor.Hid.VendorId";
        public const string PROP_HID_PRODUCT_ID = "PeripheralBatteryMonitor.Hid.ProductId";
        public const string PROP_HID_USAGE_PAGE = "PeripheralBatteryMonitor.Hid.UsagePage";
        public const string PROP_HID_USAGE = "PeripheralBatteryMonitor.Hid.Usage";
        public const string PROP_HID_INPUT_REPORT_LENGTH = "PeripheralBatteryMonitor.Hid.InputReportByteLength";
        public const string PROP_HID_OUTPUT_REPORT_LENGTH = "PeripheralBatteryMonitor.Hid.OutputReportByteLength";
        public const string PROP_HID_FEATURE_REPORT_LENGTH = "PeripheralBatteryMonitor.Hid.FeatureReportByteLength";

            //Absent means 0xFF, the device behind its own dongle, so a provider that does not
            //know about this key keeps behaving exactly as it did before receivers.
        public const string PROP_HID_DEVICE_INDEX = "PeripheralBatteryMonitor.Hid.DeviceIndex";
    }
}
