using LanChat.WinForms.Controls;

namespace LanChat.WinForms.Forms;

public class DpiForm : Form
{
    private Size _minimumAt96Dpi;

    internal DpiForm()
    {
        Font = Theme.DefaultFont;
        DoubleBuffered = true;
    }

    protected override void OnLoad(EventArgs e)
    {
        // The layout is built in code at 96 DPI. Wait for the window handle so
        // the first scale uses the monitor on which the form actually opens.
        _minimumAt96Dpi = MinimumSize;
        SuspendLayout();
        MinimumSize = Size.Empty;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ResumeLayout(true);

        if (TopLevel) FitToWorkingArea(Screen.FromControl(this).WorkingArea);
        base.OnLoad(e);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        if (TopLevel) MinimumSize = Size.Empty;
        base.OnDpiChanged(e);
        if (TopLevel) FitToWorkingArea(Screen.FromRectangle(e.SuggestedRectangle).WorkingArea);
    }

    private void FitToWorkingArea(Rectangle area)
    {
        MinimumSize = new Size(Math.Min(LogicalToDeviceUnits(_minimumAt96Dpi.Width), area.Width),
            Math.Min(LogicalToDeviceUnits(_minimumAt96Dpi.Height), area.Height));
        if (WindowState != FormWindowState.Normal) return;

        var width = Math.Min(Width, area.Width);
        var height = Math.Min(Height, area.Height);
        var x = Math.Max(area.Left, Math.Min(Left, area.Right - width));
        var y = Math.Max(area.Top, Math.Min(Top, area.Bottom - height));
        Bounds = new Rectangle(x, y, width, height);
    }
}
