namespace Esp32SpanishStudio.Services;

// Builds every command line the app runs, so the UI never assembles arguments itself.
public sealed class Pipeline
{
    public const string DatasetName = "tinystories-es";
    public const string Fqbn =
        "esp32:esp32:esp32s3:UploadSpeed=921600,USBMode=hwcdc,CDCOnBoot=cdc,UploadMode=default," +
        "CPUFreq=240,FlashMode=qio,FlashSize=16M,PartitionScheme=custom,PSRAM=opi,DebugLevel=info";
    public const string PartitionOffset = "0x110000";
    public const long PartitionBytes = 0xEE0000;

    public Pipeline(string root, AppSettings s, string prompt)
    {
        Root = root;
        S = s;
        Prompt = s.AsciiFold ? FoldAscii(prompt) : prompt;
    }

    public string Root { get; }
    public AppSettings S { get; }
    public string Prompt { get; }

    // The device display only draws printable ASCII, so an accented prompt would lose letters.
    public static string FoldAscii(string text)
    {
        var d = text.Replace("¿", "").Replace("¡", "").Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder();
        foreach (var c in d)
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark && c < 128)
                sb.Append(c);
        return sb.ToString();
    }

    public string VenvPython => Path.Combine(Root, ".venv", "Scripts", "python.exe");
    public string DataDir => Path.Combine(Root, "data", DatasetName, $"vocab-{S.VocabSize}");
    public string Tokenizer => Path.Combine(DataDir, "tokenizer.json");
    public string RunName => $"ple-{S.RunTag}-s{S.Seed}";
    public string Checkpoint => Path.Combine(Root, "runs", RunName + ".pt");
    public string ArtifactDir => Path.Combine(Root, "artifacts", DatasetName);
    public string ModelBin => Path.Combine(ArtifactDir, "model.bin");
    public string Sketch => Path.Combine(Root, "firmware", "esp32_tinystories");

    public bool DataReady => File.Exists(Path.Combine(DataDir, "train.bin")) && File.Exists(Tokenizer);
    public bool TrainReady => File.Exists(Checkpoint);
    public bool ExportReady => File.Exists(ModelBin) && File.Exists(Path.Combine(ArtifactDir, "tokenizer.json"));

    private CommandSpec Py(params string[] args) => new(VenvPython, args, Root);

    public CommandSpec Prepare()
    {
        var a = new List<string>
        {
            "-m", "research.tinystories.prepare_es",
            "--vocab", S.VocabSize.ToString(), "--shards", S.Shards.ToString(),
            "--max-mb", "1500", "--max-chars", S.MaxChars.ToString(),
        };
        if (S.AsciiFold) a.Add("--ascii");
        return Py(a.ToArray());
    }

    public CommandSpec Train() => Py(
        "-m", "research.tinystories.train",
        "--arm", "ple", "--dataset", DatasetName, "--vocab", S.VocabSize.ToString(),
        "--d-model", "96", "--n-layers", "6", "--ple-dim", "128", "--target-core", "560000",
        "--batch-size", S.BatchSize.ToString(), "--seq-len", S.SeqLen.ToString(),
        "--steps", S.Steps.ToString(), "--lr", S.LearningRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "--seed", S.Seed.ToString(), "--tag", S.RunTag);

    public CommandSpec Export() => Py(
        "-m", "research.tinystories.export", RunName,
        "--tokenizer", Tokenizer, "--out", ArtifactDir);

    public CommandSpec Sample() => Py(
        "-m", "research.tinystories.sample",
        "--run", Checkpoint, "--tokenizer", Tokenizer, "--prompt", Prompt,
        "--tokens", S.SampleTokens.ToString(),
        "--temperature", S.Temperature.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "--samples", "3");

    public CommandSpec GenerateVocab() => Py(
        Path.Combine(Sketch, "tools", "generate_vocab.py"),
        "--tokenizer", Path.Combine(ArtifactDir, "tokenizer.json"),
        "--out", Path.Combine(Sketch, "generated", "vocab.h"),
        "--prompt", Prompt);

    public CommandSpec HostGate(string compiler, string outExe, string source, string? defines) =>
        new(compiler, new[] { "-O3", "-Wall", "-Wextra" }
            .Concat(defines is null ? Array.Empty<string>() : new[] { defines })
            .Concat(new[] { "-o", outExe, Path.Combine(Root, "runtime", "host_verify", source), "-lm" }).ToArray(), Root);

    public CommandSpec Compile(string arduinoCli, string buildDir) => new(arduinoCli, new[]
    {
        "compile", "--fqbn", Fqbn,
        "--build-property", "compiler.optimization_flags=-O3",
        "--build-path", buildDir, Sketch,
    }, Root);

    public CommandSpec FlashModel(string esptool, string port) => new(esptool, new[]
    {
        "--chip", "esp32s3", "--port", port, "--baud", "921600",
        "write_flash", PartitionOffset, ModelBin,
    }, Root);

    public CommandSpec UploadFirmware(string arduinoCli, string port, string buildDir) => new(arduinoCli, new[]
    {
        "upload", "-p", port, "--fqbn", Fqbn, "--input-dir", buildDir, Sketch,
    }, Root);

    public string? Validate()
    {
        if (S.VocabSize is < 1024 or > 32768) return "El vocabulario debe estar entre 1024 y 32768 (la partición del modelo es de 15,6 MB).";
        if (S.Steps < 100) return "Usa al menos 100 pasos de entrenamiento.";
        if (S.BatchSize < 1 || S.SeqLen < 16) return "Tamaño de lote o longitud de secuencia no válidos.";
        if (string.IsNullOrWhiteSpace(S.RunTag) || S.RunTag.Any(c => !char.IsLetterOrDigit(c) && c != '_'))
            return "La etiqueta solo puede tener letras, números y guion bajo.";
        return null;
    }
}
