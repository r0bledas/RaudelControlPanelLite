namespace RaudelControlPanelLite
{
    using System;
    using System.Drawing;
    using System.IO;
    using System.Text.RegularExpressions;
    using System.Windows.Forms;

    public class AddServiceForm : Form
    {
        private TextBox _txtName = null!;
        private ComboBox _cmbType = null!;
        private TextBox _txtCommand = null!;
        private TextBox _txtDirectory = null!;
        private TextBox _txtPort = null!;
        private CheckBox _chkStartup = null!;

        public ServiceItem? Result { get; private set; }

        public AddServiceForm()
        {
            Text = "Add Service";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(520, 300);
            Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

            int left = 20, right = 130, y = 20, row = 30;

            Label L(string text, int yy) => new Label
            {
                Text = text,
                Location = new Point(left, yy + 4),
                AutoSize = true
            };

            var lblName = L("Name:", y);
            _txtName = new TextBox { Location = new Point(right, y), Width = 360 };

            y += row;
            var lblType = L("Type:", y);
            _cmbType = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(right, y),
                Width = 360
            };
            _cmbType.Items.AddRange(new object[]
            {
                "Executable (.exe)",
                "Script (.ps1/.bat/.js/.py)",
                "Docker container / compose"
            });
            _cmbType.SelectedIndex = 0;

            y += row;
            var lblCommand = L("Command:", y);
            _txtCommand = new TextBox { Location = new Point(right, y), Width = 285 };
            var btnBrowse = new Button
            {
                Text = "Browse...",
                Location = new Point(right + 290, y - 1),
                Width = 70
            };
            btnBrowse.Click += BtnBrowse_Click;

            y += row;
            var lblDir = L("Directory:", y);
            _txtDirectory = new TextBox { Location = new Point(right, y), Width = 285 };
            var btnDirBrowse = new Button
            {
                Text = "Browse...",
                Location = new Point(right + 290, y - 1),
                Width = 70
            };
            btnDirBrowse.Click += (s, e) =>
            {
                using var dlg = new FolderBrowserDialog();
                if (dlg.ShowDialog(this) == DialogResult.OK) _txtDirectory.Text = dlg.SelectedPath;
            };

            y += row;
            var lblPort = L("Port (optional):", y);
            _txtPort = new TextBox { Location = new Point(right, y), Width = 120 };

            y += row;
            var lblStartup = L("Run on Startup:", y);
            _chkStartup = new CheckBox { Location = new Point(right, y + 2), AutoSize = true, Checked = true };

            y += row + 10;
            var btnOk = new Button
            {
                Text = "Add",
                DialogResult = DialogResult.OK,
                Location = new Point(right + 120, y),
                Width = 100,
                Height = 30
            };
            var btnCancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(right + 230, y),
                Width = 100,
                Height = 30
            };

            AcceptButton = btnOk;
            CancelButton = btnCancel;

            btnOk.Click += (s, e) =>
            {
                if (string.IsNullOrWhiteSpace(_txtName.Text))
                {
                    MessageBox.Show(this, "Please enter a name.", "Add Service", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    DialogResult = DialogResult.None;
                    return;
                }
                if (string.IsNullOrWhiteSpace(_txtCommand.Text))
                {
                    MessageBox.Show(this, "Please enter a command (or browse to a file).", "Add Service", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    DialogResult = DialogResult.None;
                    return;
                }

                int? port = null;
                if (!string.IsNullOrWhiteSpace(_txtPort.Text))
                {
                    if (int.TryParse(_txtPort.Text.Trim(), out int p)) port = p;
                    else
                    {
                        MessageBox.Show(this, "Port must be a number.", "Add Service", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        DialogResult = DialogResult.None;
                        return;
                    }
                }

                string name = _txtName.Text.Trim();
                Result = new ServiceItem
                {
                    Id = Slugify(name),
                    Name = name,
                    Directory = _txtDirectory.Text.Trim(),
                    Command = _txtCommand.Text.Trim(),
                    Port = port,
                    RunOnStartup = _chkStartup.Checked
                };

                if (_cmbType.SelectedIndex == 2)
                {
                    var m = Regex.Match(Result.Command, @"\bdocker\s+(?:start|stop|restart)\s+([A-Za-z0-9_.-]+)");
                    if (m.Success) Result.DockerContainerName = m.Groups[1].Value;
                }
            };

            Controls.AddRange(new Control[]
            {
                lblName, _txtName,
                lblType, _cmbType,
                lblCommand, _txtCommand, btnBrowse,
                lblDir, _txtDirectory, btnDirBrowse,
                lblPort, _txtPort,
                lblStartup, _chkStartup,
                btnOk, btnCancel
            });
        }

        private void BtnBrowse_Click(object? sender, EventArgs e)
        {
            int type = _cmbType.SelectedIndex;

            if (type == 2)
            {
                using var dlg = new OpenFileDialog
                {
                    Filter = "Docker Compose (*.yml;*.yaml)|*.yml;*.yaml|All files (*.*)|*.*",
                    Title = "Select docker-compose file"
                };
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    _txtCommand.Text = "docker-compose up";
                    _txtDirectory.Text = Path.GetDirectoryName(dlg.FileName) ?? "";
                }
                return;
            }

            using var fileDlg = new OpenFileDialog
            {
                Filter = type == 0
                    ? "Executables (*.exe)|*.exe|All files (*.*)|*.*"
                    : "Scripts (*.ps1;*.bat;*.cmd;*.js;*.py)|*.ps1;*.bat;*.cmd;*.js;*.py|All files (*.*)|*.*",
                Title = "Select file"
            };

            if (fileDlg.ShowDialog(this) != DialogResult.OK) return;

            string path = fileDlg.FileName;
            string ext = Path.GetExtension(path).ToLowerInvariant();

            string command = type == 0
                ? Quote(path)
                : ext switch
                {
                    ".ps1" => $"powershell.exe -ExecutionPolicy Bypass -NoProfile -File {Quote(path)}",
                    ".bat" or ".cmd" => $"cmd.exe /c {Quote(path)}",
                    ".js" => $"node {Quote(path)}",
                    ".py" => $"python {Quote(path)}",
                    _ => Quote(path)
                };

            _txtCommand.Text = command;
            if (string.IsNullOrWhiteSpace(_txtDirectory.Text))
                _txtDirectory.Text = Path.GetDirectoryName(path) ?? "";
        }

        private static string Quote(string value) => "\"" + value + "\"";

        private static string Slugify(string name)
        {
            string slug = Regex.Replace(name.ToLowerInvariant(), @"[^a-z0-9]+", "_").Trim('_');
            return slug.Length == 0 ? "service_" + Guid.NewGuid().ToString("N")[..8] : slug;
        }
    }
}
