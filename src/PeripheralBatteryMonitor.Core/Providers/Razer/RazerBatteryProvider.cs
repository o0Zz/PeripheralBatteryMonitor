using System;
using PeripheralBatteryMonitor.Contracts;
using PeripheralBatteryMonitor.Diagnostics;
using PeripheralBatteryMonitor.Hid;

namespace PeripheralBatteryMonitor.Providers.Razer
{
    /// <summary>
    /// Battery for Razer wireless mice, which reach the PC on their own HyperSpeed dongle (or
    /// over Bluetooth for the Orochi V2) and are discovered through <c>HidDeviceSource</c>.
    ///
    /// Unlike every other provider here, the conversation runs on **feature reports** — see
    /// <see cref="RazerReport"/> for the framing and <c>HidDevice.OpenForFeatureReports</c> for
    /// why the handle is opened with no access rights at all.
    ///
    /// <para>
    /// Protocol from the MIT-licensed <c>xzeldon/razer-battery-report</c>; product ids and
    /// transaction ids from its device table. The collection is matched by its **91-byte
    /// feature report** rather than by usage page, which was verified against a real Razer
    /// mouse: on a Basilisk V3 the vendor protocol sits on the consumer-control collection
    /// (page <c>0x000C</c>), not the mouse collection its device table names, and the report
    /// length is the one property that picks it out on both.
    /// </para>
    /// </summary>
    public class RazerBatteryProvider : IBatteryProvider
    {
        private const ushort RAZER_VENDOR_ID = 0x1532;

            //Battery level: command class 0x07 ("power"), command id 0x80, two data bytes.
            //The level comes back in argument byte 1, as 0..255 rather than a percentage.
        private const byte COMMAND_CLASS_POWER = 0x07;
        private const byte COMMAND_ID_BATTERY_LEVEL = 0x80;
        private const byte BATTERY_DATA_SIZE = 0x02;
        private const int BATTERY_ARGUMENT = 1;

        public static readonly HidDeviceSpec HidSpec;

            //Static constructor rather than a field initializer: initializers run in
            //declaration order and Models is declared below, so building the spec inline would
            //read it while still null. Same trap as SteelSeriesBatteryProvider.
        static RazerBatteryProvider()
        {
                //Usage page and usage are left as "any" on purpose. The vendor protocol does not
                //live on a vendor-defined page, and which standard collection carries it differs
                //between models -- so the 91-byte feature report is the whole match, alongside
                //the product id. Nothing else on these devices declares a report that size.
            HidSpec = new HidDeviceSpec(
                RAZER_VENDOR_ID,
                ProductIds(),
                0,
                0,
                RazerReport.WIRE_LENGTH,
                "Razer Wireless Mouse");
        }

        public int? ReadBattery(IBatteryDeviceContext ctx)
        {
                //Cheap rejections first: this runs against every tracked device on every poll,
                //and a Razer transaction costs a 60 ms sleep it would be rude to spend twice.
            if (ctx == null || ctx.Transport != DeviceTransport.UsbHid)
                return null;

            int vendorId;
            if (!ProviderHid.TryGetInt(ctx, DeviceProperties.PROP_HID_VENDOR_ID, out vendorId) ||
                vendorId != RAZER_VENDOR_ID)
                return null;

            int productId;
            if (!ProviderHid.TryGetInt(ctx, DeviceProperties.PROP_HID_PRODUCT_ID, out productId))
                return null;

            RazerModel model = ModelFor((ushort)productId);
            if (model == null)
                return null;

            HidInterfaceInfo info = ProviderHid.DescribeFromProperties(ctx);
            if (info == null || info.FeatureReportByteLength != RazerReport.WIRE_LENGTH)
                return null;

            try
            {
                using (HidDevice hid = HidDevice.OpenForFeatureReports(info))
                {
                    if (hid == null)
                        return null;

                    byte[] arguments = RazerReport.Request(hid, model.TransactionId,
                        COMMAND_CLASS_POWER, COMMAND_ID_BATTERY_LEVEL, BATTERY_DATA_SIZE);

                    if (arguments == null || arguments.Length <= BATTERY_ARGUMENT)
                        return null;

                    return ToPercentage(arguments[BATTERY_ARGUMENT], ctx.DeviceName);
                }
            }
            catch (Exception e)
            {
                    //Raw HID access fails for plenty of benign reasons (dongle yanked
                    //mid-transaction, another process holding the collection). No reading.
                Log.Write("Razer", "read failed on '" + ctx.DeviceName + "': " + e.Message);
                return null;
            }
        }

