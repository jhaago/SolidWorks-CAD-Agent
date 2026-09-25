using System.Drawing;
using System.Windows.Forms;

namespace SolidWorksCadAgent.Desktop
{
    public partial class SettingsForm
    {
        private void InitializeComponent()
        {
            Text = "SolidWorks CAD Agent Settings";
            ClientSize = new Size(650, 410);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;

            var workspace = Field("Workspace root", @"C:\SolidWorks-CAD-Agent\Workspace", 22, 54);
            var model = Field("OpenAI model", "gpt-5.6-sol", 22, 120);
            var executable = Field("SOLIDWORKS executable override", "Automatic detection", 22, 186);
            var logging = Field("Logging level", "Information", 22, 252);
            workspace.ReadOnly = model.ReadOnly = executable.ReadOnly = logging.ReadOnly = true;

            var autoMode = new CheckBox { Text = "Auto mode (manual approval is the V1 default)", Left = 330, Top = 56, Width = 290, Enabled = false };
            var credential = new Label { Text = "OpenAI credential: Windows Credential Manager\nTarget: SolidWorksCadAgent/OpenAI", Left = 330, Top = 116, Width = 300, Height = 48 };
            var remote = new GroupBox { Text = "V2 Remote Access", Left = 330, Top = 190, Width = 290, Height = 100 };
            remote.Controls.Add(new Label { Text = "Not enabled in V1", Left = 18, Top = 38, Width = 220 });
            var close = new Button { Text = "Close", Left = 520, Top = 350, Width = 100, DialogResult = DialogResult.OK };

            Controls.AddRange(new Control[] { workspace, model, executable, logging, autoMode, credential, remote, close });
            AcceptButton = close;
        }

        private TextBox Field(string label, string value, int left, int top)
        {
            Controls.Add(new Label { Text = label, Left = left, Top = top - 22, Width = 270 });
            return new TextBox { Text = value, Left = left, Top = top, Width = 270 };
        }
    }
}
