using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using Esp32SpanishStudio.Infrastructure;
using Esp32SpanishStudio.Services;
using Microsoft.Win32;

namespace Esp32SpanishStudio.ViewModels;

public sealed record LogEntry(string Time, string Text, bool IsError);

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private static readonly Regex TrainLine = new(
        @"step\s+(\d+)\s*\|.*?val\s+([\d.]+)\s*\|\s*ppl\s+([\d.]+)\s*\|\s*(\d+)s", RegexOptions.Compiled);
    private static readonly Regex BuildLine = new(
        @"build: bytes=(\d+) fp=([0-9a-f]{8})", RegexOptions.Compiled);

    private readonly AppSettings _s = SettingsStore.Load();
    private readonly ProcessRunner _runner = new();
    private readonly SerialMonitor _monitor = new();
    private readonly StringBuilder _monitorBuffer = new();
    private readonly List<(int Step, double Val)> _history = new();
    private CancellationTokenSource? _cts;
    private Action<string>? _lineHook;

    private bool _isBusy, _isIndeterminate, _monitorConnected;
    private int _selectedPage;
    private string _status = "Listo", _progressText = "", _lastMetrics = "", _monitorText = "", _fpStatus = "";
    private double _progress;
    private PointCollection _lossPoints = new();
    private string? _expectedFp;
    private long _expectedBytes;

    public MainViewModel()
    {
        if (!ProjectLocator.IsProjectRoot(_s.ProjectRoot))
            _s.ProjectRoot = ProjectLocator.AutoDetect() ?? "";

        _runner.Line += OnRunnerLine;
        _monitor.Text += OnMonitorText;

        BrowseRootCommand = new RelayCommand(BrowseRoot, () => !IsBusy);
        CheckEnvironmentCommand = new AsyncCommand(() => Guard("Diagnóstico del entorno", CheckEnvironmentAsync), () => !IsBusy);
        InstallDepsCommand = new AsyncCommand(() => Guard("Instalar dependencias", InstallDepsAsync), () => !IsBusy);
        PrepareCommand = new AsyncCommand(() => Guard("Preparar datos en español", PrepareAsync), () => !IsBusy);
        TrainCommand = new AsyncCommand(() => Guard("Entrenar modelo", TrainAsync), () => !IsBusy);
        ExportCommand = new AsyncCommand(() => Guard("Exportar modelo", ExportAsync), () => !IsBusy);
        SampleCommand = new AsyncCommand(() => Guard("Probar generación", SampleAsync), () => !IsBusy);
        DeployCommand = new AsyncCommand(() => Guard("Flashear al ESP32-S3", DeployAsync), () => !IsBusy);
        RunAllCommand = new AsyncCommand(() => Guard("Pipeline completo", RunAllAsync), () => !IsBusy);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy);
        ClearLogCommand = new RelayCommand(() => Logs.Clear());
        SaveLogCommand = new RelayCommand(SaveLog);
        OpenArtifactsCommand = new RelayCommand(OpenArtifacts);
        RefreshPortsCommand = new RelayCommand(RefreshPorts);
        ConnectMonitorCommand = new RelayCommand(ConnectMonitor, () => !IsBusy && !MonitorConnected);
        DisconnectMonitorCommand = new RelayCommand(DisconnectMonitor, () => MonitorConnected);
        ResetBoardCommand = new RelayCommand(() => _monitor.Reset(), () => MonitorConnected);
        ClearMonitorCommand = new RelayCommand(() => { _monitorBuffer.Clear(); MonitorText = ""; FpStatus = ""; });

        RefreshPorts();
        RefreshState();
    }

    public ObservableCollection<LogEntry> Logs { get; } = new();
    public ObservableCollection<CheckItem> Checks { get; } = new();
    public ObservableCollection<string> Ports { get; } = new();

    public RelayCommand BrowseRootCommand { get; }
    public AsyncCommand CheckEnvironmentCommand { get; }
    public AsyncCommand InstallDepsCommand { get; }
    public AsyncCommand PrepareCommand { get; }
    public AsyncCommand TrainCommand { get; }
    public AsyncCommand ExportCommand { get; }
    public AsyncCommand SampleCommand { get; }
    public AsyncCommand DeployCommand { get; }
    public AsyncCommand RunAllCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ClearLogCommand { get; }
    public RelayCommand SaveLogCommand { get; }
    public RelayCommand OpenArtifactsCommand { get; }
    public RelayCommand RefreshPortsCommand { get; }
    public RelayCommand ConnectMonitorCommand { get; }
    public RelayCommand DisconnectMonitorCommand { get; }
    public RelayCommand ResetBoardCommand { get; }
    public RelayCommand ClearMonitorCommand { get; }

    // ---- settings -------------------------------------------------------------
    public string ProjectRoot
    {
        get => _s.ProjectRoot;
        set { if (_s.ProjectRoot != value) { _s.ProjectRoot = value; Raise(); RefreshState(); Persist(); } }
    }
    public int Shards { get => _s.Shards; set { _s.Shards = value; Raise(); Persist(); } }
    public int MaxChars { get => _s.MaxChars; set { _s.MaxChars = value; Raise(); Persist(); } }
    public int VocabSize { get => _s.VocabSize; set { _s.VocabSize = value; Raise(); RefreshState(); Persist(); } }
    public bool AsciiFold { get => _s.AsciiFold; set { _s.AsciiFold = value; Raise(); Persist(); } }
    public int Steps { get => _s.Steps; set { _s.Steps = value; Raise(); Persist(); } }
    public int BatchSize { get => _s.BatchSize; set { _s.BatchSize = value; Raise(); Persist(); } }
    public int SeqLen { get => _s.SeqLen; set { _s.SeqLen = value; Raise(); Raise(nameof(SeqLenWarning)); Persist(); } }
    public double LearningRate { get => _s.LearningRate; set { _s.LearningRate = value; Raise(); Persist(); } }
    public int Seed { get => _s.Seed; set { _s.Seed = value; Raise(); RefreshState(); Persist(); } }
    public string RunTag { get => _s.RunTag; set { _s.RunTag = value; Raise(); RefreshState(); Persist(); } }
    public string Prompt { get => _s.Prompt; set { _s.Prompt = value; Raise(); Persist(); } }
    public int SampleTokens { get => _s.SampleTokens; set { _s.SampleTokens = value; Raise(); Persist(); } }
    public double Temperature { get => _s.Temperature; set { _s.Temperature = value; Raise(); Persist(); } }
    public string Port { get => _s.Port; set { _s.Port = value; Raise(); Persist(); } }

    public string SeqLenWarning => SeqLen == 256 ? "" :
        "Aviso: el firmware está dimensionado para 256 (caché KV en PSRAM). Otros valores pueden no caber.";

    // ---- state ----------------------------------------------------------------
    public bool IsBusy { get => _isBusy; private set { if (Set(ref _isBusy, value)) Raise(nameof(IsIdle)); } }
    public bool IsIdle => !_isBusy;
    public bool IsIndeterminate { get => _isIndeterminate; private set => Set(ref _isIndeterminate, value); }
    public int SelectedPage { get => _selectedPage; set => Set(ref _selectedPage, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public string ProgressText { get => _progressText; private set => Set(ref _progressText, value); }
    public string LastMetrics { get => _lastMetrics; private set => Set(ref _lastMetrics, value); }
    public PointCollection LossPoints { get => _lossPoints; private set => Set(ref _lossPoints, value); }
    public string MonitorText { get => _monitorText; private set => Set(ref _monitorText, value); }
    public string FpStatus { get => _fpStatus; private set => Set(ref _fpStatus, value); }
    public bool MonitorConnected { get => _monitorConnected; private set { if (Set(ref _monitorConnected, value)) Raise(nameof(MonitorIdle)); } }
    public bool MonitorIdle => !_monitorConnected;

    public string DataState { get; private set; } = "";
    public string TrainState { get; private set; } = "";
    public string ExportState { get; private set; } = "";
    public string DeployState { get; private set; } = "";
    public string ExpectedFpText => _expectedFp is null ? "—" : $"{_expectedFp}  ({_expectedBytes:N0} bytes)";

    private void RefreshState()
    {
        if (ProjectLocator.IsProjectRoot(ProjectRoot))
        {
            var pl = new Pipeline(ProjectRoot, _s, Prompt);
            DataState = pl.DataReady ? "\u2714 Listo" : "Pendiente";
            TrainState = pl.TrainReady ? "\u2714 Listo" : "Pendiente";
            ExportState = pl.ExportReady ? "\u2714 Listo" : "Pendiente";
        }
        else DataState = TrainState = ExportState = "Raíz no válida";
        DeployState = _expectedFp is null ? "Pendiente" : "\u2714 Flasheado";
        Raise(nameof(DataState)); Raise(nameof(TrainState)); Raise(nameof(ExportState));
        Raise(nameof(DeployState)); Raise(nameof(ExpectedFpText));
    }

    private void Persist() { try { SettingsStore.Save(_s); } catch (Exception) { } }

    // ---- plumbing -------------------------------------------------------------
    private static void UI(Action a) => Application.Current.Dispatcher.BeginInvoke(a);

    private void Log(string text, bool error = false)
    {
        // Very long lines overflow WPF's text layout and crash the process.
        if (text.Length > 1000) text = text[..1000] + $"… (+{text.Length - 1000} caracteres)";
        text = text.Replace('\t', ' ');
        LogCore(text, error);
    }

    private void LogCore(string text, bool error) =>
        UI(() =>
        {
            Logs.Add(new LogEntry(DateTime.Now.ToString("HH:mm:ss"), text, error));
            if (Logs.Count > 6000) for (var i = 0; i < 1000; i++) Logs.RemoveAt(0);
        });

    private void OnRunnerLine(string line, bool isError)
    {
        Log(line, isError);
        _lineHook?.Invoke(line);
    }

    private async Task Guard(string title, Func<Task<bool>> work)
    {
        if (IsBusy) return;
        IsBusy = true;
        IsIndeterminate = true;
        Progress = 0; ProgressText = "";
        Status = title + "…";
        _cts = new CancellationTokenSource();
        var sw = Stopwatch.StartNew();
        try
        {
            var ok = await work();
            Status = _cts.IsCancellationRequested ? $"Cancelado: {title}"
                : ok ? $"Completado: {title} ({sw.Elapsed:hh\\:mm\\:ss})" : $"Error en: {title} (revisa el registro)";
        }
        catch (Exception ex)
        {
            Log(ex.ToString(), true);
            Status = $"Error en: {title}";
        }
        finally
        {
            _lineHook = null;
            IsIndeterminate = false;
            IsBusy = false;
            RefreshState();
        }
    }

    private Pipeline? NewPipeline(bool needVenv = true)
    {
        if (!ProjectLocator.IsProjectRoot(ProjectRoot))
        {
            Log("La carpeta del proyecto no es válida. Selecciónala en «Entorno».", true);
            return null;
        }
        var pl = new Pipeline(ProjectRoot, _s, Prompt);
        if (needVenv && !File.Exists(pl.VenvPython))
        {
            Log("No existe .venv. Pulsa «Instalar / reparar dependencias» en la pestaña Entorno.", true);
            return null;
        }
        var err = pl.Validate();
        if (err is not null) { Log(err, true); return null; }
        return pl;
    }

    private async Task<bool> Run(CommandSpec spec, Action<string>? hook = null)
    {
        _lineHook = hook;
        var code = await _runner.RunAsync(spec, _cts!.Token);
        _lineHook = null;
        if (code != 0 && !_cts.IsCancellationRequested) Log($"El comando terminó con código {code}.", true);
        return code == 0;
    }

    // ---- pages: environment ---------------------------------------------------
    private void BrowseRoot()
    {
        var dlg = new OpenFolderDialog { Title = "Selecciona la carpeta raíz de esp32-ai", InitialDirectory = ProjectRoot };
        if (dlg.ShowDialog() == true) ProjectRoot = dlg.FolderName;
    }

    private async Task<bool> CheckEnvironmentAsync()
    {
        var items = await EnvironmentChecker.RunAsync(ProjectRoot);
        Checks.Clear();
        foreach (var i in items) Checks.Add(i);
        RefreshPorts();
        return items.All(i => i.Status != CheckStatus.Error);
    }

    private async Task<bool> InstallDepsAsync()
    {
        var pl = NewPipeline(needVenv: false);
        if (pl is null) return false;
        var uv = ToolLocator.Find("uv");
        if (uv is null) { Log("uv no está instalado: https://docs.astral.sh/uv/", true); return false; }

        if (!File.Exists(pl.VenvPython) && !await Run(new CommandSpec(uv, new[] { "sync" }, pl.Root))) return false;

        var cuda = await ProcessRunner.CaptureAsync(new CommandSpec(pl.VenvPython,
            new[] { "-c", "import torch,sys;sys.exit(0 if torch.cuda.is_available() else 1)" }, pl.Root), TimeSpan.FromMinutes(2));
        if (cuda.Ok) Log("PyTorch ya tiene CUDA: se conserva.");
        else if (!await Run(new CommandSpec(uv, new[]
        {
            "pip", "install", "--python", pl.VenvPython, "--reinstall-package", "torch", "torch",
            "--index-url", "https://download.pytorch.org/whl/cu128",
        }, pl.Root))) return false;

        return await Run(new CommandSpec(uv, new[] { "pip", "install", "--python", pl.VenvPython, "pyarrow" }, pl.Root));
    }

    // ---- pages: data / train / export / sample --------------------------------
    private async Task<bool> PrepareAsync()
    {
        var pl = NewPipeline();
        return pl is not null && await Run(pl.Prepare());
    }

    private async Task<bool> TrainAsync()
    {
        var pl = NewPipeline();
        if (pl is null) return false;
        if (!pl.DataReady) { Log("Primero prepara los datos (paso 2).", true); return false; }

        _history.Clear();
        UI(() => { LossPoints = new PointCollection(); LastMetrics = ""; });
        IsIndeterminate = false;
        var total = Steps;
        return await Run(pl.Train(), line =>
        {
            var m = TrainLine.Match(line);
            if (!m.Success) return;
            var step = int.Parse(m.Groups[1].Value);
            var val = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            var ppl = m.Groups[3].Value;
            var elapsed = int.Parse(m.Groups[4].Value);
            var remaining = step > 0 ? TimeSpan.FromSeconds(elapsed / (double)step * (total - step)) : TimeSpan.Zero;
            UI(() =>
            {
                _history.Add((step, val));
                Progress = Math.Min(100, 100.0 * (step + 1) / total);
                ProgressText = $"Paso {step:N0} / {total:N0}" + (step > 0 ? $" · restante ≈ {remaining:hh\\:mm\\:ss}" : "");
                LastMetrics = $"Pérdida de validación {val:F4} · perplejidad {ppl}";
                LossPoints = BuildPoints(total);
            });
        });
    }

    private PointCollection BuildPoints(int total)
    {
        var pts = new PointCollection();
        if (_history.Count < 2) return pts;
        var min = _history.Min(h => h.Val);
        var max = _history.Max(h => h.Val);
        var span = Math.Max(1e-6, max - min);
        foreach (var (step, val) in _history)
            pts.Add(new Point(100.0 * step / Math.Max(1, total - 1), 100 - 100 * (val - min) / span));
        return pts;
    }

    private async Task<bool> ExportAsync()
    {
        var pl = NewPipeline();
        if (pl is null) return false;
        if (!pl.TrainReady) { Log($"No existe el checkpoint {pl.Checkpoint}. Entrena primero.", true); return false; }
        if (!await Run(pl.Export())) return false;
        var size = new FileInfo(pl.ModelBin).Length;
        Log($"model.bin: {size:N0} bytes (partición: {Pipeline.PartitionBytes:N0}).");
        if (size > Pipeline.PartitionBytes) { Log("El modelo no cabe en la partición del ESP32.", true); return false; }
        return true;
    }

    private async Task<bool> SampleAsync()
    {
        var pl = NewPipeline();
        if (pl is null) return false;
        if (!pl.TrainReady) { Log("Entrena primero el modelo.", true); return false; }
        return await Run(pl.Sample());
    }

    // ---- deploy ---------------------------------------------------------------
    private async Task<bool> DeployAsync()
    {
        var pl = NewPipeline();
        if (pl is null) return false;
        if (!pl.ExportReady) { Log("Exporta el modelo antes de flashear (paso 5).", true); return false; }
        if (string.IsNullOrWhiteSpace(Port)) { Log("Selecciona el puerto COM de la placa.", true); return false; }
        if (new FileInfo(pl.ModelBin).Length > Pipeline.PartitionBytes) { Log("model.bin excede la partición.", false); return false; }

        var cli = ToolLocator.Find("arduino-cli");
        var esptool = ToolLocator.Find("esptool");
        if (cli is null || esptool is null) { Log("Faltan arduino-cli o esptool (ver pestaña Entorno).", true); return false; }

        DisconnectMonitor();
        var build = Path.Combine(Path.GetTempPath(), "esp32ai-deploy-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(build);
        try
        {
            Log("== 1/5 Generar vocab.h y prompt.h desde el tokenizador entrenado ==");
            if (!await Run(pl.GenerateVocab())) return false;

            Log("== 2/5 Verificaciones en el PC ==");
            var cc = new[] { "gcc", "cc", "clang" }.Select(ToolLocator.Find).FirstOrDefault(p => p is not null);
            if (cc is null) Log("Sin compilador C: se omiten las verificaciones del host.");
            else if (!await HostGatesAsync(pl, cc, build)) return false;

            Log("== 3/5 Compilar el firmware (-O3) ==");
            if (!await Run(pl.Compile(cli, build))) return false;

            var (bytes, fp) = Fingerprint.Compute(pl.ModelBin);
            Log($"Huella esperada: bytes={bytes} fp={fp}");
            var confirmed = await Application.Current.Dispatcher.InvokeAsync(() => MessageBox.Show(
                $"Se reemplazará el modelo y el firmware en {Port}.\n\nModelo: {pl.ModelBin}\nHuella: {fp}\n\n¿Continuar?",
                "Confirmar flasheo", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes);
            if (!confirmed) { Log("Flasheo cancelado por el usuario."); return false; }

            Log("== 4/5 Escribir el modelo en la partición ==");
            if (!await Run(pl.FlashModel(esptool, Port))) return false;
            Log("== 5/5 Subir el firmware ==");
            if (!await Run(pl.UploadFirmware(cli, Port, build))) return false;

            _expectedFp = fp; _expectedBytes = bytes;
            Log($"Listo. La placa debe imprimir: build: bytes={bytes} fp={fp}. Usa la pestaña Monitor para comprobarlo.");
            return true;
        }
        finally
        {
            try { Directory.Delete(build, true); } catch (Exception) { }
        }
    }

    private async Task<bool> HostGatesAsync(Pipeline pl, string cc, string tmp)
    {
        var golden = Path.Combine(pl.ArtifactDir, "golden.txt");
        if (File.Exists(golden))
        {
            var exe = Path.Combine(tmp, "llm_verify.exe");
            if (!await Run(pl.HostGate(cc, exe, "verify.c", null))) return false;
            if (!await Run(new CommandSpec(exe, new[] { pl.ModelBin, golden }, pl.Root))) return false;
        }
        else Log("Sin golden.txt: se omite la verificación contra PyTorch.");

        var staging = Path.Combine(tmp, "llm_staging.exe");
        if (!await Run(pl.HostGate(cc, staging, "staging_verify.c", "-DLLM_INT8_ACT=1"))) return false;
        return await Run(new CommandSpec(staging, new[] { pl.ModelBin }, pl.Root));
    }

    private async Task<bool> RunAllAsync()
    {
        var steps = new (string Name, Func<Task<bool>> Run)[]
        {
            ("Preparar datos", PrepareAsync), ("Entrenar", TrainAsync), ("Exportar", ExportAsync),
            ("Probar", SampleAsync), ("Flashear", DeployAsync),
        };
        foreach (var (name, run) in steps)
        {
            Log($"######## {name} ########");
            Status = $"Pipeline completo: {name}…";
            IsIndeterminate = name != "Entrenar";
            if (!await run() || _cts!.IsCancellationRequested) { Log($"Pipeline detenido en: {name}", true); return false; }
        }
        return true;
    }

    // ---- serial monitor -------------------------------------------------------
    private void RefreshPorts()
    {
        var list = SerialPorts.List();
        Ports.Clear();
        foreach (var p in list) Ports.Add(p);
        if (string.IsNullOrEmpty(Port) || !list.Contains(Port)) Port = list.FirstOrDefault() ?? "";
    }

    private void ConnectMonitor()
    {
        if (string.IsNullOrWhiteSpace(Port)) { Log("Selecciona un puerto COM.", true); return; }
        try
        {
            _monitor.Open(Port);
            MonitorConnected = true;
            FpStatus = "Esperando el arranque… pulsa «Reiniciar placa».";
        }
        catch (Exception ex) { Log($"No se pudo abrir {Port}: {ex.Message}", true); }
    }

    private void DisconnectMonitor()
    {
        _monitor.Close();
        MonitorConnected = false;
    }

    private void OnMonitorText(string text) => UI(() =>
    {
        _monitorBuffer.Append(text);
        if (_monitorBuffer.Length > 200_000) _monitorBuffer.Remove(0, _monitorBuffer.Length - 150_000);
        MonitorText = _monitorBuffer.ToString();

        var m = BuildLine.Matches(MonitorText);
        if (m.Count == 0) return;
        var last = m[^1];
        var fp = last.Groups[2].Value;
        FpStatus = _expectedFp is null ? $"La placa reporta fp={fp} (aún no has flasheado desde esta sesión)."
            : fp == _expectedFp ? $"\u2714 Huella correcta ({fp}): el modelo de la placa es el que flasheaste."
            : $"\u2716 La placa reporta {fp} pero se esperaba {_expectedFp}. Vuelve a flashear.";
    });

    // ---- misc -----------------------------------------------------------------
    private void SaveLog()
    {
        var dlg = new SaveFileDialog { FileName = $"esp32-spanish-{DateTime.Now:yyyyMMdd-HHmm}.log", Filter = "Registro|*.log;*.txt" };
        if (dlg.ShowDialog() == true)
            File.WriteAllLines(dlg.FileName, Logs.Select(l => $"{l.Time} {(l.IsError ? "[ERR] " : "")}{l.Text}"));
    }

    private void OpenArtifacts()
    {
        var pl = ProjectLocator.IsProjectRoot(ProjectRoot) ? new Pipeline(ProjectRoot, _s, Prompt) : null;
        if (pl is null) return;
        Directory.CreateDirectory(pl.ArtifactDir);
        Process.Start(new ProcessStartInfo("explorer.exe", pl.ArtifactDir) { UseShellExecute = true });
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _monitor.Dispose();
    }
}
