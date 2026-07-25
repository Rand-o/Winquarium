using Microsoft.Win32;

namespace AquariumSaver;

// ── Settings data ──────────────────────────────────────────────────────────────

public sealed class SettingsData
{
    public const string RegistryPath = @"Software\AquariumSaver";

    public const int FishCountMin = 1, FishCountMax = 60;
    public const int BubbleDensityMin = 0, BubbleDensityMax = 200;
    public const float SpeedMultiplierMin = 0.25f, SpeedMultiplierMax = 3.0f;
    public static readonly int[] AllowedFpsValues = [30, 60];

    // Defaults (Win95 underwater)
    public const int DefaultFishCount = 12;
    public const int DefaultBubbleDensity = 50;
    public const float DefaultSpeedMultiplier = 1.0f;
    public const bool DefaultShowSeaweed = false;
    public const bool DefaultShowLightShafts = false;
    public const bool DefaultShowBackgroundChest = false;
    public const bool DefaultIndependentScenesPerMonitor = true;
    public const string DefaultBackgroundTopColor = "#FF001845";   // deep blue
    public const string DefaultBackgroundBottomColor = "#FF000208"; // near-black
    public const int DefaultTargetFps = 60;
    public const bool DefaultPauseOnBattery = false;

    public static SettingsData Defaults => new();

    public int FishCount { get; set; } = DefaultFishCount;
    public int BubbleDensity { get; set; } = DefaultBubbleDensity;
    public float SpeedMultiplier { get; set; } = DefaultSpeedMultiplier;
    public bool ShowSeaweed { get; set; } = DefaultShowSeaweed;
    public bool ShowLightShafts { get; set; } = DefaultShowLightShafts;
    public bool ShowBackgroundChest { get; set; } = DefaultShowBackgroundChest;
    public bool IndependentScenesPerMonitor { get; set; } = DefaultIndependentScenesPerMonitor;
    public string BackgroundTopColor { get; set; } = DefaultBackgroundTopColor;
    public string BackgroundBottomColor { get; set; } = DefaultBackgroundBottomColor;
    public int TargetFps { get; set; } = DefaultTargetFps;
    public bool PauseOnBattery { get; set; } = DefaultPauseOnBattery;

    public SettingsData Clamp()
    {
        FishCount = Math.Clamp(FishCount, FishCountMin, FishCountMax);
        BubbleDensity = Math.Clamp(BubbleDensity, BubbleDensityMin, BubbleDensityMax);
        SpeedMultiplier = Math.Clamp(SpeedMultiplier, SpeedMultiplierMin, SpeedMultiplierMax);
        TargetFps = AllowedFpsValues.OrderBy(v => Math.Abs(v - TargetFps)).First();
        if (string.IsNullOrWhiteSpace(BackgroundTopColor)) BackgroundTopColor = DefaultBackgroundTopColor;
        if (string.IsNullOrWhiteSpace(BackgroundBottomColor)) BackgroundBottomColor = DefaultBackgroundBottomColor;
        return this;
    }

    public Color GetTopColor()
    {
        try { return ParseColor(BackgroundTopColor); }
        catch { return ParseColor(DefaultBackgroundTopColor); }
    }

    public Color GetBottomColor()
    {
        try { return ParseColor(BackgroundBottomColor); }
        catch { return ParseColor(DefaultBackgroundBottomColor); }
    }

    static Color ParseColor(string hex)
    {
        if (hex.StartsWith("#")) hex = hex[1..];
        return hex.Length == 8
            ? Color.FromArgb(Convert.ToByte(hex[..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..6], 16), Convert.ToByte(hex[6..8], 16))
            : Color.FromArgb(255, Convert.ToByte(hex[..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..6], 16));
    }
}

// ── Registry store ─────────────────────────────────────────────────────────────

