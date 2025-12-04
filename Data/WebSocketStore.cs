using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace VenuePlus.Server;

public static class WebSocketStore
{
    private static readonly ConcurrentDictionary<Guid, WebSocket> Sockets = new();
    private static readonly ConcurrentDictionary<Guid, string> SocketClubs = new();
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static void Add(Guid id, WebSocket ws, string clubId)
    {
        Sockets[id] = ws;
        SocketClubs[id] = clubId;
    }

    public static void SetClub(Guid id, string clubId)
    {
        SocketClubs[id] = clubId;
    }

    public static bool TryGetClub(Guid id, out string? clubId)
    {
        if (SocketClubs.TryGetValue(id, out var c)) { clubId = c; return true; }
        clubId = null; return false;
    }

    public static void Remove(Guid id)
    {
        Sockets.TryRemove(id, out _);
        SocketClubs.TryRemove(id, out _);
    }

    public static async System.Threading.Tasks.Task BroadcastAsync(object message)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, JsonOpts));
        var seg = new ArraySegment<byte>(bytes);
        foreach (var kv in Sockets.ToArray())
        {
            var ws = kv.Value;
            if (ws.State == WebSocketState.Open)
            {
                try { await ws.SendAsync(seg, WebSocketMessageType.Text, true, System.Threading.CancellationToken.None); }
                catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"WS broadcast failed: {ex.Message}"); }
            }
            else
            {
                Sockets.TryRemove(kv.Key, out _);
            }
        }
    }

    public static async System.Threading.Tasks.Task SendAsync(WebSocket ws, object message)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, JsonOpts));
        var seg = new ArraySegment<byte>(bytes);
        if (ws.State == WebSocketState.Open)
        {
            try { await ws.SendAsync(seg, WebSocketMessageType.Text, true, System.Threading.CancellationToken.None); } catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"WS send failed: {ex.Message}"); }
        }
    }

    public static async System.Threading.Tasks.Task CloseAllAsync()
    {
        foreach (var kv in Sockets.ToArray())
        {
            var ws = kv.Value;
            try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "server stopping", System.Threading.CancellationToken.None); } catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"WS server stopping close failed: {ex.Message}"); }
            try { await System.Threading.Tasks.Task.Delay(100); } catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"WS delay failed: {ex.Message}"); }
            Sockets.TryRemove(kv.Key, out _);
            SocketClubs.TryRemove(kv.Key, out _);
            try { ws.Dispose(); } catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"WS dispose failed: {ex.Message}"); }
        }
    }

    public static async System.Threading.Tasks.Task BroadcastToClubAsync(string clubId, object message)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, JsonOpts));
        var seg = new ArraySegment<byte>(bytes);
        foreach (var kv in Sockets.ToArray())
        {
            if (!SocketClubs.TryGetValue(kv.Key, out var c) || !string.Equals(c, clubId, StringComparison.Ordinal)) continue;
            var ws = kv.Value;
            if (ws.State == WebSocketState.Open)
            {
                try { await ws.SendAsync(seg, WebSocketMessageType.Text, true, System.Threading.CancellationToken.None); }
                catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"WS club broadcast failed: {ex.Message}"); }
            }
            else
            {
                Sockets.TryRemove(kv.Key, out _);
                SocketClubs.TryRemove(kv.Key, out _);
            }
        }
    }
}
