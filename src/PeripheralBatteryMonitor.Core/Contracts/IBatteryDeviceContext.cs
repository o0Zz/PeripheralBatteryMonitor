namespace PeripheralBatteryMonitor.Contracts
{
    public interface IBatteryDeviceContext
    {
        string DeviceId { get; }
        string DeviceName { get; set; }   //settable: the GATT provider refreshes it from the live device
        DeviceTransport Transport { get; }
        bool TryGetProperty(string key, out object value);
    }
}
