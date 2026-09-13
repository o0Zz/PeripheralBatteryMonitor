using Microsoft.Win32;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using PeripheralBatteryMonitor.Diagnostics;

namespace PeripheralBatteryMonitor
{
    public partial class Settings : Form
    {
        /// <summary>Where every user setting lives. Program reads Language from here too.</summary>
        internal const string RegistryPath = "SOFTWARE\\PeripheralBatteryMonitor";

            //Auto-start is the one setting that is not ours to name: Windows reads this key,
            //and the value name is what identifies our entry in it.
        private const string AutoStartPath = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run";
        private const string AutoStartValue = "PeripheralBatteryMonitor";

            //How long the radio stays off in a Bluetooth restart. Long enough for Windows to
            //tear the stack down and let the devices notice, short enough to sit through.
        private const int BluetoothRestartDowntimeMs = 5000;

        private DeviceManager deviceManager = null;
        private Info infoForm = null;
        private bool UserClose = false;
        private bool UserShow = false;
        private bool isInitializing = true;
        private bool updatingIcon = false;
        private bool restartingBluetooth = false;
        private Dictionary<string, NotifyIcon> deviceIcons = new Dictionary<string, NotifyIcon>();
        private Dictionary<string, bool> deviceLowBatteryNotificationDone = new Dictionary<string, bool>();

        public Settings()
        {
            InitializeComponent();

                //Force the form handle so worker-thread BeginInvoke can post to UI thread
            IntPtr _ = this.Handle;

                //First of all create entry for settings
            Registry.CurrentUser.CreateSubKey(RegistryPath);

                //Reload settings
            using (RegistryKey run = Registry.CurrentUser.OpenSubKey(AutoStartPath, false))
            {
                if (run != null)
                    checkBoxStartup.Checked = run.GetValue(AutoStartValue) != null;
            }

            using (RegistryKey rk = Registry.CurrentUser.OpenSubKey(RegistryPath, false))
            {
                numericUpDownRefreshPeriod.Value = (int)rk.GetValue("IntervalMin", 5);
                checkBoxNotification.Checked = ((int)rk.GetValue("NotificationEnabled", 1)) != 0;
                checkBoxScanForEver.Checked = ((int)rk.GetValue("AutomaticDetectionEnabled", 0)) != 0;
                checkBoxOneIconPerDevice.Checked = ((int)rk.GetValue("OneIconPerDevice", 0)) != 0;
                checkBoxHideUnknownBattery.Checked = ((int)rk.GetValue("HideUnknownBattery", 0)) != 0;

                    //Program already applied this language before the window was built; the
                    //picker only has to show which one it was.
                FillLanguages(Convert.ToString(rk.GetValue("Language", "")));
            }

            ApplyStrings();

                //Asked once, here, rather than every time the menu opens: enumerating radios
                //is a WinRT call, and the one moment the user reaches for this entry is the
                //moment the stack has stopped answering -- a check on Opening would then hang
                //the very menu it is decorating. A machine with no radio at all is not going
                //to grow one mid-session.
            restartBluetoothToolStripMenuItem.Visible = BluetoothRadio.IsAvailable();

                //Instantiate everything
            deviceManager = new DeviceManager(new DeviceNotification(this));
            deviceManager.scan(checkBoxScanForEver.Checked);

            infoForm = new Info(deviceManager, () => checkBoxHideUnknownBattery.Checked, RefreshNow);

            IconTimer.Interval = ((int)numericUpDownRefreshPeriod.Value) * 60 * 1000;
            IconTimer.Start();

            UpdateIcon();

            isInitializing = false;

                //Posted, not called, and posted from here rather than from OnLoad -- which
                //never runs until the user opens this window, because SetVisibleCore keeps the
                //form hidden for the whole session otherwise. The handle was forced above, so
                //BeginInvoke has somewhere to post to and the snapshot runs on the first turn
                //of the message loop.
                //
                //It is posted because it opens every HID interface on the machine and probes
                //the unrecognised Logitech ones, which on an unlucky machine is seconds of
                //work; running it inline would hold up the tray icon appearing, on exactly the
                //machine whose owner already suspects the app is broken. Posting also keeps it
                //on the UI thread, which is where the poll tick runs, so a snapshot and a poll
                //can never be talking to one HID collection at the same time.
            BeginInvoke((Action)DiagnosticReport.WriteStartupSnapshot);
        }

