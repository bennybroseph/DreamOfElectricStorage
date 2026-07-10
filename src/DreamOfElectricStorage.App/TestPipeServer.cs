using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace DreamOfElectricStorage.App;

/// <summary>
/// Demo-mode-only command channel for the test harness (AppDriver `cmd` action):
/// drives the app in-process instead of injecting global mouse/keyboard input,
/// so tests never fight the user for the cursor. One command per connection;
/// the response is written and the pipe closed (client reads to EOF).
/// Commands run on the UI thread — same single-writer contract as real input.
/// </summary>
internal sealed class TestPipeServer : IDisposable
{
    public const string PipeName = "DreamOfElectricStorage.TestPipe";

    private readonly Func<string, Task<string>> _dispatch;
    private readonly CancellationTokenSource _cts = new();

    public TestPipeServer(Func<string, Task<string>> dispatch)
    {
        _dispatch = dispatch;
        _ = RunAsync();
    }

    private async Task RunAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.InOut, maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(_cts.Token);

                using var reader = new StreamReader(server, leaveOpen: true);
                using var writer = new StreamWriter(server, leaveOpen: true) { AutoFlush = true };

                string? line = await reader.ReadLineAsync(_cts.Token);
                string response;
                try
                {
                    response = string.IsNullOrWhiteSpace(line) ? "err empty command" : await _dispatch(line);
                }
                catch (Exception ex)
                {
                    response = $"err {ex.Message}";
                }
                await writer.WriteAsync(response);
                await writer.FlushAsync();
                server.WaitForPipeDrain();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // client vanished mid-exchange — keep serving
            }
        }
    }

    public void Dispose() => _cts.Cancel();
}