public static class Settings
{
    public static SettingsData Load()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(SettingsData.RegistryPath);
            return new SettingsData
            {
                FishCount = ReadInt(key, nameof(SettingsData.FishCount), SettingsData.DefaultFishCount),
                BubbleDensity = ReadInt(key, nameof(SettingsData.BubbleDensity), SettingsData.DefaultBubbleDensity),
                SpeedMultiplier = ReadFloat(key, nameof(SettingsData.SpeedMultiplier), SettingsData.DefaultSpeedMultiplier),
                ShowSeaweed = ReadBool(key, nameof(SettingsData.ShowSeaweed), SettingsData.DefaultShowSeaweed),
                ShowLightShafts = ReadBool(key, nameof(SettingsData.ShowLightShafts), SettingsData.DefaultShowLightShafts),
                ShowBackgroundChest = ReadBool(key, nameof(SettingsData.ShowBackgroundChest), SettingsData.DefaultShowBackgroundChest),
                IndependentScenesPerMonitor = ReadBool(key, nameof(SettingsData.IndependentScenesPerMonitor), SettingsData.DefaultIndependentScenesPerMonitor),
                BackgroundTopColor = ReadString(key, nameof(SettingsData.BackgroundTopColor), SettingsData.DefaultBackgroundTopColor),
                BackgroundBottomColor = ReadString(key, nameof(SettingsData.BackgroundBottomColor), SettingsData.DefaultBackgroundBottomColor),
                TargetFps = ReadInt(key, nameof(SettingsData.TargetFps), SettingsData.DefaultTargetFps),
                PauseOnBattery = ReadBool(key, nameof(SettingsData.PauseOnBattery), SettingsData.DefaultPauseOnBattery),
            }.Clamp();
        }
        catch { return SettingsData.Defaults; }
    }

    public static void Save(SettingsData s)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(SettingsData.RegistryPath, true);
            if (key == null) return;
            key.SetValue(nameof(s.FishCount), s.FishCount, RegistryValueKind.DWord);
            key.SetValue(nameof(s.BubbleDensity), s.BubbleDensity, RegistryValueKind.DWord);
            key.SetValue(nameof(s.SpeedMultiplier), s.SpeedMultiplier.ToString(System.Globalization.CultureInfo.InvariantCulture), RegistryValueKind.String);
            key.SetValue(nameof(s.ShowSeaweed), s.ShowSeaweed ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue(nameof(s.ShowLightShafts), s.ShowLightShafts ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue(nameof(s.ShowBackgroundChest), s.ShowBackgroundChest ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue(nameof(s.IndependentScenesPerMonitor), s.IndependentScenesPerMonitor ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue(nameof(s.BackgroundTopColor), s.BackgroundTopColor, RegistryValueKind.String);
            key.SetValue(nameof(s.BackgroundBottomColor), s.BackgroundBottomColor, RegistryValueKind.String);
            key.SetValue(nameof(s.TargetFps), s.TargetFps, RegistryValueKind.DWord);
            key.SetValue(nameof(s.PauseOnBattery), s.PauseOnBattery ? 1 : 0, RegistryValueKind.DWord);
        }
        catch { /* silent */ }
    }

    static int ReadInt(RegistryKey? k, string name, int def)
    {
        if (k?.GetValue(name) is int v) return v;
        if (k?.GetValue(name) is long lv) return (int)lv;
        return def;
    }

    static float ReadFloat(RegistryKey? k, string name, float def)
    {
        if (k?.GetValue(name) is string s && float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
            return v;
        return def;
    }

    static bool ReadBool(RegistryKey? k, string name, bool def)
    {
        if (k?.GetValue(name) is int v) return v != 0;
        if (k?.GetValue(name) is long lv) return lv != 0;
        if (k?.GetValue(name) is string s && bool.TryParse(s, out var bv)) return bv;
        return def;
    }

    static string ReadString(RegistryKey? k, string name, string def)
    {
        if (k?.GetValue(name) is string s && !string.IsNullOrWhiteSpace(s)) return s;
        return def;
    }
}
