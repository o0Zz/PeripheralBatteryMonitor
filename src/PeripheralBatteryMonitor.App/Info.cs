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

            //This window renders battery state, it does not own it -- the same reason
            //hideUnknownBattery arrives as a callback rather than being read from the registry.
        public Info(DeviceManager deviceManager, Func<bool> hideUnknownBattery, Action refreshNow)
        {
            InitializeComponent();
            this.deviceManager = deviceManager;
            this.hideUnknownBattery = hideUnknownBattery;
            this.refreshNow = refreshNow;
        }

        /// <summary>
        /// Called by Settings when the picker changes, because this popup is created once and
        /// kept for the session. The rows are rebuilt on every Activated, so only the chrome
        /// needs this.
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

            //Here rather than in the constructor: ListView column widths are plain integers
            //AutoScaleMode.Font never touches, and by OnLoad the form has been auto-scaled, so
            //ClientSize and DeviceDpi are the real ones.
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

                //Docking resizes the control but not its columns, so re-flow them whenever the
                //width changes: the initial layout and every drag of the window border.
            listView1.ClientSizeChanged += delegate { LayoutColumns(); };
            LayoutColumns();
        }

        /// <summary>
        /// The two fixed columns get a scaled width and the device name absorbs the remainder,
        /// so the list spans the window exactly.
        /// </summary>
        private void LayoutColumns()
        {
            if (listView1.Columns.Count != 3)
                return;

            int fixedColumn = Scale(100);

                //The extra pixel keeps the total just inside the client area: a device column
                //sized to the exact remainder produces a horizontal scrollbar.
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

                //Clicking a ToolStripItem does not deactivate the form, so the Deactivate
                //handler above does not fight this button.
        private void toolStripRefresh_Click(object sender, EventArgs e)
        {
            if (refreshNow != null)
                refreshNow();

            Populate();
        }

        /// <summary>
        /// Reads cached state only -- polling is RefreshNow's job. Runs on every Activated.
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
