using Microsoft.Win32;

namespace LanChat.WinForms.Controls;

internal static partial class Theme
{
    // Foreground, surface, selection and emphasis colors are paired so open controls
    // can change palette without being recreated or losing their state.
    private static readonly (Color Light, Color Dark)[] Colors =
    [
        (Color.White, Color.FromArgb(34, 40, 49)),
        (SystemColors.Window, Color.FromArgb(35, 41, 50)),
        (SystemColors.Control, Color.FromArgb(30, 35, 43)),
        (SystemColors.ControlText, Color.FromArgb(220, 229, 240)),
        (SystemColors.Highlight, Color.FromArgb(53, 79, 114)),
        (SystemColors.WindowText, Color.FromArgb(222, 230, 241)),
        (SystemColors.GrayText, Color.FromArgb(153, 166, 185)),
        (Color.FromArgb(245, 247, 251), Color.FromArgb(23, 28, 36)),
        (Color.FromArgb(30, 42, 61), Color.FromArgb(234, 240, 249)),
        (Color.FromArgb(89, 105, 126), Color.FromArgb(178, 192, 211)),
        (Color.FromArgb(35, 100, 220), Color.FromArgb(138, 184, 255)),
        (Color.FromArgb(21, 73, 159), Color.FromArgb(25, 53, 94)),
        (Color.FromArgb(221, 229, 239), Color.FromArgb(84, 99, 120)),
        (Color.FromArgb(231, 241, 255), Color.FromArgb(47, 68, 98)),
        (Color.FromArgb(142, 83, 0), Color.FromArgb(255, 197, 103)),
        (Color.FromArgb(28, 110, 65), Color.FromArgb(138, 223, 172)),
        (Color.FromArgb(174, 43, 43), Color.FromArgb(255, 158, 156)),
        (Color.Firebrick, Color.FromArgb(255, 157, 154)),
        (Color.FromArgb(237, 244, 255), Color.FromArgb(35, 53, 77)),
        (Color.FromArgb(201, 219, 245), Color.FromArgb(77, 106, 143)),
        (Color.FromArgb(235, 240, 247), Color.FromArgb(39, 48, 63)),
        (Color.FromArgb(208, 226, 249), Color.FromArgb(46, 66, 95)),
        (Color.FromArgb(232, 237, 245), Color.FromArgb(44, 52, 66)),
        (Color.FromArgb(65, 82, 108), Color.FromArgb(195, 209, 229)),
        (Color.FromArgb(218, 235, 255), Color.FromArgb(46, 65, 89)),
        (Color.FromArgb(255, 235, 160), Color.FromArgb(111, 80, 24)),
        (Color.FromArgb(228, 233, 240), Color.FromArgb(48, 57, 71)),
        (Color.FromArgb(240, 244, 249), Color.FromArgb(42, 51, 64)),
        (Color.FromArgb(213, 229, 252), Color.FromArgb(57, 78, 108)),
        (Color.FromArgb(37, 78, 161), Color.FromArgb(159, 194, 255)),
        (Color.FromArgb(26, 100, 77), Color.FromArgb(150, 225, 192)),
        (Color.FromArgb(107, 56, 137), Color.FromArgb(220, 184, 245)),
        (Color.FromArgb(144, 69, 35), Color.FromArgb(250, 192, 154)),
        (Color.FromArgb(143, 56, 88), Color.FromArgb(250, 176, 204))
    ];
    private static readonly Dictionary<Color, Color> DarkColors = Colors.ToDictionary(pair => pair.Light, pair => pair.Dark);
    private static readonly Dictionary<Color, Color> LightColors = Colors.ToDictionary(pair => pair.Dark, pair => pair.Light);

    internal static bool IsDark { get; private set; }
    private static bool _darkWasApplied;
    internal static Color Surface => Adapt(Color.White);
    private static readonly Color DarkPrimary = Color.FromArgb(35, 85, 167);
    internal static Color Primary => IsDark ? DarkPrimary : Blue;
    internal static Color Disabled => Adapt(SystemColors.GrayText);

