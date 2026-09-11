using System;

namespace PeripheralBatteryMonitor.Hid
{
    /// <summary>
    /// Describes the one HID interface that stands for a physical device the app should
    /// track. Needed because a device publishes several collections and because most HID
    /// devices are not battery powered at all -- discovery only surfaces interfaces a
    /// provider has declared it can read (see <c>HidDeviceSpecRegistry</c>).
    ///
    /// Only register a spec for devices that have <b>no</b> Bluetooth association endpoint.
    /// A Bluetooth device (an Apple Magic Mouse, say) is already discovered by the Bluetooth
    /// watchers, and registering it here would surface it a second time.
    /// </summary>
    public class HidDeviceSpec
    {
        public readonly ushort VendorId;
        public readonly ushort[] ProductIds;   //null or empty = any product of this vendor
        public readonly ushort UsagePage;      //0 = any
        public readonly ushort Usage;          //0 = any
        public readonly string FallbackName;   //used when the device exposes no product string

            //Exact feature report length the collection must declare, or 0 for "don't care".
            //
            //Usage page and usage are the obvious way to pick a collection out of the several a
            //device publishes, and they are enough for a vendor-defined page. They are not
            //enough when the protocol rides on a *standard* page: a Razer mouse answers its
            //vendor protocol on the consumer-control collection, and matching page 0x000C
            //usage 0x0001 alone would also catch the volume-key collection sitting right next
            //to it. The 91-byte feature report is what actually identifies it, so allow a spec
            //to say so.
        public readonly int FeatureReportByteLength;

            //Null for the ordinary case: this interface *is* one device. Non-null when the
            //interface is a receiver, which may carry several -- see IHidDeviceExpander. It
            //hangs off the spec rather than off the discovery source so that knowing how to
            //enumerate a vendor's receiver stays with that vendor's provider.
        public readonly IHidDeviceExpander Expander;

        public HidDeviceSpec(ushort vendorId, ushort[] productIds, ushort usagePage, ushort usage, string fallbackName)
            : this(vendorId, productIds, usagePage, usage, 0, fallbackName, null)
        {
        }

        public HidDeviceSpec(ushort vendorId, ushort[] productIds, ushort usagePage, ushort usage,
                             int featureReportByteLength, string fallbackName)
            : this(vendorId, productIds, usagePage, usage, featureReportByteLength, fallbackName, null)
        {
        }

        public HidDeviceSpec(ushort vendorId, ushort[] productIds, ushort usagePage, ushort usage,
                             int featureReportByteLength, string fallbackName, IHidDeviceExpander expander)
        {
            this.VendorId = vendorId;
            this.ProductIds = productIds;
            this.UsagePage = usagePage;
            this.Usage = usage;
            this.FeatureReportByteLength = featureReportByteLength;
            this.FallbackName = fallbackName;
            this.Expander = expander;
        }

        public bool Matches(HidInterfaceInfo info)
        {
            if (info == null)
                return false;
            if (info.VendorId != VendorId)
                return false;
            if (UsagePage != 0 && info.UsagePage != UsagePage)
                return false;
            if (Usage != 0 && info.Usage != Usage)
                return false;
            if (FeatureReportByteLength != 0 && info.FeatureReportByteLength != FeatureReportByteLength)
                return false;

            if (ProductIds != null && ProductIds.Length > 0)
            {
                bool found = false;
                foreach (ushort pid in ProductIds)
                {
                    if (info.ProductId == pid) { found = true; break; }
                }
                if (!found)
                    return false;
            }

            return true;
        }

        /// <summary>Display name for a matched interface: the device's own string, else the fallback.</summary>
        public string NameFor(HidInterfaceInfo info)
        {
            if (info != null && !String.IsNullOrWhiteSpace(info.Product))
                return info.Product.Trim();
            return FallbackName;
        }
    }
}
