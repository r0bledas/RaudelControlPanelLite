namespace RaudelControlPanelLite
{
    public class SettingsForm : Form
    {
        private NumericUpDown _nudInterval;
        private CheckBox _chkAutoListDocker;
        private Button _btnOk;
        private Button _btnCancel;

        public int RefreshIntervalMs { get; private set; }
        public bool AutoListDocker { get; private set; }

        public SettingsForm(int currentIntervalMs, bool autoListDocker)
        {
            RefreshIntervalMs = currentIntervalMs;
            AutoListDocker = autoListDocker;

            Text = "Settings";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(380, 230);

            var lblInterval = new Label
            {
                Text = "Refresh interval (milliseconds):",
                Location = new Point(20, 25),
                AutoSize = true
            };

            _nudInterval = new NumericUpDown
            {
                Minimum = 500,
                Maximum = 30000,
                Increment = 500,
                Value = currentIntervalMs,
                Location = new Point(20, 50),
                Width = 280
            };

            _chkAutoListDocker = new CheckBox
            {
                Text = "Auto-list Docker containers (live)",
                Location = new Point(20, 90),
                AutoSize = true,
                Checked = autoListDocker
            };

            _btnOk = new Button
            {
                Text = "OK",
                DialogResult = DialogResult.OK,
                Location = new Point(120, 135),
                Width = 85
            };
            _btnOk.Click += (s, e) =>
            {
                RefreshIntervalMs = (int)_nudInterval.Value;
                AutoListDocker = _chkAutoListDocker.Checked;
            };

            _btnCancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(215, 135),
                Width = 85
            };

            AcceptButton = _btnOk;
            CancelButton = _btnCancel;

            Controls.AddRange(new Control[] { lblInterval, _nudInterval, _chkAutoListDocker, _btnOk, _btnCancel });
        }
    }
}
