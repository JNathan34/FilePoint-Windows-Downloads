using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace FilePoint.Windows;

internal sealed class TrayMenu : ContextMenuStrip
{
    private const int RowWidth = 236;
    private const int RowHeight = 30;
    private const int HeaderHeight = 51;
    private readonly ToolStripLabel heading = new() { AutoSize = false, Size = new(RowWidth, HeaderHeight) };
    private readonly ToolStripMenuItem browse;
    private readonly ToolStripMenuItem recycleBin;
    private readonly ToolStripMenuItem updates;
    private readonly ToolStripMenuItem quit;
    private readonly ToolStripMenuItem recovery;
    private readonly MenuRenderer themedRenderer;
    private readonly ToolStripSystemRenderer contrastRenderer = new();

    internal TrayMenu(Icon icon, Action open, Action browse, Action openRecycleBin, Action updates, Action quit, Action openRecovery)
    {
        themedRenderer = new MenuRenderer(icon.ToBitmap());
        Renderer = themedRenderer;
        ShowImageMargin = false;
        ShowCheckMargin = false;
        Padding = new Padding(0, 3, 0, 5);
        Items.Add(heading);
        Items.Add(new MenuDivider());
        AddAction("Open FilePoint", MenuIcon.Open, open);
        this.browse = AddAction("Open in File Explorer", MenuIcon.Folder, browse);
        this.updates = AddAction("Updates…", MenuIcon.Refresh, updates);
        recycleBin = AddAction("Recycle Bin", MenuIcon.Recycle, openRecycleBin);
        recovery = AddAction("Open Unsaved Uploads", MenuIcon.Folder, openRecovery);
        Items.Add(new MenuDivider());
        this.quit = AddAction("Quit FilePoint", MenuIcon.Power, quit);
    }

    internal void RefreshState(string? mountPath, bool busy, Version? update, bool hasRecoveryFolder, bool hasRecycleBin)
    {
        string status = busy ? "Working…" : mountPath is { Length: >= 2 }
            ? $"Connected · {mountPath[..2]}" : "Not connected";
        heading.Text = $"FilePoint — {status}";
        themedRenderer.Status = status;
        themedRenderer.Connected = !busy && mountPath is not null;
        browse.Enabled = mountPath is not null && !busy;
        recycleBin.Visible = hasRecycleBin;
        recycleBin.Enabled = !busy;
        updates.Text = update is null ? "Updates…" : $"Update {update} available";
        updates.Enabled = quit.Enabled = !busy;
        recovery.Visible = hasRecoveryFolder;
        Renderer = SystemInformation.HighContrast ? contrastRenderer : themedRenderer;
        heading.Size = new Size(LogicalToDeviceUnits(RowWidth), LogicalToDeviceUnits(HeaderHeight));
        Padding = new Padding(0, LogicalToDeviceUnits(3), 0, LogicalToDeviceUnits(5));
        ApplyShape();
    }

    private ToolStripMenuItem AddAction(string text, MenuIcon icon, Action action)
    {
        var item = new MenuAction(text, icon);
        item.Click += (_, _) =>
        {
            Close(ToolStripDropDownCloseReason.ItemClicked);
            action();
        };
        Items.Add(item);
        return item;
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        ApplyShape();
    }

    private void ApplyShape()
    {
        if (SystemInformation.HighContrast || Width < 20 || Height < 20)
        {
            Region = null;
            return;
        }
        using var shape = MenuShape(this);
        Region = new Region(shape);
    }

    private static GraphicsPath MenuShape(ToolStrip menu)
    {
        var shape = new GraphicsPath();
        int corner = menu.LogicalToDeviceUnits(18);
        shape.AddRoundedRectangle(new Rectangle(0, 0, menu.Width - 1, menu.Height - 1), new Size(corner, corner));
        return shape;
    }

    private enum MenuIcon { Open, Folder, Refresh, Recycle, Power }

    private sealed class MenuDivider : ToolStripSeparator
    {
        public override Size GetPreferredSize(Size constrainingSize)
        {
            Size preferred = base.GetPreferredSize(constrainingSize);
            return new Size(preferred.Width, Owner?.LogicalToDeviceUnits(7) ?? 7);
        }
    }

    private sealed class MenuAction(string text, MenuIcon icon) : ToolStripMenuItem(text)
    {
        internal MenuIcon IconKind { get; } = icon;

        public override Size GetPreferredSize(Size constrainingSize)
        {
            Size preferred = base.GetPreferredSize(constrainingSize);
            int width = Owner?.LogicalToDeviceUnits(RowWidth) ?? RowWidth;
            int height = Owner?.LogicalToDeviceUnits(RowHeight) ?? RowHeight;
            return new Size(Math.Max(width, preferred.Width), Math.Max(height, preferred.Height));
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            themedRenderer.Dispose();
        base.Dispose(disposing);
    }

    private sealed class MenuRenderer(Bitmap logo) : ToolStripProfessionalRenderer, IDisposable
    {
        private static readonly Color Surface = Color.FromArgb(27, 29, 33);
        private static readonly Color TextColor = Color.FromArgb(241, 242, 244);
        private static readonly Color Muted = Color.FromArgb(180, 186, 195);
        private static readonly Color Outline = Color.FromArgb(75, 80, 88);
        private static readonly Color Selection = Color.FromArgb(47, 50, 55);
        private static readonly Color Green = Color.FromArgb(128, 228, 137);
        private static readonly Color QuitTile = Color.FromArgb(106, 45, 48);
        private const TextFormatFlags TextFlags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;
        private readonly Font titleFont = new("Segoe UI", 11.5f, FontStyle.Bold);
        private readonly Font bodyFont = new("Segoe UI", 10f);
        private readonly Font statusFont = new("Segoe UI", 8.5f);
        private readonly Font iconFont = new("Segoe MDL2 Assets", 10f);

