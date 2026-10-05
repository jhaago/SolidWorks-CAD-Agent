using System.Drawing;
using System.Windows.Forms;

namespace SolidWorksCadAgent.Desktop
{
    public partial class SettingsForm
    {
        private TextBox workspaceTextBox, modelTextBox, executableTextBox, loggingTextBox;
        private ComboBox modeComboBox;
        private Button saveButton;
        private Label statusLabel;
        private void InitializeComponent()
        {
            Text = "SolidWorks CAD Agent Settings";
            ClientSize = new Size(650, 455);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;

            workspaceTextBox = Field("Workspace root", "", 22, 54);
            modelTextBox = Field("OpenAI model", "", 22, 120);
            executableTextBox = Field("SOLIDWORKS executable override", "", 22, 186);
            loggingTextBox = Field("Logging level", "", 22, 252);
            workspaceTextBox.ReadOnly = executableTextBox.ReadOnly = loggingTextBox.ReadOnly = true;
            modelTextBox.Enabled = false;

            Controls.Add(new Label { Text = "Execution mode", Left = 22, Top = 296, Width = 270 });
            modeComboBox = new ComboBox { Left = 22, Top = 318, Width = 270, DropDownStyle = ComboBoxStyle.DropDownList, Enabled = false };
            modeComboBox.Items.AddRange(new object[] { "Real", "Simulation" });
            statusLabel = new Label { Text = "Loading settings…", Left = 22, Top = 359, Width = 598, Height = 46 };
            saveButton = new Button { Text = "Save", Left = 410, Top = 412, Width = 100, Enabled = false };
            saveButton.Click += SaveButton_Click;

            var autoMode = new CheckBox { Text = "Auto mode (manual approval is the V1 default)", Left = 330, Top = 56, Width = 290, Enabled = false };
            var credential = new Label { Text = "OpenAI credential: Windows Credential Manager\nTarget: SolidWorksCadAgent/OpenAI", Left = 330, Top = 116, Width = 300, Height = 48 };
            var remote = new GroupBox { Text = "V2 Remote Access", Left = 330, Top = 190, Width = 290, Height = 100 };
            remote.Controls.Add(new Label { Text = "Not enabled in V1", Left = 18, Top = 38, Width = 220 });
            var close = new Button { Text = "Close", Left = 520, Top = 412, Width = 100, DialogResult = DialogResult.Cancel };

            Controls.AddRange(new Control[] { workspaceTextBox, modelTextBox, executableTextBox, loggingTextBox, autoMode, credential, remote, modeComboBox, statusLabel, saveButton, close });
            AcceptButton = saveButton;
            CancelButton = close;
        }

        private TextBox Field(string label, string value, int left, int top)
        {
            Controls.Add(new Label { Text = label, Left = left, Top = top - 22, Width = 270 });
            return new TextBox { Text = value, Left = left, Top = top, Width = 270 };
        }
    }
}
