namespace PeripheralBatteryMonitor
{
    partial class Settings
    {
        private System.ComponentModel.IContainer components = null;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows
        private void InitializeComponent()
        {
            this.components = new System.ComponentModel.Container();
            this.NotifyIcon = new System.Windows.Forms.NotifyIcon(this.components);
            this.contextMenuStrip = new System.Windows.Forms.ContextMenuStrip(this.components);
            this.refreshToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.restartBluetoothToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.settingsToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.openLogToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.separatorDeviceActions = new System.Windows.Forms.ToolStripSeparator();
            this.separatorExit = new System.Windows.Forms.ToolStripSeparator();
            this.exitToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.IconTimer = new System.Windows.Forms.Timer(this.components);
            this.layoutRoot = new System.Windows.Forms.TableLayoutPanel();
            this.groupGeneral = new System.Windows.Forms.GroupBox();
            this.layoutGeneral = new System.Windows.Forms.FlowLayoutPanel();
            this.checkBoxStartup = new System.Windows.Forms.CheckBox();
            this.hintStartup = new System.Windows.Forms.Label();
            this.checkBoxNotification = new System.Windows.Forms.CheckBox();
            this.hintNotification = new System.Windows.Forms.Label();
            this.layoutRefreshPeriod = new System.Windows.Forms.FlowLayoutPanel();
            this.labelRefreshPeriod = new System.Windows.Forms.Label();
            this.numericUpDownRefreshPeriod = new System.Windows.Forms.NumericUpDown();
            this.labelRefreshUnit = new System.Windows.Forms.Label();
            this.hintRefreshPeriod = new System.Windows.Forms.Label();
            this.layoutLanguage = new System.Windows.Forms.FlowLayoutPanel();
            this.labelLanguage = new System.Windows.Forms.Label();
            this.comboBoxLanguage = new System.Windows.Forms.ComboBox();
            this.hintLanguage = new System.Windows.Forms.Label();
            this.groupDevices = new System.Windows.Forms.GroupBox();
            this.layoutDevices = new System.Windows.Forms.FlowLayoutPanel();
            this.checkBoxScanForEver = new System.Windows.Forms.CheckBox();
            this.hintScanForEver = new System.Windows.Forms.Label();
            this.checkBoxOneIconPerDevice = new System.Windows.Forms.CheckBox();
            this.hintOneIconPerDevice = new System.Windows.Forms.Label();
            this.checkBoxHideUnknownBattery = new System.Windows.Forms.CheckBox();
            this.hintHideUnknownBattery = new System.Windows.Forms.Label();
            this.layoutButtons = new System.Windows.Forms.FlowLayoutPanel();
            this.buttonClose = new System.Windows.Forms.Button();
            this.buttonAbout = new System.Windows.Forms.Button();
            this.contextMenuStrip.SuspendLayout();
            this.layoutRoot.SuspendLayout();
            this.groupGeneral.SuspendLayout();
            this.layoutGeneral.SuspendLayout();
            this.layoutRefreshPeriod.SuspendLayout();
            this.layoutLanguage.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.numericUpDownRefreshPeriod)).BeginInit();
            this.groupDevices.SuspendLayout();
            this.layoutDevices.SuspendLayout();
            this.layoutButtons.SuspendLayout();
            this.SuspendLayout();
            //
            // NotifyIcon
            //
            this.NotifyIcon.ContextMenuStrip = this.contextMenuStrip;
            this.NotifyIcon.Icon = global::PeripheralBatteryMonitor.Properties.Resources.Icon_Battery_100;
            this.NotifyIcon.Text = "Peripheral Battery Monitor";
            this.NotifyIcon.Visible = true;
            this.NotifyIcon.MouseDoubleClick += new System.Windows.Forms.MouseEventHandler(this.NotifyIcon_MouseDoubleClick);
            //
            // contextMenuStrip
            //
            this.contextMenuStrip.ImageScalingSize = new System.Drawing.Size(20, 20);
            this.contextMenuStrip.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.settingsToolStripMenuItem,
            this.openLogToolStripMenuItem,
            this.separatorDeviceActions,
            this.restartBluetoothToolStripMenuItem,
            this.refreshToolStripMenuItem,
            this.separatorExit,
            this.exitToolStripMenuItem});
            this.contextMenuStrip.Name = "contextMenuStrip1";
            //
            // refreshToolStripMenuItem
            //
            // Last before Exit on purpose: a tray menu opens upwards from the notification
            // area, so the bottom entry is the one nearest the pointer, and this is the entry
            // reached for most. English literal like its neighbours -- see the Interface
            // language notes; ApplyStrings overwrites it straight after this runs.
            this.refreshToolStripMenuItem.Name = "refreshToolStripMenuItem";
            this.refreshToolStripMenuItem.Text = "Refresh";
            this.refreshToolStripMenuItem.Click += new System.EventHandler(this.refreshToolStripMenuItem_Click);
            //
            // restartBluetoothToolStripMenuItem
            //
            // Next to Refresh, for the same reason: both act on the devices instead of
            // opening a window, and both belong near the pointer. Whether it is shown at all
            // is decided once at startup, in the Settings constructor: a machine with no
            // Bluetooth radio can only fail it.
            this.restartBluetoothToolStripMenuItem.Name = "restartBluetoothToolStripMenuItem";
            this.restartBluetoothToolStripMenuItem.Text = "Restart Bluetooth";
            this.restartBluetoothToolStripMenuItem.Click += new System.EventHandler(this.restartBluetoothToolStripMenuItem_Click);
            //
            // settingsToolStripMenuItem
            //
            this.settingsToolStripMenuItem.Name = "settingsToolStripMenuItem";
            this.settingsToolStripMenuItem.Text = "Settings";
            this.settingsToolStripMenuItem.Click += new System.EventHandler(this.settingsToolStripMenuItem_Click);
            //
            // openLogToolStripMenuItem
            //
            // The log is always being written, so this only has to reveal it. Reachable in one
            // click and not hidden behind a modifier: the person who needs it is being talked
            // through it by someone who cannot see their screen. It sits with Settings rather
            // than with the device actions below -- it opens a folder, it does not touch a
            // device.
            this.openLogToolStripMenuItem.Name = "openLogToolStripMenuItem";
            this.openLogToolStripMenuItem.Text = "Open log folder";
            this.openLogToolStripMenuItem.Click += new System.EventHandler(this.openLogToolStripMenuItem_Click);
            //
            // separatorDeviceActions
            //
            // The menu is three groups, ordered by distance from the pointer rather than by
            // importance: the menu opens upwards out of the notification area, so the window
            // opener sits at the top, the two device actions sit just above Exit where the
            // pointer already is, and Exit keeps the bottom it has in every tray app. There
            // is no About entry -- the Settings window carries the About... button, and
            // duplicating it here made a five-item menu out of a three-item job.
            this.separatorDeviceActions.Name = "separatorDeviceActions";
            //
            // separatorExit
            //
            this.separatorExit.Name = "separatorExit";
            //
            // exitToolStripMenuItem
            //
            this.exitToolStripMenuItem.Name = "exitToolStripMenuItem";
            this.exitToolStripMenuItem.Text = "Exit";
            this.exitToolStripMenuItem.Click += new System.EventHandler(this.exitToolStripMenuItem_Click);
            //
            // IconTimer
            //
            this.IconTimer.Interval = 120000;
            this.IconTimer.Tick += new System.EventHandler(this.IconTimer_Tick);
            //
            // layoutRoot
            //
            // One column at 100% so both group boxes stretch to the same width, and
            // AutoSize rows so each one is exactly as tall as its own contents. The
            // trailing 100% row is an empty spacer that keeps the groups at the top
            // if the window is ever taller than they are.
            this.layoutRoot.ColumnCount = 1;
            this.layoutRoot.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 100F));
            this.layoutRoot.Controls.Add(this.groupGeneral, 0, 0);
            this.layoutRoot.Controls.Add(this.groupDevices, 0, 1);
            this.layoutRoot.Dock = System.Windows.Forms.DockStyle.Fill;
            this.layoutRoot.AutoSize = true;
            this.layoutRoot.AutoSizeMode = System.Windows.Forms.AutoSizeMode.GrowAndShrink;
            this.layoutRoot.Location = new System.Drawing.Point(0, 0);
            this.layoutRoot.Name = "layoutRoot";
            this.layoutRoot.Padding = new System.Windows.Forms.Padding(12, 12, 12, 0);
            this.layoutRoot.RowCount = 3;
            this.layoutRoot.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.AutoSize));
            this.layoutRoot.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.AutoSize));
            this.layoutRoot.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 100F));
            this.layoutRoot.TabIndex = 0;
            //
            // groupGeneral
            //
            this.groupGeneral.Controls.Add(this.layoutGeneral);
            this.groupGeneral.Dock = System.Windows.Forms.DockStyle.Fill;
            this.groupGeneral.AutoSize = true;
            this.groupGeneral.AutoSizeMode = System.Windows.Forms.AutoSizeMode.GrowAndShrink;
            this.groupGeneral.Margin = new System.Windows.Forms.Padding(0, 0, 0, 10);
            this.groupGeneral.Name = "groupGeneral";
            this.groupGeneral.Padding = new System.Windows.Forms.Padding(10, 4, 10, 8);
            this.groupGeneral.TabIndex = 0;
            this.groupGeneral.TabStop = false;
            this.groupGeneral.Text = "General";
            //
            // layoutGeneral
            //
            this.layoutGeneral.AutoSize = true;
            this.layoutGeneral.AutoSizeMode = System.Windows.Forms.AutoSizeMode.GrowAndShrink;
            this.layoutGeneral.Controls.Add(this.checkBoxStartup);
            this.layoutGeneral.Controls.Add(this.hintStartup);
            this.layoutGeneral.Controls.Add(this.checkBoxNotification);
            this.layoutGeneral.Controls.Add(this.hintNotification);
            this.layoutGeneral.Controls.Add(this.layoutRefreshPeriod);
            this.layoutGeneral.Controls.Add(this.hintRefreshPeriod);
            this.layoutGeneral.Controls.Add(this.layoutLanguage);
            this.layoutGeneral.Controls.Add(this.hintLanguage);
            this.layoutGeneral.Dock = System.Windows.Forms.DockStyle.Fill;
            this.layoutGeneral.FlowDirection = System.Windows.Forms.FlowDirection.TopDown;
            this.layoutGeneral.Margin = new System.Windows.Forms.Padding(0);
            this.layoutGeneral.Name = "layoutGeneral";
            this.layoutGeneral.TabIndex = 0;
            this.layoutGeneral.WrapContents = false;
            //
            // checkBoxStartup
            //
            this.checkBoxStartup.AutoSize = true;
            this.checkBoxStartup.Margin = new System.Windows.Forms.Padding(3, 3, 3, 0);
            this.checkBoxStartup.Name = "checkBoxStartup";
            this.checkBoxStartup.TabIndex = 0;
            this.checkBoxStartup.Text = "Launch application on startup";
            this.checkBoxStartup.UseVisualStyleBackColor = true;
            this.checkBoxStartup.CheckedChanged += new System.EventHandler(this.checkBoxStartup_CheckedChanged);
            //
            // hintStartup
            //
            this.hintStartup.AutoSize = true;
            this.hintStartup.ForeColor = System.Drawing.SystemColors.GrayText;
            this.hintStartup.Margin = new System.Windows.Forms.Padding(20, 0, 3, 10);
            this.hintStartup.MaximumSize = new System.Drawing.Size(400, 0);
            this.hintStartup.Name = "hintStartup";
            this.hintStartup.Text = "Start with Windows, straight into the tray. Off by default.";
            //
            // checkBoxNotification
            //
            this.checkBoxNotification.AutoSize = true;
            this.checkBoxNotification.Margin = new System.Windows.Forms.Padding(3, 0, 3, 0);
            this.checkBoxNotification.Name = "checkBoxNotification";
            this.checkBoxNotification.TabIndex = 1;
            this.checkBoxNotification.Text = "Enable notifications";
            this.checkBoxNotification.UseVisualStyleBackColor = true;
            this.checkBoxNotification.CheckedChanged += new System.EventHandler(this.checkBoxNotification_CheckedChanged);
            //
            // hintNotification
            //
            this.hintNotification.AutoSize = true;
            this.hintNotification.ForeColor = System.Drawing.SystemColors.GrayText;
            this.hintNotification.Margin = new System.Windows.Forms.Padding(20, 0, 3, 10);
            this.hintNotification.MaximumSize = new System.Drawing.Size(400, 0);
            this.hintNotification.Name = "hintNotification";
            this.hintNotification.Text = "Show a balloon once each time a device drops to 20% or below, not on every refresh while it stays there.";
            //
            // layoutRefreshPeriod
            //
            this.layoutRefreshPeriod.AutoSize = true;
            this.layoutRefreshPeriod.AutoSizeMode = System.Windows.Forms.AutoSizeMode.GrowAndShrink;
            this.layoutRefreshPeriod.Controls.Add(this.labelRefreshPeriod);
            this.layoutRefreshPeriod.Controls.Add(this.numericUpDownRefreshPeriod);
            this.layoutRefreshPeriod.Controls.Add(this.labelRefreshUnit);
            this.layoutRefreshPeriod.Margin = new System.Windows.Forms.Padding(0);
            this.layoutRefreshPeriod.Name = "layoutRefreshPeriod";
            this.layoutRefreshPeriod.TabIndex = 2;
            this.layoutRefreshPeriod.WrapContents = false;
            //
            // labelRefreshPeriod
            //
            this.labelRefreshPeriod.AutoSize = true;
            // Anchor.None centres a child vertically inside a left-to-right FlowLayoutPanel
            // row. It replaces a hand-tuned top margin of 6 that faked the same thing: that
            // number was measured against a 13 px label beside a 20 px spin box at 96 DPI, and
            // above 100% the label grows to 20 px while the constant does not, dropping the
            // caption 3 px below the number it labels. Same trap as ListView column widths.
            this.labelRefreshPeriod.Anchor = System.Windows.Forms.AnchorStyles.None;
            this.labelRefreshPeriod.Margin = new System.Windows.Forms.Padding(3, 3, 3, 0);
            this.labelRefreshPeriod.Name = "labelRefreshPeriod";
            this.labelRefreshPeriod.Text = "Refresh period:";
            //
            // numericUpDownRefreshPeriod
            //
            // No Anchor here, unlike the labels either side. NumericUpDown is a
            // ContainerControl and does its own auto-scaling pass; combining that with
            // FlowLayoutPanel's Anchor.None centring put it 12 px below its own caption at
            // 150%. Left at the default it anchors top-left and the labels centre on it.
            this.numericUpDownRefreshPeriod.Margin = new System.Windows.Forms.Padding(3, 3, 3, 0);
            this.numericUpDownRefreshPeriod.Maximum = new decimal(new int[] {
            1440,
            0,
            0,
            0});
            this.numericUpDownRefreshPeriod.Minimum = new decimal(new int[] {
            1,
            0,
            0,
            0});
            this.numericUpDownRefreshPeriod.Name = "numericUpDownRefreshPeriod";
            this.numericUpDownRefreshPeriod.Size = new System.Drawing.Size(65, 20);
            this.numericUpDownRefreshPeriod.TabIndex = 0;
            this.numericUpDownRefreshPeriod.Value = new decimal(new int[] {
            5,
            0,
            0,
            0});
            this.numericUpDownRefreshPeriod.ValueChanged += new System.EventHandler(this.numericUpDownRefreshPeriod_ValueChanged);
            //
            // labelRefreshUnit
            //
            this.labelRefreshUnit.AutoSize = true;
            this.labelRefreshUnit.Anchor = System.Windows.Forms.AnchorStyles.None;
            this.labelRefreshUnit.Margin = new System.Windows.Forms.Padding(3, 3, 3, 0);
            this.labelRefreshUnit.Name = "labelRefreshUnit";
            this.labelRefreshUnit.Text = "min";
            //
            // hintRefreshPeriod
            //
            this.hintRefreshPeriod.AutoSize = true;
            this.hintRefreshPeriod.ForeColor = System.Drawing.SystemColors.GrayText;
            this.hintRefreshPeriod.Margin = new System.Windows.Forms.Padding(3, 2, 3, 0);
            this.hintRefreshPeriod.MaximumSize = new System.Drawing.Size(400, 0);
            this.hintRefreshPeriod.Name = "hintRefreshPeriod";
            this.hintRefreshPeriod.Text = "How often every tracked device is polled. Between 1 minute and 24 hours.";
            //
            // layoutLanguage
            //
            this.layoutLanguage.AutoSize = true;
            this.layoutLanguage.AutoSizeMode = System.Windows.Forms.AutoSizeMode.GrowAndShrink;
            this.layoutLanguage.Controls.Add(this.labelLanguage);
            this.layoutLanguage.Controls.Add(this.comboBoxLanguage);
            this.layoutLanguage.Margin = new System.Windows.Forms.Padding(0, 10, 0, 0);
            this.layoutLanguage.Name = "layoutLanguage";
            this.layoutLanguage.TabIndex = 3;
            this.layoutLanguage.WrapContents = false;
            //
            // labelLanguage
            //
            this.labelLanguage.AutoSize = true;
            this.labelLanguage.Anchor = System.Windows.Forms.AnchorStyles.None;
            this.labelLanguage.Margin = new System.Windows.Forms.Padding(3, 3, 3, 0);
            this.labelLanguage.Name = "labelLanguage";
            this.labelLanguage.Text = "Language:";
            //
            // comboBoxLanguage
            //
            // DropDownList, not DropDown: the list is fixed and typing into it would only
            // ever produce a value that is not a language.
            this.comboBoxLanguage.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.comboBoxLanguage.Anchor = System.Windows.Forms.AnchorStyles.None;
            this.comboBoxLanguage.Margin = new System.Windows.Forms.Padding(3, 3, 3, 0);
            this.comboBoxLanguage.Name = "comboBoxLanguage";
            // Deliberately no Size here. A ComboBox takes its height from its font once, at
            // construction, and AutoScaleMode.Font never revisits it -- so any value written
            // here is pinned for the life of the form. At 150% that left a 21 px box beside a
            // 20 px caption while the row sized itself to the 31 px the box actually wanted,
            // which is what threw the caption out of line. Settings.FitLanguageBox sizes it
            // from the font after scaling instead.
            this.comboBoxLanguage.TabIndex = 0;
            this.comboBoxLanguage.SelectedIndexChanged += new System.EventHandler(this.comboBoxLanguage_SelectedIndexChanged);
            //
            // hintLanguage
            //
            this.hintLanguage.AutoSize = true;
            this.hintLanguage.ForeColor = System.Drawing.SystemColors.GrayText;
            this.hintLanguage.Margin = new System.Windows.Forms.Padding(3, 2, 3, 0);
            this.hintLanguage.MaximumSize = new System.Drawing.Size(400, 0);
            this.hintLanguage.Name = "hintLanguage";
            this.hintLanguage.Text = "Applies straight away. Names reported by a device stay as the device spells them.";
            //
            // groupDevices
            //
            this.groupDevices.Controls.Add(this.layoutDevices);
            this.groupDevices.Dock = System.Windows.Forms.DockStyle.Fill;
            this.groupDevices.AutoSize = true;
            this.groupDevices.AutoSizeMode = System.Windows.Forms.AutoSizeMode.GrowAndShrink;
            this.groupDevices.Margin = new System.Windows.Forms.Padding(0);
            this.groupDevices.Name = "groupDevices";
            this.groupDevices.Padding = new System.Windows.Forms.Padding(10, 4, 10, 8);
            this.groupDevices.TabIndex = 1;
            this.groupDevices.TabStop = false;
            this.groupDevices.Text = "Devices and tray icons";
            //
            // layoutDevices
            //
            this.layoutDevices.AutoSize = true;
            this.layoutDevices.AutoSizeMode = System.Windows.Forms.AutoSizeMode.GrowAndShrink;
            this.layoutDevices.Controls.Add(this.checkBoxScanForEver);
            this.layoutDevices.Controls.Add(this.hintScanForEver);
            this.layoutDevices.Controls.Add(this.checkBoxOneIconPerDevice);
            this.layoutDevices.Controls.Add(this.hintOneIconPerDevice);
            this.layoutDevices.Controls.Add(this.checkBoxHideUnknownBattery);
            this.layoutDevices.Controls.Add(this.hintHideUnknownBattery);
            this.layoutDevices.Dock = System.Windows.Forms.DockStyle.Fill;
            this.layoutDevices.FlowDirection = System.Windows.Forms.FlowDirection.TopDown;
            this.layoutDevices.Margin = new System.Windows.Forms.Padding(0);
            this.layoutDevices.Name = "layoutDevices";
            this.layoutDevices.TabIndex = 0;
            this.layoutDevices.WrapContents = false;
            //
            // checkBoxScanForEver
            //
            this.checkBoxScanForEver.AutoSize = true;
            this.checkBoxScanForEver.Margin = new System.Windows.Forms.Padding(3, 3, 3, 0);
            this.checkBoxScanForEver.Name = "checkBoxScanForEver";
            this.checkBoxScanForEver.TabIndex = 0;
            this.checkBoxScanForEver.Text = "Automatically detect new devices";
            this.checkBoxScanForEver.UseVisualStyleBackColor = true;
            this.checkBoxScanForEver.CheckedChanged += new System.EventHandler(this.checkBoxScanForEver_CheckedChanged);
            //
            // hintScanForEver
            //
            this.hintScanForEver.AutoSize = true;
            this.hintScanForEver.ForeColor = System.Drawing.SystemColors.GrayText;
            this.hintScanForEver.Margin = new System.Windows.Forms.Padding(20, 0, 3, 10);
            this.hintScanForEver.MaximumSize = new System.Drawing.Size(400, 0);
            this.hintScanForEver.Name = "hintScanForEver";
            this.hintScanForEver.Text = "Keep watching for newly paired Bluetooth devices. When off, only devices already paired at startup are tracked. USB dongles are always picked up.";
            //
            // checkBoxOneIconPerDevice
            //
            this.checkBoxOneIconPerDevice.AutoSize = true;
            this.checkBoxOneIconPerDevice.Margin = new System.Windows.Forms.Padding(3, 0, 3, 0);
            this.checkBoxOneIconPerDevice.Name = "checkBoxOneIconPerDevice";
            this.checkBoxOneIconPerDevice.TabIndex = 1;
            this.checkBoxOneIconPerDevice.Text = "Show one tray icon per device";
            this.checkBoxOneIconPerDevice.UseVisualStyleBackColor = true;
            this.checkBoxOneIconPerDevice.CheckedChanged += new System.EventHandler(this.checkBoxOneIconPerDevice_CheckedChanged);
            //
            // hintOneIconPerDevice
            //
            this.hintOneIconPerDevice.AutoSize = true;
            this.hintOneIconPerDevice.ForeColor = System.Drawing.SystemColors.GrayText;
            this.hintOneIconPerDevice.Margin = new System.Windows.Forms.Padding(20, 0, 3, 10);
            this.hintOneIconPerDevice.MaximumSize = new System.Drawing.Size(400, 0);
            this.hintOneIconPerDevice.Name = "hintOneIconPerDevice";
            this.hintOneIconPerDevice.Text = "When off, one icon shows the lowest battery of all devices and its tooltip lists each one.";
            //
            // checkBoxHideUnknownBattery
            //
            this.checkBoxHideUnknownBattery.AutoSize = true;
            this.checkBoxHideUnknownBattery.Margin = new System.Windows.Forms.Padding(3, 0, 3, 0);
            this.checkBoxHideUnknownBattery.Name = "checkBoxHideUnknownBattery";
            this.checkBoxHideUnknownBattery.TabIndex = 2;
            this.checkBoxHideUnknownBattery.Text = "Hide devices with unknown battery level";
            this.checkBoxHideUnknownBattery.UseVisualStyleBackColor = true;
            this.checkBoxHideUnknownBattery.CheckedChanged += new System.EventHandler(this.checkBoxHideUnknownBattery_CheckedChanged);
            //
            // hintHideUnknownBattery
            //
            this.hintHideUnknownBattery.AutoSize = true;
            this.hintHideUnknownBattery.ForeColor = System.Drawing.SystemColors.GrayText;
            this.hintHideUnknownBattery.Margin = new System.Windows.Forms.Padding(20, 0, 3, 0);
            this.hintHideUnknownBattery.MaximumSize = new System.Drawing.Size(400, 0);
            this.hintHideUnknownBattery.Name = "hintHideUnknownBattery";
            this.hintHideUnknownBattery.Text = "Some devices report no battery to Windows at all. When on, they are left out of the tray, the tooltip and the device list.";
            //
            // layoutButtons
            //
            // RightToLeft flow, so the control added first sits rightmost.
            this.layoutButtons.AutoSize = true;
            this.layoutButtons.AutoSizeMode = System.Windows.Forms.AutoSizeMode.GrowAndShrink;
            this.layoutButtons.Controls.Add(this.buttonClose);
            this.layoutButtons.Controls.Add(this.buttonAbout);
            this.layoutButtons.Dock = System.Windows.Forms.DockStyle.Bottom;
            this.layoutButtons.FlowDirection = System.Windows.Forms.FlowDirection.RightToLeft;
            this.layoutButtons.Name = "layoutButtons";
            this.layoutButtons.Padding = new System.Windows.Forms.Padding(8, 8, 12, 10);
            this.layoutButtons.TabIndex = 1;
            //
            // buttonClose
            //
            this.buttonClose.AutoSize = true;
            this.buttonClose.AutoSizeMode = System.Windows.Forms.AutoSizeMode.GrowAndShrink;
            this.buttonClose.MinimumSize = new System.Drawing.Size(84, 26);
            this.buttonClose.Name = "buttonClose";
            this.buttonClose.TabIndex = 0;
            this.buttonClose.Text = "Close";
            this.buttonClose.UseVisualStyleBackColor = true;
            this.buttonClose.Click += new System.EventHandler(this.buttonClose_Click);
            //
            // buttonAbout
            //
            this.buttonAbout.AutoSize = true;
            this.buttonAbout.AutoSizeMode = System.Windows.Forms.AutoSizeMode.GrowAndShrink;
            this.buttonAbout.MinimumSize = new System.Drawing.Size(84, 26);
            this.buttonAbout.Name = "buttonAbout";
            this.buttonAbout.TabIndex = 1;
            this.buttonAbout.Text = "About...";
            this.buttonAbout.UseVisualStyleBackColor = true;
            this.buttonAbout.Click += new System.EventHandler(this.buttonAbout_Click);
            //
            // Settings
            //
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            // AutoSize rather than a fixed ClientSize: every row in here is a wrapped
            // label, so the height that fits depends on the display scale. Letting the
            // form ask its own contents is the only version that is right at 100% and
            // at 150%.
            this.AutoSize = true;
            this.AutoSizeMode = System.Windows.Forms.AutoSizeMode.GrowAndShrink;
            // Docking is applied from the highest index down, so the filled panel has to
            // sit at index 0 -- added first -- to be laid out last and take what is left.
            this.Controls.Add(this.layoutRoot);
            this.Controls.Add(this.layoutButtons);
            // Enter and Escape both dismiss the window. There is no Cancel: every
            // setting is written to the registry the moment it changes, so there is
            // nothing pending for a Cancel to discard.
            this.AcceptButton = this.buttonClose;
            this.CancelButton = this.buttonClose;
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
            this.Icon = global::PeripheralBatteryMonitor.Properties.Resources.Icon_Battery_100;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Name = "Settings";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "Peripheral Battery Monitor";
            this.FormClosing += new System.Windows.Forms.FormClosingEventHandler(this.DeviceListForm_FormClosing);
            this.contextMenuStrip.ResumeLayout(false);
            this.layoutRoot.ResumeLayout(false);
            this.layoutRoot.PerformLayout();
            this.groupGeneral.ResumeLayout(false);
            this.groupGeneral.PerformLayout();
            this.layoutGeneral.ResumeLayout(false);
            this.layoutGeneral.PerformLayout();
            this.layoutRefreshPeriod.ResumeLayout(false);
            this.layoutRefreshPeriod.PerformLayout();
            this.layoutLanguage.ResumeLayout(false);
            this.layoutLanguage.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)(this.numericUpDownRefreshPeriod)).EndInit();
            this.groupDevices.ResumeLayout(false);
            this.groupDevices.PerformLayout();
            this.layoutDevices.ResumeLayout(false);
            this.layoutDevices.PerformLayout();
            this.layoutButtons.ResumeLayout(false);
            this.layoutButtons.PerformLayout();
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion
        private System.Windows.Forms.NotifyIcon NotifyIcon;
        private System.Windows.Forms.Timer IconTimer;
        private System.Windows.Forms.ContextMenuStrip contextMenuStrip;
        private System.Windows.Forms.ToolStripMenuItem refreshToolStripMenuItem;
        private System.Windows.Forms.ToolStripMenuItem restartBluetoothToolStripMenuItem;
        private System.Windows.Forms.ToolStripMenuItem settingsToolStripMenuItem;
        private System.Windows.Forms.ToolStripMenuItem openLogToolStripMenuItem;
        private System.Windows.Forms.ToolStripSeparator separatorDeviceActions;
        private System.Windows.Forms.ToolStripSeparator separatorExit;
        private System.Windows.Forms.ToolStripMenuItem exitToolStripMenuItem;
        private System.Windows.Forms.TableLayoutPanel layoutRoot;
        private System.Windows.Forms.GroupBox groupGeneral;
        private System.Windows.Forms.FlowLayoutPanel layoutGeneral;
        private System.Windows.Forms.CheckBox checkBoxStartup;
        private System.Windows.Forms.Label hintStartup;
        private System.Windows.Forms.CheckBox checkBoxNotification;
        private System.Windows.Forms.Label hintNotification;
        private System.Windows.Forms.FlowLayoutPanel layoutRefreshPeriod;
        private System.Windows.Forms.Label labelRefreshPeriod;
        private System.Windows.Forms.NumericUpDown numericUpDownRefreshPeriod;
        private System.Windows.Forms.Label labelRefreshUnit;
        private System.Windows.Forms.Label hintRefreshPeriod;
        private System.Windows.Forms.FlowLayoutPanel layoutLanguage;
        private System.Windows.Forms.Label labelLanguage;
        private System.Windows.Forms.ComboBox comboBoxLanguage;
        private System.Windows.Forms.Label hintLanguage;
        private System.Windows.Forms.GroupBox groupDevices;
        private System.Windows.Forms.FlowLayoutPanel layoutDevices;
        private System.Windows.Forms.CheckBox checkBoxScanForEver;
        private System.Windows.Forms.Label hintScanForEver;
        private System.Windows.Forms.CheckBox checkBoxOneIconPerDevice;
        private System.Windows.Forms.Label hintOneIconPerDevice;
        private System.Windows.Forms.CheckBox checkBoxHideUnknownBattery;
        private System.Windows.Forms.Label hintHideUnknownBattery;
        private System.Windows.Forms.FlowLayoutPanel layoutButtons;
        private System.Windows.Forms.Button buttonClose;
        private System.Windows.Forms.Button buttonAbout;
    }
}
