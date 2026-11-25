using System.Diagnostics;
using System.Globalization;

namespace RcpFlatten;

public enum FlattenStage
{
    Reading,
    Indexing,
}

/// <summary>Progress of the current stage, <see cref="Fraction"/> in [0, 1].</summary>
public readonly record struct FlattenProgress(FlattenStage Stage, double Fraction);

public sealed record FlattenRequest(string Input, string Output, string PolylineFile, double Left, double Right);

public sealed record FlattenOutcome(int ExitCode, bool Cancelled, string? Points, string? Error)
{
    public const int ExitOk = 0;
    public const int ExitNoPoint = 2;
}

public sealed record RcpInfo(long Points, int Scans);

/// <summary>
/// Runs RcpFlattenCli out of process and parses its stdout protocol
/// (PROGRESS, INDEX, RESULT, ERROR). The helper flushes one line per million
/// points read, so reading the progress costs nothing measurable.
/// </summary>
public static class CliRunner
{
    public static string ExePath =>
        Path.Combine(Path.GetDirectoryName(typeof(CliRunner).Assembly.Location) ?? "", "cli", "RcpFlattenCli.exe");

    /// <summary>Blocks until the helper exits. <paramref name="onProgress"/> is called on the calling thread.</summary>
    public static FlattenOutcome Run(FlattenRequest request, Action<FlattenProgress>? onProgress, CancellationToken cancellation)
    {
        var psi = CreateStartInfo(
            "--in", request.Input,
            "--out", request.Output,
            "--poly", request.PolylineFile,
            "--left", request.Left.ToString("R", CultureInfo.InvariantCulture),
            "--right", request.Right.ToString("R", CultureInfo.InvariantCulture));

        string? error = null;
        string? result = null;
        using var process = Process.Start(psi)!;
        bool cancelled = false;
        using var registration = cancellation.Register(() =>
        {
            cancelled = true;
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }     // already exited
            catch (System.ComponentModel.Win32Exception) { }
        });

        string? line;
        while ((line = process.StandardOutput.ReadLine()) != null)
        {
            if (line.StartsWith("PROGRESS ", StringComparison.Ordinal))
            {
                var parts = line.Split(' ');
                if (parts.Length == 3 && double.TryParse(parts[1], CultureInfo.InvariantCulture, out var done) &&
                    double.TryParse(parts[2], CultureInfo.InvariantCulture, out var total) && total > 0)
                    onProgress?.Invoke(new FlattenProgress(FlattenStage.Reading, Math.Clamp(done / total, 0, 1)));
            }
            else if (line.StartsWith("INDEX ", StringComparison.Ordinal))
            {
                if (double.TryParse(line.AsSpan(6), CultureInfo.InvariantCulture, out var percent))
                    onProgress?.Invoke(new FlattenProgress(FlattenStage.Indexing, Math.Clamp(percent / 100, 0, 1)));
            }
            else if (line.StartsWith("ERROR ", StringComparison.Ordinal))
                error = line[6..];
            else if (line.StartsWith("RESULT ", StringComparison.Ordinal))
                result = line[7..];
        }
        process.WaitForExit();

        return new FlattenOutcome(process.ExitCode, cancelled, result, error);
    }

    /// <summary>Reads the point and scan counts of a project (RcpFlattenCli --info). Null on failure.</summary>
    public static RcpInfo? GetInfo(string rcp, CancellationToken cancellation)
    {
        if (!File.Exists(ExePath))
            return null;

        using var process = Process.Start(CreateStartInfo("--info", rcp))!;
        using var registration = cancellation.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });

        RcpInfo? info = null;
        string? line;
        while ((line = process.StandardOutput.ReadLine()) != null)
        {
            // POINTS <n> SCANS <n>
            var parts = line.Split(' ');
            if (parts.Length == 4 && parts[0] == "POINTS" &&
                long.TryParse(parts[1], CultureInfo.InvariantCulture, out var points) &&
                int.TryParse(parts[3], CultureInfo.InvariantCulture, out var scans))
                info = new RcpInfo(points, scans);
        }
        process.WaitForExit();
        return process.ExitCode == FlattenOutcome.ExitOk ? info : null;
    }

    private static ProcessStartInfo CreateStartInfo(params string[] arguments)
    {
        var psi = new ProcessStartInfo(ExePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(ExePath)!,
        };
        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);
        return psi;
    }
}
