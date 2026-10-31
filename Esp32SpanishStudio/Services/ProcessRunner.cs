using System.Diagnostics;
using System.IO;
using System.Text;

namespace Esp32SpanishStudio.Services;

public sealed record CommandSpec(string FileName, IReadOnlyList<string> Args, string WorkingDirectory)
{
    public string Display => FileName + " " + string.Join(' ', Args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
}

public sealed record CaptureResult(int ExitCode, string Output)
{
    public bool Ok => ExitCode == 0;
}

public static class ToolLocator
{
    // GUI apps often start with a trimmed PATH, so also probe the usual per-user install folders.
    private static readonly string[] ExtraDirs =
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Python", "bin"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python"),
        @"C:\Program Files\Arduino CLI",
    };

    public static string? Find(string name)
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Concat(ExtraDirs);
        foreach (var dir in dirs)
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), name + ".exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        return null;
    }
}

public sealed class ProcessRunner
{
    public event Action<string, bool>? Line;

    private static ProcessStartInfo Build(CommandSpec spec)
    {
        var psi = new ProcessStartInfo(spec.FileName)
        {
            WorkingDirectory = spec.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in spec.Args) psi.ArgumentList.Add(a);
        psi.Environment["PYTHONUNBUFFERED"] = "1";
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";
        return psi;
    }

    public async Task<int> RunAsync(CommandSpec spec, CancellationToken ct)
    {
        Line?.Invoke("> " + spec.Display, false);
        using var p = new Process { StartInfo = Build(spec) };
        try { p.Start(); }
        catch (Exception ex)
        {
            Line?.Invoke($"No se pudo iniciar '{spec.FileName}': {ex.Message}", true);
            return -1;
        }

        using var reg = ct.Register(() =>
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        });

        var outTask = PumpAsync(p.StandardOutput, false);
        var errTask = PumpAsync(p.StandardError, true);
        await Task.WhenAll(outTask, errTask);
        await p.WaitForExitAsync(CancellationToken.None);
        return ct.IsCancellationRequested ? -2 : p.ExitCode;
    }

    public static async Task<CaptureResult> CaptureAsync(CommandSpec spec, TimeSpan timeout)
    {
        using var p = new Process { StartInfo = Build(spec) };
        try { p.Start(); }
        catch (Exception ex) { return new CaptureResult(-1, ex.Message); }

        using var cts = new CancellationTokenSource(timeout);
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            return new CaptureResult(-3, "tiempo de espera agotado");
        }
        return new CaptureResult(p.ExitCode, (await stdout + await stderr).Trim());
    }

    // tqdm and esptool redraw progress with bare carriage returns, so split on both.
    private async Task PumpAsync(StreamReader reader, bool isError)
    {
        var sb = new StringBuilder();
        var buf = new char[2048];
        int n;
        while ((n = await reader.ReadAsync(buf, 0, buf.Length)) > 0)
        {
            for (var i = 0; i < n; i++)
            {
                if (buf[i] is '\n' or '\r')
                {
                    if (sb.Length > 0) { Line?.Invoke(sb.ToString(), isError); sb.Clear(); }
                }
                else sb.Append(buf[i]);
            }
        }
        if (sb.Length > 0) Line?.Invoke(sb.ToString(), isError);
    }
}
