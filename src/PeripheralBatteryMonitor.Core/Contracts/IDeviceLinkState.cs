namespace PeripheralBatteryMonitor.Contracts
{
    /// <summary>
    /// Optional capability for a provider that maintains a live link (only GATT does).
    /// <see cref="IsLinkUp"/> must not perform I/O -- it reports cached state.
    /// </summary>
    public interface IDeviceLinkState
    {
        bool IsLinkUp(IBatteryDeviceContext ctx);
    }
}