    internal static Color Adapt(Color value) =>
        _darkWasApplied && (IsDark ? DarkColors : LightColors).TryGetValue(value, out var mapped) ? mapped : value;

    internal static void SetMode(UiTheme mode)
    {
        var dark = !SystemInformation.HighContrast && mode == UiTheme.Dark;
        if (dark == IsDark) return;
        IsDark = dark;
        _darkWasApplied |= dark;
        foreach (Form form in System.Windows.Forms.Application.OpenForms.Cast<Form>().ToArray())
            if (form.TopLevel && form is not Forms.RegionCaptureForm && !form.IsDisposed)
                ApplyTree(form);
    }

    internal static async Task<UiTheme> ResolveModeAsync(UiTheme mode, CancellationToken ct)
    {
        if (mode != UiTheme.Windows) return mode;
        var dark = await Task.Run(WindowsUsesDarkApps, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return dark ? UiTheme.Dark : UiTheme.Light;
    }

    private static bool WindowsUsesDarkApps()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return false; }
    }

    internal static void ApplyTree(Control control)
    {
        if (!_darkWasApplied || control.IsDisposed) return;
        if (control is Form or Panel or UserControl or GroupBox or SplitContainer or TabControl)
        {
            control.ControlAdded -= ApplyAddedControl;
            control.ControlAdded += ApplyAddedControl;
        }

        var back = control is ButtonBase ? AdaptButtonFill(control.BackColor) : Adapt(control.BackColor);
        if (back != control.BackColor) control.BackColor = back;
        var fore = control.ForeColor == Color.White && control is not TextBoxBase ? Color.White : Adapt(control.ForeColor);
        if (fore != control.ForeColor) control.ForeColor = fore;

        if (control is ButtonBase button)
        {
            button.FlatAppearance.BorderColor = Adapt(button.FlatAppearance.BorderColor);
            button.FlatAppearance.MouseOverBackColor = AdaptButtonFill(button.FlatAppearance.MouseOverBackColor);
            button.FlatAppearance.MouseDownBackColor = AdaptButtonFill(button.FlatAppearance.MouseDownBackColor);
        }
        if (control is LinkLabel link)
        {
            link.LinkColor = Adapt(link.LinkColor);
            link.ActiveLinkColor = Adapt(link.ActiveLinkColor);
            link.VisitedLinkColor = Adapt(link.VisitedLinkColor);
            link.DisabledLinkColor = Adapt(link.DisabledLinkColor);
        }
        if (control is DataGridView grid)
        {
            grid.BackgroundColor = Adapt(grid.BackgroundColor);
            grid.GridColor = Adapt(grid.GridColor);
            grid.EnableHeadersVisualStyles = !IsDark;
            ApplyCells(grid.DefaultCellStyle);
            ApplyCells(grid.RowsDefaultCellStyle);
            ApplyCells(grid.AlternatingRowsDefaultCellStyle);
            ApplyCells(grid.ColumnHeadersDefaultCellStyle);
            ApplyCells(grid.RowHeadersDefaultCellStyle);
        }
        if (control is MentionTextControl mention) mention.ApplyTheme();
        if (control is IconTabControl tabs) tabs.ApplyTheme();
        foreach (Control child in control.Controls) ApplyTree(child);
        control.Invalidate();
    }

    private static Color AdaptButtonFill(Color value) =>
        value == Color.FromArgb(35, 100, 220) || value == DarkPrimary ? Primary : Adapt(value);

    private static void ApplyAddedControl(object? sender, ControlEventArgs e) => ApplyTree(e.Control);

    private static void ApplyCells(DataGridViewCellStyle style)
    {
        style.BackColor = Adapt(style.BackColor);
        style.ForeColor = Adapt(style.ForeColor);
        style.SelectionBackColor = Adapt(style.SelectionBackColor);
        style.SelectionForeColor = Adapt(style.SelectionForeColor);
    }
}
