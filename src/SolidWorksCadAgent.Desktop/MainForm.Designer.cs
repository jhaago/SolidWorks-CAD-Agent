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
        private Button completeButton;
        private ListBox historyListBox;
        private Button refreshHistoryButton;
        private Button olderHistoryButton;

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
            attachmentButton = ButtonAt("Image / Design Intake", 950, 124, 200, DesignIntakeButton_Click);

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
            completeButton = ButtonAt("Accept / Complete", 18, 458, 150, CompleteButton_Click);
            approveButton.Enabled = requestChangesButton.Enabled = cancelButton.Enabled = completeButton.Enabled = false;
            sendButton.Enabled = attachButton.Enabled = launchButton.Enabled = false;

            Controls.Add(LabelAt("Saved job history", 18, 502, 300));
            historyListBox = new ListBox { Left = 18, Top = 526, Width = 350, Height = 100, Anchor = AnchorStyles.Left | AnchorStyles.Bottom };

            historyListBox.SelectedIndexChanged += HistoryListBox_SelectedIndexChanged;
            refreshHistoryButton = ButtonAt("Refresh", 18, 638, 110, RefreshHistoryButton_Click);
            olderHistoryButton = ButtonAt("Load older", 138, 638, 110, OlderHistoryButton_Click);
            refreshHistoryButton.Enabled = olderHistoryButton.Enabled = false;

            Controls.AddRange(new Control[] { hostStatusLabel, solidWorksStatusLabel, messageLabel, settingsButton, attachButton, launchButton,
                promptTextBox, sendButton, attachmentButton, currentJobLabel, ambiguityTextBox, planTextBox, verificationTextBox,
                approveButton, requestChangesButton, cancelButton, completeButton, historyListBox, refreshHistoryButton, olderHistoryButton });
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
