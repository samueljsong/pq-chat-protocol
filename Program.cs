using System.Threading;

var server = new TcpChatServer(9001);

var cts = new CancellationTokenSource();

Console.CancelKeyPress += (s, e) =>
{
    Console.WriteLine("Shutting down...");
    e.Cancel = true;
    cts.Cancel();
};

await server.RunAsync(cts.Token);