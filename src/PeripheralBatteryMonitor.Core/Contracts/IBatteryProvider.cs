namespace PeripheralBatteryMonitor.Contracts
{
    public interface IBatteryProvider
    {
        /// <summary>
        /// Battery level 0..100, or null for "cannot read this device right now" -- which
        /// covers both "does not apply to it" and "momentarily unavailable", so this doubles
        /// as the capability check. The caller keeps the previous value on null. May do I/O
        /// and cache whatever it establishes.
        /// </summary>
        int? ReadBattery(IBatteryDeviceContext ctx);
    }
}
