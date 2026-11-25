using System.ComponentModel;

namespace LanChat.WinForms.Controls;

internal sealed class ThemeMenuStrip : ContextMenuStrip
{
    private static readonly ToolStripRenderer DarkRenderer = new MenuRenderer();

    protected override void OnOpening(CancelEventArgs e)
    {
        if (Theme.IsDark) Renderer = DarkRenderer;
        else RenderMode = ToolStripRenderMode.ManagerRenderMode;
        base.OnOpening(e);
    }

    private sealed class MenuRenderer() : ToolStripProfessionalRenderer(new MenuColors())
    {
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Theme.Ink : Theme.Disabled;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
        {
            if (e.Image is null) return;
            base.OnRenderItemImage(new ToolStripItemImageRenderEventArgs(e.Graphics, e.Item, UiIcons.MenuImage(e.Image), e.ImageRectangle));
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Theme.Ink;
            base.OnRenderArrow(e);
        }
    }

    private sealed class MenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.Surface;
        public override Color ImageMarginGradientBegin => Theme.Surface;
        public override Color ImageMarginGradientMiddle => Theme.Surface;
        public override Color ImageMarginGradientEnd => Theme.Surface;
        public override Color MenuBorder => Theme.Border;
        public override Color MenuItemBorder => Theme.Border;
        public override Color MenuItemSelected => Theme.Selection;
        public override Color SeparatorDark => Theme.Border;
        public override Color SeparatorLight => Theme.Surface;
    }
}
