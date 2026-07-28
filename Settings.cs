using Microsoft.Win32;

namespace AquariumSaver;

// ── Per-emitter bubble config ──────────────────────────────────────────────────

public sealed class BubbleEmitterConfig
{
    public const float XMin = 0f, XMax = 100f;
    public const float YMin = 0f, YMax = 100f;
    public const float SpeedMin = 0.1f, SpeedMax = 3.0f;
    public const float SizeMinMin = 5f, SizeMinMax = 80f;

    public const float DefaultX = 50f;
    public const float DefaultY = 10f;
    public const float DefaultSpeed = 1.0f;
    public const float DefaultSizeMin = 15f;
    public const float DefaultSizeMax = 30f;

    public float X { get; set; }
    public float Y { get; set; }
    public float Speed { get; set; } = DefaultSpeed;
    public float SizeMin { get; set; } = DefaultSizeMin;
    public float SizeMax { get; set; } = DefaultSizeMax;
    public bool Enabled { get; set; } = true;

    public BubbleEmitterConfig Clamp()
    {
        X = Math.Clamp(X, XMin, XMax);
        Y = Math.Clamp(Y, YMin, YMax);
        Speed = Math.Clamp(Speed, SpeedMin, SpeedMax);
        SizeMin = Math.Clamp(SizeMin, SizeMinMin, SizeMinMax);
        SizeMax = Math.Clamp(SizeMax, SizeMinMin, SizeMinMax);

        // Swap if SizeMin > SizeMax so the user's two chosen values are preserved
        if (SizeMin > SizeMax)
        {
            (SizeMin, SizeMax) = (SizeMax, SizeMin);
        }

        return this;
    }

    public BubbleEmitterConfig Clone() => new BubbleEmitterConfig
    {
        X = X, Y = Y, Speed = Speed, SizeMin = SizeMin, SizeMax = SizeMax, Enabled = Enabled
    };
}

// ── Per-species fish config ────────────────────────────────────────────────────

public sealed class SpeciesConfig
{
    public const float SpeedMin = 0.005f;
    public const float SpeedMax = 0.10f;
    public const float SpeedDefault = 0.025f;

    public const float ScaleMin = 0.3f;
    public const float ScaleMax = 3.0f;
    public const float ScaleDefault = 1.0f;

    public string Name { get; set; } = string.Empty;
    public float Speed { get; set; } = SpeedDefault;
    public float Scale { get; set; } = ScaleDefault;

    public SpeciesConfig Clamp()
    {
        Speed = Math.Clamp(Speed, SpeedMin, SpeedMax);
        Scale = Math.Clamp(Scale, ScaleMin, ScaleMax);
        return this;
    }

    public SpeciesConfig Clone()
    {
        return new SpeciesConfig { Name = Name, Speed = Speed, Scale = Scale };
    }
}

// ── Settings data ──────────────────────────────────────────────────────────────

public sealed class SettingsData
{
    public const string RegistryPath = @"Software\AquariumSaver";
    public const string RegistryVersionKey = "Version";
    public const int CurrentVersion = 5;

    public const float SwimAngleMin = 0f;
    public const float SwimAngleMax = 45f;

    public const int MinEmitters = 1;
    public const int MaxEmitters = 6;

    public static readonly int[] AllowedFpsValues = [30, 50, 60, 100, 120];

    // Defaults
    public const float DefaultSwimAngle = 15f;
    public const bool DefaultIndependentScenesPerMonitor = true;
    public const string DefaultBackgroundTopColor = "#FF001845";   // deep blue
    public const string DefaultBackgroundBottomColor = "#FF000208"; // near-black
    public const int DefaultTargetFps = 0; // 0 = Auto (detect monitor refresh rate)
    public const bool DefaultPauseOnBattery = false;


    public static SettingsData Defaults => new()
    {
        BubbleEmitters = [new BubbleEmitterConfig()]
    };

    // Global settings
    public float SwimAngle { get; set; } = DefaultSwimAngle;
    public bool IndependentScenesPerMonitor { get; set; } = DefaultIndependentScenesPerMonitor;
    public string BackgroundTopColor { get; set; } = DefaultBackgroundTopColor;
    public string BackgroundBottomColor { get; set; } = DefaultBackgroundBottomColor;
    public int TargetFps { get; set; } = DefaultTargetFps;
    public bool PauseOnBattery { get; set; } = DefaultPauseOnBattery;

    // Per-emitter bubble configs (1–6 emitters)
    public BubbleEmitterConfig[] BubbleEmitters { get; set; } = [new BubbleEmitterConfig()];

    // Per-species settings (keyed by species name from manifest)
    public Dictionary<string, SpeciesConfig> SpeciesConfigs { get; set; } = new();

