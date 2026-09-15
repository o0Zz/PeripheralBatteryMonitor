using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Windows.Devices.Enumeration;
using PeripheralBatteryMonitor.Contracts;
using PeripheralBatteryMonitor.Diagnostics;
using PeripheralBatteryMonitor.Providers;

namespace PeripheralBatteryMonitor
{
    /// <summary>
    /// One tracked device's battery state: property caching plus the provider bound to it.
    /// A successful provider is remembered and read directly on later polls; if it ever comes
    /// up empty the others are probed again, so a transient failure never leaves the device
    /// stuck on a source that went quiet.
    /// </summary>
    public class BatteryDevice : IBatteryDeviceContext
    {
        private string              deviceID = "";
        private string              deviceName = "";
        private int                 batteryLevel = -1;
        private DeviceTransport     transport = DeviceTransport.BluetoothLowEnergy;
        private DateTime            lastUpdatedTime;
            //For transports with no OS-level connection state (USB HID), answering at all
            //*is* the liveness test.
        private bool                lastReadSucceeded = false;
        private ConcurrentDictionary<string, object> propertyCache = new ConcurrentDictionary<string, object>();

            //One list per device, so a stateful provider (GATT) can cache its connection.
        private readonly List<IBatteryProvider> providers = BatteryProviderRegistry.CreateProviders();

        private IBatteryProvider boundProvider = null;

        public BatteryDevice(DeviceInformation deviceInfo, DeviceTransport transport)
        {
            this.deviceID = deviceInfo.Id;
            this.deviceName = deviceInfo.Name;
            this.transport = transport;
            CacheProperties(deviceInfo.Properties);
            UpdateBatteryLevel();
        }

        /// <summary>
        /// For devices with no <see cref="DeviceInformation"/> behind them. The caller seeds the
        /// property bag with whatever a provider needs to reach the device again.
        /// </summary>
        public BatteryDevice(string deviceId, string deviceName, DeviceTransport transport, IReadOnlyDictionary<string, object> properties)
        {
            this.deviceID = deviceId;
            this.deviceName = deviceName;
            this.transport = transport;
            if (properties != null)
                CacheProperties(properties);
            UpdateBatteryLevel();
        }

        internal void UpdateProperties(IReadOnlyDictionary<string, object> updated)
        {
            if (updated == null) return;
            CacheProperties(updated);
        }

        private void CacheProperties(IReadOnlyDictionary<string, object> source)
        {
            foreach (KeyValuePair<string, object> kv in source)
            {
                if (kv.Value != null)
                    propertyCache[kv.Key] = kv.Value;
            }
        }

        public void UpdateBatteryLevel()
        {
            lastUpdatedTime = DateTime.Now;

            if (boundProvider != null)
            {
                int? level = boundProvider.ReadBattery(this);
                if (level.HasValue)
                {
                    batteryLevel = level.Value;
                    lastReadSucceeded = true;
                    return;
                }
            }

                //A null reading means "cannot read this device right now", so probing in
                //priority order doubles as the capability check -- and lets a higher-priority
                //provider preempt when it comes online.
            foreach (IBatteryProvider provider in providers)
            {
                if (provider == boundProvider)
                    continue;   //already attempted on the fast path above

                int? level = provider.ReadBattery(this);
                if (level.HasValue)
                {
                    batteryLevel = level.Value;
                    boundProvider = provider;
                    lastReadSucceeded = true;
                    Log.Write("Battery", Describe(level.Value, provider));
                    return;
                }
            }

                //Keep the last known level; -1 means never read.
            lastReadSucceeded = false;
            Log.Write("Battery", Describe(null, null));
        }

        /// <summary>
        /// Both outcomes of a poll go through this, so a log reader can follow one device down
        /// the file and watch level, liveness and which provider answered change together.
        /// <see cref="IsConnected"/> is safe here: a bag read plus, at most, a no-I/O link check.
        /// </summary>
        private string Describe(int? reading, IBatteryProvider provider)
        {
            return "'" + deviceName + "'"
                + " transport=" + transport
                + " level=" + (reading.HasValue ? reading.Value.ToString() + "%" : "none (holding " + batteryLevel + ")")
                + " connected=" + IsConnected()
                + " provider=" + (provider != null ? provider.GetType().Name : GetBoundProviderName());
        }

        /// <summary>
        /// Three sources, most authoritative first. A wrong answer is visible either way: say
        /// "connected" for a device in a drawer and its last reading drags the tray icon down;
        /// say "disconnected" for a live one and it vanishes from the tray.
        /// </summary>
        public bool IsConnected()
        {
                //1. The BOUND provider, when it maintains a live link (only GATT does). It must
                //   be the bound one and not the first candidate implementing the interface: the
                //   candidate list holds every registered provider, so for any BLE device that
                //   always found BluetoothLEBatteryProvider -- whose link is legitimately down
                //   when it never opened one -- and a BLE device reading its level from the
                //   property bag instead reported "disconnected" for its entire life.
            IDeviceLinkState link = boundProvider as IDeviceLinkState;
            if (link != null)
                return link.IsLinkUp(this);

                //2. Windows' own answer, for both Bluetooth transports. BEFORE the read test
                //   below, because a reading outlives the connection that produced it: Windows
                //   nulls PROP_BATTERY_LEVEL on disconnect and CacheProperties drops nulls, so
                //   the last percentage stays in the bag and the provider keeps handing it back.
            object aepConnected;
            if (propertyCache.TryGetValue(DeviceProperties.PROP_AEP_IS_CONNECTED, out aepConnected) && aepConnected is bool)
                return (bool)aepConnected;

                //3. USB HID: no OS-level connection state exists for a device behind a dongle,
                //   and the dongle stays plugged in while the peripheral is off, so whether it
                //   answered the most recent poll IS the liveness test.
            return lastReadSucceeded;
        }

        public int GetBatteryLevel()
        {
            return batteryLevel;
        }

        public string GetName()
        {
            return deviceName;
        }

        internal void UpdateName(string name)
        {
            if (!String.IsNullOrWhiteSpace(name))
                deviceName = name;
        }

        internal bool TryGetProperty(string key, out object value)
        {
            return propertyCache.TryGetValue(key, out value);
        }

        public DeviceTransport GetTransport()
        {
            return transport;
        }

        public DateTime GetLastUpdatedTime()
        {
            return lastUpdatedTime;
        }

        /// <summary>
        /// "Which provider answered" is the most useful fact about a device that reads wrong,
        /// and it is otherwise invisible from outside.
        /// </summary>
        private string GetBoundProviderName()
        {
            IBatteryProvider provider = boundProvider;
            return provider == null ? "(none)" : provider.GetType().Name;
        }

        /* ---- IBatteryDeviceContext (data providers read/write) ---- */

        string IBatteryDeviceContext.DeviceId { get { return deviceID; } }

        string IBatteryDeviceContext.DeviceName
        {
            get { return deviceName; }
            set { deviceName = value; }
        }

        DeviceTransport IBatteryDeviceContext.Transport { get { return transport; } }

        bool IBatteryDeviceContext.TryGetProperty(string key, out object value)
        {
            return TryGetProperty(key, out value);
        }
    }
}
