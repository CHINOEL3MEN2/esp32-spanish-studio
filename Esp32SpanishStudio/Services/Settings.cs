using System.Text.Json;

namespace Esp32SpanishStudio.Services;

public sealed class AppSettings
{
    public string ProjectRoot { get; set; } = "";
    public int Shards { get; set; } = 4;
    public int MaxChars { get; set; } = 1000;
    public int VocabSize { get; set; } = 8192;
    public bool AsciiFold { get; set; } = true;
    public int Steps { get; set; } = 20000;
    public int BatchSize { get; set; } = 32;
    public int SeqLen { get; set; } = 256;
    public double LearningRate { get; set; } = 0.001;
    public int Seed { get; set; } = 0;
    public string RunTag { get; set; } = "es";
    public string Prompt { get; set; } = "Habia una vez";
    public int SampleTokens { get; set; } = 150;
    public double Temperature { get; set; } = 0.6;
    public string Port { get; set; } = "";
}

public static class SettingsStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Esp32SpanishStudio", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception) { }
        return new AppSettings();
    }

    public static void Save(AppSettings s)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
    }
}

public static class ProjectLocator
{
    public static bool IsProjectRoot(string? dir) =>
        !string.IsNullOrWhiteSpace(dir)
        && File.Exists(Path.Combine(dir, "pyproject.toml"))
        && File.Exists(Path.Combine(dir, "runtime", "llm.h"))
        && Directory.Exists(Path.Combine(dir, "research", "tinystories"));

    // Walks up from the executable so the app works from windows/.../bin/... inside the repo.
    public static string? AutoDetect()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (IsProjectRoot(dir.FullName)) return dir.FullName;
        return null;
    }
}

public static class Fingerprint
{
    // Same FNV-1a the firmware prints at boot ("build: bytes=... fp=...").
    public static (long Bytes, string Fp) Compute(string path)
    {
        uint h = 2166136261u;
        long n = 0;
        using var fs = File.OpenRead(path);
        var buf = new byte[1 << 20];
        int read;
        while ((read = fs.Read(buf, 0, buf.Length)) > 0)
        {
            for (var i = 0; i < read; i++) { h ^= buf[i]; h *= 16777619u; }
            n += read;
        }
        return (n, h.ToString("x8"));
    }
}
