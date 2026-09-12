using System.Diagnostics;

namespace GameSaveManager.Core;

public sealed record ProcessResult(int ExitCode, string Output, string Error);
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken ct = default);
}
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        var operationToken = timeout.Token;
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("Could not start " + executable);
        var output = process.StandardOutput.ReadToEndAsync(operationToken);
        var error = process.StandardError.ReadToEndAsync(operationToken);
        try { await process.WaitForExitAsync(operationToken); }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { }
            if (!ct.IsCancellationRequested) throw new TimeoutException("The operation exceeded ten minutes. Try a selected game or review slow/cloud-only save locations.");
            throw;
        }
        return new(process.ExitCode, await output, await error);
    }
}
