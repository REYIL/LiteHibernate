using System.Drawing;
using Forms = System.Windows.Forms;

namespace LiteHibernate.UI;

internal static class TrayTheme
{
    internal static Forms.ContextMenuStrip CreateMenu() => new()
    {
        Renderer = new MenuRenderer(),
        BackColor = Color.FromArgb(20, 28, 43), ForeColor = Color.FromArgb(238, 243, 252),
        Font = new Font("Segoe UI", 9.5f), ShowImageMargin = false,
        Padding = new Forms.Padding(6)
    };
    internal static Icon CreateIcon()
    {
        var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/LiteHibernate;component/Assets/LiteHibernate.ico"))
            ?? throw new IOException("Не найдена иконка приложения.");
        using var stream = resource.Stream;
        using var icon = new Icon(stream, Forms.SystemInformation.SmallIconSize);
        return (Icon)icon.Clone();
    }
    private sealed class MenuRenderer : Forms.ToolStripProfessionalRenderer
    {
        internal MenuRenderer() : base(new Colors { UseSystemColors = false }) { RoundedEdges = false; }
        protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
        {
            // Paint hover directly: no native selection gradient or bright outline.
            var highlighted = e.Item.Enabled && (e.Item.Selected || e.Item.Pressed);
            using var brush = new SolidBrush(highlighted ? Color.FromArgb(34, 44, 61) : e.ToolStrip?.BackColor ?? Color.FromArgb(20, 28, 43));
            e.Graphics.FillRectangle(brush, new Rectangle(Point.Empty, e.Item.Size));
        }
    }
    private sealed class Colors : Forms.ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Color.FromArgb(20, 28, 43);
        public override Color MenuBorder => Color.FromArgb(48, 64, 91);
        public override Color MenuItemSelected => Color.FromArgb(34, 44, 61);
        public override Color MenuItemBorder => Color.FromArgb(40, 52, 72);
        public override Color MenuItemSelectedGradientBegin => MenuItemSelected;
        public override Color MenuItemSelectedGradientEnd => MenuItemSelected;
        public override Color MenuItemPressedGradientBegin => MenuItemSelected;
        public override Color MenuItemPressedGradientMiddle => MenuItemSelected;
        public override Color MenuItemPressedGradientEnd => MenuItemSelected;
        public override Color SeparatorDark => MenuBorder;
        public override Color SeparatorLight => MenuBorder;
    }
}
