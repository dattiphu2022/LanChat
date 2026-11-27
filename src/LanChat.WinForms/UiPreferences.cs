namespace LanChat.WinForms;

public enum UiTheme { Light, Dark, Windows }

public enum UiDensity { Compact, Standard, Comfortable }

// Local value settings only; never part of replicated profile or chat data.
public sealed record UiPreferences(float MessageFontSize = 10, UiDensity Density = UiDensity.Standard,
    bool HideNotificationContent = false, bool OnboardingCompleted = false, UiTheme Theme = UiTheme.Light, bool RemindUnreadMessages = true,
    int UnreadReminderMinutes = 5, bool SaveSystemResources = false)
{
    internal const int MaxReminderMinutes = 120;

    internal UiPreferences Validated() => this with
    {
        MessageFontSize = new[] { 10f,11f,12f,14f }.Contains(MessageFontSize) ? MessageFontSize : 10,
        Density = Enum.IsDefined(typeof(UiDensity),Density) ? Density : UiDensity.Standard,
        Theme = Enum.IsDefined(typeof(UiTheme), Theme) ? Theme : UiTheme.Light,
        UnreadReminderMinutes = UnreadReminderMinutes is >= 1 and <= MaxReminderMinutes ? UnreadReminderMinutes : 5
    };
    internal int ConversationRowHeight => Density switch { UiDensity.Compact=>68, UiDensity.Comfortable=>84, _=>72 };
}
