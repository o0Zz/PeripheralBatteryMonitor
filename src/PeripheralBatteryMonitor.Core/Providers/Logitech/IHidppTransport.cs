using System;

namespace PeripheralBatteryMonitor.Providers.Logitech
{
    /// <summary>
    /// One HID++ 2.0 conversation, whatever framing carries it underneath.
    ///
    /// <b><see cref="Request"/> returns a reply normalised to the HID++ long-report layout</b>
    /// -- <c>[0x11][deviceIndex][featureIndex][functionId&lt;&lt;4|swId][payload...]</c> -- whatever
    /// actually went over the wire. That single rule is what lets one feature layer and one
    /// set of battery decoders serve both framings; Solaar does the same.
    /// </summary>
    internal interface IHidppTransport : IDisposable
    {
        /// <summary>
        /// Normalised reply frame -- byte 4 onwards is the payload -- or null on write
        /// failure, timeout or a device-reported error.
        /// </summary>
        byte[] Request(byte deviceIndex, byte featureIndex, byte functionId, byte[] parameters, int timeoutMs);

        bool Ping(byte deviceIndex, int timeoutMs);

        /// <summary>This device's index for a feature id, or 0 when it does not implement it.</summary>
        byte GetFeatureIndex(byte deviceIndex, ushort featureId, int timeoutMs);
    }
}
