using System.Drawing;

namespace P2PShare.Desktop;

/// <summary>A late-90s/2000s file-sharing app palette: silver chrome with a neon-green accent.</summary>
internal static class Theme
{
    public static readonly Color WindowBackground = Color.FromArgb(0xEC, 0xEC, 0xEC);
    public static readonly Color PanelBackground = Color.FromArgb(0xF6, 0xF6, 0xF6);
    public static readonly Color Accent = Color.FromArgb(0x1E, 0xB9, 0x5A);
    public static readonly Color AccentDark = Color.FromArgb(0x0F, 0x7A, 0x38);
    public static readonly Color ConsoleBackground = Color.FromArgb(0x0C, 0x12, 0x0C);
    public static readonly Color ConsoleText = Color.FromArgb(0x4C, 0xE6, 0x6B);
    public static readonly Color HeaderText = Color.FromArgb(0x1A, 0x1A, 0x1A);

    public const string TitleFont = "Tahoma";
}
