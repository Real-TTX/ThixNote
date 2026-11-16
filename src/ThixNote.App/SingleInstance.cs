using System.IO.Pipes;
using System.Net.Sockets;

namespace ThixNote.App;

/// <summary>
/// Keeps one ThixNote window per user: a second start (e.g. double click on a .txts file)
/// hands its files to the running instance, which opens them as tabs.
/// </summary>
internal static class SingleInstance
{
    private static readonly string PipeName = "ThixNote-" + Environment.UserName;

    public static bool TryForwardToRunningInstance(IReadOnlyList<string> files)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(250);
            using var writer = new StreamWriter(client);
            writer.Write(string.Join('\n', files));
            writer.Flush();
            return true;
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or UnauthorizedAccessException or SocketException)
        {
            return false;
        }
    }

    public static void StartListening(Action<string[]> onFilesReceived)
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync();
                    using var reader = new StreamReader(server);
                    var message = await reader.ReadToEndAsync();
                    onFilesReceived(message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SocketException)
                {
                    // Another instance owns the pipe or the connection broke: single instance is a convenience, never fatal.
                    return;
                }
            }
        });
    }
}
