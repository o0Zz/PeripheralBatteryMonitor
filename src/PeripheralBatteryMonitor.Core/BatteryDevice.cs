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
    /// One tracked device's battery state. A thin state holder: property caching plus the
    /// battery provider bound to this device. Once a provider reads the device successfully
    /// it's remembered (<see cref="boundProvider"/>) and read directly on later polls as a
    /// fast path; if it ever comes up empty we fall back to probing the other providers, so
    /// a transient failure never leaves the device stuck. Implements
    /// <see cref="IBatteryDeviceContext"/> so providers read what they need without touching
    /// these internals.
    /// </summary>
    public class BatteryDevice : IBatteryDeviceContext
    {
        private string              deviceID = "";
        private string              deviceName = "";
        private int                 batteryLevel = -1;
        private DeviceTransport     transport = DeviceTransport.BluetoothLowEnergy;
        private DateTime            lastUpdatedTime;
            //Did the most recent poll actually get a value out of the device? For transports
            //with no OS-level connection state to consult (USB HID), answering the device
            //*is* the liveness test.
        private bool                lastReadSucceeded = false;
        private ConcurrentDictionary<string, object> propertyCache = new ConcurrentDictionary<string, object>();

            //Candidate providers for this device, in priority order. One list per device so a
            //stateful provider (e.g. GATT) can cache its connection. See Providers/.
        private readonly List<IBatteryProvider> providers = BatteryProviderRegistry.CreateProviders();

            //The provider that last read this device (a fast-path hint, not a hard lock).
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
        /// For devices that reach the PC outside Bluetooth and so have no
        /// <see cref="DeviceInformation"/> behind them (a peripheral on its own USB dongle,
        /// found by <see cref="HidDeviceSource"/>). The property bag is seeded by the caller
        /// with whatever the bound provider needs to reach the device again.
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

                //Fast path: the provider that read this device last time is usually still the
                //right one, so read it directly without re-probing everything.
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

                //(Re)resolve: probe providers in priority order and take the first that yields
                //a reading. Falling back like this (instead of staying stuck on a provider
                //that went quiet) preserves the original behaviour of trying every source
                //until one produces a value. A null reading means "can't read this device
                //right now", so it doubles as the capability check.
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

                //Nothing produced a value this tick -> keep the last known level (-1 = never read).
            lastReadSucceeded = false;
            Log.Write("Battery", Describe(null, null));
        }

        /// <summary>
        /// Is the device reachable right now? Three sources, most authoritative first.
        ///
        /// The caller uses this to decide what the tray icon and its tooltip are allowed to
        /// report, so a wrong answer is visible either way: say "connected" for a device in a
        /// drawer and its last reading keeps dragging the icon down, say "disconnected" for a
        /// live one and it vanishes from the tray.
        /// </summary>
        /// <summary>
        /// One line carrying everything worth knowing about this device at this moment. Both
        /// outcomes of a poll go through it, so a log reader can follow one device down the
        /// file and see level, liveness and which provider answered change together.
        ///
        /// <see cref="IsConnected"/> is safe to call here: it is a property-bag read and, at
        /// most, a bound provider's no-I/O link check.
        /// </summary>
        private string Describe(int? reading, IBatteryProvider provider)
        {
            return "'" + deviceName + "'"
                + " transport=" + transport
                + " level=" + (reading.HasValue ? reading.Value.ToString() + "%" : "none (holding " + batteryLevel + ")")
                + " connected=" + IsConnected()
                + " provider=" + (provider != null ? provider.GetType().Name : GetBoundProviderName());
        }

        public bool IsConnected()
        {
                //1. The provider actually BOUND to this device, when it maintains a live link
                //   (only GATT does). It must be the bound one, not the first candidate that
                //   implements the interface: the candidate list holds every registered
                //   provider, so for any BLE device it always found BluetoothLEBatteryProvider
                //   -- whose link is legitimately down when it never opened one. A BLE device
                //   with no GATT battery service, reading its level from the Windows property
                //   bag instead, therefore reported "disconnected" for its entire life.
            IDeviceLinkState link = boundProvider as IDeviceLinkState;
            if (link != null)
                return link.IsLinkUp(this);

                //2. Windows' own answer, published for both Bluetooth transports.
                //   Consulted BEFORE the read test below, because a battery reading outlives
                //   the connection that produced it: Windows nulls PROP_BATTERY_LEVEL on
                //   disconnect and CacheProperties drops nulls, so the last percentage stays
                //   in the bag and BluetoothBatteryProvider keeps handing it back.
            object aepConnected;
            if (propertyCache.TryGetValue(DeviceProperties.PROP_AEP_IS_CONNECTED, out aepConnected) && aepConnected is bool)
                return (bool)aepConnected;

                //3. Nothing authoritative to consult. USB HID is the case that matters here:
                //   there is no OS-level connection state for a device behind its own dongle,
                //   and the dongle stays plugged in while the peripheral is switched off, so
                //   whether it answered the most recent poll IS the liveness test.
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

            //The one implementation. IBatteryDeviceContext.TryGetProperty below is the same
            //lookup seen through the provider-facing contract, and delegates here rather than
            //repeating it.
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
        /// Which provider last produced a reading for this device, for the log line
        /// <see cref="Describe"/> builds. "Which one answered" is the single most useful fact
        /// about a device that reads wrong, and it is otherwise invisible from outside.
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
