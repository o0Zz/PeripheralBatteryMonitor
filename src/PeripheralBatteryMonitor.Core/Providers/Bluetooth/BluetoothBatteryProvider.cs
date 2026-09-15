using System;
using PeripheralBatteryMonitor.Contracts;

namespace PeripheralBatteryMonitor.Providers
{
    /// <summary>
    /// The battery level Windows itself publishes, from the association endpoint's property
    /// bag. Covers devices with no GATT battery service, which is why it backs up
    /// <see cref="BluetoothLEBatteryProvider"/>.
    ///
    /// The property is null while a device is disconnected, so a null return here routinely
    /// means "asleep" rather than "unsupported".
    /// </summary>
    public class BluetoothBatteryProvider : IBatteryProvider
    {
        public int? ReadBattery(IBatteryDeviceContext ctx)
        {
                //An allowlist rather than a denylist, so any future transport is excluded by
                //default.
            if (ctx.Transport != DeviceTransport.BluetoothLowEnergy &&
                ctx.Transport != DeviceTransport.BluetoothClassic)
                return null;

            object val;
            if (!ctx.TryGetProperty(DeviceProperties.PROP_BATTERY_LEVEL, out val) || val == null)
                return null;
            try
            {
                int value = Convert.ToInt32(val);
                if (value >= 0 && value <= 100)
                    return value;
            }
            catch { }
            return null;
        }
    }
}
