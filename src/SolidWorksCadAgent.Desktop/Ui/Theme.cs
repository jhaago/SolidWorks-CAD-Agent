using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SolidWorksCadAgent.Desktop.Ui
{
    internal static class Theme
    {
        public static readonly Color Background = Color.FromArgb(16, 21, 28);
        public static readonly Color Surface = Color.FromArgb(23, 31, 42);
        public static readonly Color Input = Color.FromArgb(19, 26, 35);
        public static readonly Color Border = Color.FromArgb(44, 57, 73);
        public static readonly Color Text = Color.FromArgb(232, 238, 246);
        public static readonly Color Muted = Color.FromArgb(155, 174, 194);
        public static readonly Color Accent = Color.FromArgb(67, 184, 225);
        public static readonly Color Success = Color.FromArgb(97, 204, 163);
        public static readonly Color Warning = Color.FromArgb(235, 187, 103);
        public static readonly Color Error = Color.FromArgb(246, 142, 148);

        public static void Apply(Form form)
        {
            form.BackColor = Background;
            form.ForeColor = Text;
            form.Font = new Font("Segoe UI", 10F);
            form.AutoScaleDimensions = new SizeF(96F, 96F);
            form.AutoScaleMode = AutoScaleMode.Dpi;
        }

        public static Label Label(string text, float size = 10F, Color? color = null, bool bold = false) =>
            new Label { Text = text, Dock = DockStyle.Fill, AutoEllipsis = true, UseMnemonic = false, TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = color ?? Text, Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular), Margin = new Padding(0) };

        public static TextBox TextBox(bool readOnly = false, bool multiline = false) =>
            new TextBox { BackColor = Input, ForeColor = Text, BorderStyle = BorderStyle.FixedSingle,
                ReadOnly = readOnly, Multiline = multiline, ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None,
                Dock = DockStyle.Fill, Margin = new Padding(0), Font = new Font("Segoe UI", 10F) };

        public static ActionButton Button(string text, EventHandler handler, bool primary = false, string icon = null)
        {
            var button = new ActionButton { Text = text, Primary = primary, Glyph = icon, Height = 38, Width = 130,
                Margin = new Padding(0, 0, 8, 0), AccessibleName = text.Replace("&", ""), Cursor = Cursors.Hand,
                Font = new Font("Segoe UI", 9.5F) };
            if (handler != null) button.Click += handler;
            return button;
        }

        public static TableLayoutPanel Stack(params float[] rows)
        {
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = rows.Length,
                Margin = new Padding(0), BackColor = Color.Transparent };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (var row in rows) panel.RowStyles.Add(new RowStyle(row < 0 ? SizeType.Percent : SizeType.Absolute, Math.Abs(row)));
            return panel;
        }

        public static Panel Card(Control content, Padding? padding = null)
        {
            var panel = new SurfacePanel { Dock = DockStyle.Fill, BackColor = Surface,
                Padding = padding ?? new Padding(16), Margin = new Padding(0, 0, 0, 12) };
            panel.Controls.Add(content);
            return panel;
        }

        public static FlowLayoutPanel Actions() => new FlowLayoutPanel { Dock = DockStyle.Fill,
            WrapContents = false, Margin = new Padding(0), Padding = new Padding(0, 4, 0, 0) };

        public static DataGridView Grid()
        {
            var grid = new DataGridView { Dock = DockStyle.Fill, BackgroundColor = Surface, BorderStyle = BorderStyle.None,
                ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
                AutoGenerateColumns = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal, GridColor = Border,
                EnableHeadersVisualStyles = false, ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None,
                ColumnHeadersHeight = 38, Margin = new Padding(0), AccessibleName = "CAD operations" };
            grid.DefaultCellStyle = new DataGridViewCellStyle { BackColor = Surface, ForeColor = Text,
                SelectionBackColor = Color.FromArgb(34, 61, 79), SelectionForeColor = Text, Padding = new Padding(5, 4, 5, 4),
                Font = new Font("Segoe UI", 9F) };
            grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Input, ForeColor = Muted,
                Font = new Font("Segoe UI", 9F), Padding = new Padding(5) };
            grid.RowTemplate.Height = 38;
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Sequence", DataPropertyName = "Sequence", HeaderText = "#", Width = 38,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Operation", DataPropertyName = "Operation", HeaderText = "Operation", FillWeight = 36 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Status", DataPropertyName = "Status", HeaderText = "Status", FillWeight = 24 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Detail", DataPropertyName = "Detail", HeaderText = "Details", FillWeight = 40 });
            grid.CellFormatting += (sender, e) =>
            {
                if (e.ColumnIndex != 2 || e.Value == null) return;
                var value = e.Value.ToString();
                e.CellStyle.ForeColor = value == "Failed" ? Error : value == "Passed" || value == "Completed" ? Success : Muted;
            };
            return grid;
        }
    }

    internal sealed class SurfacePanel : Panel
    {
        public SurfacePanel() { DoubleBuffered = true; }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(Theme.Border)) e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }

    internal sealed class ActionButton : Button
    {
        public bool Primary { get; set; }
        public string Glyph { get; set; }
        public bool IsTab { get; set; }
        private bool _active;
        public bool Active
        {
            get => _active;
            set
            {
                if (_active == value) return;
                _active = value;
                Invalidate();
                if (IsHandleCreated) { AccessibilityNotifyClients(AccessibleEvents.StateChange, -1); if (value) AccessibilityNotifyClients(AccessibleEvents.Selection, -1); }
            }
        }
        private bool _hover;
        public ActionButton() { SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); }
        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override AccessibleObject CreateAccessibilityInstance() => IsTab ? new TabAccessibility(this) : base.CreateAccessibilityInstance();

        private sealed class TabAccessibility : ControlAccessibleObject
        {
            private readonly ActionButton _owner;
            public TabAccessibility(ActionButton owner) : base(owner) { _owner = owner; }
            public override AccessibleRole Role => AccessibleRole.PageTab;
            public override AccessibleStates State => base.State | AccessibleStates.Selectable | (_owner.Active ? AccessibleStates.Selected : AccessibleStates.None);
            public override string DefaultAction => "Select";
            public override void DoDefaultAction() => _owner.PerformClick();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var fill = Primary ? Theme.Accent : _hover ? Color.FromArgb(36, 49, 64) : Theme.Surface;
            var text = !Enabled ? Theme.Muted : Primary ? Theme.Background : Theme.Text;
            if (!Enabled) fill = Theme.Input;
            if (IsTab) { fill = Theme.Background; text = Active ? Theme.Text : Theme.Muted; }
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var brush = new SolidBrush(fill)) e.Graphics.FillRectangle(brush, ClientRectangle);
            using (var pen = new Pen(Primary && Enabled ? Theme.Accent : Theme.Border))
                if (!IsTab) e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            if (IsTab && Active) using (var pen = new Pen(Theme.Accent, 3)) e.Graphics.DrawLine(pen, 8, Height - 2, Width - 8, Height - 2);
            var labelArea = new Rectangle(8, 0, Width - 16, Height);
            if (Glyph != null)
            {
                DrawGlyph(e.Graphics, Glyph, new Rectangle(12, (Height - 16) / 2, 16, 16), text);
                labelArea = new Rectangle(34, 0, Width - 40, Height);
            }
            TextRenderer.DrawText(e.Graphics, Text, Font, labelArea, text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                (ShowKeyboardCues ? 0 : TextFormatFlags.HidePrefix));
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, new Rectangle(4, 4, Width - 8, Height - 8), text, fill);
        }

        private static void DrawGlyph(Graphics g, string kind, Rectangle r, Color color)
        {
            using (var p = new Pen(color, 1.6F))
            {
                if (kind == "plus") { g.DrawLine(p, r.Left + 8, r.Top + 2, r.Left + 8, r.Bottom - 2); g.DrawLine(p, r.Left + 2, r.Top + 8, r.Right - 2, r.Top + 8); }
                else if (kind == "check") g.DrawLines(p, new[] { new Point(r.Left + 1, r.Top + 8), new Point(r.Left + 6, r.Bottom - 3), new Point(r.Right - 1, r.Top + 3) });
                else if (kind == "refresh") { g.DrawArc(p, r.Left + 2, r.Top + 2, 12, 12, 35, 290); g.DrawLines(p, new[] { new Point(r.Right - 1, r.Top + 1), new Point(r.Right - 1, r.Top + 6), new Point(r.Right - 6, r.Top + 6) }); }
                else if (kind == "send") g.DrawLines(p, new[] { new Point(r.Left + 1, r.Top + 2), new Point(r.Right - 1, r.Top + 8), new Point(r.Left + 1, r.Bottom - 2), new Point(r.Left + 4, r.Top + 8), new Point(r.Left + 1, r.Top + 2) });
                else { g.DrawRectangle(p, r.Left + 3, r.Top + 3, 10, 10); g.DrawLine(p, r.Left + 5, r.Top + 8, r.Right - 5, r.Top + 8); }
            }
        }
    }

    internal sealed class WorkspaceTabs : UserControl
    {
        private readonly List<Panel> _pages = new List<Panel>();
        private readonly List<ActionButton> _buttons = new List<ActionButton>();
        private readonly FlowLayoutPanel _header;
        private readonly Panel _body;
        private int _selectedIndex;
        public WorkspaceTabs()
        {
            BackColor = Theme.Background;
            AccessibleRole = AccessibleRole.PageTabList;
            var layout = Theme.Stack(40, -100);
            _header = Theme.Actions();
            _header.Padding = new Padding(0);
            _body = new SurfacePanel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = new Padding(1), Margin = new Padding(0) };
            layout.Controls.Add(_header, 0, 0);
            layout.Controls.Add(_body, 0, 1);
            Controls.Add(layout);
        }

        public void AddPages(params Panel[] pages)
        {
            foreach (var page in pages)
            {
                var index = _pages.Count;
                _pages.Add(page);
                page.Dock = DockStyle.Fill;
                _body.Controls.Add(page);
                var button = Theme.Button(page.Text, (sender, e) => SelectedIndex = index);
                button.IsTab = true;
                button.Width = 112;
                button.AccessibleRole = AccessibleRole.PageTab;
                _buttons.Add(button);
                _header.Controls.Add(button);
            }
            SelectedIndex = 0;
        }

        public int SelectedIndex
        {
            get => _selectedIndex;
            set
            {
                if (value < 0 || value >= _pages.Count) return;
                _selectedIndex = value;
                for (var i = 0; i < _pages.Count; i++)
                {
                    _pages[i].Visible = i == value;
                    _buttons[i].Active = i == value;
                    _buttons[i].Invalidate();
                }
                _pages[value].BringToFront();
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.Tab) || keyData == (Keys.Control | Keys.Shift | Keys.Tab))
            {
                if (_pages.Count > 0) SelectedIndex = (SelectedIndex + ((keyData & Keys.Shift) != 0 ? _pages.Count - 1 : 1)) % _pages.Count;
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }
}
