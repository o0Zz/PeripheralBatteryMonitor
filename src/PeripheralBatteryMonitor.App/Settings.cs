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

            //Not ours to name: Windows reads this key.
        private const string AutoStartPath = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run";
        private const string AutoStartValue = "PeripheralBatteryMonitor";

            //Long enough for Windows to tear the stack down and the devices to notice, short
            //enough to sit through.
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

            IntPtr _ = this.Handle;

            Registry.CurrentUser.CreateSubKey(RegistryPath);

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

                FillLanguages(Convert.ToString(rk.GetValue("Language", "")));
            }

            ApplyStrings();

                //Once here rather than on every menu open: enumerating radios is a WinRT call,
                //and the moment the user reaches for this entry is the moment the stack has
                //stopped answering -- a check on Opening would hang the menu it decorates.
            restartBluetoothToolStripMenuItem.Visible = BluetoothRadio.IsAvailable();

            deviceManager = new DeviceManager(new DeviceNotification(this));
            deviceManager.scan(checkBoxScanForEver.Checked);

            infoForm = new Info(deviceManager, () => checkBoxHideUnknownBattery.Checked, RefreshNow);

            IconTimer.Interval = ((int)numericUpDownRefreshPeriod.Value) * 60 * 1000;
            IconTimer.Start();

            UpdateIcon();

            isInitializing = false;

                //From here rather than OnLoad, which never runs while SetVisibleCore keeps this
                //form hidden. Posted rather than called because it opens every HID interface on
                //the machine -- seconds of work on an unlucky one -- and running it inline would
                //hold up the tray icon appearing. Posting also keeps it on the UI thread, where
                //the poll tick runs, so the two can never talk to one HID collection at once.
            BeginInvoke((Action)DiagnosticReport.WriteStartupSnapshot);
        }

            //Auto-scaling has happened by OnLoad, which is what makes the font measurements
            //below the real ones.
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            FitInputRows();
        }

        /// <summary>
        /// Repairs the two captioned input rows, which WinForms gets wrong above 100% and
        /// neither of which can be fixed with a constant. The caption carries
        /// <c>Anchor.None</c> so the FlowLayoutPanel centres it on the input control, which only
        /// works if that control's own geometry is honest -- and for these two it is not:
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
        /// Height from what the control says it wants, width from its widest entry. No pixel
        /// constant anywhere, which is the point: a hardcoded 160x21 was right at 100% and wrong
        /// at every other scale, and clipped a long "same as Windows" translation besides.
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

                //The drop-down arrow is a system metric, so already scaled.
            comboBoxLanguage.Width = widest + SystemInformation.VerticalScrollBarWidth + comboBoxLanguage.Margin.Horizontal;
        }

        /// <summary>
        /// There is no OK/Cancel on this form: every handler writes the moment its control
        /// changes, so this is the whole of persistence, in one place that cannot forget to
        /// close the key.
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
        /// Shared by the tray menu's Refresh entry and the device list's Refresh button, so
        /// refreshing from the list updates the tray icon and tooltip too.
        /// </summary>
        internal void RefreshNow()
        {
                //A poll is real I/O on the UI thread -- a single GATT read allows itself 5 s.
                //Re-entry needs no guard: UpdateIcon already drops a nested call.
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
        /// The log is always being written, so there is nothing to generate and nothing to wait
        /// for; this entry exists so "send me the log" need not start by reciting a path.
        /// </summary>
        private void openLogToolStripMenuItem_Click(object sender, EventArgs e)
        {
            string path = Log.FilePath;
            try
            {
                    //Select rather than open: the reporter is being asked to attach the file,
                    //not read it.
                System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, path + "\n\n" + ex.Message,
                    Strings.Get("tray.openLog"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        /// <summary>
        /// The shortcut for a trip to the Windows Settings toggle, which is the only way out of
        /// a wedged stack.
        ///
        /// Progress is a balloon but deliberately not through <see cref="Notify"/>: that honours
        /// the notifications checkbox, which is about a device reaching 20% and not about
        /// feedback for something the user just clicked. A failure gets a message box, because
        /// it leaves the radio off and must not be droppable.
        /// </summary>
        private void RestartBluetooth()
        {
            if (restartingBluetooth)
                return;

            try
            {
                //On the UI thread on purpose: the one call here that can put a consent prompt
                //on screen, so it wants a message loop.
                BluetoothRadio.RequestAccess();
            }
            catch (Exception error)
            {
                ReportBluetoothRestartFailure(error);
                return;
            }

            restartingBluetooth = true;
            restartBluetoothToolStripMenuItem.Enabled = false;

                //A tick landing while the radio is down would wait out a 30 s GATT connect
                //timeout per BLE device, on the UI thread, to read nothing. RefreshNow restarts
                //it at the end; the failure path below does it by hand.
            IconTimer.Stop();

            NotifyIcon.ShowBalloonTip(300, Strings.Get("app.name"), Strings.Format("notify.bluetoothRestart.started", BluetoothRestartDowntimeMs / 1000), ToolTipIcon.Info);

                //Sleeping the downtime on this thread would freeze the tray icon and its menu
                //and have Windows declare the app hung. Completion returns through BeginInvoke;
                //the constructor forces the handle so that is safe from a worker.
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

                    //Exit is reachable during the downtime, and posting to a dead form would
                    //throw on a thread where nothing catches it.
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

                //Every Bluetooth device dropped off while the radio was down, and a watcher that
                //had already finished enumerating never reports them returning. HID devices need
                //nothing -- the poll tick re-snapshots those.
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
        /// A faulted WinRT task arrives as an AggregateException, whose own Message is a sentence
        /// about aggregate exceptions rather than about Bluetooth.
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
        /// <paramref name="rediscover"/> marks the pass the user asked for and carries that into
        /// discovery, where a receiver sweep costs six radio round trips and so does not run
        /// every tick. A Refresh serving a cached answer is a button that does nothing.
        /// </summary>
        public void UpdateIcon(bool rediscover)
        {
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action<bool>(UpdateIcon), rediscover);
                return;
            }

                //refreshHidDevices reports new devices synchronously and OnNewDevice calls back
                //into here. The nested call has nothing left to do: the device is already in the
                //dictionary this pass is about to walk.
            if (updatingIcon)
                return;

            updatingIcon = true;
            try
            {
                    //No watcher behind these, so re-snapshot each tick to catch a dongle being
                    //plugged or unplugged.
                deviceManager.refreshHidDevices(rediscover);

                ConcurrentDictionary<string, BatteryDevice> deviceDict = deviceManager.getDeviceList();

                foreach (BatteryDevice device in deviceDict.Values)
                    device.UpdateBatteryLevel();

                    //Per-device mode can come up empty with devices still tracked -- every one
                    //disconnected. Falling through matters: with no per-device icon and the main
                    //one hidden, the app has no tray presence and no way to reach its menu.
                if (!checkBoxOneIconPerDevice.Checked || deviceDict.IsEmpty || !UpdateIconPerDevice(deviceDict))
                    UpdateSingleIcon(deviceDict);
            }
            finally
            {
                updatingIcon = false;
            }
        }

        /// <summary>
        /// A disconnected device keeps its last reading -- deliberately, and the Info window
        /// still lists it -- but the tray must not report it: a mouse switched off at 20% would
        /// hold the icon red and occupy a tooltip line for as long as it stays paired, hiding
        /// whatever is actually in use.
        ///
        /// Shared by both icon modes so they cannot drift apart on which devices count.
        /// </summary>
        private bool TrayReports(BatteryDevice device)
        {
            if (!device.IsConnected())
                return false;

            return device.GetBatteryLevel() >= 0 || !checkBoxHideUnknownBattery.Checked;
        }

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

                //TrayTooltip.Fit normally keeps every reading by shortening names; this order is
                //the fallback priority only when even the shortest marked names cannot all fit.
                //Lowest battery first, because that is what the icon is showing.
            known.Sort((a, b) => a.Key.CompareTo(b.Key));

            List<TrayTooltip.Line> lines = new List<TrayTooltip.Line>(known.Count + unknown.Count);
            foreach (var entry in known)
                lines.Add(entry.Value);
            lines.AddRange(unknown);

            NotifyIcon.Icon = GetIconForBatteryLevel(theLowestBattery);

                //theLowestBattery is still its 100 sentinel, so the icon reads full. An empty
                //tooltip would be indistinguishable from a genuinely full battery.
            if (lines.Count == 0)
                lines.Add(new TrayTooltip.Line(null, Strings.Get("tray.noDevice")));

            NotifyIcon.Text = TrayTooltip.Fit(lines);
        }

        /// <summary>
        /// False when it showed none -- every device disconnected or filtered out -- so the
        /// caller can put the single icon back rather than leave the app with no tray presence.
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

                //Keyed on what was painted this pass, not on what the manager still tracks: a
                //disconnected device (or one "hide unknown battery" now filters) stays in the
                //dictionary, so a ContainsKey test left its icon showing a stale percentage for
                //ever.
            foreach (string id in new List<string>(deviceIcons.Keys))
            {
                if (shown.Contains(id))
                    continue;

                deviceIcons[id].Visible = false;
                deviceIcons[id].Dispose();
                deviceIcons.Remove(id);

                    //Only when the device is gone for good: clearing the latch on a mere
                    //disconnect would re-fire the balloon every time a flat device wakes up.
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
                //Hide rather than Close: closing is what exiting means here.
            Hide();
        }

        private static void ShowAbout()
        {
                //No owner: the owner would be the hidden Settings form, and ShowDialog refuses
                //an invisible one.
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

                //Live rather than on next launch, which only works because of the panel layout:
                //every label is AutoSize inside an AutoSize form, so the window re-measures
                //itself around the new text. Info is long-lived too and has to be told; About is
                //built fresh each time and needs nothing.
            ApplyStrings();
            FitLanguageBox();
            infoForm.ApplyStrings();

                //The tray tooltip is built from translated text.
            UpdateIcon();
        }

        private void FillLanguages(string savedCode)
        {
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
        /// The designer files keep English literals so the WinForms designer still renders a sane
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

                //The one combo item whose label is translated; the rest name themselves.
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
