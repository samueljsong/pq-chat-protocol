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

    // conversationId -> set of userIds (or connections)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _rooms = new();

    public TcpChatServer(int port)
    {
        _port = port;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Any, _port);
        listener.Start();
        Console.WriteLine($"TCP Chat Server listening on port {_port}");

        while (!ct.IsCancellationRequested)
        {
            var tcp = await listener.AcceptTcpClientAsync(ct);
            _ = HandleClientAsync(tcp, ct); // fire & forget per client
        }
    }

    private async Task HandleClientAsync(TcpClient tcp, CancellationToken ct)
    {
        var conn = new ClientConn(tcp);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var payload = await ReadFrame(conn.Stream, ct);
                if (payload is null) break;

                var json = Encoding.UTF8.GetString(payload);
                var msg = JsonSerializer.Deserialize<ClientMsg>(json);
                if (msg is null) continue;

                switch (msg.Type)
                {
                    case "AUTH":
                        // TODO: validate JWT token properly and extract userId.
                        // For prototype: treat token field as userId (DON'T ship this).
                        var userId = ValidateTokenAndGetUserId(msg.Token);
                        conn.UserId = userId;

                        _users[userId] = conn;

                        await SendAsync(conn, new ServerMsg
                        {
                            Type = "AUTH_OK",
                            UserId = userId
                        }, ct);
                        break;

                    case "JOIN_CONVERSATION":
                        EnsureAuthed(conn);

                        // TODO: verify membership in DB:
                        // SELECT 1 FROM conversation_members WHERE conversation_id=@cid AND user_id=@uid AND left_at IS NULL;
                        var cid = msg.ConversationId ?? throw new Exception("Missing conversationId");

                        var set = _rooms.GetOrAdd(cid, _ => new ConcurrentDictionary<string, byte>());
                        set[conn.UserId!] = 0;

                        await SendAsync(conn, new ServerMsg
                        {
                            Type = "JOIN_OK",
                            ConversationId = cid
                        }, ct);
                        break;

                    case "SEND_MESSAGE":
                        EnsureAuthed(conn);

                        var convId = msg.ConversationId ?? throw new Exception("Missing conversationId");
                        var ciphertext = msg.Ciphertext ?? "";

                        // Verify sender joined / is member
                        if (!_rooms.TryGetValue(convId, out var members) || !members.ContainsKey(conn.UserId!))
                            throw new Exception("Not joined to conversation");

                        // Forward to all other online members
                        foreach (var memberUserId in members.Keys)
                        {
                            if (memberUserId == conn.UserId) continue;

                            if (_users.TryGetValue(memberUserId, out var target) && target.IsOpen)
                            {
                                await SendAsync(target, new ServerMsg
                                {
                                    Type = "MESSAGE",
                                    ConversationId = convId,
                                    FromUserId = conn.UserId,
                                    Ciphertext = ciphertext
                                }, ct);
                            }
                        }

                        // Optional: ack sender
                        await SendAsync(conn, new ServerMsg
                        {
                            Type = "SEND_OK",
                            ConversationId = convId
                        }, ct);
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Client error: {ex.Message}");
        }
        finally
        {
            // cleanup
            if (conn.UserId is not null)
            {
                _users.TryRemove(conn.UserId, out _);
                foreach (var kv in _rooms)
                    kv.Value.TryRemove(conn.UserId, out _);
            }

            conn.Dispose();
        }
    }

    private static void EnsureAuthed(ClientConn conn)
    {
        if (string.IsNullOrWhiteSpace(conn.UserId))
            throw new Exception("Not authenticated");
    }

    private static string ValidateTokenAndGetUserId(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new Exception("Missing token");

        // TODO: replace with your real JWT validation (same key/issuer/audience as API)
        // Return claim: ClaimTypes.NameIdentifier
        return token; // PROTOTYPE ONLY
    }

    private static async Task SendAsync(ClientConn conn, ServerMsg msg, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(msg);
        var bytes = Encoding.UTF8.GetBytes(json);
        await WriteFrame(conn.Stream, bytes, ct);
    }

    private static async Task WriteFrame(NetworkStream stream, byte[] payload, CancellationToken ct)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(payload, ct);
        await stream.FlushAsync(ct);
    }

    private static async Task<byte[]?> ReadFrame(NetworkStream stream, CancellationToken ct)
    {
        var header = new byte[4];
        var got = await ReadExactly(stream, header, 0, 4, ct);
        if (got == 0) return null;

        int len = BinaryPrimitives.ReadInt32BigEndian(header);
        if (len < 0 || len > 10_000_000) throw new Exception("Bad frame length");

        var payload = new byte[len];
        await ReadExactly(stream, payload, 0, len, ct);
        return payload;
    }

    private static async Task<int> ReadExactly(NetworkStream stream, byte[] buf, int off, int count, CancellationToken ct)
    {
        int total = 0;
        while (total < count)
        {
            int n = await stream.ReadAsync(buf.AsMemory(off + total, count - total), ct);
            if (n == 0) return total;
            total += n;
        }
        return total;
    }

    private sealed class ClientConn : IDisposable
    {
        public TcpClient Tcp { get; }
        public NetworkStream Stream { get; }
        public string? UserId { get; set; }
        public bool IsOpen => Tcp.Connected;

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
        public string Type { get; init; } = "";
        public string? Token { get; init; }
        public string? ConversationId { get; init; }
        public string? Ciphertext { get; init; }
    }

    private sealed record ServerMsg
    {
        public string Type { get; init; } = "";
        public string? UserId { get; init; }
        public string? ConversationId { get; init; }
        public string? FromUserId { get; init; }
        public string? Ciphertext { get; init; }
    }
}