            //Auto-scaling has happened by the time OnLoad runs, which is what makes the
            //font measurements below the real ones. Same reason Info builds its ListView
            //columns here rather than in its constructor.
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            FitInputRows();
        }

        /// <summary>
        /// Repairs the two rows that pair a caption with an input control, both of which
        /// WinForms gets wrong above 100% and neither of which can be fixed with a constant.
        ///
        /// The captions carry <c>Anchor.None</c> so a left-to-right FlowLayoutPanel centres
        /// them vertically on whatever the input control turns out to be. That only works if
        /// the input control's own geometry is honest, and for these two it is not:
        ///
        /// <list type="bullet">
        /// <item><description><b>NumericUpDown is a ContainerControl</b>, so it runs its own
        /// auto-scale pass on top of the form's and its margin is scaled more than once — a
        /// declared top margin of 3 arrived as 28 at 150%, which pushed the spinner nine pixels
        /// below its caption and stretched the row to 54 px. Copying the caption's margin puts
        /// it back on the one value that was scaled exactly once.</description></item>
        /// <item><description><b>ComboBox takes its height from its font once</b>, at
        /// construction, and never revisits it, so anything written to Size is pinned for the
        /// life of the form.</description></item>
        /// </list>
        /// </summary>
        private void FitInputRows()
        {
            numericUpDownRefreshPeriod.Margin = labelRefreshPeriod.Margin;
            FitLanguageBox();
        }

        /// <summary>
        /// Sizes the language box from its own font: height from what the control says it
        /// wants, width from the widest entry it has to show.
        ///
        /// Nothing here is a pixel constant, and that is the point. A hardcoded 160x21 was
        /// right at 100% and wrong at every other scale, and it would also have clipped a
        /// language whose "same as Windows" wording runs long.
        /// </summary>
        private void FitLanguageBox()
        {
            comboBoxLanguage.Height = comboBoxLanguage.PreferredHeight;

            int widest = 0;
            using (Graphics g = comboBoxLanguage.CreateGraphics())
            {
                foreach (object item in comboBoxLanguage.Items)
                {
                    int width = (int)Math.Ceiling(g.MeasureString(item.ToString(), comboBoxLanguage.Font).Width);
                    if (width > widest)
                        widest = width;
                }
            }

                //Room for the drop-down arrow, which is a system metric and so already scaled.
            comboBoxLanguage.Width = widest + SystemInformation.VerticalScrollBarWidth + comboBoxLanguage.Margin.Horizontal;
        }

