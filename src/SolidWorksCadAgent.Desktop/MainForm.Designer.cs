using System.Drawing;
using System.Windows.Forms;

namespace SolidWorksCadAgent.Desktop
{
    public partial class MainForm
    {
        private Label hostStatusLabel;
        private Label solidWorksStatusLabel;
        private Label messageLabel;
        private TextBox promptTextBox;
        private Button sendButton;
        private Button attachButton;
        private Button launchButton;
        private Button settingsButton;
        private Button attachmentButton;
        private Label currentJobLabel;
        private TextBox ambiguityTextBox;
        private TextBox planTextBox;
        private TextBox verificationTextBox;
        private Button approveButton;
        private Button requestChangesButton;
        private Button cancelButton;
        private ListBox historyListBox;

        private void InitializeComponent()
        {
            Text = "SolidWorks CAD Agent";
            ClientSize = new Size(1180, 760);
            MinimumSize = new Size(980, 680);
            StartPosition = FormStartPosition.CenterScreen;

            hostStatusLabel = LabelAt("Agent Host: checking...", 18, 16, 260);
            solidWorksStatusLabel = LabelAt("SOLIDWORKS: checking...", 290, 16, 390);
            messageLabel = LabelAt("Starting...", 18, 46, 790);
            settingsButton = ButtonAt("Settings", 1040, 12, 110, SettingsButton_Click);
            attachButton = ButtonAt("Attach", 790, 12, 110, AttachButton_Click);
            launchButton = ButtonAt("Launch", 910, 12, 110, LaunchButton_Click);

            promptTextBox = new TextBox { Left = 18, Top = 82, Width = 920, Height = 76, Multiline = true, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            sendButton = ButtonAt("Send", 1040, 82, 110, SendButton_Click);
            attachmentButton = ButtonAt("Images: V1 disabled", 950, 124, 200, null);
            attachmentButton.Enabled = false;

            currentJobLabel = LabelAt("No current job", 18, 178, 900);
            currentJobLabel.Font = new Font(currentJobLabel.Font, FontStyle.Bold);
            ambiguityTextBox = BoxAt(18, 216, 350, 190);
            planTextBox = BoxAt(382, 216, 430, 410);
            verificationTextBox = BoxAt(826, 216, 324, 410);
            Controls.Add(LabelAt("Ambiguities", 18, 194, 200));
            Controls.Add(LabelAt("Proposed plan", 382, 194, 200));
            Controls.Add(LabelAt("Progress and verification", 826, 194, 260));

            approveButton = ButtonAt("Approve Build", 18, 420, 110, ApproveButton_Click);
            requestChangesButton = ButtonAt("Request Changes", 138, 420, 130, RequestChangesButton_Click);
            cancelButton = ButtonAt("Cancel", 278, 420, 90, CancelButton_Click);
            approveButton.Enabled = requestChangesButton.Enabled = cancelButton.Enabled = false;

            Controls.Add(LabelAt("Job history (this desktop session)", 18, 468, 300));
            historyListBox = new ListBox { Left = 18, Top = 492, Width = 350, Height = 134, Anchor = AnchorStyles.Left | AnchorStyles.Bottom };

            Controls.AddRange(new Control[] { hostStatusLabel, solidWorksStatusLabel, messageLabel, settingsButton, attachButton, launchButton,
                promptTextBox, sendButton, attachmentButton, currentJobLabel, ambiguityTextBox, planTextBox, verificationTextBox,
                approveButton, requestChangesButton, cancelButton, historyListBox });
        }

        private static Label LabelAt(string text, int left, int top, int width) =>
            new Label { Text = text, Left = left, Top = top, Width = width, Height = 24 };

        private static TextBox BoxAt(int left, int top, int width, int height) =>
            new TextBox { Left = left, Top = top, Width = width, Height = height, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };

        private static Button ButtonAt(string text, int left, int top, int width, System.EventHandler handler)
        {
            var button = new Button { Text = text, Left = left, Top = top, Width = width, Height = 32 };
            if (handler != null) button.Click += handler;
            return button;
        }
    }
}
