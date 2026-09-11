using System;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;
using PeripheralBatteryMonitor.Contracts;

namespace PeripheralBatteryMonitor.Providers
{
    /// <summary>
    /// Reads battery over Bluetooth Low Energy, from the **GATT Battery Service 0x180F /
    /// level characteristic 0x2A19**. BLE only -- GATT does not exist on Bluetooth Classic --
    /// and it latches off if the service is absent, so an unsupported device is probed once
    /// per lifetime rather than every poll. Uses 30 s connect / 5 s read timeouts and caches
    /// the connection, so a bound device doesn't reconnect on every poll.
    ///
    /// Note this is not the only provider that serves BLE devices: one that reports no GATT
    /// battery service can still be read by <see cref="BluetoothBatteryProvider"/> if Windows
    /// publishes a level for it.
    /// </summary>
    public class BluetoothLEBatteryProvider : IBatteryProvider, IDeviceLinkState
    {
        private static readonly Guid BATTERY_UUID = Guid.Parse("{0000180F-0000-1000-8000-00805F9B34FB}");
        private static readonly Guid BATTERY_LEVEL_UUID = Guid.Parse("{00002A19-0000-1000-8000-00805F9B34FB}");

        private const int bleConnectionTimeoutMs = 30000;
        private const int bleReadTimeoutMs = 5000;

        private BluetoothLEDevice   bleDev = null;
        private GattCharacteristic  gattCharacteristic = null;
        private bool                supportGattBattery = true;

        public int? ReadBattery(IBatteryDeviceContext ctx)
        {
            if (ctx.Transport != DeviceTransport.BluetoothLowEnergy || !supportGattBattery)
                return null;

            try
            {
                //Definitive + self-healing: connect and confirm the battery characteristic
                //exists, re-establishing the link if it dropped since we bound.
                if (!IsGattConnected())
                    ConnectAndDiscover(ctx);
                if (!IsGattConnected())
                    return null;

                Task<GattReadResult> gattReadTask = gattCharacteristic.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask();
                if (gattReadTask.Wait(bleReadTimeoutMs))
                {
                    if (GattCommunicationStatus.Success.Equals(gattReadTask.Result.Status))
                    {
                        IBuffer buffer = gattReadTask.Result.Value;
                        byte[] data = new byte[buffer.Length];
                        DataReader.FromBuffer(buffer).ReadBytes(data);
                        return data[0];
                    }
                }
                return null;
            }
            catch (ObjectDisposedException)
            {
                ResetConnection();
                return null;
            }
            catch (AggregateException ex) when (IsClosedObjectFailure(ex))
            {
                ResetConnection();
                return null;
            }
        }

        public bool IsLinkUp(IBatteryDeviceContext ctx)
        {
                //Cheap, no I/O -- just the cached connection status.
            return IsGattConnected();
        }

        private void ConnectAndDiscover(IBatteryDeviceContext ctx)
        {
            ResetConnection();

            Task<BluetoothLEDevice> bleTask = BluetoothLEDevice.FromIdAsync(ctx.DeviceId).AsTask();
            if (!bleTask.Wait(bleConnectionTimeoutMs))
                return;

            bleDev = bleTask.Result;
            if (bleDev == null)
                return;

            ctx.DeviceName = bleDev.Name;

            Task<GattDeviceServicesResult> batteryServiceTask = bleDev.GetGattServicesForUuidAsync(BATTERY_UUID, BluetoothCacheMode.Uncached).AsTask();
            if (!batteryServiceTask.Wait(bleReadTimeoutMs))
                return;
            if (!GattCommunicationStatus.Success.Equals(batteryServiceTask.Result.Status))
                return;

            if (batteryServiceTask.Result.Services == null || batteryServiceTask.Result.Services.Count == 0)
            {
                    //GATT 0x180F not present -- skip GATT for the rest of this device's lifetime
                supportGattBattery = false;
                return;
            }

            Task<GattCharacteristicsResult> gattCharacteristicsTask = batteryServiceTask.Result.Services[0].GetCharacteristicsForUuidAsync(BATTERY_LEVEL_UUID, BluetoothCacheMode.Uncached).AsTask();
            if (!gattCharacteristicsTask.Wait(bleReadTimeoutMs))
                return;

            if (GattCommunicationStatus.Success.Equals(gattCharacteristicsTask.Result.Status)
                && gattCharacteristicsTask.Result.Characteristics != null
                && gattCharacteristicsTask.Result.Characteristics.Count > 0)
            {
                gattCharacteristic = gattCharacteristicsTask.Result.Characteristics[0];
            }
        }

        private bool IsGattConnected()
        {
            return ((gattCharacteristic != null) && (bleDev != null) && (bleDev.ConnectionStatus == BluetoothConnectionStatus.Connected));
        }

        private void ResetConnection()
        {
            gattCharacteristic = null;

            if (bleDev != null)
            {
                try { bleDev.Dispose(); }
                catch (ObjectDisposedException) { }
                bleDev = null;
            }
        }

        private static bool IsClosedObjectFailure(AggregateException error)
        {
            AggregateException flattened = error.Flatten();
            if (flattened.InnerExceptions.Count == 0)
                return false;

            foreach (Exception inner in flattened.InnerExceptions)
            {
                if (!(inner is ObjectDisposedException))
                    return false;
            }

            return true;
        }
    }
}
