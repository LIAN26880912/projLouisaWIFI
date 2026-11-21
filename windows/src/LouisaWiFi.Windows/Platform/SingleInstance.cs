using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;

namespace LouisaWiFi.Windows.Platform;

/// One tray icon per Windows user. A second launch forwards its arguments to the running app
/// over a named pipe that only the same user can open (PipeOptions.CurrentUserOnly).
public sealed class SingleInstance : IDisposable
{
    internal const int MaxMessageBytes = 4096;
    private readonly Mutex mutex;

    private SingleInstance(Mutex mutex, string name)
    {
        this.mutex = mutex;
        Name = name;
    }

    public string Name { get; }

    public static string DefaultName => "LouisaWiFi-" + WindowsIdentity.GetCurrent().User!.Value;

    /// null when another instance already owns the name.
    public static SingleInstance? TryAcquire(string? name = null)
    {
        name ??= DefaultName;
        var mutex = new Mutex(false, @"Local\" + name, out var createdNew);
        if (createdNew) return new SingleInstance(mutex, name);
        mutex.Dispose();
        return null;
    }

    public static bool Send(string[] args, string? name = null, int timeoutMs = 5000)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", name ?? DefaultName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(timeoutMs);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(args);
            if (bytes.Length > MaxMessageBytes) return false;
            client.Write(bytes);
            return true;
        }
        catch (Exception error) when (error is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// Runs until cancelled; each message is handed to onArgs (on a pool thread).
    public async Task ListenAsync(Action<string[]> onArgs, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(Name, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellationToken);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var args = await ReadAsync(server, timeout.Token);
                if (args is not null) onArgs(args);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception error) when (error is IOException or OperationCanceledException or JsonException or UnauthorizedAccessException)
            {
                AppLog.Write($"指令接收失敗：{error.GetType().Name}");
                await Task.Delay(200, CancellationToken.None);
            }
        }
    }

    internal static async Task<string[]?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxMessageBytes + 1];
        var length = 0;
        int read;
        while (length < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken)) > 0)
            length += read;
        if (length == 0 || length > MaxMessageBytes) return null;
        var args = JsonSerializer.Deserialize<string?[]>(buffer.AsSpan(0, length));
        return args is null || args.Any(a => a is null) ? null : args.Select(a => a!).ToArray();
    }

    public void Dispose() => mutex.Dispose();
}