        /// <summary>
        /// Razer reports the level over the full byte range, not as a percentage: 255 is full.
        /// </summary>
        private static int? ToPercentage(byte raw, string deviceName)
        {
                //A mouse that is switched off or out of range answers 0 rather than not
                //answering. Reporting that as a flat battery would fire the low-battery balloon
                //every poll for a mouse sitting in a drawer, so treat it as "can't read right
                //now" -- which is what a null return means, and it keeps the last real value.
            if (raw == 0)
                return null;

            int percent = (int)Math.Round(raw * 100.0 / 255.0);
            if (percent < 1)
                percent = 1;
            if (percent > 100)
                percent = 100;

            Log.Write("Razer", "'" + deviceName + "' raw " + raw + "/255 -> " + percent + "%");
            return percent;
        }

        /* ============================ model table ============================ */

        private sealed class RazerModel
        {
            public readonly ushort ProductId;
            public readonly string Name;

                //Echoed in every frame and *not* uniform: the V3/V4 generation and the Orochi V2
                //use 0x1F, the V2 generation 0x3F. Send the wrong one and the device stays silent.
            public readonly byte TransactionId;

            public RazerModel(ushort productId, byte transactionId, string name)
            {
                this.ProductId = productId;
                this.TransactionId = transactionId;
                this.Name = name;
            }
        }

        private const byte TXN_NEWER = 0x1F;
        private const byte TXN_V2 = 0x3F;

        /// <summary>
        /// Supported product ids. Each mouse enumerates under a different id wired than
        /// wireless, so both are listed — the wired entry reports a charging mouse rather than
        /// nothing.
        ///
        /// <para>
        /// **Not verified on hardware**, in the sense that no battery-carrying Razer device was
        /// available here; the transport itself was exercised against a wired Basilisk V3. Ids
        /// and transaction ids come from <c>xzeldon/razer-battery-report</c>.
        /// </para>
        /// </summary>
        private static readonly RazerModel[] Models =
        {
            new RazerModel(0x007A, TXN_V2,    "Razer Viper Ultimate (wired)"),
            new RazerModel(0x007B, TXN_V2,    "Razer Viper Ultimate"),
            new RazerModel(0x007C, TXN_V2,    "Razer DeathAdder V2 Pro (wired)"),
            new RazerModel(0x007D, TXN_V2,    "Razer DeathAdder V2 Pro"),
            new RazerModel(0x0094, TXN_NEWER, "Razer Orochi V2 (2.4 GHz)"),
            new RazerModel(0x0095, TXN_NEWER, "Razer Orochi V2 (Bluetooth)"),
            new RazerModel(0x00AA, TXN_NEWER, "Razer Basilisk V3 Pro (wired)"),
            new RazerModel(0x00AB, TXN_NEWER, "Razer Basilisk V3 Pro"),
            new RazerModel(0x00B6, TXN_NEWER, "Razer DeathAdder V3 Pro (wired)"),
            new RazerModel(0x00B7, TXN_NEWER, "Razer DeathAdder V3 Pro"),
            new RazerModel(0x00BE, TXN_NEWER, "Razer DeathAdder V4 Pro (wired)"),
            new RazerModel(0x00BF, TXN_NEWER, "Razer DeathAdder V4 Pro"),
            new RazerModel(0x00C4, TXN_NEWER, "Razer DeathAdder V3 HyperSpeed (wired)"),
            new RazerModel(0x00C5, TXN_NEWER, "Razer DeathAdder V3 HyperSpeed"),
            new RazerModel(0x00CC, TXN_NEWER, "Razer Basilisk V3 Pro 35K (wired)"),
            new RazerModel(0x00CD, TXN_NEWER, "Razer Basilisk V3 Pro 35K"),
            new RazerModel(0x00D6, TXN_NEWER, "Razer Basilisk V3 Pro 35K Phantom Green (wired)"),
            new RazerModel(0x00D7, TXN_NEWER, "Razer Basilisk V3 Pro 35K Phantom Green"),
        };

        private static RazerModel ModelFor(ushort productId)
        {
            foreach (RazerModel model in Models)
            {
                if (model.ProductId == productId)
                    return model;
            }
            return null;
        }

        private static ushort[] ProductIds()
        {
            ushort[] ids = new ushort[Models.Length];
            for (int i = 0; i < Models.Length; i++)
                ids[i] = Models[i].ProductId;
            return ids;
        }
    }
}
