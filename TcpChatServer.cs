using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

public class TcpChatServer
{
    private readonly int _port;

    // userId -> connection
    private readonly ConcurrentDictionary<string, ClientConn> _users = new();

    // conversationId -> set of userIds
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _rooms = new();

    public TcpChatServer(int port)
    {
        _port = port;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var listener = new TcpListener(IPAddress.Any, _port);
        listener.Start();
        Console.WriteLine($"TCP Chat Server listening on port {_port}");

        while (!cancellationToken.IsCancellationRequested)
        {
            var tcp = await listener.AcceptTcpClientAsync(cancellationToken);
            _ = HandleClientAsync(tcp, cancellationToken); // fire & forget per client
        }
    }

    private async Task HandleClientAsync(TcpClient tcp, CancellationToken cancellationToken)
    {
        var connection = new ClientConn(tcp);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var payload = await ReadFrame(connection.Stream, cancellationToken);
                if (payload is null) break;

                var json = Encoding.UTF8.GetString(payload);
                var msg  = JsonSerializer.Deserialize<ClientMsg>(json);
                if (msg is null) continue;

                switch (msg.Type)
                {
                    case "AUTH":
                    {
                        var userId = ValidateTokenAndGetUserId(msg.Token);
                        connection.UserId = userId;

                        _users[userId] = connection;

                        await SendAsync(connection, new ServerMsg
                        {
                            Type = "AUTH_OK",
                            UserId = userId
                        }, cancellationToken);

                        break;
                    }

                    case "JOIN_CONVERSATION":
                    {
                        EnsureAuthed(connection);

                        var conversationId = msg.ConversationId ?? throw new Exception("Missing conversationId");

                        var set = _rooms.GetOrAdd(conversationId, _ => new ConcurrentDictionary<string, byte>());
                        set[connection.UserId!] = 0;

                        await SendAsync(connection, new ServerMsg
                        {
                            Type           = "JOIN_OK",
                            UserId         = connection.UserId,
                            ConversationId = conversationId
                        }, cancellationToken);

                        break;
                    }

                    case "SEND_MESSAGE":
                    {
                        EnsureAuthed(connection);

                        var conversationId = msg.ConversationId ?? throw new Exception("Missing conversationId");
                        var ciphertext     = msg.Ciphertext ?? "";

                        // Verify sender joined / is member
                        if (!_rooms.TryGetValue(conversationId, out var members) || !members.ContainsKey(connection.UserId!))
                            throw new Exception("Not joined to conversation");

                        // Forward MESSAGE to all other online members
                        foreach (var memberUserId in members.Keys)
                        {
                            if (memberUserId == connection.UserId) continue;

                            if (_users.TryGetValue(memberUserId, out var target) && target.IsOpen)
                            {
                                await SendAsync(target, new ServerMsg
                                {
                                    Type           = "MESSAGE",
                                    ConversationId = conversationId,
                                    FromUserId     = connection.UserId,
                                    Ciphertext     = ciphertext
                                }, cancellationToken);
                            }
                        }

                        // Single ACK back to sender
                        await SendAsync(connection, new ServerMsg
                        {
                            Type           = "SEND_OK",
                            UserId         = connection.UserId,
                            ConversationId = conversationId,
                            FromUserId     = connection.UserId,
                            Ciphertext     = ciphertext
                        }, cancellationToken);

                        break;
                    }
                }
            }
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Client error: {exception.Message}");
        }
        finally
        {
            if (connection.UserId is not null)
            {
                _users.TryRemove(connection.UserId, out _);
                foreach (var keyValue in _rooms)
                    keyValue.Value.TryRemove(connection.UserId, out _);
            }

            connection.Dispose();
        }
    }

    private static void EnsureAuthed(ClientConn connection)
    {
        if (string.IsNullOrWhiteSpace(connection.UserId))
            throw new Exception("Not authenticated");
    }

    private static string ValidateTokenAndGetUserId(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new Exception("Missing token");
        return token; // PROTOTYPE ONLY
    }

    private static async Task SendAsync(ClientConn connection, ServerMsg message, CancellationToken cancellationToken)
    {
        var json  = JsonSerializer.Serialize(message);
        var bytes = Encoding.UTF8.GetBytes(json);
        
        await WriteFrame(connection.Stream, bytes, cancellationToken);
    }

    private static async Task WriteFrame(NetworkStream stream, byte[] payload, CancellationToken cancellationTokent)
    {
        var header = new byte[4];
        
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        
        await stream.WriteAsync(header, cancellationTokent);
        await stream.WriteAsync(payload, cancellationTokent);
        await stream.FlushAsync(cancellationTokent);
    }

    private static async Task<byte[]?> ReadFrame(NetworkStream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        var got = await ReadExactly(stream, header, 0, 4, cancellationToken);
        if (got == 0) return null;

        int len = BinaryPrimitives.ReadInt32BigEndian(header);
        if (len < 0 || len > 10_000_000) throw new Exception("Bad frame length");

        var payload = new byte[len];
        await ReadExactly(stream, payload, 0, len, cancellationToken);
        return payload;
    }

    private static async Task<int> ReadExactly(NetworkStream stream, byte[] buf, int off, int count, CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < count)
        {
            int n = await stream.ReadAsync(buf.AsMemory(off + total, count - total), cancellationToken);
            if (n == 0) return total;
            total += n;
        }
        return total;
    }

    private sealed class ClientConn : IDisposable
    {
        public TcpClient     Tcp    { get; }
        public NetworkStream Stream { get; }
        public string?       UserId { get; set; }
        public bool          IsOpen => Tcp.Connected;

        public ClientConn(TcpClient tcp)
        {
            Tcp = tcp;
            Stream = tcp.GetStream();
        }

        public void Dispose()
        {
            try { Stream.Close(); } catch { }
            try { Tcp.Close(); } catch { }
        }
    }

    private sealed record ClientMsg
    {
        public string  Type           { get; init; } = "";
        public string? Token          { get; init; }
        public string? ConversationId { get; init; }
        public string? Ciphertext     { get; init; }
    }

    private sealed record ServerMsg
    {
        public string  Type           { get; init; } = "";
        public string? UserId         { get; init; }
        public string? ConversationId { get; init; }
        public string? FromUserId     { get; init; }
        public string? Ciphertext     { get; init; }
    }
}
