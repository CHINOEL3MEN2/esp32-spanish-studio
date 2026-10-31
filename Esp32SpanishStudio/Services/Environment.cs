using System.IO.Ports;
using System.Text;

namespace Esp32SpanishStudio.Services;

public enum CheckStatus { Ok, Warning, Error }

public sealed record CheckItem(string Name, CheckStatus Status, string Detail)
{
    public string Icon => Status switch { CheckStatus.Ok => "\u2714", CheckStatus.Warning => "\u26A0", _ => "\u2716" };
}

public static class EnvironmentChecker
{
    public static async Task<List<CheckItem>> RunAsync(string? root)
    {
        var items = new List<CheckItem>();
        var rootOk = ProjectLocator.IsProjectRoot(root);
        items.Add(new("Proyecto esp32-ai", rootOk ? CheckStatus.Ok : CheckStatus.Error,
            rootOk ? root! : "Selecciona la carpeta raíz del repositorio (contiene pyproject.toml)."));
        if (!rootOk) return items;

        var uv = ToolLocator.Find("uv");
        items.Add(uv is null
            ? new("uv", CheckStatus.Error, "No encontrado. Instala uv: https://docs.astral.sh/uv/")
            : new("uv", CheckStatus.Ok, uv));

        var py = Path.Combine(root!, ".venv", "Scripts", "python.exe");
        if (!File.Exists(py))
        {
            items.Add(new("Entorno Python (.venv)", CheckStatus.Error, "No existe. Pulsa «Instalar / reparar dependencias»."));
        }
        else
        {
            var probe = await ProcessRunner.CaptureAsync(new CommandSpec(py, new[]
            {
                "-c",
                "import torch,sys;print(torch.__version__);print(torch.cuda.is_available());" +
                "print(torch.cuda.get_device_name(0) if torch.cuda.is_available() else '')",
            }, root!), TimeSpan.FromSeconds(120));
            if (!probe.Ok)
            {
                items.Add(new("PyTorch", CheckStatus.Error, "No se pudo importar. Pulsa «Instalar / reparar dependencias»."));
            }
            else
            {
                var lines = probe.Output.Split('\n', StringSplitOptions.TrimEntries);
                var cuda = lines.Length > 1 && lines[1] == "True";
                items.Add(cuda
                    ? new("PyTorch + CUDA", CheckStatus.Ok, $"{lines[0]} · {lines[2]}")
                    : new("PyTorch + CUDA", CheckStatus.Warning,
                        $"{lines[0]} sin GPU: el entrenamiento sería muy lento. Pulsa «Instalar / reparar dependencias»."));
            }

            foreach (var (module, label) in new[] { ("pyarrow", "pyarrow (lectura de datos)"), ("tokenizers", "tokenizers"), ("model", "Paquete del proyecto") })
            {
                var r = await ProcessRunner.CaptureAsync(new CommandSpec(py, new[] { "-c", $"import {module}" }, root!), TimeSpan.FromSeconds(60));
                items.Add(new(label, r.Ok ? CheckStatus.Ok : CheckStatus.Error, r.Ok ? "Instalado" : "Falta. Pulsa «Instalar / reparar dependencias»."));
            }
        }

        var cli = ToolLocator.Find("arduino-cli");
        if (cli is null) items.Add(new("arduino-cli", CheckStatus.Error, "No encontrado en PATH."));
        else
        {
            var cores = await ProcessRunner.CaptureAsync(new CommandSpec(cli, new[] { "core", "list" }, root!), TimeSpan.FromSeconds(60));
            var hasCore = cores.Output.Contains("esp32:esp32");
            items.Add(new("arduino-cli + núcleo ESP32", hasCore ? CheckStatus.Ok : CheckStatus.Error,
                hasCore ? cli : "Instala el núcleo: arduino-cli core install esp32:esp32"));
        }

        var esptool = ToolLocator.Find("esptool");
        items.Add(esptool is null
            ? new("esptool", CheckStatus.Error, "No encontrado. Instala con: pip install esptool")
            : new("esptool", CheckStatus.Ok, esptool));

        var cc = new[] { "gcc", "cc", "clang" }.Select(ToolLocator.Find).FirstOrDefault(p => p is not null);
        items.Add(cc is null
            ? new("Compilador C (opcional)", CheckStatus.Warning, "No encontrado: se omitirán las verificaciones en el PC antes de flashear.")
            : new("Compilador C (opcional)", CheckStatus.Ok, cc));

        try
        {
            var free = new DriveInfo(Path.GetPathRoot(root!)!).AvailableFreeSpace / (1L << 30);
            items.Add(new("Espacio en disco", free >= 5 ? CheckStatus.Ok : CheckStatus.Warning, $"{free} GB libres (se recomiendan 5 GB)"));
        }
        catch (Exception) { }

        var ports = SerialPorts.List();
        items.Add(new("Puertos serie", ports.Count > 0 ? CheckStatus.Ok : CheckStatus.Warning,
            ports.Count > 0 ? string.Join(", ", ports) : "Ninguno. Conecta la placa por USB."));
        return items;
    }
}

public static class SerialPorts
{
    public static List<string> List() => SerialPort.GetPortNames()
        .OrderBy(p => int.TryParse(p.Replace("COM", ""), out var n) ? n : 999).ToList();
}

public sealed class SerialMonitor : IDisposable
{
    private SerialPort? _port;

    public event Action<string>? Text;
    public bool IsOpen => _port?.IsOpen == true;

    public void Open(string name)
    {
        Close();
        _port = new SerialPort(name, 115200) { Encoding = Encoding.UTF8, ReadTimeout = 500, DtrEnable = false, RtsEnable = false };
        _port.DataReceived += (_, _) =>
        {
            try { var s = _port?.ReadExisting(); if (!string.IsNullOrEmpty(s)) Text?.Invoke(s); } catch (Exception) { }
        };
        _port.Open();
    }

    // Pulsing RTS drives EN on the ESP32-S3 USB-serial bridge; boot output is printed only once.
    public void Reset()
    {
        if (_port is not { IsOpen: true }) return;
        _port.RtsEnable = true;
        Thread.Sleep(120);
        _port.RtsEnable = false;
    }

    public void Close()
    {
        try { if (_port?.IsOpen == true) _port.Close(); } catch (Exception) { }
        _port?.Dispose();
        _port = null;
    }

    public void Dispose() => Close();
}
