using System;
using System.IO;
using System.Text.Json;

namespace CodexEnhance;
public sealed class Settings
{
    public static string StateDirectory => Environment.GetEnvironmentVariable("CODEX_ENHANCE_STATE_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexEnhance");
    public bool Expanded { get; set; } = false;
    public bool FollowMode { get; set; } = true;
    public string? LockedThreadId { get; set; }
    public string? ManualThreadId { get; set; }
    public double RightOffset { get; set; } = 22;
    public double? TopOffset { get; set; }
    public string Theme { get; set; } = "system";
    public string? NodePath { get; set; }
    public bool QuotaIncludeAstraLongContext { get; set; } = false;
    public bool QuotaNormalizeFast { get; set; } = true;
    public bool ConversationCostAutoRefresh { get; set; } = true;
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, PropertyNameCaseInsensitive = true };
    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path.Combine(StateDirectory, "settings.json")), JsonOptions) ?? new(); }
        catch { return new(); }
    }
    public void Save()
    {
        Directory.CreateDirectory(StateDirectory);
        string target = Path.Combine(StateDirectory, "settings.json"), temp = target + "." + Environment.ProcessId + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temp, target, true);
    }
}
