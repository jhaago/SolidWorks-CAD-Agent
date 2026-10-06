using System.Drawing;
using System.Windows.Forms;

namespace SolidWorksCadAgent.Desktop
{
    public sealed class RevisionInstructionsForm : Form
    {
        private readonly TextBox _instructions;
        public string Instructions => _instructions.Text.Trim();

        public RevisionInstructionsForm(string ambiguity)
        {
            Text = "Clarify or revise the plan";
            ClientSize = new Size(600, 350);
            MinimumSize = new Size(500, 350);
            StartPosition = FormStartPosition.CenterParent;
            var context = new TextBox { Left = 16, Top = 16, Width = 568, Height = 90,
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                Text = string.IsNullOrWhiteSpace(ambiguity) ? "Describe the changes to the proposed plan. The revised plan will be shown for review." : ambiguity,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            _instructions = new TextBox { Left = 16, Top = 122, Width = 568, Height = 164,
                Multiline = true, ScrollBars = ScrollBars.Vertical,
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
            var submit = new Button { Text = "Revise plan", Left = 348, Top = 304, Width = 110, Height = 30,
                Enabled = false, Anchor = AnchorStyles.Bottom | AnchorStyles.Right, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "Back", Left = 474, Top = 304, Width = 110, Height = 30,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right, DialogResult = DialogResult.Cancel };
            _instructions.TextChanged += (sender, args) => submit.Enabled = !string.IsNullOrWhiteSpace(_instructions.Text);
            Controls.AddRange(new Control[] { context, _instructions, submit, cancel });
            CancelButton = cancel;
            Shown += (sender, args) => _instructions.Focus();
        }
    }
}