using System;
using System.Collections.Generic;
using PeripheralBatteryMonitor.Contracts;
using PeripheralBatteryMonitor.Hid;

namespace PeripheralBatteryMonitor.Providers
{
    /// <summary>
    /// Shared plumbing for providers that reopen the exact HID interface a device was
    /// discovered on.
    ///
    /// This lives in <c>Providers/</c> rather than in <c>Hid/</c> or <c>Contracts/</c>
    /// because it is the only layer that legitimately depends on both: it turns an
    /// <see cref="IBatteryDeviceContext"/> (Contracts) into a
    /// <see cref="HidInterfaceInfo"/> (Hid).
    /// Putting it in either of those would make one depend on the other and collapse the
    /// separation the two folders exist for.
    /// </summary>
    internal static class ProviderHid
    {
        /// <summary>
        /// Rebuild the interface description <c>HidDeviceSource</c> put in the property bag, so
        /// a provider can open that one collection without walking the HID stack again. Null
        /// when the device did not come from HID discovery, or the bag is incomplete.
        /// </summary>
        internal static HidInterfaceInfo DescribeFromProperties(IBatteryDeviceContext ctx)
        {
            if (ctx == null)
                return null;

            object path;
            if (!ctx.TryGetProperty(DeviceProperties.PROP_HID_PATH, out path) || path == null)
                return null;

            int inputLength, outputLength;
            if (!TryGetInt(ctx, DeviceProperties.PROP_HID_INPUT_REPORT_LENGTH, out inputLength))
                return null;
            if (!TryGetInt(ctx, DeviceProperties.PROP_HID_OUTPUT_REPORT_LENGTH, out outputLength))
                return null;

            HidInterfaceInfo info = new HidInterfaceInfo();
            info.Path = path.ToString();
            info.InputReportByteLength = inputLength;
            info.OutputReportByteLength = outputLength;

                //Optional: a collection with no feature reports simply leaves this 0, which is
                //the same thing the enumerator would have recorded.
            int featureLength;
            if (TryGetInt(ctx, DeviceProperties.PROP_HID_FEATURE_REPORT_LENGTH, out featureLength))
                info.FeatureReportByteLength = featureLength;

                //Identity too, not just the geometry needed to size a buffer. These are all in
                //the bag already, and a provider that serves more than one collection has to
                //know which one it is looking at -- the Logitech one picks its framing from
                //the usage page. It also makes the reconstruction print as the same line the
                //discovery log printed, which is the whole value of that line.
            int number;
            if (TryGetInt(ctx, DeviceProperties.PROP_HID_VENDOR_ID, out number))
                info.VendorId = (ushort)number;
            if (TryGetInt(ctx, DeviceProperties.PROP_HID_PRODUCT_ID, out number))
                info.ProductId = (ushort)number;
            if (TryGetInt(ctx, DeviceProperties.PROP_HID_USAGE_PAGE, out number))
                info.UsagePage = (ushort)number;
            if (TryGetInt(ctx, DeviceProperties.PROP_HID_USAGE, out number))
                info.Usage = (ushort)number;

            return info;
        }

        /// <summary>
        /// Build the descriptor for one device found on one HID interface, property bag and
        /// all.
        ///
        /// This lives here rather than in <c>HidDeviceSource</c> for the reason the class
        /// remarks give: a receiver expander is in <c>Providers/</c> and needs to build the
        /// same thing, and <c>Providers/</c> must not name a type at the project root. It
        /// needs both <c>Contracts/</c> (the property keys) and <c>Hid/</c> (the interface),
        /// which is exactly this file's remit.
        ///
        /// <paramref name="deviceIndex"/> is null for a device that *is* the interface, which
        /// keeps its id byte-identical to what it was before receivers existed -- the tray's
        /// per-device icons and the low-battery latch are keyed on it. A receiver child gets
        /// the index appended, because the path alone no longer identifies it.
        /// </summary>
        internal static HidDiscoveredDevice Describe(HidInterfaceInfo info, string name, byte? deviceIndex)
        {
            HidDiscoveredDevice device = new HidDiscoveredDevice();
            device.Interface = info;
            device.Id = deviceIndex.HasValue
                ? info.Path + "#" + deviceIndex.Value.ToString("X2")
                : info.Path;
            device.Name = name;
            device.Properties = PropertiesFor(info);

            if (deviceIndex.HasValue)
                device.Properties[DeviceProperties.PROP_HID_DEVICE_INDEX] = (int)deviceIndex.Value;

            return device;
        }

        /// <summary>
        /// Seed a device's property bag with everything a provider needs to reopen this exact
        /// interface, so reading a battery never has to re-enumerate the HID stack.
        /// </summary>
        internal static Dictionary<string, object> PropertiesFor(HidInterfaceInfo info)
        {
            Dictionary<string, object> properties = new Dictionary<string, object>();
            properties[DeviceProperties.PROP_HID_PATH] = info.Path;
            properties[DeviceProperties.PROP_HID_VENDOR_ID] = (int)info.VendorId;
            properties[DeviceProperties.PROP_HID_PRODUCT_ID] = (int)info.ProductId;
            properties[DeviceProperties.PROP_HID_USAGE_PAGE] = (int)info.UsagePage;
            properties[DeviceProperties.PROP_HID_USAGE] = (int)info.Usage;
            properties[DeviceProperties.PROP_HID_INPUT_REPORT_LENGTH] = info.InputReportByteLength;
            properties[DeviceProperties.PROP_HID_OUTPUT_REPORT_LENGTH] = info.OutputReportByteLength;
            properties[DeviceProperties.PROP_HID_FEATURE_REPORT_LENGTH] = info.FeatureReportByteLength;
            return properties;
        }

        /// <summary>
        /// Which device on the far end of the interface this context was discovered on. 0xFF
        /// -- the device behind its own dongle -- when the bag says nothing, which is every
        /// HID device the app supported before receivers.
        /// </summary>
        internal static byte DeviceIndexOf(IBatteryDeviceContext ctx, byte fallback)
        {
            int index;
            if (!TryGetInt(ctx, DeviceProperties.PROP_HID_DEVICE_INDEX, out index))
                return fallback;
            return (byte)index;
        }

        /// <summary>
        /// Read a property that should hold a number. The bag is <c>object</c>-typed and its
        /// HID entries are boxed ints, but going through <see cref="Convert"/> keeps a
        /// differently-boxed value from silently reading as absent.
        /// </summary>
        internal static bool TryGetInt(IBatteryDeviceContext ctx, string key, out int value)
        {
            value = 0;
            object raw;
            if (ctx == null || !ctx.TryGetProperty(key, out raw) || raw == null)
                return false;
            try
            {
                value = Convert.ToInt32(raw);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
