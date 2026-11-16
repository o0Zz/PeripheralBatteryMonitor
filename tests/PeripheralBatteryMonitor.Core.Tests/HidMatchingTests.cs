using PeripheralBatteryMonitor.Hid;
using PeripheralBatteryMonitor.Providers;
using PeripheralBatteryMonitor.Providers.Sony;
using Xunit;

namespace PeripheralBatteryMonitor.Tests
{
    public class HidPathTests
    {
        private const string USB_PATH =
            @"\\?\hid#vid_046d&pid_c547&mi_02&col02#b&b78eb83&0&0001#{4d1e55b2-f16f-11cf-88cb-001111000030}";

        private const string BLUETOOTH_PATH =
            @"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&0002054c_pid&0ce6#9&2c2e7b6f&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";

        [Fact]
        public void ShortPathDropsPrefixAndClassGuid()
        {
            Assert.Equal(@"vid_1b1c&pid_2b00&mi_03&col01#9&1fe59b30&0&0000\kbd",
                HidInterfaceInfo.ShortPath(
                    @"\\?\hid#vid_1b1c&pid_2b00&mi_03&col01#9&1fe59b30&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}\kbd"));
        }

        [Fact]
        public void BluetoothPathIsRecognised()
        {
            Assert.True(HidInterfaceInfo.IsBluetoothPath(BLUETOOTH_PATH));
            Assert.False(HidInterfaceInfo.IsBluetoothPath(USB_PATH));
        }

        [Fact]
        public void CollectionGroupKeyIgnoresCollectionIndex()
        {
            Assert.Equal("vid_046d&pid_c547&mi_02|b&b78eb83&0", HidInterfaceInfo.CollectionGroupKey(USB_PATH));
            Assert.Equal(HidInterfaceInfo.CollectionGroupKey(USB_PATH),
                HidInterfaceInfo.CollectionGroupKey(USB_PATH.Replace("col02#b&b78eb83&0&0001", "col01#b&b78eb83&0&0000")));
        }

        [Fact]
        public void SpecRegistryClaimsDualSenseOnCableOnly()
        {
            HidInterfaceInfo usb = DualSense(@"\\?\hid#vid_054c&pid_0ce6&mi_03#8&1a2b3c4d&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}");
            Assert.Same(SonyBatteryProvider.HidSpec, HidDeviceSpecRegistry.Match(usb));

                //Paired over Bluetooth, the watchers list it; a spec match would list it twice.
            Assert.Null(HidDeviceSpecRegistry.Match(DualSense(BLUETOOTH_PATH)));

            HidInterfaceInfo joystick = DualSense(usb.Path);
            joystick.Usage = 0x0004;
            Assert.False(SonyBatteryProvider.HidSpec.Matches(joystick));
        }

        private static HidInterfaceInfo DualSense(string path)
        {
            return new HidInterfaceInfo
            {
                Path = path,
                VendorId = 0x054C,
                ProductId = 0x0CE6,
                UsagePage = 0x0001,
                Usage = 0x0005,
                InputReportByteLength = 64,
            };
        }
    }
}
