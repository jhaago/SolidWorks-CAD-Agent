using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Desktop.Api;

namespace SolidWorksCadAgent.Desktop
{
    public sealed class DesignIntakeForm : Form
    {
        private readonly DesignIntakeClient _client;
        private readonly bool _ownsClient;
        private readonly Action<Guid> _onCadJobCreated;
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private readonly ComboBox _designs = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 210 };
        private readonly TextBox _title = new TextBox { Width = 170, MaxLength = 160 };
        private readonly Button _new = Button("New design"), _open = Button("Open"), _refresh = Button("Refresh");
        private readonly Button _attach = Button("Attach PNG / JPEG"), _send = Button("Analyse / Send"), _approve = Button("Approve Design Brief"), _plan = Button("Create CAD Plan");
        private readonly ListBox _references = new ListBox { Dock = DockStyle.Fill };
        private readonly TextBox _label = new TextBox { Dock = DockStyle.Fill, MaxLength = 160 };
        private readonly ComboBox _view = new ComboBox { Dock = DockStyle.Fill };
        private readonly PictureBox _preview = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(235, 239, 243) };
        private readonly TextBox _instructions = new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, MaxLength = 4000 };
        private readonly TextBox _conversation = ReadOnlyText();
        private readonly TextBox _brief = ReadOnlyText();
        private readonly ComboBox _revisions = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly Label _briefStatus = new Label { AutoSize = true, Dock = DockStyle.Fill };
        private readonly Label _status = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, Text = "Loading saved designs…" };
        private JObject _session;
        private bool _busy, _binding, _closing, _cleaned;

        public DesignIntakeForm(Action<Guid> onCadJobCreated = null) : this(new DesignIntakeClient(), onCadJobCreated, true) { }
        public DesignIntakeForm(DesignIntakeClient client, Action<Guid> onCadJobCreated) : this(client, onCadJobCreated, false) { }
        private DesignIntakeForm(DesignIntakeClient client, Action<Guid> callback, bool ownsClient)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _onCadJobCreated = callback; _ownsClient = ownsClient;
            Text = "Image design intake"; Width = 1100; Height = 850; MinimumSize = new Size(820, 660);
            Font = new Font("Segoe UI", 9F); StartPosition = FormStartPosition.CenterParent;
            BuildLayout();
            _new.Click += async (_, __) => await RunAsync(async token => { Display(await _client.CreateAsync(_title.Text, token)); await LoadListAsync(token); });
            _open.Click += async (_, __) => await RunAsync(async token => { if (_designs.SelectedItem is Item item) Display(await _client.GetAsync(item.Id, token)); });
            _refresh.Click += async (_, __) => await RunAsync(async token => { await LoadListAsync(token); if (_session != null) Display(await _client.GetAsync(SessionId, token)); });
            _attach.Click += async (_, __) => await AttachAsync();
            _send.Click += async (_, __) => await RunAsync(async token => { var result = await _client.SendMessageAsync(SessionId, _instructions.Text, token); Display(result); _instructions.Clear(); });
            _approve.Click += async (_, __) => { if (CanApprove) await RunAsync(async token => Display(await _client.ApproveAsync(SessionId, SelectedRevisionId, token))); };
            _plan.Click += async (_, __) =>
            {
                if (!CanPlan) return;
                await RunAsync(async token =>
                {
                    Display(await _client.CreateCadPlanAsync(SessionId, SelectedRevisionId, token));
                    if (Guid.TryParse(TextOf(_session, "CadJobId"), out var job)) { _status.Text = "CAD plan created. Review and approve it separately before execution."; _onCadJobCreated?.Invoke(job); }
                });
            };
            _references.SelectedIndexChanged += async (_, __) => { if (!_binding) await PreviewAsync(); };
            _revisions.SelectedIndexChanged += (_, __) => { if (!_binding) DisplayBrief(); };
            _instructions.TextChanged += (_, __) => UpdateActions();
            _title.TextChanged += (_, __) => UpdateActions();
            _designs.SelectedIndexChanged += (_, __) => UpdateActions();
            UpdateActions();
        }
        private static Button Button(string text) => new Button { Text = text, AutoSize = true, Margin = new Padding(4) };
        private static TextBox ReadOnlyText() => new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.White, WordWrap = true };
        private void BuildLayout()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 5 };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 150)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            root.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "DESIGN DISCUSSION — NO CAD MODIFICATION\nImages and answers build a design brief. CAD planning and CAD execution require separate approvals.", ForeColor = Color.DarkSlateBlue, Font = new Font(Font, FontStyle.Bold) }, 0, 0);
            var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = true };
            toolbar.Controls.Add(new Label { Text = "Saved design", AutoSize = true, Margin = new Padding(4, 8, 4, 0) }); toolbar.Controls.Add(_designs); toolbar.Controls.Add(_open); toolbar.Controls.Add(_refresh);
            toolbar.Controls.Add(new Label { Text = "New title", AutoSize = true, Margin = new Padding(4, 8, 4, 0) }); toolbar.Controls.Add(_title); toolbar.Controls.Add(_new); root.Controls.Add(toolbar, 0, 1);
            var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32)); body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 68));
            var refs = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(0, 0, 10, 0) };
            foreach (var height in new[] { 22, 110, 22, 30, 22, 30 }) refs.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            refs.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            refs.Controls.Add(new Label { Text = "Reference images · select to preview", Dock = DockStyle.Fill }, 0, 0); refs.Controls.Add(_references, 0, 1);
            refs.Controls.Add(new Label { Text = "Optional image label", Dock = DockStyle.Fill }, 0, 2); refs.Controls.Add(_label, 0, 3);
            refs.Controls.Add(new Label { Text = "Optional view type (or type your own)", Dock = DockStyle.Fill }, 0, 4); _view.Items.AddRange(new object[] { "", "front", "side", "top", "isometric", "detail", "sketch" }); refs.Controls.Add(_view, 0, 5);
            var previewArea = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 }; previewArea.RowStyles.Add(new RowStyle(SizeType.Absolute, 38)); previewArea.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); previewArea.Controls.Add(_attach, 0, 0); previewArea.Controls.Add(_preview, 0, 1); refs.Controls.Add(previewArea, 0, 6); body.Controls.Add(refs, 0, 0);
            var tabs = new TabControl { Dock = DockStyle.Fill };
            var historyTab = new TabPage("Conversation"); historyTab.Controls.Add(_conversation); tabs.TabPages.Add(historyTab);
            var briefTab = new TabPage("Design brief / revisions"); var briefArea = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            briefArea.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); briefArea.RowStyles.Add(new RowStyle(SizeType.Absolute, 42)); briefArea.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); briefArea.Controls.Add(_revisions, 0, 0); briefArea.Controls.Add(_briefStatus, 0, 1); briefArea.Controls.Add(_brief, 0, 2); briefTab.Controls.Add(briefArea); tabs.TabPages.Add(briefTab); body.Controls.Add(tabs, 1, 0); root.Controls.Add(body, 0, 2);
            var input = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 }; input.RowStyles.Add(new RowStyle(SizeType.Absolute, 24)); input.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); input.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            input.Controls.Add(new Label { Text = "Instructions or answers · upload first, then Analyse / Send", Dock = DockStyle.Fill }, 0, 0); input.Controls.Add(_instructions, 0, 1);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill }; actions.Controls.AddRange(new Control[] { _send, _approve, _plan }); input.Controls.Add(actions, 0, 2); root.Controls.Add(input, 0, 3); root.Controls.Add(_status, 0, 4); Controls.Add(root);
        }
        protected override async void OnShown(EventArgs e) { base.OnShown(e); await RunAsync(LoadListAsync); }
        protected override void OnFormClosing(FormClosingEventArgs e) { _closing = true; _lifetime.Cancel(); base.OnFormClosing(e); }
        protected override void OnFormClosed(FormClosedEventArgs e) { Cleanup(); base.OnFormClosed(e); }
        protected override void Dispose(bool disposing) { if (disposing) Cleanup(); base.Dispose(disposing); }
        private void Cleanup()
        {
            if (_cleaned) return; _cleaned = true; _closing = true;
            _lifetime.Cancel(); SetImage(null); if (_ownsClient) _client.Dispose(); _lifetime.Dispose();
        }
        private async Task RunAsync(Func<CancellationToken, Task> action)
        {
            if (_closing || _busy || IsDisposed || _lifetime.IsCancellationRequested) return;
            _busy = true; UpdateActions(); _status.ForeColor = Color.Black; _status.Text = "Working…";
            var token = _lifetime.Token;
            try { await action(token); if (!_closing && !IsDisposed && _status.Text == "Working…") _status.Text = "Ready · discussion does not modify CAD."; }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (!_closing && !IsDisposed) { _status.ForeColor = Color.Firebrick; _status.Text = ex is AgentHostUnavailableException ? "Agent Host unavailable. Start the local host and try again." : ex is ArgumentException ? "Check the title, instructions, or image (PNG/JPEG, at most 4 MiB)." : "Design request failed. Refresh the design and try again."; }
            }
            finally { _busy = false; if (!_closing && !IsDisposed) UpdateActions(); }
        }
        private async Task LoadListAsync(CancellationToken token)
        {
            var sessions = await _client.ListAsync(token); token.ThrowIfCancellationRequested();
            _binding = true; _designs.Items.Clear();
            foreach (var session in sessions.OfType<JObject>()) if (Guid.TryParse(TextOf(session, "Id"), out var id)) _designs.Items.Add(new Item(id, TextOf(session, "Title") + " · " + TextOf(session, "State"), session));
            if (_session != null) for (var i = 0; i < _designs.Items.Count; i++) if (((Item)_designs.Items[i]).Id == SessionId) _designs.SelectedIndex = i;
            if (_designs.SelectedIndex < 0 && _designs.Items.Count > 0) _designs.SelectedIndex = 0;
            _binding = false;
        }
        private void Display(JObject session)
        {
            if (_closing || IsDisposed) return; if (TextOf(_session, "Id") != TextOf(session, "Id")) _instructions.Clear(); _session = session; _binding = true; SetImage(null); _references.Items.Clear(); _revisions.Items.Clear();
            foreach (var reference in ArrayOf(session, "References").OfType<JObject>()) if (Guid.TryParse(TextOf(reference, "Id"), out var id)) _references.Items.Add(new Item(id, TextOf(reference, "FileName") + (string.IsNullOrWhiteSpace(TextOf(reference, "Label")) ? "" : " · " + TextOf(reference, "Label")), reference));
            foreach (var revision in ArrayOf(session, "Revisions").OfType<JObject>()) if (Guid.TryParse(TextOf(revision, "Id"), out var id)) _revisions.Items.Add(new Item(id, "Revision " + TextOf(revision, "Number") + " · " + TextOf(revision, "CreatedUtc"), revision));
            if (_revisions.Items.Count > 0) _revisions.SelectedIndex = _revisions.Items.Count - 1;
            var text = new StringBuilder(); foreach (var message in ArrayOf(session, "Messages").OfType<JObject>()) text.AppendLine(TextOf(message, "Role") + " · " + TextOf(message, "CreatedUtc")).AppendLine(TextOf(message, "Text")).AppendLine(); _conversation.Text = text.ToString();
            Text = "Image design intake · " + TextOf(session, "Title"); _binding = false; DisplayBrief();
            if (!string.IsNullOrWhiteSpace(TextOf(session, "LastError"))) _status.Text = "Analysis could not finish. Review your images and send instructions again.";
        }
        private void DisplayBrief()
        {
            var item = _revisions.SelectedItem as Item; _brief.Text = item == null ? "Attach references and send instructions to build the first brief." : FormatBrief(Token(item.Data, "Brief") as JObject);
            _briefStatus.Text = TextOf(_session, "State") + (IsCurrentRevision ? " · Current revision" : " · Prior revision (read-only)") + (HasCadJob ? "\nCAD job created. Use its revision controls for further changes." : "\nApproval of this brief does not approve CAD execution."); UpdateActions();
        }
        private static string FormatBrief(JObject brief)
        {
            if (brief == null) return "No interpretation available.";
            var text = new StringBuilder();
            var fields = new[] { "Summary", "Observations", "VisibleText", "VisibleDimensions", "Inferences", "Assumptions", "Unknowns", "MissingDimensions", "Dimensions", "Constraints", "FeatureIntent", "Materials", "SuggestedViews", "ModellingStrategy", "RequiredCadFeatures", "UnsupportedFeatures", "Confidence", "Warnings" };
            foreach (var field in fields) { text.AppendLine(System.Text.RegularExpressions.Regex.Replace(field, "([a-z])([A-Z])", "$1 $2")); var value = Token(brief, field); if (value is JArray list) { if (list.Count == 0) text.AppendLine("None reported."); foreach (var line in list) text.AppendLine("• " + line); } else text.AppendLine(value?.ToString() ?? "Not reported."); text.AppendLine(); }
            text.AppendLine("Resolved unknowns");
            var resolvedUnknowns = ArrayOf(brief, "ResolvedUnknowns");
            if (resolvedUnknowns.Count == 0) text.AppendLine("None reported.");
            foreach (var resolution in resolvedUnknowns.OfType<JObject>())
                text.AppendLine("Unknown: " + TextOf(resolution, "Unknown")).AppendLine("Evidence: " + TextOf(resolution, "Evidence")).AppendLine();
            text.AppendLine();
            text.AppendLine("Resolved questions");
            var resolvedQuestions = ArrayOf(brief, "ResolvedQuestions");
            if (resolvedQuestions.Count == 0) text.AppendLine("None reported.");
            foreach (var resolution in resolvedQuestions.OfType<JObject>())
                text.AppendLine("Question ID: " + TextOf(resolution, "QuestionId")).AppendLine("Evidence: " + TextOf(resolution, "Evidence")).AppendLine();
            text.AppendLine();
            text.AppendLine("Questions"); var questions = ArrayOf(brief, "Questions"); if (questions.Count == 0) text.AppendLine("None reported."); foreach (var question in questions.OfType<JObject>()) text.AppendLine((TextOf(question, "Critical") == "True" ? "CRITICAL · " : "") + TextOf(question, "Question")).AppendLine("Answer: " + (string.IsNullOrWhiteSpace(TextOf(question, "Answer")) ? "Not answered" : TextOf(question, "Answer"))).AppendLine(); return text.ToString();
        }
        private async Task AttachAsync()
        {
            if (_busy || _session == null) return;
            using (var dialog = new OpenFileDialog { Filter = "Reference images (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg", Multiselect = false, CheckFileExists = true })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                await RunAsync(async token =>
                {
                    var info = new FileInfo(dialog.FileName); if (info.Length <= 0 || info.Length > DesignIntakeClient.MaximumReferenceBytes) throw new ArgumentException();
                    var bytes = File.ReadAllBytes(dialog.FileName); token.ThrowIfCancellationRequested();
                    Display(await _client.AddReferenceAsync(SessionId, info.Name, bytes, _label.Text.Trim(), _view.Text.Trim(), token));
                    _status.Text = "Reference attached. Enter instructions, then Analyse / Send.";
                });
            }
        }
        private async Task PreviewAsync()
        {
            if (_busy || !(_references.SelectedItem is Item reference)) return;
            await RunAsync(async token =>
            {
                var response = await _client.GetReferenceAsync(SessionId, reference.Id, token);
                var encoded = TextOf(response, "base64"); if (encoded.Length > 5600000) throw new ArgumentException();
                var bytes = Convert.FromBase64String(encoded); if (bytes.Length > DesignIntakeClient.MaximumReferenceBytes) throw new ArgumentException();
                using (var stream = new MemoryStream(bytes)) using (var image = Image.FromStream(stream))
                {
                    if ((long)image.Width * image.Height > 30000000) throw new ArgumentException();
                    token.ThrowIfCancellationRequested(); if (!_closing && !IsDisposed) SetImage(new Bitmap(image));
                }
            });
        }
        private void SetImage(Image image) { var old = _preview.Image; _preview.Image = image; old?.Dispose(); }
        private Guid SessionId => Guid.Parse(TextOf(_session, "Id"));
        private Guid SelectedRevisionId => (_revisions.SelectedItem as Item)?.Id ?? Guid.Empty;
        private bool IsCurrentRevision => _revisions.SelectedIndex >= 0 && _revisions.SelectedIndex == _revisions.Items.Count - 1;
        private bool HasCadJob => Guid.TryParse(TextOf(_session, "CadJobId"), out _);
        private bool HasCurrentBrief => Token((_revisions.SelectedItem as Item)?.Data, "Brief") is JObject;
        private bool CanApprove => HasCurrentBrief && !HasCadJob && !_busy && IsCurrentRevision && TextOf(_session, "State") == "AwaitingDesignApproval" && string.IsNullOrWhiteSpace(TextOf(_session, "LastError"));
        private bool CanPlan => HasCurrentBrief && !HasCadJob && !_busy && IsCurrentRevision && TextOf(_session, "State") == "DesignApproved" && string.IsNullOrWhiteSpace(TextOf(_session, "LastError")) && TextOf(_session, "ApprovedRevisionId") == SelectedRevisionId.ToString("D") && ArrayOf(Token((_revisions.SelectedItem as Item)?.Data, "Brief") as JObject, "UnsupportedFeatures").Count == 0;
        private void UpdateActions()
        {
            if (_closing || IsDisposed) return;
            _new.Enabled = !_busy && !string.IsNullOrWhiteSpace(_title.Text); _open.Enabled = !_busy && _designs.SelectedItem != null; _refresh.Enabled = !_busy; _designs.Enabled = !_busy;
            _attach.Enabled = !_busy && _session != null && !HasCadJob; _send.Enabled = !_busy && _session != null && !HasCadJob && !string.IsNullOrWhiteSpace(_instructions.Text); _approve.Enabled = CanApprove; _plan.Enabled = CanPlan; _references.Enabled = !_busy; _revisions.Enabled = !_busy; _instructions.Enabled = !_busy && !HasCadJob; _title.Enabled = !_busy; _label.Enabled = !_busy; _view.Enabled = !_busy;
        }
        private static JToken Token(JObject value, string name) => value?.GetValue(name, StringComparison.OrdinalIgnoreCase);
        private static string TextOf(JObject value, string name) => Token(value, name)?.Type == JTokenType.Null ? "" : Token(value, name)?.ToString() ?? "";
        private static JArray ArrayOf(JObject value, string name) => Token(value, name) as JArray ?? new JArray();
        private sealed class Item { public Guid Id; public string Label; public JObject Data; public Item(Guid id, string label, JObject data) { Id = id; Label = label; Data = data; } public override string ToString() => Label; }
    }
}
