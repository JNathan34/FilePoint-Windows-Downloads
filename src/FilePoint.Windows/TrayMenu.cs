using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace FilePoint.Windows;

internal sealed class TrayMenu : ContextMenuStrip
{
    private const int RowWidth = 232;
    private const int RowHeight = 34;
    private readonly ToolStripLabel heading = new() { AutoSize = false, Size = new(RowWidth, 53) };
    private readonly ToolStripMenuItem browse;
    private readonly ToolStripMenuItem updates;
    private readonly ToolStripMenuItem quit;
    private readonly MenuRenderer themedRenderer;
    private readonly ToolStripSystemRenderer contrastRenderer = new();

    internal TrayMenu(Icon icon, Action open, Action browse, Action updates, Action quit)
    {
        themedRenderer = new MenuRenderer(icon.ToBitmap());
        Renderer = themedRenderer;
        ShowImageMargin = false;
        ShowCheckMargin = false;
        Items.Add(heading);
        Items.Add(new ToolStripSeparator());
        AddAction("Open FilePoint", "\uE8A7", open);
        this.browse = AddAction("Open in File Explorer", "\uED25", browse);
        this.updates = AddAction("Updates…", "\uE72C", updates);
        Items.Add(new ToolStripSeparator());
        this.quit = AddAction("Quit FilePoint", "\uE8BB", quit);
    }

    internal void RefreshState(string? mountPath, bool busy, Version? update)
    {
        string status = busy ? "Working…" : mountPath is { Length: >= 2 }
            ? $"Connected · {mountPath[..2]}" : "Not connected";
        heading.Text = $"FilePoint — {status}";
        themedRenderer.Status = status;
        themedRenderer.Connected = !busy && mountPath is not null;
        browse.Enabled = mountPath is not null && !busy;
        updates.Text = update is null ? "Updates…" : $"Update {update} available";
        updates.Enabled = quit.Enabled = !busy;
        Renderer = SystemInformation.HighContrast ? contrastRenderer : themedRenderer;
        heading.Size = new Size(LogicalToDeviceUnits(RowWidth), LogicalToDeviceUnits(53));
        ApplyShape();
    }

    private ToolStripMenuItem AddAction(string text, string glyph, Action action)
    {
        var item = new MenuAction(text, glyph);
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
        int corner = menu.LogicalToDeviceUnits(12);
        shape.AddRoundedRectangle(new Rectangle(0, 0, menu.Width - 1, menu.Height - 1), new Size(corner, corner));
        return shape;
    }

    private sealed class MenuAction(string text, string glyph) : ToolStripMenuItem(text)
    {
        internal string Glyph { get; } = glyph;

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
        private static readonly Color Surface = Color.FromArgb(37, 39, 43);
        private static readonly Color TextColor = Color.FromArgb(241, 242, 244);
        private static readonly Color Muted = Color.FromArgb(180, 186, 195);
        private static readonly Color Outline = Color.FromArgb(71, 75, 82);
        private static readonly Color Selection = Color.FromArgb(53, 70, 90);
        private static readonly Color Green = Color.FromArgb(117, 208, 163);
        private const TextFormatFlags TextFlags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;
        private readonly Font titleFont = new("Segoe UI", 10.5f, FontStyle.Bold);
        private readonly Font bodyFont = new("Segoe UI", 9.75f);
        private readonly Font statusFont = new("Segoe UI", 8.5f);
        private readonly Font iconFont = new("Segoe MDL2 Assets", 11f);

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
            int inset = e.ToolStrip?.LogicalToDeviceUnits(5) ?? 5;
            int corner = e.ToolStrip?.LogicalToDeviceUnits(7) ?? 7;
            var bounds = new Rectangle(inset, 1, e.Item.Width - inset * 2, e.Item.Height - 2);
            using var shape = new GraphicsPath();
            shape.AddRoundedRectangle(bounds, new Size(corner, corner));
            using var brush = new SolidBrush(Selection);
            var previous = e.Graphics.SmoothingMode;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.FillPath(brush, shape);
            e.Graphics.SmoothingMode = previous;
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            int Px(int value) => e.ToolStrip?.LogicalToDeviceUnits(value) ?? value;

            if (e.Item is ToolStripLabel)
            {
                e.Graphics.DrawImage(logo, new Rectangle(Px(8), Px(10), Px(32), Px(32)));
                TextRenderer.DrawText(e.Graphics, "FilePoint", titleFont, new Rectangle(Px(50), Px(8), e.Item.Width - Px(60), Px(20)),
                    TextColor, TextFlags);
                TextRenderer.DrawText(e.Graphics, Status, statusFont, new Rectangle(Px(50), Px(28), e.Item.Width - Px(60), Px(17)),
                    Connected ? Green : Muted, TextFlags);
                return;
            }

            var color = e.Item.Enabled ? TextColor : Color.FromArgb(134, 141, 151);
            if (e.Item is MenuAction action)
                TextRenderer.DrawText(e.Graphics, action.Glyph, iconFont, new Rectangle(Px(16), 0, Px(22), e.Item.Height),
                    color, TextFlags);
            TextRenderer.DrawText(e.Graphics, e.Text, bodyFont, new Rectangle(Px(45), 0, e.Item.Width - Px(55), e.Item.Height),
                color, TextFlags);
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