        /// <summary>
        /// Persists one setting. There is no OK/Cancel on this form -- every handler writes the
        /// moment its control changes, so this is the whole of persistence, which is reason
        /// enough to have it in one place that cannot forget to close the key.
        /// </summary>
        private static void SaveSetting(string name, object value, RegistryValueKind kind = RegistryValueKind.DWord)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegistryPath, true))
            {
                if (key != null)
                    key.SetValue(name, value, kind);
            }
        }

        protected override void SetVisibleCore(bool value)
        {
            base.SetVisibleCore(UserShow ? value : false);
            UserShow = false;
        }

        private void DeviceListForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                if (!UserClose)
                {
                    e.Cancel = true;
                    Hide();
                }
            }
        }

        private void IconTimer_Tick(object sender, EventArgs e)
        {
            UpdateIcon();
        }

        /// <summary>
        /// Polls every device now instead of waiting for the next tick. Shared by the tray
        /// menu's Refresh entry and the device list's Refresh button, so both do exactly the
        /// same thing -- which for the list means the tray icon and tooltip update with it.
        /// </summary>
        internal void RefreshNow()
        {
                //A poll is I/O on the UI thread -- a single GATT read allows itself 5 s --
                //so say so, rather than letting the window look frozen. Re-entry needs no
                //guard here: UpdateIcon already drops a nested call.
            Cursor previous = Cursor.Current;
            Cursor.Current = Cursors.WaitCursor;
            try
            {
                    //Forced: this is the one pass that may re-run the cached parts of
                    //discovery, because it is the one the user asked for.
                UpdateIcon(true);
            }
            finally
            {
                Cursor.Current = previous;
            }

                //Restart the interval so the next automatic poll is a full period away
                //instead of firing seconds after the user already asked for one.
            IconTimer.Stop();
            IconTimer.Start();
        }

        private void refreshToolStripMenuItem_Click(object sender, EventArgs e)
        {
            RefreshNow();
        }

        private void restartBluetoothToolStripMenuItem_Click(object sender, EventArgs e)
        {
            RestartBluetooth();
        }

        /// <summary>
        /// Shows the user the log file. It is always being written, so there is nothing to
        /// generate here and nothing to wait for -- this entry exists only so that "send me
        /// the log" does not have to start with reciting a path.
        /// </summary>
        private void openLogToolStripMenuItem_Click(object sender, EventArgs e)
        {
            string path = Log.FilePath;
            try
            {
                    //Select rather than open: the reporter is being asked to attach this file,
                    //not to read it, and Explorer is where they can drag it from.
                System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, path + "\n\n" + ex.Message,
                    Strings.Get("tray.openLog"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        /// <summary>
        /// Switches the Bluetooth radio off, waits, and switches it back on -- the shortcut
        /// for what would otherwise be a trip to the Windows Settings toggle, which is the
        /// only way out of a stack that has wedged. See <see cref="BluetoothRadio"/> for why
        /// the radio and not the Bluetooth service.
        ///
        /// Progress is reported with balloons rather than a window, and deliberately not
        /// through <see cref="Notify"/>: that honours the notifications checkbox, which is
        /// about a device reaching 20% and not about feedback for something the user just
        /// clicked. A failure gets a message box, because it leaves the radio off and is
        /// worth more than a balloon that may never be shown.
        /// </summary>
        private void RestartBluetooth()
        {
            if (restartingBluetooth)
                return;

            try
            {
                    //On the UI thread on purpose: this is the one call that can put a consent
                    //prompt on screen, so it wants a thread with a message loop.
                BluetoothRadio.RequestAccess();
            }
            catch (Exception error)
            {
                ReportBluetoothRestartFailure(error);
                return;
            }

            restartingBluetooth = true;
            restartBluetoothToolStripMenuItem.Enabled = false;

                //Hold the poll off for the duration. A tick that lands while the radio is
                //down would sit on the UI thread waiting out a 30 s GATT connect timeout per
                //BLE device -- and it has nothing to read anyway. RefreshNow at the end
                //starts it again; the failure path below does it by hand.
            IconTimer.Stop();

            NotifyIcon.ShowBalloonTip(300, Strings.Get("app.name"), Strings.Format("notify.bluetoothRestart.started", BluetoothRestartDowntimeMs / 1000), ToolTipIcon.Info);

                //The radio is off for seconds, and this thread is the one that draws the tray
                //icon and its menu: doing the wait here would freeze both for the whole
                //downtime and have Windows call the app hung. Completion hops back through
                //BeginInvoke -- the constructor forces the handle so this is safe from a
                //worker thread.
            ThreadPool.QueueUserWorkItem(delegate
            {
                Exception failure = null;
                try
                {
                    BluetoothRadio.Restart(BluetoothRestartDowntimeMs);
                }
                catch (Exception error)
                {
                    failure = error;
                }

                    //Exit is reachable during the downtime, and posting to a form that has
                    //gone would throw on this thread -- where nothing catches it and the
                    //process dies after the user already asked it to quit.
                try
                {
                    if (!IsDisposed)
                        BeginInvoke(new Action<Exception>(BluetoothRestartFinished), failure);
                }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
            });
        }

        private void BluetoothRestartFinished(Exception failure)
        {
            restartingBluetooth = false;
            restartBluetoothToolStripMenuItem.Enabled = true;

            if (failure != null)
            {
                IconTimer.Start();
                ReportBluetoothRestartFailure(failure);
                return;
            }

                //Every Bluetooth device dropped off while the radio was down, and a watcher
                //that had already finished its enumeration will not report them coming back
                //-- so discovery starts over instead of waiting for a scan that is not going
                //to happen. HID devices are unaffected; the poll tick re-snapshots those.
            deviceManager.stopScan();
            deviceManager.scan(checkBoxScanForEver.Checked);

            NotifyIcon.ShowBalloonTip(300, Strings.Get("app.name"), Strings.Get("notify.bluetoothRestart.done"), ToolTipIcon.Info);

            RefreshNow();
        }

        private static void ReportBluetoothRestartFailure(Exception error)
        {
            MessageBox.Show(Strings.Format("notify.bluetoothRestart.failed", DescribeFailure(error)),
                            Strings.Get("app.name"), MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        /// <summary>
        /// The one line worth showing the user out of an exception. Everything in
        /// <see cref="BluetoothRadio"/> waits on a WinRT task, and a task that faulted
        /// reports it as an AggregateException -- whose own Message is a sentence about
        /// aggregate exceptions rather than about Bluetooth.
        /// </summary>
        private static string DescribeFailure(Exception error)
        {
            AggregateException aggregate = error as AggregateException;
            if (aggregate != null)
            {
                AggregateException flattened = aggregate.Flatten();
                if (flattened.InnerExceptions.Count > 0)
                    return flattened.InnerExceptions[0].Message;
            }
            return error.Message;
        }

        private static Icon GetIconForBatteryLevel(int level)
        {
            if (level < 0 || level >= 90) return PeripheralBatteryMonitor.Properties.Resources.Icon_Battery_100;
            if (level >= 70) return PeripheralBatteryMonitor.Properties.Resources.Icon_Battery_80;
            if (level >= 50) return PeripheralBatteryMonitor.Properties.Resources.Icon_Battery_60;
            if (level >= 30) return PeripheralBatteryMonitor.Properties.Resources.Icon_Battery_40;
            return PeripheralBatteryMonitor.Properties.Resources.Icon_Battery_20;
        }

        public void UpdateIcon()
        {
            UpdateIcon(false);
        }

        /// <summary>
        /// One poll pass. <paramref name="rediscover"/> marks the pass the user asked for, and
        /// carries that distinction into discovery: parts of it cache -- a Logitech receiver
        /// sweep costs six radio round trips, so it does not run every tick -- and a Refresh
        /// that served a cached answer would be a button that does nothing, which is precisely
        /// how a missing device gets reported as one that cannot be found at all.
        /// </summary>
        public void UpdateIcon(bool rediscover)
        {
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action<bool>(UpdateIcon), rediscover);
                return;
            }

                //refreshHidDevices below reports new devices synchronously, and OnNewDevice
                //calls back into here. The nested call has nothing left to do -- the device is
                //already in the dictionary this pass is about to walk -- so drop it.
            if (updatingIcon)
                return;

            updatingIcon = true;
            try
            {
                    //USB HID devices have no watcher behind them; re-snapshot them each tick
                    //so plugging or unplugging a dongle is picked up.
                deviceManager.refreshHidDevices(rediscover);

                ConcurrentDictionary<string, BatteryDevice> deviceDict = deviceManager.getDeviceList();

                    //Request to update battery level
                foreach (BatteryDevice device in deviceDict.Values)
                    device.UpdateBatteryLevel();

                    //Per-device mode can now come up empty even with devices tracked -- every
                    //one of them may be disconnected. Falling through to the single icon then
                    //is not cosmetic: with no per-device icon shown and the main one hidden,
                    //the app would have no tray presence at all and no way to reach its menu.
                if (!checkBoxOneIconPerDevice.Checked || deviceDict.IsEmpty || !UpdateIconPerDevice(deviceDict))
                    UpdateSingleIcon(deviceDict);
            }
            finally
            {
                updatingIcon = false;
            }
        }

        /// <summary>
        /// May the tray speak for this device on this pass?
        ///
        /// A disconnected device keeps its last reading -- that is deliberate, and the Info
        /// window still lists it -- but the tray must not report it. Left in, a mouse switched
        /// off at 20% holds the icon red and occupies a tooltip line for as long as it stays
        /// paired, hiding whatever is actually in use.
        ///
        /// Shared by both icon modes so the two cannot drift apart on which devices count.
        /// </summary>
        private bool TrayReports(BatteryDevice device)
        {
            if (!device.IsConnected())
                return false;

            return device.GetBatteryLevel() >= 0 || !checkBoxHideUnknownBattery.Checked;
        }

        /// <summary>One device's tooltip line. Both icon modes word it the same way.</summary>
        private static TrayTooltip.Line TrayLine(string name, int level)
        {
            return new TrayTooltip.Line(name, (level < 0)
                ? Strings.Format("tray.device.unknown", name)
                : Strings.Format("tray.device.known", name, level));
        }

        private void UpdateSingleIcon(ConcurrentDictionary<string, BatteryDevice> deviceDict)
        {
            ClearPerDeviceIcons();
            NotifyIcon.Visible = true;

            int theLowestBattery = 100;
            List<KeyValuePair<int, TrayTooltip.Line>> known = new List<KeyValuePair<int, TrayTooltip.Line>>();
            List<TrayTooltip.Line> unknown = new List<TrayTooltip.Line>();

            foreach (var kv in deviceDict)
            {
                if (!TrayReports(kv.Value))
                    continue;

                int level = kv.Value.GetBatteryLevel();
                string name = kv.Value.GetName();

                if (level < 0)
                {
                    unknown.Add(TrayLine(name, level));
                }
                else
                {
                    if (level < theLowestBattery)
                        theLowestBattery = level;

                    known.Add(new KeyValuePair<int, TrayTooltip.Line>(level, TrayLine(name, level)));
                }

                NotifyLowBattery(kv.Key, name, level);
            }

                //The tooltip holds 63 characters and a device list can easily exceed that.
                //TrayTooltip.Fit normally keeps every reading by shortening names; this order is
                //the fallback priority only when even the shortest marked names cannot all fit.
                //The lowest known battery comes first because that is what the icon represents;
                //devices still reading "?" carry less information and queue behind it.
            known.Sort((a, b) => a.Key.CompareTo(b.Key));

            List<TrayTooltip.Line> lines = new List<TrayTooltip.Line>(known.Count + unknown.Count);
            foreach (var entry in known)
                lines.Add(entry.Value);
            lines.AddRange(unknown);

            NotifyIcon.Icon = GetIconForBatteryLevel(theLowestBattery);

                //theLowestBattery is still its 100 sentinel here, so the icon reads full. Say
                //so in words rather than leaving an empty tooltip, which is indistinguishable
                //from a full battery.
            if (lines.Count == 0)
                lines.Add(new TrayTooltip.Line(null, Strings.Get("tray.noDevice")));

            NotifyIcon.Text = TrayTooltip.Fit(lines);
        }

        /// <summary>
        /// One tray icon per device. Returns false when it ended up showing none -- every
        /// device disconnected, or every one filtered out -- so the caller can put the single
        /// icon back rather than leave the app with no tray presence.
        /// </summary>
        private bool UpdateIconPerDevice(ConcurrentDictionary<string, BatteryDevice> deviceDict)
        {
            NotifyIcon.Visible = false;

            HashSet<string> shown = new HashSet<string>();

            foreach (var kv in deviceDict)
            {
                if (!TrayReports(kv.Value))
                    continue;

                int level = kv.Value.GetBatteryLevel();
                string name = kv.Value.GetName();

                shown.Add(kv.Key);

                NotifyIcon icon;
                if (!deviceIcons.TryGetValue(kv.Key, out icon))
                {
                    icon = new NotifyIcon(this.components);
                    icon.ContextMenuStrip = this.contextMenuStrip;
                    icon.MouseDoubleClick += this.NotifyIcon_MouseDoubleClick;
                    icon.Visible = true;
                    deviceIcons[kv.Key] = icon;
                }

                icon.Icon = GetIconForBatteryLevel(level);
                icon.Text = TrayTooltip.Fit(new TrayTooltip.Line[] { TrayLine(name, level) });

                NotifyLowBattery(kv.Key, name, level);
            }

                //Drop every icon this pass did not just paint. Keyed on what was shown, not
                //on what the manager still tracks: a device that disconnects (or that
                //"hide unknown battery" now filters out) stays in the dictionary, so the old
                //ContainsKey test left its icon on the tray showing a stale percentage for
                //ever. Devices that disappeared from the manager are covered by the same rule.
            foreach (string id in new List<string>(deviceIcons.Keys))
            {
                if (shown.Contains(id))
                    continue;

                deviceIcons[id].Visible = false;
                deviceIcons[id].Dispose();
                deviceIcons.Remove(id);

                    //Only forget the low-battery latch when the device is gone for good.
                    //Clearing it on a mere disconnect would re-fire the balloon every time a
                    //flat device wakes up.
                if (!deviceDict.ContainsKey(id))
                    deviceLowBatteryNotificationDone.Remove(id);
            }

            return shown.Count > 0;
        }

        private void NotifyLowBattery(string id, string name, int level)
        {
            if (level < 0)
                return;

            bool wasLow;
            deviceLowBatteryNotificationDone.TryGetValue(id, out wasLow);

            if (level <= 20 && !wasLow)
                Notify(Strings.Format("notify.lowBattery", name, level), ToolTipIcon.Warning);

            deviceLowBatteryNotificationDone[id] = (level <= 20);
        }

        private void ClearPerDeviceIcons()
        {
            foreach (NotifyIcon icon in deviceIcons.Values)
            {
                icon.Visible = false;
                icon.Dispose();
            }
            deviceIcons.Clear();
        }


        public void Notify(string message, ToolTipIcon icon = ToolTipIcon.Info)
        {
            if (checkBoxNotification.Checked)
                NotifyIcon.ShowBalloonTip(300, Strings.Get("app.name"), message, icon);
        }

        private void exitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            infoForm.Close();

            deviceManager.stopScan();

            ClearPerDeviceIcons();
            NotifyIcon.Visible = false;

                //Because of an issue, we have to show the setting form before closing it
            UserShow = true;
            Show();

            UserClose = true;
            Close();
        }

        private void settingsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            UserShow = true;
            Show();
            Activate();
        }

        private void buttonAbout_Click(object sender, EventArgs e)
        {
            ShowAbout();
        }

        private void buttonClose_Click(object sender, EventArgs e)
        {
                //Hide rather than Close: closing is what exiting means here, and
                //DeviceListForm_FormClosing would only cancel it and hide anyway.
            Hide();
        }

        private static void ShowAbout()
        {
                //No owner: this form is usually hidden by SetVisibleCore, and ShowDialog
                //refuses an invisible owner outright.
            using (AboutForm about = new AboutForm())
                about.ShowDialog();
        }

        private void NotifyIcon_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            infoForm.Show();
        }

        private void numericUpDownRefreshPeriod_ValueChanged(object sender, EventArgs e)
        {
            if (isInitializing) return;

            SaveSetting("IntervalMin", (int)numericUpDownRefreshPeriod.Value);

            IconTimer.Stop();
            IconTimer.Interval = (int)(numericUpDownRefreshPeriod.Value * 60 * 1000);
            IconTimer.Start();
        }
        private void checkBoxStartup_CheckedChanged(object sender, EventArgs e)
        {
            if (isInitializing) return;

            using (RegistryKey run = Registry.CurrentUser.OpenSubKey(AutoStartPath, true))
            {
                if (checkBoxStartup.Checked)
                    run.SetValue(AutoStartValue, Application.ExecutablePath);
                else
                    run.DeleteValue(AutoStartValue, false);
            }
        }

        private void checkBoxScanForEver_CheckedChanged(object sender, EventArgs e)
        {
            if (isInitializing) return;

            SaveSetting("AutomaticDetectionEnabled", checkBoxScanForEver.Checked ? 1 : 0);

                //Restart the watchers so the new flag takes effect immediately
            deviceManager.stopScan();
            deviceManager.scan(checkBoxScanForEver.Checked);
        }

        private void checkBoxNotification_CheckedChanged(object sender, EventArgs e)
        {
            if (isInitializing) return;

            SaveSetting("NotificationEnabled", checkBoxNotification.Checked ? 1 : 0);
        }

        private void checkBoxOneIconPerDevice_CheckedChanged(object sender, EventArgs e)
        {
            if (isInitializing) return;

            SaveSetting("OneIconPerDevice", checkBoxOneIconPerDevice.Checked ? 1 : 0);
            UpdateIcon();
        }

        private void comboBoxLanguage_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (isInitializing) return;

            Strings.Language chosen = comboBoxLanguage.SelectedItem as Strings.Language;
            string code = chosen == null ? "" : chosen.Code;

            SaveSetting("Language", code, RegistryValueKind.String);

            Strings.Use(code);

                //Applied live rather than on next launch, which the panel-based layout makes
                //possible: every label is AutoSize inside an AutoSize form, so the window
                //re-measures itself around the new text instead of clipping it. The Info popup
                //is long-lived too and has to be told; the About box is built fresh each time
                //it opens, so it needs nothing.
            ApplyStrings();
            FitLanguageBox();
            infoForm.ApplyStrings();

                //The tray tooltip is built from translated text, so it stays stale until the
                //next poll unless it is rebuilt now.
            UpdateIcon();
        }

        /// <summary>Offers every language embedded in the exe, plus following Windows.</summary>
        private void FillLanguages(string savedCode)
        {
                //First entry, and the default: the right answer for most people is the language
                //their computer is already in.
            comboBoxLanguage.Items.Add(new Strings.Language("", Strings.Get("settings.language.auto")));

            foreach (Strings.Language language in Strings.Available)
                comboBoxLanguage.Items.Add(language);

            int chosen = 0;
            for (int i = 1; i < comboBoxLanguage.Items.Count; i++)
            {
                if (((Strings.Language)comboBoxLanguage.Items[i]).Code == savedCode)
                {
                    chosen = i;
                    break;
                }
            }
            comboBoxLanguage.SelectedIndex = chosen;
        }

        /// <summary>
        /// Pushes the current language onto every piece of text this window owns, including the
        /// tray menu.
        ///
        /// The designer keeps English literals so the WinForms designer still renders a sane
        /// form, and they double as the last-resort fallback; this overwrites them right after
        /// InitializeComponent and again whenever the language changes.
        /// </summary>
        private void ApplyStrings()
        {
            Text = Strings.Get("settings.title");

            refreshToolStripMenuItem.Text = Strings.Get("tray.refresh");
            restartBluetoothToolStripMenuItem.Text = Strings.Get("tray.restartBluetooth");
            openLogToolStripMenuItem.Text = Strings.Get("tray.openLog");
            settingsToolStripMenuItem.Text = Strings.Get("tray.settings");
            exitToolStripMenuItem.Text = Strings.Get("tray.exit");

            groupGeneral.Text = Strings.Get("settings.group.general");
            checkBoxStartup.Text = Strings.Get("settings.startup");
            hintStartup.Text = Strings.Get("settings.startup.hint");
            checkBoxNotification.Text = Strings.Get("settings.notifications");
            hintNotification.Text = Strings.Get("settings.notifications.hint");
            labelRefreshPeriod.Text = Strings.Get("settings.refresh");
            labelRefreshUnit.Text = Strings.Get("settings.refresh.unit");
            hintRefreshPeriod.Text = Strings.Get("settings.refresh.hint");
            labelLanguage.Text = Strings.Get("settings.language");
            hintLanguage.Text = Strings.Get("settings.language.hint");

            groupDevices.Text = Strings.Get("settings.group.devices");
            checkBoxScanForEver.Text = Strings.Get("settings.autoDetect");
            hintScanForEver.Text = Strings.Get("settings.autoDetect.hint");
            checkBoxOneIconPerDevice.Text = Strings.Get("settings.oneIconPerDevice");
            hintOneIconPerDevice.Text = Strings.Get("settings.oneIconPerDevice.hint");
            checkBoxHideUnknownBattery.Text = Strings.Get("settings.hideUnknown");
            hintHideUnknownBattery.Text = Strings.Get("settings.hideUnknown.hint");

            buttonAbout.Text = Strings.Get("button.about");
            buttonClose.Text = Strings.Get("button.close");

                //The "same as Windows" entry is the one combo item whose label is translated;
                //the rest name themselves and must not be.
            if (comboBoxLanguage.Items.Count > 0)
            {
                int selected = comboBoxLanguage.SelectedIndex;
                comboBoxLanguage.Items[0] = new Strings.Language("", Strings.Get("settings.language.auto"));
                comboBoxLanguage.SelectedIndex = selected;
            }
        }

        private void checkBoxHideUnknownBattery_CheckedChanged(object sender, EventArgs e)
        {
            if (isInitializing) return;

            SaveSetting("HideUnknownBattery", checkBoxHideUnknownBattery.Checked ? 1 : 0);
            UpdateIcon();
        }
    }

    class DeviceNotification : IDeviceNotification
    {
        private Settings form;
        public DeviceNotification(Settings form)
        {
            this.form = form;
        }

        public void OnNewDevice(BatteryDevice aDevice)
        {
            this.form.UpdateIcon();
        }

        public void OnDeviceRemoved(string deviceId)
        {
            this.form.UpdateIcon();
        }

    }
}
