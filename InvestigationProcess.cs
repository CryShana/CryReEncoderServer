using System.Diagnostics;
using System.Text.RegularExpressions;

public partial class InvestigationProcess : IDisposable
{
    public string InputPath { get; }

    Process? _process;

    public InvestigationProcess(string input_path)
    {
        InputPath = input_path;   
    }

    [GeneratedRegex(@"lavfi\.signalstats\.YMIN=(?<val>\d+)")]
    private static partial Regex SignalStatsRegex();

    public async Task<bool?> HasTransparency()
    {
        var (success, log) = await RunWithCommand(@"-vf ""alphaextract,signalstats,metadata=print:file=-"" -v quiet -f null -");
        if (!success) return null;

        // lavfi.signalstats.YMIN=0 
        var match = SignalStatsRegex().Match(log);
        if (!match.Success) return false;

        var val = int.Parse(match.Groups["val"].Value);
        return val < 255;   
    }

    public async Task<(bool success, string output)> RunWithCommand(string command)
    {
        var info = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = $"""
            -hide_banner -i "{InputPath}" {command}
            """,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        string log = "";
        _process = Process.Start(info) ?? throw new Exception("Failed to start encoding process, make sure FFmpeg is accessible in PATH");
        _process.ErrorDataReceived += (_, b) =>
        {
            if (b.Data == null) return;
            log += b.Data + "\n";
        };
        _process.OutputDataReceived += (_, b) =>
        {
            if (b.Data == null) return;
            log += b.Data + "\n";
        };
        _process.BeginErrorReadLine();
        _process.BeginOutputReadLine();

        await _process.WaitForExitAsync();
        var code = _process.ExitCode;
        _process = null;

        return (code == 0, log);
    }

    public void Dispose()
    {
        _process?.Kill();
    }
}
