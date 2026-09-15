using System;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace PeripheralBatteryMonitor
{
    internal sealed class AboutForm : Form
    {
        private const string ProjectUrl = "https://github.com/o0Zz/PeripheralBatteryMonitor";

            //Not derived from BatteryProviderRegistry: a provider is a way of reading a battery,
            //not a device family a user would recognise. Keep in step with CLAUDE.md, the README
            //and every Languages/*.lang file.
        private static readonly string[] SupportedDeviceKeys =
        {
            "about.device.ble",
            "about.device.classic",
            "about.device.apple",
            "about.device.logitech",
            "about.device.steelseries",
            "about.device.razer",
        };

        internal AboutForm()
        {
            Text = Strings.Get("about.title");
            Icon = Properties.Resources.Icon_Battery_100;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;

                //CenterScreen, not CenterParent: the owner is the hidden Settings form, and
                //centering on an invisible window lands this in the top-left corner.
            StartPosition = FormStartPosition.CenterScreen;

            AutoScaleMode = AutoScaleMode.Font;
            AutoScaleDimensions = new SizeF(6F, 13F);
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            TableLayoutPanel body = new TableLayoutPanel();
            body.Dock = DockStyle.Fill;
            body.ColumnCount = 1;
            body.AutoSize = true;
            body.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            body.Padding = new Padding(16, 14, 16, 6);
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

            Label name = new Label();
            name.Text = Strings.Get("app.name");
            name.Font = new Font(Font.FontFamily, 13F, FontStyle.Bold);
            name.AutoSize = true;
            name.Margin = new Padding(0, 0, 0, 2);

            Label version = new Label();
            version.Text = Strings.Format("about.version", Assembly.GetExecutingAssembly().GetName().Version);
            version.AutoSize = true;
            version.ForeColor = SystemColors.GrayText;
            version.Margin = new Padding(2, 0, 0, 12);

            Label what = Paragraph(Strings.Get("about.what"));
            what.Margin = new Padding(2, 0, 0, 12);

            Label supportedCaption = new Label();
            supportedCaption.Text = Strings.Get("about.supported");
            supportedCaption.Font = new Font(Font, FontStyle.Bold);
            supportedCaption.AutoSize = true;
            supportedCaption.Margin = new Padding(2, 0, 0, 4);

            string[] devices = new string[SupportedDeviceKeys.Length];
            for (int i = 0; i < devices.Length; i++)
                devices[i] = Strings.Get(SupportedDeviceKeys[i]);

            Label supported = Paragraph("• " + string.Join("\n• ", devices));
            supported.Margin = new Padding(10, 0, 0, 14);

            LinkLabel link = new LinkLabel();
            link.Text = Strings.Format("about.author", ProjectUrl);
            link.AutoSize = true;
            link.Margin = new Padding(2, 0, 0, 0);
            link.LinkArea = new LinkArea(link.Text.IndexOf("https", StringComparison.Ordinal), ProjectUrl.Length);
            link.LinkClicked += OpenProjectPage;

            Button ok = new Button();
            ok.Text = Strings.Get("button.ok");
            ok.AutoSize = true;
            ok.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            ok.MinimumSize = new Size(84, 26);
            ok.DialogResult = DialogResult.OK;

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Bottom;
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.AutoSize = true;
            buttons.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            buttons.Padding = new Padding(8, 6, 14, 10);
            buttons.Controls.Add(ok);

            Control[] rows = { name, version, what, supportedCaption, supported, link };
            foreach (Control row in rows)
            {
                body.RowCount++;
                body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                body.Controls.Add(row);
            }

                //Fill first, Bottom second: docking is applied from the highest index down, so
                //the filled panel has to sit at index 0 to be laid out last and take what is left.
            Controls.Add(body);
            Controls.Add(buttons);

            AcceptButton = ok;
            CancelButton = ok;
        }

            //MaximumSize with a zero height turns AutoSize into "wrap at this width and grow
            //downwards", and WinForms scales it with the rest of the form.
            //
            //Measured, not guessed: at 96 DPI the longest line is the Spanish Bluetooth Low
            //Energy entry at 425 px -- five past the 420 this used to wrap at, which spilled
            //that bullet onto a second line. Re-measure before shrinking it.
        private const int WrapWidth = 600;

        private static Label Paragraph(string text)
        {
            Label label = new Label();
            label.Text = text;
            label.AutoSize = true;
            label.MaximumSize = new Size(WrapWidth, 0);
            label.Margin = new Padding(2, 0, 0, 4);
            return label;
        }

        private void OpenProjectPage(object sender, LinkLabelLinkClickedEventArgs e)
        {
            try
            {
                Process.Start(ProjectUrl);
                ((LinkLabel)sender).LinkVisited = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ProjectUrl + "\n\n" + ex.Message, Strings.Get("about.link.failed"),
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }
}
