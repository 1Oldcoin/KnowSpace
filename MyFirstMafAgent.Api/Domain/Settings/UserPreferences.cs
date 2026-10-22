namespace MyFirstMafAgent.Api.Domain.Settings;

/// <summary>用户学习偏好（值对象）。</summary>
public sealed class UserPreferences
{
    public const string DefaultDisplayName = "Lin";
    public const int DefaultDailyGoalMinutes = 60;

    public string DisplayName { get; init; } = DefaultDisplayName;
    public int DailyGoalMinutes { get; init; } = DefaultDailyGoalMinutes;

    public static UserPreferences Create(string? displayName, int? dailyGoalMinutes)
    {
        var name = string.IsNullOrWhiteSpace(displayName) ? DefaultDisplayName : displayName.Trim();
        if (name.Length > 30) name = name[..30];

        return new UserPreferences
        {
            DisplayName = name,
            DailyGoalMinutes = Math.Clamp(dailyGoalMinutes ?? DefaultDailyGoalMinutes, 0, 1440)
        };
    }
}