        internal string Status { get; set; } = "";
        internal bool Connected { get; set; }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) => e.Graphics.Clear(Surface);

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using var shape = MenuShape(e.ToolStrip);
            using var pen = new Pen(Outline);
            var previous = e.Graphics.SmoothingMode;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.DrawPath(pen, shape);
            e.Graphics.SmoothingMode = previous;
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using var pen = new Pen(Outline);
            int y = e.Item.Height / 2;
            int inset = e.ToolStrip?.LogicalToDeviceUnits(12) ?? 12;
            e.Graphics.DrawLine(pen, inset, y, e.Item.Width - inset, y);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Enabled || !e.Item.Selected)
                return;
            int inset = e.ToolStrip?.LogicalToDeviceUnits(7) ?? 7;
            int corner = e.ToolStrip?.LogicalToDeviceUnits(6) ?? 6;
            var bounds = new Rectangle(inset, 1, e.Item.Width - inset * 2, e.Item.Height - 2);
            DrawTile(e.Graphics, bounds, corner, Selection);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            int Px(int value) => e.ToolStrip?.LogicalToDeviceUnits(value) ?? value;

            if (e.Item is ToolStripLabel)
            {
                DrawTile(e.Graphics, new Rectangle(Px(12), Px(7), Px(34), Px(34)), Px(10), Selection);
                e.Graphics.DrawImage(logo, new Rectangle(Px(16), Px(11), Px(26), Px(26)));
                TextRenderer.DrawText(e.Graphics, "FilePoint", titleFont, new Rectangle(Px(55), Px(5), e.Item.Width - Px(67), Px(21)),
                    TextColor, TextFlags);
                var statusColor = Connected ? Green : Muted;
                DrawTile(e.Graphics, new Rectangle(Px(56), Px(31), Px(6), Px(6)), Px(6), statusColor);
                TextRenderer.DrawText(e.Graphics, Status, statusFont, new Rectangle(Px(68), Px(25), e.Item.Width - Px(80), Px(18)),
                    statusColor, TextFlags);
                return;
            }

            var color = e.Item.Enabled ? TextColor : Color.FromArgb(134, 141, 151);
            if (e.Item is MenuAction action)
            {
                int center = e.Item.Height / 2;
                if (action.IconKind == MenuIcon.Power)
                    DrawTile(e.Graphics, new Rectangle(Px(13), center - Px(11), Px(26), Px(22)), Px(6),
                        action.Enabled ? QuitTile : Selection);
                DrawIcon(e.Graphics, action.IconKind, Px(18), center - Px(8), Px(16), color);
                if (action.IconKind != MenuIcon.Power)
                    DrawChevron(e.Graphics, e.Item.Width - Px(21), center, Px(4), color);
            }
            TextRenderer.DrawText(e.Graphics, e.Text, bodyFont, new Rectangle(Px(48), 0, e.Item.Width - Px(78), e.Item.Height),
                color, TextFlags);
        }

        private static void DrawTile(Graphics graphics, Rectangle bounds, int radius, Color color)
        {
            using var shape = new GraphicsPath();
            shape.AddRoundedRectangle(bounds, new Size(radius, radius));
            using var brush = new SolidBrush(color);
            var previous = graphics.SmoothingMode;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.FillPath(brush, shape);
            graphics.SmoothingMode = previous;
        }

        private static void DrawChevron(Graphics graphics, int x, int center, int size, Color color)
        {
            var previous = graphics.SmoothingMode;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(color, size * 0.33f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            graphics.DrawLines(pen, [new Point(x, center - size), new Point(x + size, center), new Point(x, center + size)]);
            graphics.SmoothingMode = previous;
        }

        private void DrawIcon(Graphics graphics, MenuIcon icon, int x, int y, int size, Color color)
        {
            if (icon == MenuIcon.Recycle)
            {
                TextRenderer.DrawText(graphics, "\uE74D", iconFont, new Rectangle(x, y, size, size), color,
                    TextFormatFlags.NoPadding | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            var state = graphics.Save();
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TranslateTransform(x, y);
            graphics.ScaleTransform(size / 24f, size / 24f);
            using var pen = new Pen(color, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            switch (icon)
            {
                case MenuIcon.Open:
                    Lines((11, 4), (4, 4), (2, 6), (2, 20), (4, 22), (18, 22), (20, 20), (20, 13));
                    Lines((14, 2), (22, 2), (22, 10));
                    Lines((22, 2), (11, 13));
                    break;
                case MenuIcon.Folder:
                    Lines((2, 20), (2, 5), (4, 3), (9, 3), (12, 6), (20, 6), (21, 8), (21, 10));
                    Lines((2, 20), (7, 10), (23, 10), (19, 21), (3, 21), (2, 20));
                    break;
                case MenuIcon.Refresh:
                    graphics.DrawArc(pen, 2, 2, 20, 20, 192, 153);
                    graphics.DrawArc(pen, 2, 2, 20, 20, 12, 153);
                    Lines((22, 3), (22, 9), (16, 9));
                    Lines((2, 21), (2, 15), (8, 15));
                    break;
                case MenuIcon.Power:
                    graphics.DrawArc(pen, 2, 3, 20, 19, -56, 292);
                    Lines((12, 1), (12, 11));
                    break;
            }
            graphics.Restore(state);

            void Lines(params (int X, int Y)[] points) =>
                graphics.DrawLines(pen, Array.ConvertAll(points, point => new Point(point.X, point.Y)));
        }

        public void Dispose()
        {
            logo.Dispose();
            titleFont.Dispose();
            bodyFont.Dispose();
            statusFont.Dispose();
            iconFont.Dispose();
        }
    }
}
