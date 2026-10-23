namespace RaudelControlPanelLite
{
    using System;
    using System.Diagnostics;
    using System.Drawing;
    using System.IO;
    using System.Text;
    using System.Windows.Forms;

    public class PropertiesForm : Form
    {
        public PropertiesForm(ServiceItem svc)
        {
            Text = $"Properties: {svc.Name}";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(490, 440);
            Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

            int leftCol = 20;
            int rightCol = 145;
            int y = 20;
            int rowSpacing = 24;

            Label CreateLabel(string text, int yPos) => new Label
            {
                Text = text,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                Location = new Point(leftCol, yPos),
                Size = new Size(120, 20),
                TextAlign = ContentAlignment.MiddleLeft
            };

            Label CreateValue(string text, int yPos) => new Label
            {
                Text = text,
                Location = new Point(rightCol, yPos),
                Size = new Size(320, 20),
                TextAlign = ContentAlignment.MiddleLeft
            };

            // Service
            var lblNameTitle = CreateLabel("Service:", y);
            var lblNameVal = CreateValue(svc.Name, y);

            // ID
            y += rowSpacing;
            var lblIdTitle = CreateLabel("ID:", y);
            var lblIdVal = CreateValue(svc.Id, y);

            // Status
            y += rowSpacing;
            var lblStatusTitle = CreateLabel("Status:", y);
            string statusText = svc.IsOnline ? "Online" : (svc.IsStarting ? "Starting..." : "Offline");
            var lblStatusVal = CreateValue(statusText, y);

            // User
            y += rowSpacing;
            var lblUserTitle = CreateLabel("User:", y);
            var lblUserVal = CreateValue(string.IsNullOrEmpty(svc.User) ? "-" : svc.User, y);

            // Port
            y += rowSpacing;
            var lblPortTitle = CreateLabel("Port:", y);
            var lblPortVal = CreateValue(svc.PortDisplayString, y);

            // Resources
            y += rowSpacing;
            var lblResTitle = CreateLabel("Resources:", y);
            string resText = svc.IsOnline ? $"{svc.Cpu:F1}% CPU | {svc.Mem:F1} MB RAM" : "-";
            var lblResVal = CreateValue(resText, y);

            // Active PIDs
            y += rowSpacing;
            var lblPidsTitle = CreateLabel("Active PIDs:", y);
            string pidsText = svc.ActivePids.Count > 0 && svc.ActivePids[0] != 0
                ? string.Join(", ", svc.ActivePids)
                : (svc.IsOnline ? svc.PidsString : "-");
            var lblPidsVal = CreateValue(pidsText, y);

            // Run on Startup
            y += rowSpacing;
            var lblStartupTitle = CreateLabel("Run on Startup:", y);
            var lblStartupVal = CreateValue(svc.RunOnStartup ? "Yes" : "No", y);

            // Directory
            y += rowSpacing;
            var lblDirTitle = CreateLabel("Directory:", y);
            var txtDir = new TextBox
            {
                Text = svc.Directory,
                ReadOnly = true,
                Location = new Point(rightCol, y),
                Size = new Size(320, 23)
            };

            // Command Line Log
            y += rowSpacing + 6;
            var lblCmdTitle = CreateLabel("Command Line Log:", y);
            y += 22;
            var txtCmd = new TextBox
            {
                Text = svc.Command,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Font = new Font("Consolas", 8.5F),
                Location = new Point(leftCol, y),
                Size = new Size(445, 80)
            };

            // Buttons
            y += 92;
            var btnCopy = new Button
            {
                Text = "Copy to Clipboard",
                Location = new Point(leftCol, y),
                Size = new Size(130, 28)
            };

            var copyTimer = new System.Windows.Forms.Timer { Interval = 1500 };
            copyTimer.Tick += (s, e) =>
            {
                btnCopy.Text = "Copy to Clipboard";
                copyTimer.Stop();
            };

            btnCopy.Click += (s, e) =>
            {
                var sb = new StringBuilder();
                sb.AppendLine($"Service: {svc.Name}");
                sb.AppendLine($"ID: {svc.Id}");
                sb.AppendLine($"Status: {statusText}");
                sb.AppendLine($"User: {(string.IsNullOrEmpty(svc.User) ? "-" : svc.User)}");
                sb.AppendLine($"Port: {svc.PortDisplayString}");
                sb.AppendLine($"Resources: {resText}");
                sb.AppendLine($"Active PIDs: {pidsText}");
                sb.AppendLine($"Run on Startup: {(svc.RunOnStartup ? "Yes" : "No")}");
                sb.AppendLine($"Directory: {svc.Directory}");
                sb.AppendLine($"Command Line Log: {svc.Command}");

                try
                {
                    Clipboard.SetText(sb.ToString());
                    btnCopy.Text = "Copied!";
                    copyTimer.Start();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };

            var btnOpenDir = new Button
            {
                Text = "Open Directory",
                Location = new Point(leftCol + 140, y),
                Size = new Size(110, 28)
            };
            btnOpenDir.Click += (s, e) =>
            {
                if (Directory.Exists(svc.Directory))
                {
                    try { Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{svc.Directory}\"", UseShellExecute = true }); } catch { }
                }
            };

            var btnClose = new Button
            {
                Text = "OK",
                DialogResult = DialogResult.OK,
                Location = new Point(380, y),
                Size = new Size(85, 28)
            };

            AcceptButton = btnClose;
            CancelButton = btnClose;

            Controls.AddRange(new Control[]
            {
                lblNameTitle, lblNameVal,
                lblIdTitle, lblIdVal,
                lblStatusTitle, lblStatusVal,
                lblUserTitle, lblUserVal,
                lblPortTitle, lblPortVal,
                lblResTitle, lblResVal,
                lblPidsTitle, lblPidsVal,
                lblStartupTitle, lblStartupVal,
                lblDirTitle, txtDir,
                lblCmdTitle, txtCmd,
                btnCopy, btnOpenDir, btnClose
            });
        }
    }
}
