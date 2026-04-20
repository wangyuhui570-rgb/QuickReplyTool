using System.Text;
using System.Windows.Input;

namespace QuickReply.Desktop.Services;

public static class ScreenshotHotkeyBinding
{
    public const string DefaultHotkey = "Alt+A";
    public const string LegacyDefaultHotkey = "Ctrl+Alt+A";
    public const int CurrentMigrationVersion = 1;

    public static bool IsValid(string? value)
    {
        return TryNormalize(value, out _, out _);
    }

    public static string NormalizeOrDefault(string? value)
    {
        return TryNormalize(value, out var normalized, out _)
            ? normalized
            : DefaultHotkey;
    }

    public static bool ShouldMigrateLegacyDefault(string? value)
    {
        return string.Equals((value ?? string.Empty).Trim(), LegacyDefaultHotkey, StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryNormalize(string? value, out string normalized, out string error)
    {
        var raw = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            normalized = string.Empty;
            error = "快捷键不能为空。";
            return false;
        }

        var tokens = raw
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => token.Trim())
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .ToArray();

        if (tokens.Length == 0)
        {
            normalized = string.Empty;
            error = "快捷键不能为空。";
            return false;
        }

        var modifiers = ModifierKeys.None;
        string? mainKey = null;
        foreach (var token in tokens)
        {
            switch (token.ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL":
                    modifiers |= ModifierKeys.Control;
                    break;
                case "ALT":
                    modifiers |= ModifierKeys.Alt;
                    break;
                case "SHIFT":
                    modifiers |= ModifierKeys.Shift;
                    break;
                case "WIN":
                case "META":
                case "WINDOWS":
                    modifiers |= ModifierKeys.Windows;
                    break;
                default:
                    if (mainKey is not null)
                    {
                        normalized = string.Empty;
                        error = "一个快捷键只能包含一个主按键。";
                        return false;
                    }

                    if (!TryNormalizeMainKey(token, out mainKey))
                    {
                        normalized = string.Empty;
                        error = "仅支持字母、数字、F1-F24、Space、Tab、Esc、Enter。";
                        return false;
                    }

                    break;
            }
        }

        if (modifiers == ModifierKeys.None)
        {
            normalized = string.Empty;
            error = "至少需要一个修饰键，例如 Ctrl、Alt、Shift 或 Win。";
            return false;
        }

        if (mainKey is null)
        {
            normalized = string.Empty;
            error = "请再按一个主按键，例如字母、数字或 F 键。";
            return false;
        }

        normalized = FormatHotkey(modifiers, mainKey);
        error = string.Empty;
        return true;
    }

    public static bool TryBuildFromKeyEvent(System.Windows.Input.KeyEventArgs e, out string hotkey, out bool cancelRequested, out string error)
    {
        ArgumentNullException.ThrowIfNull(e);

        hotkey = string.Empty;
        error = string.Empty;
        cancelRequested = false;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;

        if (IsModifierOnlyKey(key))
        {
            error = "请再按一个主按键，例如字母、数字或 F 键。";
            return false;
        }

        if (key == Key.Escape && modifiers == ModifierKeys.None)
        {
            cancelRequested = true;
            return false;
        }

        if (modifiers == ModifierKeys.None)
        {
            error = "至少需要一个修饰键，例如 Ctrl、Alt、Shift 或 Win。";
            return false;
        }

        if (!TryNormalizeMainKey(key, out var mainKey))
        {
            error = "仅支持字母、数字、F1-F24、Space、Tab、Esc、Enter。";
            return false;
        }

        hotkey = FormatHotkey(modifiers, mainKey);
        return true;
    }

    private static bool TryNormalizeMainKey(string token, out string mainKey)
    {
        var upper = token.Trim().ToUpperInvariant();
        if (upper.Length == 1 && upper[0] is >= 'A' and <= 'Z')
        {
            mainKey = upper;
            return true;
        }

        if (upper.Length == 1 && upper[0] is >= '0' and <= '9')
        {
            mainKey = upper;
            return true;
        }

        if (upper.StartsWith("F", StringComparison.Ordinal) &&
            int.TryParse(upper[1..], out var functionNumber) &&
            functionNumber is >= 1 and <= 24)
        {
            mainKey = $"F{functionNumber}";
            return true;
        }

        mainKey = upper switch
        {
            "SPACE" => "Space",
            "TAB" => "Tab",
            "ESC" or "ESCAPE" => "Esc",
            "ENTER" or "RETURN" => "Enter",
            _ => string.Empty
        };

        return !string.IsNullOrWhiteSpace(mainKey);
    }

    private static bool TryNormalizeMainKey(Key key, out string mainKey)
    {
        if (key is >= Key.A and <= Key.Z)
        {
            mainKey = key.ToString().ToUpperInvariant();
            return true;
        }

        if (key is >= Key.D0 and <= Key.D9)
        {
            mainKey = ((char)('0' + (key - Key.D0))).ToString();
            return true;
        }

        if (key is >= Key.F1 and <= Key.F24)
        {
            mainKey = key.ToString().ToUpperInvariant();
            return true;
        }

        mainKey = key switch
        {
            Key.Space => "Space",
            Key.Tab => "Tab",
            Key.Escape => "Esc",
            Key.Enter or Key.Return => "Enter",
            _ => string.Empty
        };

        return !string.IsNullOrWhiteSpace(mainKey);
    }

    private static bool IsModifierOnlyKey(Key key)
    {
        return key is Key.LeftAlt or Key.RightAlt or
            Key.LeftCtrl or Key.RightCtrl or
            Key.LeftShift or Key.RightShift or
            Key.LWin or Key.RWin;
    }

    private static string FormatHotkey(ModifierKeys modifiers, string mainKey)
    {
        var builder = new StringBuilder();
        AppendModifier(builder, modifiers, ModifierKeys.Control, "Ctrl");
        AppendModifier(builder, modifiers, ModifierKeys.Alt, "Alt");
        AppendModifier(builder, modifiers, ModifierKeys.Shift, "Shift");
        AppendModifier(builder, modifiers, ModifierKeys.Windows, "Win");

        if (builder.Length > 0)
        {
            builder.Append('+');
        }

        builder.Append(mainKey);
        return builder.ToString();
    }

    private static void AppendModifier(StringBuilder builder, ModifierKeys modifiers, ModifierKeys flag, string text)
    {
        if (!modifiers.HasFlag(flag))
        {
            return;
        }

        if (builder.Length > 0)
        {
            builder.Append('+');
        }

        builder.Append(text);
    }
}
