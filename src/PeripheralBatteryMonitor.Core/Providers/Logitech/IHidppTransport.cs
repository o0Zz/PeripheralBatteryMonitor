using System;

namespace PeripheralBatteryMonitor.Providers.Logitech
{
    /// <summary>
    /// One HID++ 2.0 conversation, whatever framing carries it underneath.
    ///
    /// <b><see cref="Request"/> returns a reply normalised to the HID++ long-report layout</b>
    /// -- <c>[0x11][deviceIndex][featureIndex][functionId&lt;&lt;4|swId][payload...]</c> -- whatever
    /// actually went over the wire. A transport whose wire framing differs rebuilds the reply
    /// in that shape rather than making the caller learn which one it got.
    ///
    /// That single rule is what lets one feature layer and one set of battery decoders serve
    /// both framings, and it is what Solaar does too: it unwraps a Centurion frame into a
    /// standard long message and then runs its ordinary HID++ 2.0 machinery on it.
    ///
    /// Internal on purpose. <c>LogitechBatteryProvider</c> is the only consumer and the only
    /// thing outside Core touches is the battery level it returns as an int.
    /// </summary>
    internal interface IHidppTransport : IDisposable
    {
        /// <summary>
        /// Run one transaction. Returns the normalised reply frame -- byte 4 onwards is the
        /// payload -- or null on write failure, timeout or a device-reported error.
        /// </summary>
        byte[] Request(byte deviceIndex, byte featureIndex, byte functionId, byte[] parameters, int timeoutMs);

        /// <summary>Root feature ping: is anything listening at this device index?</summary>
        bool Ping(byte deviceIndex, int timeoutMs);

        /// <summary>This device's index for a feature id, or 0 when it does not implement it.</summary>
        byte GetFeatureIndex(byte deviceIndex, ushort featureId, int timeoutMs);
    }
}
