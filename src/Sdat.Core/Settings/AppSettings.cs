namespace Sdat.Core.Settings;

public sealed record AppSettings
{
    public IReadOnlyList<int> ReminderOffsetsMinutes { get; init; } = [2];

    public bool CriticalOverlayEnabled { get; init; } = true;

    public bool StartCompanionAtLogin { get; init; }

    public int DailyOverlapWindowMinutes { get; init; } = 120;

    public string PaletteHotkey { get; init; } = "Ctrl+Alt+S";

    public AppSettings Validate()
    {
        var offsets = ReminderOffsetsMinutes.Distinct().OrderDescending().ToArray();
        if (offsets.Length > 5 || offsets.Any(offset => offset is < 1 or > 1440))
        {
            throw new ArgumentOutOfRangeException(
                nameof(ReminderOffsetsMinutes),
                "Use at most five unique reminder offsets between 1 and 1440 minutes.");
        }

        if (DailyOverlapWindowMinutes is < 0 or > 1440)
        {
            throw new ArgumentOutOfRangeException(
                nameof(DailyOverlapWindowMinutes),
                "The daily overlap window must be between 0 and 1440 minutes.");
        }

        var hotkey = HotkeyGesture.Parse(PaletteHotkey);
        return this with
        {
            ReminderOffsetsMinutes = offsets,
            PaletteHotkey = hotkey.ToString(),
        };
    }
}

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1 << 0,
    Control = 1 << 1,
    Shift = 1 << 2,
    Windows = 1 << 3,
}

public readonly record struct HotkeyGesture(HotkeyModifiers Modifiers, string Key)
{
    public static HotkeyGesture Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new FormatException("Enter a hotkey such as Ctrl+Alt+S.");
        }

        var modifiers = HotkeyModifiers.None;
        string? key = null;
        foreach (var rawPart in value.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var part = rawPart.ToUpperInvariant();
            var modifier = part switch
            {
                "CTRL" or "CONTROL" => HotkeyModifiers.Control,
                "ALT" => HotkeyModifiers.Alt,
                "SHIFT" => HotkeyModifiers.Shift,
                "WIN" or "WINDOWS" => HotkeyModifiers.Windows,
                _ => HotkeyModifiers.None,
            };
            if (modifier != HotkeyModifiers.None)
            {
                if ((modifiers & modifier) != 0)
                {
                    throw new FormatException($"The {rawPart} modifier is repeated.");
                }

                modifiers |= modifier;
                continue;
            }

            if (key is not null)
            {
                throw new FormatException("A hotkey must contain exactly one key.");
            }

            key = NormalizeKey(part);
        }

        if (modifiers == HotkeyModifiers.None || key is null)
        {
            throw new FormatException("A hotkey requires at least one modifier and one key, such as Ctrl+Alt+S.");
        }

        return new HotkeyGesture(modifiers, key);
    }

    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Windows)) parts.Add("Win");
        parts.Add(Key);
        return string.Join('+', parts);
    }

    private static string NormalizeKey(string key)
    {
        if (key.Length == 1 && (key[0] is >= 'A' and <= 'Z' or >= '0' and <= '9'))
        {
            return key;
        }

        if (key.Length is 2 or 3 && key[0] == 'F' && int.TryParse(key[1..], out var function) &&
            function is >= 1 and <= 24)
        {
            return $"F{function}";
        }

        throw new FormatException("Use a letter, number, or F1-F24 as the hotkey key.");
    }
}

public interface IAppSettingsRepository
{
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task<AppSettings> SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}
