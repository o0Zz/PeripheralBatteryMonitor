using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Windows.Devices.Enumeration;
using PeripheralBatteryMonitor.Contracts;
using PeripheralBatteryMonitor.Hid;

namespace PeripheralBatteryMonitor
{
    /// <summary>
    /// Discovery. Two <see cref="DeviceWatcher"/> instances in parallel -- one for BLE, one
    /// for Bluetooth Classic / BR-EDR -- maintaining the live set of paired devices.
    ///
    /// A peripheral on its own vendor dongle has no association endpoint and no pairing, so the
    /// watchers never see it; those come from <see cref="HidDeviceSource"/> through
    /// <see cref="refreshHidDevices"/> into the same dictionary. Both sources are
    /// indistinguishable to the UI.
    /// </summary>
    public class DeviceManager
    {
        private const string BLE_PROTOCOL_GUID = "{bb7bb05e-5972-42b5-94fc-76eaa7084d49}";
        private const string BREDR_PROTOCOL_GUID = "{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}";

            //AEP category value that marks a phone. See ReconcileSiblingNames.
        private const string PHONE_CATEGORY = "Communication.Phone";

        private static readonly string[] requestedProperties = new string[]
        {
            DeviceProperties.PROP_AEP_DEVICE_ADDRESS,
            DeviceProperties.PROP_AEP_IS_PAIRED,
            DeviceProperties.PROP_AEP_IS_CONNECTED,
            DeviceProperties.PROP_AEP_CONTAINER_ID,
            DeviceProperties.PROP_AEP_CATEGORY,
            DeviceProperties.PROP_BATTERY_LEVEL,
        };

        private ConcurrentDictionary<string, BatteryDevice> deviceDict = new ConcurrentDictionary<string, BatteryDevice>();
        private List<DeviceWatcher> watchers = new List<DeviceWatcher>();
        private IDeviceNotification deviceNotification;
        private bool running = false;
        private bool scanForEver = false;

        public DeviceManager(IDeviceNotification deviceNotification)
        {
            this.deviceNotification = deviceNotification;
        }

        public void scan(bool scanForEver = false)
        {
            if (running == true)
                return; //Scan already in progress ...

            running = true;
            this.scanForEver = scanForEver;

            watchers.Add(CreateWatcher(BLE_PROTOCOL_GUID, DeviceTransport.BluetoothLowEnergy));
            watchers.Add(CreateWatcher(BREDR_PROTOCOL_GUID, DeviceTransport.BluetoothClassic));

            foreach (DeviceWatcher w in watchers)
                w.Start();

                //No HID enumeration here on purpose: refreshHidDevices reports new devices
                //synchronously, and the UI answers OnNewDevice by running a poll pass -- which
                //would re-enter this method mid-scan. The poll tick is the single driver.
        }

        /// <summary>
        /// There is no watcher behind USB HID devices, so the caller drives this from the poll
        /// tick -- which is what makes unplugging a dongle show up. Only touches
        /// <see cref="DeviceTransport.UsbHid"/> entries; the Bluetooth ones belong to the
        /// watchers.
        ///
        /// <paramref name="force"/> is the user asking rather than the timer, and bypasses the
        /// caches inside discovery -- a receiver sweep being the one part not re-run every tick
        /// -- so that Refresh means what it says.
        /// </summary>
        public void refreshHidDevices(bool force = false)
        {
            if (!running)
                return;

            HashSet<string> presentIds = new HashSet<string>();

            foreach (HidDiscoveredDevice found in HidDeviceSource.Discover(force))
            {
                presentIds.Add(found.Id);

                if (deviceDict.ContainsKey(found.Id))
                    continue;

                BatteryDevice device = new BatteryDevice(found.Id, found.Name,
                    DeviceTransport.UsbHid, found.Properties);

                if (deviceDict.TryAdd(found.Id, device))
                    this.deviceNotification.OnNewDevice(device);
            }

            foreach (KeyValuePair<string, BatteryDevice> kv in deviceDict)
            {
                if (kv.Value.GetTransport() != DeviceTransport.UsbHid)
                    continue;
                if (presentIds.Contains(kv.Key))
                    continue;

                RemoveDevice(kv.Key);
            }
        }

