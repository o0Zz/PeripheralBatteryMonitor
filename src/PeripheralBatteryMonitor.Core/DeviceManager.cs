using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Windows.Devices.Enumeration;
using PeripheralBatteryMonitor.Contracts;
using PeripheralBatteryMonitor.Hid;

namespace PeripheralBatteryMonitor
{
    /// <summary>
    /// Discovery layer. Runs two <see cref="DeviceWatcher"/> instances in
    /// parallel -- one for BLE, one for Bluetooth Classic / BR-EDR -- and maintains the
    /// live set of paired <see cref="BatteryDevice"/>, notifying the UI via
    /// <see cref="IDeviceNotification"/>.
    ///
    /// Not every battery-powered device is a Bluetooth one: a peripheral on its own vendor
    /// dongle has no association endpoint and no pairing, so the watchers never see it.
    /// Those come from <see cref="HidDeviceSource"/> through <see cref="refreshHidDevices"/>,
    /// which feeds the same dictionary. Both sources are otherwise indistinguishable to the
    /// UI -- a device is a device.
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
        /// Re-snapshot the non-Bluetooth (USB HID) devices and reconcile them into the device
        /// list: newly plugged ones are added, vanished ones removed. There is no watcher to
        /// subscribe to for these, so the caller drives this from the poll tick -- cheap
        /// enough to do every time, and that is what makes unplugging the dongle show up.
        ///
        /// Only touches <see cref="DeviceTransport.UsbHid"/> entries; the Bluetooth ones are
        /// owned by the watchers.
        ///
        /// <paramref name="force"/> is the user asking for this rather than the timer: it
        /// bypasses the caches inside discovery -- a receiver sweep is the one part of this
        /// that is not re-run every tick -- so that Refresh means what it says.
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

                        //Forward fresh property values into the cached BatteryDevice so the property-based
                        //battery strategies see updates without a full re-enumeration.
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
        /// Windows exposes a dual-mode phone as two association endpoints, one per transport,
        /// rolled up under a single AEP container. On iOS the Classic endpoint carries the name
        /// the user chose while the BLE one advertises an opaque local name, so once both have
        /// appeared the Classic name is copied onto the BLE sibling. No device-name pattern and
        /// no Apple-specific value is assumed.
        ///
        /// <b>The container is what establishes that two endpoints are one physical device.</b>
        /// For a paired device it is the real PnP container id -- verified by dumping a paired
        /// device's AEP bag and finding the same GUID on its nodes in the device tree. An
        /// *unpaired* endpoint instead gets one synthesised per protocol and address, so the two
        /// transports of one unpaired device do not share it; that costs nothing here, since
        /// only paired devices are tracked at all.
        ///
        /// The phone category only narrows the scope, and is required on <b>either</b> endpoint
        /// rather than on both: the container already proves same-device, while the BLE endpoint
        /// of a phone is not reliably categorised, and demanding it there is enough on its own to
        /// silently disable the whole reconcile.
        ///
        /// This makes the two entries read alike; it does not merge them. A phone still occupies
        /// two rows and two tooltip lines -- see the note in CLAUDE.md.
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

                //WinRT delivers this as a boxed Guid; the string form is parsed too rather
                //than depending on that.
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
