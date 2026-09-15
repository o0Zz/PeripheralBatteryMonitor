namespace PeripheralBatteryMonitor
{
    public interface IDeviceNotification
    {
        void OnNewDevice(BatteryDevice aDevice);
        void OnDeviceRemoved(string deviceId);
    }
}
