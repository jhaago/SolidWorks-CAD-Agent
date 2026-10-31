using System;
using System.Drawing;
using System.Windows.Forms;
using SolidWorksCadAgent.Desktop.Api;
using SolidWorksCadAgent.Desktop.Ui;

namespace SolidWorksCadAgent.Desktop
{
    public partial class MainForm
    {
        private Label hostStatusLabel, solidWorksStatusLabel, messageLabel, currentJobLabel, modeLabel;
        private Label workflowLabel, planSummaryLabel, activitySummaryLabel, composerLabel;
        private TextBox promptTextBox, ambiguityTextBox, planTextBox, verificationTextBox, briefTextBox, requestTextBox;
        private Button sendButton, attachButton, launchButton, settingsButton, refreshButton;
        private Button approveButton, requestChangesButton, cancelButton, newJobButton, moreHistoryButton;
        private ListBox historyListBox;
        private DataGridView planGrid, activityGrid;
        private ProgressBar busyProgress;
        private WorkspaceTabs reviewTabs;
        private readonly ToolTip _tips = new ToolTip();
        private Button completeButton, attachmentButton;

        private void InitializeComponent()
        {
            SuspendLayout();
            Theme.Apply(this);
            Text = "SolidWorks CAD Agent";
            ClientSize = new Size(1440, 900);
            MinimumSize = new Size(1180, 800);
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;
            var shell = new TableLayoutPanel { ColumnCount = 2, RowCount = 1,
                Margin = new Padding(0), Padding = new Padding(0) };
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 232));
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            shell.Controls.Add(BuildSidebar(), 0, 0);
            var workspace = Theme.Stack(76, 74, -100, 36);
            workspace.Padding = new Padding(24, 0, 24, 0);
            workspace.Controls.Add(BuildHeader(), 0, 0);
            workspace.Controls.Add(BuildConnections(), 0, 1);
            workspace.Controls.Add(BuildWorkspace(), 0, 2);
            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            messageLabel = Theme.Label("Connecting to Agent Host…", 9, Theme.Muted);
            messageLabel.AccessibleName = "Application status";
            busyProgress = new ProgressBar { Dock = DockStyle.Fill, Style = ProgressBarStyle.Marquee,
                Visible = false, MarqueeAnimationSpeed = 30, Margin = new Padding(8, 14, 0, 14), AccessibleName = "Request in progress" };
            footer.Controls.Add(messageLabel, 0, 0);
            footer.Controls.Add(busyProgress, 1, 0);
            workspace.Controls.Add(footer, 0, 3);
            shell.Controls.Add(workspace, 1, 0);
            // Windows can constrain a form to a screen smaller than MinimumSize.
            // Keep the layout readable and let that smaller viewport scroll it.
            var viewport = new Panel { Dock = DockStyle.Fill, AutoScroll = true,
                AccessibleName = "Scrollable design workspace" };
            // MinimumSize participates in WinForms DPI scaling with the controls.
            shell.MinimumSize = new Size(1164, 761);
            viewport.Controls.Add(shell);
            viewport.Resize += (sender, args) => shell.Size = new Size(
                Math.Max(shell.MinimumSize.Width, viewport.ClientSize.Width),
                Math.Max(shell.MinimumSize.Height, viewport.ClientSize.Height));
            shell.Size = shell.MinimumSize;
            Controls.Add(viewport);
            ResumeLayout(true);
            UpdateActions();
        }

        private Control BuildSidebar()
        {
            var sidebar = Theme.Stack(78, 104, 48, -100, 48, 92);
            sidebar.BackColor = Theme.Surface;
            sidebar.Padding = new Padding(18, 0, 18, 18);
            var brand = Theme.Stack(45, 23);
            brand.Controls.Add(Theme.Label("CAD / AGENT", 16, Theme.Text, true), 0, 0);
            brand.Controls.Add(Theme.Label("SOLIDWORKS WORKSPACE", 8, Theme.Muted), 0, 1);
            sidebar.Controls.Add(brand, 0, 0);
            newJobButton = Theme.Button("&New request", NewJobButton_Click, false, "plus");
            newJobButton.Dock = DockStyle.Fill;
            newJobButton.Margin = new Padding(0, 7, 0, 11);
            var requestActions = Theme.Stack(52, 52);
            requestActions.Controls.Add(newJobButton, 0, 0);
            attachmentButton = Theme.Button("Design intake", DesignIntakeButton_Click);
            attachmentButton.Dock = DockStyle.Fill;
            requestActions.Controls.Add(attachmentButton, 0, 1);
            sidebar.Controls.Add(requestActions, 0, 1);
            sidebar.Controls.Add(Theme.Label("RECENT JOBS", 8, Theme.Muted, true), 0, 2);
            historyListBox = new ListBox { Dock = DockStyle.Fill, BackColor = Theme.Surface, ForeColor = Theme.Text,
                BorderStyle = BorderStyle.None, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 70,
                IntegralHeight = false, Margin = new Padding(0), AccessibleName = "Persisted CAD job history" };
            historyListBox.FormattingEnabled = true;
            historyListBox.Format += (sender, e) => e.Value = JobPresentation.Redact(e.Value?.ToString());
            historyListBox.DrawItem += HistoryListBox_DrawItem;
            historyListBox.SelectedIndexChanged += HistoryListBox_SelectedIndexChanged;
            sidebar.Controls.Add(historyListBox, 0, 3);
            moreHistoryButton = Theme.Button("Load &older jobs", MoreHistoryButton_Click);
            moreHistoryButton.Dock = DockStyle.Fill;
            moreHistoryButton.Margin = new Padding(0, 8, 0, 2);
            moreHistoryButton.Enabled = false;
            sidebar.Controls.Add(moreHistoryButton, 0, 4);
            sidebar.Controls.Add(Theme.Label("Native CAD, with review\r\n\r\nUse Design intake for images.\r\nInspect native output in SolidWorks.", 8, Theme.Muted), 0, 5);
            return sidebar;
        }

        private Control BuildHeader()
        {
            var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
            header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 128));
            var title = Theme.Stack(45, 25);
            title.Controls.Add(Theme.Label("Design workspace", 20, Theme.Text, true), 0, 0);
            title.Controls.Add(Theme.Label("Describe. Review. Build. Verify.", 9, Theme.Muted), 0, 1);
            header.Controls.Add(title, 0, 0);
            settingsButton = Theme.Button("&Settings", SettingsButton_Click, false, "settings");
            settingsButton.Dock = DockStyle.Fill;
            settingsButton.Margin = new Padding(0, 19, 0, 19);
            header.Controls.Add(settingsButton, 1, 0);
            return header;
        }

        private Control BuildConnections()
        {
            var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
            row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 294));
            var states = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = new Padding(0) };
            states.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            states.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            states.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            states.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            hostStatusLabel = Theme.Label("Host · Checking…", 9, Theme.Muted);
            solidWorksStatusLabel = Theme.Label("SolidWorks · Checking…", 9, Theme.Muted);
            modeLabel = Theme.Label("Execution mode · Unknown", 8, Theme.Warning);
            states.Controls.Add(hostStatusLabel, 0, 0);
            states.Controls.Add(solidWorksStatusLabel, 1, 0);
            states.Controls.Add(modeLabel, 0, 1);
            states.SetColumnSpan(modeLabel, 2);
            row.Controls.Add(states, 0, 0);
            var actions = Theme.Actions();
            attachButton = Theme.Button("&Attach", AttachButton_Click); attachButton.Width = 86;
            launchButton = Theme.Button("&Launch", LaunchButton_Click); launchButton.Width = 86;
            refreshButton = Theme.Button("Refresh", RefreshButton_Click, false, "refresh"); refreshButton.Width = 102;
            actions.Controls.AddRange(new Control[] { attachButton, launchButton, refreshButton });
            row.Controls.Add(actions, 1, 0);
            return Theme.Card(row, new Padding(12, 9, 8, 9));
        }

        private Control BuildWorkspace()
        {
            var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
            row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 43));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 57));
            var conversation = Theme.Stack(28, 112, -100, 208);
            conversation.Margin = new Padding(0, 0, 16, 0);
            conversation.Controls.Add(Theme.Label("DESIGN REQUEST", 9, Theme.Muted, true), 0, 0);
            var request = Theme.Stack(28, -100);
            request.Controls.Add(Theme.Label("Your brief", 10, Theme.Text, true), 0, 0);
            requestTextBox = Theme.TextBox(true, true);
            requestTextBox.Text = "No job selected. Start with a new CAD request below.";
            requestTextBox.AccessibleName = "Submitted CAD request";
            request.Controls.Add(requestTextBox, 0, 1);
            conversation.Controls.Add(Theme.Card(request), 0, 1);
            var response = Theme.Stack(30, -100, 26, 60);
            response.Controls.Add(Theme.Label("Agent interpretation", 11, Theme.Text, true), 0, 0);
            briefTextBox = Theme.TextBox(true, true);
            briefTextBox.Text = "The design interpretation will appear here after planning.";
            briefTextBox.AccessibleName = "Agent design interpretation";
            response.Controls.Add(briefTextBox, 0, 1);
            response.Controls.Add(Theme.Label("Clarifications & safeguards", 9, Theme.Warning), 0, 2);
            ambiguityTextBox = Theme.TextBox(true, true);
            ambiguityTextBox.Text = "Include dimensions, units, planes and feature intent.";
            ambiguityTextBox.AccessibleName = "Design clarifications";
            response.Controls.Add(ambiguityTextBox, 0, 3);
            conversation.Controls.Add(Theme.Card(response), 0, 2);
            var composer = Theme.Stack(28, -100, 28, 46);
            composerLabel = Theme.Label("Describe a part or request a revision", 10, Theme.Text, true);
            composer.Controls.Add(composerLabel, 0, 0);
            promptTextBox = Theme.TextBox(false, true);
            promptTextBox.AccessibleName = "CAD instruction or revision details";
            composer.Controls.Add(promptTextBox, 0, 1);
            composer.Controls.Add(Theme.Label("Ctrl+Enter to submit · Dimensions in mm", 8, Theme.Muted), 0, 2);
            var submit = Theme.Actions();
            sendButton = Theme.Button("&Plan request", SendButton_Click, true, "send"); sendButton.Width = 142;
            requestChangesButton = Theme.Button("Request &changes", RequestChangesButton_Click); requestChangesButton.Width = 146;
            submit.Controls.AddRange(new Control[] { sendButton, requestChangesButton });
            composer.Controls.Add(submit, 0, 3);
            conversation.Controls.Add(Theme.Card(composer), 0, 3);
            row.Controls.Add(conversation, 0, 0);

            var execution = Theme.Stack(28, 142, -100, 112);
            execution.Controls.Add(Theme.Label("BUILD & VALIDATION", 9, Theme.Muted, true), 0, 0);
            var state = Theme.Stack(32, 26, -100);
            currentJobLabel = Theme.Label("Ready for a new design", 13, Theme.Accent, true);
            currentJobLabel.AccessibleName = "CAD job state";
            workflowLabel = Theme.Label(JobPresentation.Guidance(null), 9, Theme.Muted);
            planSummaryLabel = Theme.Label("A validated plan is required before execution.", 9, Theme.Muted);
            state.Controls.Add(currentJobLabel, 0, 0);
            state.Controls.Add(planSummaryLabel, 0, 1);
            state.Controls.Add(workflowLabel, 0, 2);
            execution.Controls.Add(Theme.Card(state), 0, 1);
            reviewTabs = new WorkspaceTabs { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 12), AccessibleName = "Execution review views" };
            planGrid = Theme.Grid();
            activityGrid = Theme.Grid();
            var activity = Theme.Stack(40, -100);
            activitySummaryLabel = Theme.Label("No execution records yet.", 9, Theme.Muted);
            activity.Controls.Add(activitySummaryLabel, 0, 0);
            activity.Controls.Add(activityGrid, 0, 1);
            var diagnostics = Theme.Stack(26, -55, 26, -45);
            diagnostics.Controls.Add(Theme.Label("Plan / revision JSON", 9, Theme.Muted), 0, 0);
            planTextBox = Theme.TextBox(true, true); planTextBox.Font = new Font("Consolas", 9F);
            planTextBox.AccessibleName = "Redacted plan diagnostics";
            diagnostics.Controls.Add(planTextBox, 0, 1);
            diagnostics.Controls.Add(Theme.Label("Execution / verification JSON", 9, Theme.Muted), 0, 2);
            verificationTextBox = Theme.TextBox(true, true); verificationTextBox.Font = new Font("Consolas", 9F);
            verificationTextBox.AccessibleName = "Redacted execution diagnostics";
            diagnostics.Controls.Add(verificationTextBox, 0, 3);
            reviewTabs.AddPages(Page("Plan", planGrid), Page("Activity", activity), Page("Diagnostics", diagnostics));
            execution.Controls.Add(reviewTabs, 0, 2);
            var approval = Theme.Stack(44, -100);
            var approvalActions = Theme.Actions();
            approveButton = Theme.Button("Approve &build", ApproveButton_Click, true, "check"); approveButton.Width = 156;
            cancelButton = Theme.Button("&Cancel job", CancelButton_Click); cancelButton.Width = 116;
            completeButton = Theme.Button("&Complete", CompleteButton_Click); completeButton.Width = 108;
            approvalActions.Controls.AddRange(new Control[] { approveButton, cancelButton, completeButton });
            approval.Controls.Add(approvalActions, 0, 0);
            approval.Controls.Add(Theme.Label("Approval applies to the displayed revision.\r\nInspect native output in the SolidWorks window.", 8, Theme.Muted), 0, 1);
            execution.Controls.Add(Theme.Card(approval, new Padding(12)), 0, 3);
            row.Controls.Add(execution, 1, 0);
            _tips.SetToolTip(approveButton, "Execute only the validated revision displayed above.");
            _tips.SetToolTip(requestChangesButton, "Send the composer text as changes to this job's current revision.");
            _tips.SetToolTip(cancelButton, "Stops future commands; an operation already in progress may finish.");
            _tips.SetToolTip(refreshButton, "Refresh host, SolidWorks, history and the displayed job (F5).");
            return row;
        }

        private static Panel Page(string text, Control content)
        {
            var page = new Panel { Text = text, BackColor = Theme.Surface, ForeColor = Theme.Text, Padding = new Padding(10),
                AccessibleName = text, AccessibleRole = AccessibleRole.PropertyPage };
            page.Controls.Add(content);
            return page;
        }

        private void HistoryListBox_DrawItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            var job = (JobViewDto)historyListBox.Items[e.Index];
            var selected = (e.State & DrawItemState.Selected) != 0;
            using (var brush = new SolidBrush(selected ? Color.FromArgb(34, 61, 79) : Theme.Surface)) e.Graphics.FillRectangle(brush, e.Bounds);
            if (selected) using (var pen = new Pen(Theme.Accent, 3)) e.Graphics.DrawLine(pen, e.Bounds.Left + 1, e.Bounds.Top + 8, e.Bounds.Left + 1, e.Bounds.Bottom - 8);
            var textBounds = new Rectangle(e.Bounds.Left + 10, e.Bounds.Top + 9, e.Bounds.Width - 18, 28);
            TextRenderer.DrawText(e.Graphics, JobPresentation.Redact(job.Prompt ?? "CAD request"), Font, textBounds,
                Theme.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            using (var small = new Font("Segoe UI", 8F)) TextRenderer.DrawText(e.Graphics,
                JobPresentation.StateTitle(job.State) + (job.IsSimulated ? " · Simulation" : string.Empty), small,
                new Rectangle(textBounds.Left, e.Bounds.Top + 39, textBounds.Width, 22), Theme.Muted,
                TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            if ((e.State & DrawItemState.Focus) != 0) e.DrawFocusRectangle();
        }
    }
}
