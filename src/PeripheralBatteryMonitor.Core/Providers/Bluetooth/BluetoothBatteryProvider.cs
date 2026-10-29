using System;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using PeripheralBatteryMonitor.Contracts;
using PeripheralBatteryMonitor.Diagnostics;

namespace PeripheralBatteryMonitor.Providers
{
    /// <summary>
    /// The battery level Windows itself publishes. Covers devices with no GATT battery
    /// service, which is why it backs up <see cref="BluetoothLEBatteryProvider"/>.
    ///
    /// Windows writes that one property in one of two places, and which one is not up to the
    /// device: on the association endpoint's property bag, or on a single PnP node under the
    /// same container -- the Hands-Free profile node for a Classic headset, the BTHLE node for
    /// a BLE one. The bag is read first because it is already cached and kept current by the
    /// watcher; the nodes are enumerated only when it has nothing.
    ///
    /// The bag's value is nulled on disconnect, the node's is not: a headset switched off still
    /// carries its last percentage there. So the node lookup is gated on Windows' own
    /// connection flag, and a null return routinely means "asleep" rather than "unsupported".
    /// </summary>
    public class BluetoothBatteryProvider : IBatteryProvider
    {
        private const int nodeQueryTimeoutMs = 5000;

        private static readonly string[] nodeProperties = { DeviceProperties.PROP_BATTERY_LEVEL };

        private string batteryNodeId = null;

        public int? ReadBattery(IBatteryDeviceContext ctx)
        {
                //An allowlist rather than a denylist, so any future transport is excluded by
                //default.
            if (ctx.Transport != DeviceTransport.BluetoothLowEnergy &&
                ctx.Transport != DeviceTransport.BluetoothClassic)
                return null;

            object val;
            if (ctx.TryGetProperty(DeviceProperties.PROP_BATTERY_LEVEL, out val))
            {
                int? level = AsPercentage(val);
                if (level.HasValue)
                    return level;
            }

            return ReadFromDeviceNodes(ctx);
        }

        private int? ReadFromDeviceNodes(IBatteryDeviceContext ctx)
        {
            object connected;
            if (!ctx.TryGetProperty(DeviceProperties.PROP_AEP_IS_CONNECTED, out connected) || !(connected is bool) || !(bool)connected)
                return null;

            Guid container;
            if (!TryGetContainerId(ctx, out container))
                return null;

            try
            {
                string aqs = DeviceProperties.PROP_CONTAINER_ID + ":=\"" + container.ToString("B") + "\"";
                Task<DeviceInformationCollection> query = DeviceInformation.FindAllAsync(aqs, nodeProperties, DeviceInformationKind.Device).AsTask();
                if (!query.Wait(nodeQueryTimeoutMs))
                    return null;

                foreach (DeviceInformation node in query.Result)
                {
                    object val;
                    if (!node.Properties.TryGetValue(DeviceProperties.PROP_BATTERY_LEVEL, out val))
                        continue;

                    int? level = AsPercentage(val);
                    if (!level.HasValue)
                        continue;

                    if (!String.Equals(node.Id, batteryNodeId, StringComparison.OrdinalIgnoreCase))
                    {
                        batteryNodeId = node.Id;
                        Log.Write("Bluetooth", "battery level published on device node " + node.Id);
                    }
                    return level;
                }
            }
            catch (AggregateException ex)
            {
                Log.Write("Bluetooth", "device node query failed: " + ex.InnerException.Message);
            }
            return null;
        }

        private static bool TryGetContainerId(IBatteryDeviceContext ctx, out Guid containerId)
        {
            containerId = Guid.Empty;

            object value;
            if (!ctx.TryGetProperty(DeviceProperties.PROP_AEP_CONTAINER_ID, out value) || value == null)
                return false;

            if (value is Guid)
                containerId = (Guid)value;
            else if (!Guid.TryParse(value.ToString(), out containerId))
                return false;

            return containerId != Guid.Empty;
        }

        private static int? AsPercentage(object val)
        {
            if (val == null)
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