    public SettingsData Clamp()
    {
        SwimAngle = Math.Clamp(SwimAngle, SwimAngleMin, SwimAngleMax);
        if (TargetFps > 0)
            TargetFps = AllowedFpsValues.OrderBy(v => Math.Abs(v - TargetFps)).First();
        if (string.IsNullOrWhiteSpace(BackgroundTopColor)) BackgroundTopColor = DefaultBackgroundTopColor;
        if (string.IsNullOrWhiteSpace(BackgroundBottomColor)) BackgroundBottomColor = DefaultBackgroundBottomColor;

        // Enforce min 1 emitter
        if (BubbleEmitters.Length == 0)
            BubbleEmitters = [new BubbleEmitterConfig()];
        else if (BubbleEmitters.Length > MaxEmitters)
            BubbleEmitters = BubbleEmitters[..MaxEmitters];

        foreach (var be in BubbleEmitters) be.Clamp();
        foreach (var kvp in SpeciesConfigs)
            kvp.Value.Clamp();

        return this;
    }

    /// <summary>Get or create a default SpeciesConfig for a species name.</summary>
    public SpeciesConfig GetSpeciesConfig(string speciesName)
    {
        if (!SpeciesConfigs.TryGetValue(speciesName, out var config))
        {
            config = new SpeciesConfig { Name = speciesName };
            SpeciesConfigs[speciesName] = config;
        }
        return config;
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
            if (key == null) return SettingsData.Defaults;

            // Check version — if missing or old, discard and start fresh
            int version = ReadInt(key, SettingsData.RegistryVersionKey, 0);
            if (version != SettingsData.CurrentVersion)
                return LoadDefaultsAndPopulateSpecies(key);

            var s = new SettingsData
            {
                SwimAngle = ReadFloat(key, nameof(SettingsData.SwimAngle), SettingsData.DefaultSwimAngle),
                IndependentScenesPerMonitor = ReadBool(key, nameof(SettingsData.IndependentScenesPerMonitor), SettingsData.DefaultIndependentScenesPerMonitor),
                BackgroundTopColor = ReadString(key, nameof(SettingsData.BackgroundTopColor), SettingsData.DefaultBackgroundTopColor),
                BackgroundBottomColor = ReadString(key, nameof(SettingsData.BackgroundBottomColor), SettingsData.DefaultBackgroundBottomColor),
                TargetFps = ReadInt(key, nameof(SettingsData.TargetFps), SettingsData.DefaultTargetFps),
                PauseOnBattery = ReadBool(key, nameof(SettingsData.PauseOnBattery), SettingsData.DefaultPauseOnBattery),
            };

            // Load per-emitter bubble configs
            LoadBubbleEmitters(key, s);

            // Load per-species configs
            LoadSpeciesConfigs(key, s);

            return s.Clamp();
        }
        catch { return SettingsData.Defaults; }
    }

    private static SettingsData LoadDefaultsAndPopulateSpecies(RegistryKey? key)
    {
        var s = SettingsData.Defaults;
        // Try to load species configs if they exist, otherwise leave empty
        LoadSpeciesConfigs(key, s);
        return s.Clamp();
    }

    private static void LoadBubbleEmitters(RegistryKey? key, SettingsData s)
    {
        try
        {
            if (key?.OpenSubKey("BubbleEmitters") is RegistryKey beKey)
            {
                int count = Math.Clamp(ReadInt(beKey, "Count", 1), SettingsData.MinEmitters, SettingsData.MaxEmitters);
                var emitters = new List<BubbleEmitterConfig>(count);

                for (int i = 0; i < count; i++)
                {
                    using var sk = beKey.OpenSubKey(i.ToString());
                    if (sk == null)
                    {
                        emitters.Add(new BubbleEmitterConfig());
                    }
                    else
                    {
                        emitters.Add(new BubbleEmitterConfig
                        {
                            X = ReadFloat(sk, "X", BubbleEmitterConfig.DefaultX),
                            Y = ReadFloat(sk, "Y", BubbleEmitterConfig.DefaultY),
                            Speed = ReadFloat(sk, "Speed", BubbleEmitterConfig.DefaultSpeed),
                            SizeMin = ReadFloat(sk, "SizeMin", BubbleEmitterConfig.DefaultSizeMin),
                            SizeMax = ReadFloat(sk, "SizeMax", BubbleEmitterConfig.DefaultSizeMax),
                            Enabled = ReadBool(sk, "Enabled", true),
                        }.Clamp());
                    }
                }
                s.BubbleEmitters = emitters.ToArray();
            }
        }
        catch { /* bubble emitters are optional */ }
    }

    private static void LoadSpeciesConfigs(RegistryKey? key, SettingsData s)
    {
        try
        {
            if (key?.OpenSubKey("Species") is RegistryKey speciesKey)
            {
                foreach (string speciesName in speciesKey.GetSubKeyNames())
                {
                    using var spk = speciesKey.OpenSubKey(speciesName);
                    if (spk == null) continue;
                    var config = new SpeciesConfig
                    {
                        Name = speciesName,
                        Speed = ReadFloat(spk, "Speed", SpeciesConfig.SpeedDefault),
                        Scale = ReadFloat(spk, "Scale", SpeciesConfig.ScaleDefault),
                    };
                    s.SpeciesConfigs[speciesName] = config.Clamp();
                }
            }
        }
        catch { /* species configs are optional */ }
    }

    public static void Save(SettingsData s)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(SettingsData.RegistryPath, true);
            if (key == null) return;

            key.SetValue(SettingsData.RegistryVersionKey, SettingsData.CurrentVersion, RegistryValueKind.DWord);
            key.SetValue(nameof(s.SwimAngle), s.SwimAngle.ToString(System.Globalization.CultureInfo.InvariantCulture), RegistryValueKind.String);
            key.SetValue(nameof(s.IndependentScenesPerMonitor), s.IndependentScenesPerMonitor ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue(nameof(s.BackgroundTopColor), s.BackgroundTopColor, RegistryValueKind.String);
            key.SetValue(nameof(s.BackgroundBottomColor), s.BackgroundBottomColor, RegistryValueKind.String);
            key.SetValue(nameof(s.TargetFps), s.TargetFps, RegistryValueKind.DWord);
            key.SetValue(nameof(s.PauseOnBattery), s.PauseOnBattery ? 1 : 0, RegistryValueKind.DWord);

            // Save per-emitter bubble configs
            using var beKey = key.CreateSubKey("BubbleEmitters", true);
            if (beKey != null)
            {
                beKey.SetValue("Count", s.BubbleEmitters.Length, RegistryValueKind.DWord);

                for (int i = 0; i < s.BubbleEmitters.Length; i++)
                {
                    var be = s.BubbleEmitters[i];
                    using var sk = beKey.CreateSubKey(i.ToString(), true);
                    if (sk != null)
                    {
                        sk.SetValue("X", be.X.ToString(System.Globalization.CultureInfo.InvariantCulture), RegistryValueKind.String);
                        sk.SetValue("Y", be.Y.ToString(System.Globalization.CultureInfo.InvariantCulture), RegistryValueKind.String);
                        sk.SetValue("Speed", be.Speed.ToString(System.Globalization.CultureInfo.InvariantCulture), RegistryValueKind.String);
                        sk.SetValue("SizeMin", be.SizeMin.ToString(System.Globalization.CultureInfo.InvariantCulture), RegistryValueKind.String);
                        sk.SetValue("SizeMax", be.SizeMax.ToString(System.Globalization.CultureInfo.InvariantCulture), RegistryValueKind.String);
                        sk.SetValue("Enabled", be.Enabled ? 1 : 0, RegistryValueKind.DWord);
                    }
                }

                // Remove stale indices beyond current count
                if (beKey.GetSubKeyNames() is string[] existingSubKeys)
                {
                    foreach (string name in existingSubKeys)
                    {
                        if (int.TryParse(name, out int idx) && idx >= s.BubbleEmitters.Length)
                            beKey.DeleteSubKeyTree(name, false);
                    }
                }
            }

            // Save per-species configs
            using var speciesKey = key.CreateSubKey("Species", true);
            if (speciesKey != null)
            {
                // Remove stale species entries
                if (speciesKey.GetSubKeyNames() is string[] existingSpecies)
                {
                    foreach (string name in existingSpecies)
                    {
                        if (!s.SpeciesConfigs.ContainsKey(name))
                            speciesKey.DeleteSubKeyTree(name, false);
                    }
                }

                foreach (var kvp in s.SpeciesConfigs)
                {
                    using var spk = speciesKey.CreateSubKey(kvp.Key, true);
                    if (spk == null) continue;
                    spk.SetValue("Speed", kvp.Value.Speed.ToString(System.Globalization.CultureInfo.InvariantCulture), RegistryValueKind.String);
                    spk.SetValue("Scale", kvp.Value.Scale.ToString(System.Globalization.CultureInfo.InvariantCulture), RegistryValueKind.String);
                }
            }
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
        if (k?.GetValue(name) is string str && float.TryParse(str, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
            return v;
        return def;
    }

    static bool ReadBool(RegistryKey? k, string name, bool def)
    {
        if (k?.GetValue(name) is int v) return v != 0;
        if (k?.GetValue(name) is long lv) return lv != 0;
        if (k?.GetValue(name) is string str && bool.TryParse(str, out var bv)) return bv;
        return def;
    }

    static string ReadString(RegistryKey? k, string name, string def)
    {
        if (k?.GetValue(name) is string str && !string.IsNullOrWhiteSpace(str)) return str;
        return def;
    }
}
