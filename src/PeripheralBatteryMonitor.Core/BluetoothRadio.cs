using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Radios;

namespace PeripheralBatteryMonitor
{
    /// <summary>
    /// Turns the machine's Bluetooth radios off and back on, the same thing as the toggle in
    /// Windows Settings. For the one failure polling cannot fix: a wedged stack where every
    /// provider times out until the radio is reset.
    ///
    /// <para>Why <see cref="Radio"/> and not <c>bthserv</c>: stopping the Bluetooth Support
    /// Service needs administrator rights and does not touch the radio, and disabling the device
    /// node needs elevation too. The radio toggle needs neither, which is what keeps this app
    /// non-elevated.</para>
    ///
    /// <para>A restart blocks for the requested downtime, so do not run it on the UI thread --
    /// except <see cref="RequestAccess"/>, which has the opposite requirement.</para>
    /// </summary>
    public static class BluetoothRadio
    {
            //Generous, because the failure this class exists for is a stack that has stopped
            //answering: hanging ten seconds beats declaring failure over a slow driver that
            //would have come back.
        private const int radioCallTimeoutMs = 10000;
        private const int accessRequestTimeoutMs = 10000;

        /// <summary>
        /// <b>Call this on the UI thread</b>, before handing the rest to a worker: it is the
        /// one call here that can show a consent prompt, and a WinRT call that may put UI on
        /// screen wants a thread with a message loop.
        /// </summary>
        public static void RequestAccess()
        {
            Task<RadioAccessStatus> access = Radio.RequestAccessAsync().AsTask();
            if (!access.Wait(accessRequestTimeoutMs))
                throw new TimeoutException("Windows did not answer the request for radio access.");

            if (access.Result != RadioAccessStatus.Allowed)
                throw new InvalidOperationException("Windows denied access to the radios (" + access.Result + ").");
        }

        /// <summary>
        /// So the caller can leave the menu entry out rather than offer something that can only
        /// fail. Enumerating radios is a WinRT call, so decide it once, not on menu open.
        /// </summary>
        public static bool IsAvailable()
        {
            try
            {
                return GetBluetoothRadios().Count > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Blocks for at least <paramref name="downtimeMs"/>, and throws if any step fails --
        /// including on the way back up, which is the failure worth reporting: the radio is then
        /// left off.
        /// </summary>
        public static void Restart(int downtimeMs)
        {
            IList<Radio> radios = GetBluetoothRadios();
            if (radios.Count == 0)
                throw new InvalidOperationException("No Bluetooth radio found on this machine.");

            SetState(radios, RadioState.Off);

            Thread.Sleep(downtimeMs);

                //Re-enumerate rather than reuse the handles above: a radio that went off can
                //come back as a different Radio instance, and the stale one refuses the state
                //change. Name is the only identity this type exposes, so that is what the two
                //lists are matched on.
            SetState(Rebind(radios), RadioState.On);
        }

        private static IList<Radio> GetBluetoothRadios()
        {
            Task<IReadOnlyList<Radio>> radiosTask = Radio.GetRadiosAsync().AsTask();
            if (!radiosTask.Wait(radioCallTimeoutMs))
                throw new TimeoutException("Windows did not answer the request for the radio list.");

            List<Radio> bluetooth = new List<Radio>();
            foreach (Radio radio in radiosTask.Result)
            {
                if (radio.Kind == RadioKind.Bluetooth)
                    bluetooth.Add(radio);
            }
            return bluetooth;
        }

        private static IList<Radio> Rebind(IList<Radio> radios)
        {
            IList<Radio> current;
            try
            {
                current = GetBluetoothRadios();
            }
            catch (Exception)
            {
                return radios;
            }

            List<Radio> rebound = new List<Radio>();
            foreach (Radio was in radios)
            {
                Radio match = was;
                foreach (Radio now in current)
                {
                    if (String.Equals(now.Name, was.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        match = now;
                        break;
                    }
                }
                rebound.Add(match);
            }
            return rebound;
        }

        private static void SetState(IList<Radio> radios, RadioState state)
        {
            foreach (Radio radio in radios)
            {
                Task<RadioAccessStatus> stateTask = radio.SetStateAsync(state).AsTask();
                if (!stateTask.Wait(radioCallTimeoutMs))
                    throw new TimeoutException("The Bluetooth radio did not answer a request to switch " + (state == RadioState.On ? "on" : "off") + ".");

                if (stateTask.Result != RadioAccessStatus.Allowed)
                    throw new InvalidOperationException("Could not switch the Bluetooth radio " + (state == RadioState.On ? "on" : "off") + " (" + stateTask.Result + ").");
            }
        }
    }
}
