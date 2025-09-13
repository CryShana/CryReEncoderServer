using System.Diagnostics;

public class EncodingProcess : IDisposable
{
    public string Executable { get; }
    public string InputPath { get; }
    public string? OutputPath { get; private set; }
    public EncodingProfile Profile { get; }
    public string Command { get; }
    public string Extension { get; }
    public string Log { get; private set; } = "";

    Process? _process;

    public EncodingProcess(string input_path, EncodingProfile profile)
    {
        Executable = string.IsNullOrEmpty(profile.executable_override) ? "ffmpeg" : profile.executable_override;
        Profile = profile;
        InputPath = input_path;
        Command = profile.command?.Trim() ?? "";
        Extension = profile.extension?.Trim() ?? "";
        if (!Extension.StartsWith('.'))
            Extension = $".{Extension}";

        if (string.IsNullOrEmpty(Command))
            throw new Exception("No command specified for encoding profile!");

        if (string.IsNullOrEmpty(Extension))
            throw new Exception("No extension specified for encoding profile!");

        OutputPath = Path.Combine(Path.GetDirectoryName(input_path) ?? ".", Path.GetFileNameWithoutExtension(input_path) + "_" + Path.GetRandomFileName() + Extension);
    }

    static string ProcessCommand(string command, string input, string output)
    {
        var has_overrides = command.IndexOf("$INPUT") >= 0 || command.IndexOf("$OUTPUT") >= 0;
        if (has_overrides)
        {
            command = command.Replace("$INPUT", $"\"{input}\"");
            command = command.Replace("$OUTPUT", $"\"{output}\"");
            return command;
        }

        return $"""
            -hide_banner -loglevel info -stats -i "{input}" {command} "{output}"
        """;
    }
    
    public async Task<bool> RunAsync(ILogger log)
    {
        if (_process != null) throw new InvalidOperationException("Encoding already started");

        var command = ProcessCommand(Command, InputPath, OutputPath ?? "");
        log.LogInformation("Started process: {0} {1}", Executable, command);

        var info = new ProcessStartInfo
        {
            FileName = Executable,
            Arguments = command,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = false,
            CreateNoWindow = true
        };

        _process = Process.Start(info) ?? throw new Exception("Failed to start encoding process, make sure FFmpeg is accessible in PATH");
        _process.ErrorDataReceived += (_, b) =>
        {
            if (b.Data == null) return;
            Log += b.Data + "\n";
        };
        _process.BeginErrorReadLine();

        await _process.WaitForExitAsync();
        var code = _process.ExitCode;
        _process = null;

        return code == 0;
    }

    public void Dispose()
    {
        _process?.Kill();
    }
}
