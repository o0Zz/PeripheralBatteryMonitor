using System;
using System.Collections.Generic;
using PeripheralBatteryMonitor.Contracts;
using PeripheralBatteryMonitor.Providers.Logitech;
using PeripheralBatteryMonitor.Providers.Razer;
using PeripheralBatteryMonitor.Providers.SteelSeries;

namespace PeripheralBatteryMonitor.Providers
{
    /// <summary>
    /// Ordered registry of battery providers. **Registration order is priority order**: a
    /// device binds to the first provider whose <see cref="IBatteryProvider.ReadBattery"/>
    /// returns a value. Append your own with <see cref="Register"/> before the first device is
    /// created.
    /// </summary>
    public static class BatteryProviderRegistry
    {
        private static readonly List<Func<IBatteryProvider>> factories = new List<Func<IBatteryProvider>>();

        static BatteryProviderRegistry()
        {
                //Each provider rejects the transports it does not serve, so this order only
                //decides who wins where several could answer.
            Register(() => new BluetoothLEBatteryProvider());     //1. GATT 0x180F (BLE only)
            Register(() => new BluetoothBatteryProvider());       //2. Battery level published by Windows
            Register(() => new AppleBatteryProvider());           //3. Apple Magic HID report 0x90
            Register(() => new LogitechBatteryProvider());        //4. Logitech HID++ (USB HID only)
            Register(() => new SteelSeriesBatteryProvider());     //5. SteelSeries Arctis Nova (USB HID only)
            Register(() => new RazerBatteryProvider());           //6. Razer feature reports (USB HID only)
        }

        public static void Register(Func<IBatteryProvider> factory)
        {
            if (factory == null) throw new ArgumentNullException("factory");
            factories.Add(factory);
        }

        public static List<IBatteryProvider> CreateProviders()
        {
            List<IBatteryProvider> list = new List<IBatteryProvider>(factories.Count);
            foreach (Func<IBatteryProvider> factory in factories)
                list.Add(factory());
            return list;
        }
    }
}
