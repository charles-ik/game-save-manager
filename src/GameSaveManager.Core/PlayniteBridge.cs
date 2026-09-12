using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using GameSaveManager.Contracts;

namespace GameSaveManager.Core;

public sealed class PlayniteBridge : IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    public Func<BridgeMessage, Task<BridgeMessage>>? Handle { get; set; }
    public void Start() => _ = ListenAsync();
    public static string PipeName => "GameSaveManager-" + WindowsIdentity.GetCurrent().User!.Value;
    private async Task ListenAsync()
    {
        while (!lifetime.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(lifetime.Token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(20));
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
                var line = await reader.ReadLineAsync(timeout.Token);
                if (line == null || line.Length > 4_000_000) continue;
                var request = Json.Read<BridgeMessage>(line);
                var response = request.Version == 1 && Handle != null ? await Handle(request) : new BridgeMessage { Kind = "error", Error = "Unsupported bridge protocol." };
                response.RequestId = request.RequestId;
                await writer.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(response));
            }
            catch (OperationCanceledException) { }
            catch (Exception) { await Task.Delay(1000, lifetime.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing); }
        }
    }
    public void Dispose() { lifetime.Cancel(); lifetime.Dispose(); }
}