        private DeviceWatcher CreateWatcher(string protocolGuid, DeviceTransport transport)
        {
            string aqsFilter = "(System.Devices.Aep.ProtocolId:=\"" + protocolGuid + "\")";
            DeviceWatcher watcher = DeviceInformation.CreateWatcher(aqsFilter, requestedProperties, DeviceInformationKind.AssociationEndpoint);

            watcher.Added += (DeviceWatcher deviceWatcher, DeviceInformation devInfo) =>
            {
                if (String.IsNullOrWhiteSpace(devInfo.Name))
                    return;

                if (!devInfo.Pairing.IsPaired)
                    return;

                if (deviceDict.ContainsKey(devInfo.Id))
                    return;

                BatteryDevice device = new BatteryDevice(devInfo, transport);
                if (deviceDict.TryAdd(devInfo.Id, device))
                {
                    ReconcileSiblingNames(device);
                    this.deviceNotification.OnNewDevice(device);
                }
            };

            watcher.Updated += (DeviceWatcher deviceWatcher, DeviceInformationUpdate devUpdate) =>
            {
                if (devUpdate.Properties != null)
                {
                        //An IsPaired flip to false won't fire Removed, only Updated. Re-check pairing.
                    object isPaired;
                    if (devUpdate.Properties.TryGetValue(DeviceProperties.PROP_AEP_IS_PAIRED, out isPaired)
                        && isPaired is bool
                        && !(bool)isPaired)
                    {
                        RemoveDevice(devUpdate.Id);
                        return;
                    }

                        //So the property-based providers see updates without a re-enumeration.
                    BatteryDevice existing;
                    if (deviceDict.TryGetValue(devUpdate.Id, out existing))
                        existing.UpdateProperties(devUpdate.Properties);
                }
            };

            watcher.Removed += (DeviceWatcher deviceWatcher, DeviceInformationUpdate devUpdate) =>
            {
                RemoveDevice(devUpdate.Id);
            };

            watcher.EnumerationCompleted += (DeviceWatcher deviceWatcher, object arg) =>
            {
                deviceWatcher.Stop();
            };

            watcher.Stopped += (DeviceWatcher deviceWatcher, object arg) =>
            {
                if (running && this.scanForEver)
                    deviceWatcher.Start();
            };

            return watcher;
        }

        /// <summary>
        /// Windows exposes a dual-mode phone as two association endpoints under one AEP
        /// container. On iOS the Classic endpoint carries the name the user chose while the BLE
        /// one advertises an opaque local name, so the Classic name is copied onto the BLE
        /// sibling. No device-name pattern and no Apple-specific value is assumed.
        ///
        /// <b>The container is what proves two endpoints are one physical device</b> -- for a
        /// paired device it is the real PnP container id, verified against the device tree. An
        /// *unpaired* endpoint gets one synthesised per protocol and address instead, which
        /// costs nothing here since only paired devices are tracked.
        ///
        /// The phone category only narrows the scope, and is required on <b>either</b> endpoint
        /// rather than both: the container already proves same-device, and the BLE endpoint of a
        /// phone is not reliably categorised -- demanding it there is enough on its own to
        /// silently disable the whole reconcile.
        ///
        /// This makes the two entries read alike; it does not merge them.
        /// </summary>
        private void ReconcileSiblingNames(BatteryDevice added)
        {
            Guid container;
            if (!TryGetContainerId(added, out container))
                return;

            bool anyPhone = false;
            string classicName = null;
            List<BatteryDevice> lowEnergySiblings = new List<BatteryDevice>();

            foreach (BatteryDevice sibling in deviceDict.Values)
            {
                Guid siblingContainer;
                if (!TryGetContainerId(sibling, out siblingContainer) || siblingContainer != container)
                    continue;

                anyPhone |= IsPhone(sibling);

                if (sibling.GetTransport() == DeviceTransport.BluetoothLowEnergy)
                    lowEnergySiblings.Add(sibling);
                else if (sibling.GetTransport() == DeviceTransport.BluetoothClassic &&
                         classicName == null && !String.IsNullOrWhiteSpace(sibling.GetName()))
                    classicName = sibling.GetName();
            }

            if (!anyPhone || classicName == null)
                return;

            foreach (BatteryDevice lowEnergy in lowEnergySiblings)
                lowEnergy.UpdateName(classicName);
        }

        private static bool TryGetContainerId(BatteryDevice device, out Guid containerId)
        {
            containerId = Guid.Empty;

            object value;
            if (!device.TryGetProperty(DeviceProperties.PROP_AEP_CONTAINER_ID, out value) || value == null)
                return false;

                //WinRT delivers this as a boxed Guid; the string form is parsed too rather than
                //depending on that.
            if (value is Guid)
                containerId = (Guid)value;
            else if (!Guid.TryParse(value.ToString(), out containerId))
                return false;

            return containerId != Guid.Empty;
        }

        private static bool IsPhone(BatteryDevice device)
        {
            object value;
            if (!device.TryGetProperty(DeviceProperties.PROP_AEP_CATEGORY, out value))
                return false;

            string[] categories = value as string[];
            if (categories == null)
                return false;

            foreach (string category in categories)
            {
                if (String.Equals(category, PHONE_CATEGORY, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private void RemoveDevice(string id)
        {
            if (deviceDict.TryRemove(id, out _))
                this.deviceNotification.OnDeviceRemoved(id);
        }

        public void stopScan()
        {
            running = false;
            foreach (DeviceWatcher w in watchers)
            {
                try { w.Stop(); } catch { /* already stopped */ }
            }
            watchers.Clear();
        }

        public ConcurrentDictionary<string, BatteryDevice> getDeviceList()
        {
            return deviceDict;
        }
    }
}
