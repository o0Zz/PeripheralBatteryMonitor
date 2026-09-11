using System;
using System.Collections.Concurrent;
using System.Windows.Forms;

namespace PeripheralBatteryMonitor
{
    public partial class Info : Form
    {
        private DeviceManager deviceManager;
        private Func<bool> hideUnknownBattery;
        private Action refreshNow;

            //refreshNow is Settings.RefreshNow: this window shows battery state but does not
            //own the polling, the same reason hideUnknownBattery arrives as a callback rather
            //than this form reading the registry itself.
        public Info(DeviceManager deviceManager, Func<bool> hideUnknownBattery, Action refreshNow)
        {
            InitializeComponent();
            this.deviceManager = deviceManager;
            this.hideUnknownBattery = hideUnknownBattery;
            this.refreshNow = refreshNow;
        }

        /// <summary>
        /// Pushes the current language onto this window. Called by Settings when the picker
        /// changes, because this popup is created once and kept for the whole session.
        /// The rows themselves are rebuilt on every Activated, so only the chrome needs it.
        /// </summary>
        internal void ApplyStrings()
        {
            if (listView1.Columns.Count == 3)
            {
                listView1.Columns[0].Text = Strings.Get("info.column.device");
                listView1.Columns[1].Text = Strings.Get("info.column.state");
                listView1.Columns[2].Text = Strings.Get("info.column.battery");
            }
            toolStripStatusLabel1.Text = Strings.Get("info.lastUpdated") + " ";
            toolStripRefresh.Text = Strings.Get("button.refresh");
        }

            //Columns are built here rather than in the constructor because ListView
            //columns are the one thing AutoScaleMode.Font does not scale: their widths
            //are plain integers the control never revisits. By OnLoad the form has been
            //auto-scaled, so ClientSize and DeviceDpi are the real ones and the two
            //fixed columns can be scaled to match.
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            if (listView1.Columns.Count != 0)
                return;

            int fixedColumn = Scale(100);
            listView1.Columns.Add(Strings.Get("info.column.device"), fixedColumn);
            listView1.Columns.Add(Strings.Get("info.column.state"), fixedColumn);
            listView1.Columns.Add(Strings.Get("info.column.battery"), fixedColumn);

            ApplyStrings();

                //Docking resizes the control but not its columns -- a ListView column width is
                //a plain integer it never revisits, the same reason these are scaled by hand in
                //the first place. So re-flow them whenever the control's width changes, which
                //covers both the initial layout and every drag of the window border.
            listView1.ClientSizeChanged += delegate { LayoutColumns(); };
            LayoutColumns();
        }

        /// <summary>
        /// Gives the two fixed columns a scaled width and lets the device name absorb whatever
        /// is left, so the list always spans the window exactly.
        /// </summary>
        private void LayoutColumns()
        {
            if (listView1.Columns.Count != 3)
                return;

            int fixedColumn = Scale(100);

                //ClientSize already excludes a vertical scrollbar when one is showing. The
                //extra pixel keeps the total just inside the client area, because a device
                //column sized to the exact remainder produces a horizontal scrollbar.
            int device = listView1.ClientSize.Width - (2 * fixedColumn) - 1;

                //A window dragged narrow must not produce a negative width, which throws.
            int minimum = Scale(60);
            if (device < minimum)
                device = minimum;

            listView1.Columns[0].Width = device;
            listView1.Columns[1].Width = fixedColumn;
            listView1.Columns[2].Width = fixedColumn;
        }

            //96 is the DPI the designer laid this form out at -- see AutoScaleDimensions.
        private int Scale(int designPixels)
        {
            return (int)Math.Round(designPixels * (this.DeviceDpi / 96.0));
        }

        public new void Show()
        {
            base.Show();

            this.Left = Cursor.Position.X - this.Width;
            this.Top = Cursor.Position.Y - this.Height - 20;
            
            this.Activate();
        }

        private void Info_Deactivate(object sender, EventArgs e)
        {
            Hide();
        }

        private void Info_Activated(object sender, EventArgs e)
        {
            Populate();
        }

            //Clicking an item on this form does not deactivate it, so the Deactivate handler
            //above does not fight the button. Poll first, then rebuild from what came back.
        private void toolStripRefresh_Click(object sender, EventArgs e)
        {
            if (refreshNow != null)
                refreshNow();

            Populate();
        }

        /// <summary>
        /// Rebuilds the rows from the battery state already cached on each device. Reads
        /// only -- polling is RefreshNow's job, and this runs on every Activated.
        /// </summary>
        private void Populate()
        {
            DateTime ?lastUpdated = null;

            listView1.BeginUpdate();
            listView1.Items.Clear();

            ConcurrentDictionary<string, BatteryDevice> deviceDict = deviceManager.getDeviceList();

            foreach (BatteryDevice device in deviceDict.Values)
            {
                int theBatteryLevel = device.GetBatteryLevel();
                lastUpdated = device.GetLastUpdatedTime();

                if (theBatteryLevel < 0 && hideUnknownBattery != null && hideUnknownBattery())
                    continue;

                ListViewItem listViewItem = new ListViewItem
                {
                    Text = device.GetName()
                };
                listViewItem.SubItems.Add(Strings.Get(device.IsConnected()
                    ? "info.state.connected" : "info.state.disconnected"));
                listViewItem.SubItems.Add(theBatteryLevel < 0
                    ? "?" : theBatteryLevel + "%");
                listView1.Items.Add(listViewItem);

            }

            listView1.EndUpdate();

            toolStripStatusLabel2.Text = lastUpdated.HasValue
                ? lastUpdated.Value.ToString()
                : Strings.Get("info.never");
        }
    }
}
