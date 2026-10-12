using System.Drawing;
using System.Windows.Forms;
using SolidWorksCadAgent.Desktop.Ui;

namespace SolidWorksCadAgent.Desktop
{
    public partial class SettingsForm
    {
        private TextBox workspaceField, modelField, executableField;
        private ComboBox loggingField, executionField;
        private CheckBox autoModeField;
        private Label settingsMessageLabel, credentialLabel;
        private Button saveButton, closeButton;
        private Panel fieldsPanel;

        private void InitializeComponent()
        {
            Theme.Apply(this);
            Text = "CAD Agent — Settings";
            ClientSize = new Size(820, 710);
            MinimumSize = new Size(760, 740);
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            var root = Theme.Stack(74, -100, 132, 56, 48);
            root.Padding = new Padding(24, 12, 24, 20);
            var heading = Theme.Stack(40, 30);
            heading.Controls.Add(Theme.Label("Workspace settings", 18, Theme.Text, true), 0, 0);
            heading.Controls.Add(Theme.Label("Loaded from Agent Host. Changes are saved only when you select Save settings.", 9, Theme.Muted), 0, 1);
            root.Controls.Add(heading, 0, 0);

            var fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 7, Margin = new Padding(0) };
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 212));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (var i = 0; i < 5; i++) fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            fields.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            workspaceField = Theme.TextBox();
            modelField = Theme.TextBox();
            executableField = Theme.TextBox();
            loggingField = Choice("Trace", "Debug", "Information", "Warning", "Error", "Critical");
            executionField = Choice("Real", "Simulation");
            AddField(fields, "Workspace folder", workspaceField, 0);
            AddField(fields, "AI model", modelField, 1);
            AddField(fields, "SolidWorks executable", executableField, 2);
            AddField(fields, "Logging level", loggingField, 3);
            AddField(fields, "Configured execution mode", executionField, 4);
            autoModeField = new CheckBox { Text = "Auto mode — execute without manual approval", ForeColor = Theme.Text,
                BackColor = Theme.Surface, Dock = DockStyle.Fill, AutoSize = true, AccessibleName = "Auto execution mode" };
            fields.Controls.Add(autoModeField, 0, 5); fields.SetColumnSpan(autoModeField, 2);
            var warning = Theme.Label("Auto mode still requires a validated, unambiguous plan.\r\nWorkspace, model, executable and mode changes require a Host restart.\r\nLeave the executable field empty for automatic detection.", 9, Theme.Warning);
            fields.Controls.Add(warning, 0, 6); fields.SetColumnSpan(warning, 2);
            fieldsPanel = Theme.Card(fields);
            fieldsPanel.Enabled = false;
            root.Controls.Add(fieldsPanel, 0, 1);

            var security = Theme.Stack(28, 25, -100);
            security.Controls.Add(Theme.Label("Credentials & connection", 11, Theme.Text, true), 0, 0);
            credentialLabel = Theme.Label("OpenAI credential · Checking…", 9, Theme.Muted);
            security.Controls.Add(credentialLabel, 0, 1);
            security.Controls.Add(Theme.Label("Manage the generic credential in Windows Credential Manager: SolidWorksCadAgent/OpenAI.\r\nAgent Host uses localhost only. Remote access is not enabled.", 8, Theme.Muted), 0, 2);
            root.Controls.Add(Theme.Card(security, new Padding(12)), 0, 2);
            settingsMessageLabel = Theme.Label("Loading settings…", 9, Theme.Muted);
            settingsMessageLabel.AccessibleName = "Settings save status";
            root.Controls.Add(settingsMessageLabel, 0, 3);
            var actions = Theme.Actions();
            actions.FlowDirection = FlowDirection.RightToLeft;
            closeButton = Theme.Button("&Close", (sender, e) => Close()); closeButton.Width = 110;
            saveButton = Theme.Button("&Save settings", SaveButton_Click, true, "check"); saveButton.Width = 166; saveButton.Enabled = false;
            actions.Controls.AddRange(new Control[] { closeButton, saveButton });
            root.Controls.Add(actions, 0, 4);
            Controls.Add(root);
            CancelButton = closeButton;
        }

        private static ComboBox Choice(params string[] values)
        {
            var control = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Theme.Input, ForeColor = Theme.Text, FlatStyle = FlatStyle.Flat, DrawMode = DrawMode.OwnerDrawFixed };
            control.Items.AddRange(values);
            control.DrawItem += (sender, e) =>
            {
                using (var brush = new SolidBrush(Theme.Input)) e.Graphics.FillRectangle(brush, e.Bounds);
                if (e.Index >= 0) TextRenderer.DrawText(e.Graphics, control.Items[e.Index].ToString(), control.Font,
                    e.Bounds, Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
                e.DrawFocusRectangle();
            };
            return control;
        }

        private static void AddField(TableLayoutPanel panel, string label, Control field, int row)
        {
            panel.Controls.Add(Theme.Label(label, 9, Theme.Muted), 0, row);
            field.Dock = DockStyle.Top;
            field.Margin = new Padding(0, 10, 0, 8);
            field.AccessibleName = label;
            panel.Controls.Add(field, 1, row);
        }
    }
}
