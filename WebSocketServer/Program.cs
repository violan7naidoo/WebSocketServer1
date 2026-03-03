using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

internal static class Program
{
    // -----------------------------
    // Roles
    // -----------------------------
    private const string ROLE_EGM = "egm";
    private const string ROLE_ROULETTE = "roulette";


    // Stop immediate duplicate round_result (same roundId repeated back-to-back)
    private static string _lastRoundResultRoundId = "";

    // -----------------------------
    // Connection State
    // -----------------------------
    private static readonly object _clientsLock = new();
    private static readonly List<WebSocket> _clients = new();
    private static readonly ConcurrentDictionary<WebSocket, string> _roleBySocket = new();

    // Send session_initialized once per roulette socket
    private static readonly ConcurrentDictionary<WebSocket, byte> _sessionSentToRoulette = new();

    // Message queue (single consumer)
    private static readonly ConcurrentQueue<(WebSocket sender, string message)> _queue = new();

    // -----------------------------
    // Session/Balance State
    // -----------------------------
    private static long _sequence = 0;
    private static volatile bool _sessionInitializedFromEgm = false;
    private static  decimal _currentEgmBalance = 0m;

    // Pending bet storage for bet_commit -> round_result correlation
    private static volatile int _pendingBetStakeCents = 0;

    // -----------------------------
    // Logging
    // -----------------------------
    private static readonly string _logFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        "ws",
        "websocket_adapter.txt"
    );

    public static async Task Main(string[] args)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_logFile)!);
        LogInfo("=== WebSocket Adapter START ===");
        LogInfo("Listening on http://localhost:5000");
        LogInfo($"Log: {_logFile}");

        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls("http://localhost:5000");

        var app = builder.Build();
        app.UseWebSockets();

        // Background processor
        _ = Task.Run(ProcessQueueAsync);

        // Route by path, role is fixed at accept time.
        app.Map("/ws/egm", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync("WebSocket required");
                return;
            }

            using var ws = await context.WebSockets.AcceptWebSocketAsync();
            await HandleSocketAsync(ws, ROLE_EGM);
        });

        app.Map("/ws/roulette", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync("WebSocket required");
                return;
            }

            using var ws = await context.WebSockets.AcceptWebSocketAsync();
            await HandleSocketAsync(ws, ROLE_ROULETTE);
        });

        await app.RunAsync();
    }

    private static async Task HandleSocketAsync(WebSocket ws, string role)
    {
        RegisterClient(ws, role);

        // If roulette connects after EGM session initialized, push session_initialized immediately (once per socket).
        if (role.Equals(ROLE_ROULETTE, StringComparison.OrdinalIgnoreCase) && _sessionInitializedFromEgm)
        {
            if (_sessionSentToRoulette.TryAdd(ws, 0))
            {
                await SendSessionInitializedToSingleRouletteAsync(ws, _currentEgmBalance);
            }
        }

        var buffer = new byte[1024 * 8];

        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                if (result.MessageType != WebSocketMessageType.Text)
                    continue;

                var msg = Encoding.UTF8.GetString(buffer, 0, result.Count);
                LogReceived(role, msg);

                _queue.Enqueue((ws, msg));
            }
        }
        catch (Exception ex)
        {
            LogInfo($"[SOCKET ERROR] role={role} err={ex}");
        }
        finally
        {
            UnregisterClient(ws);
            try
            {
                if (ws.State != WebSocketState.Closed)
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
            }
            catch { /* ignore */ }

            LogInfo($"[DISCONNECT] role={role}");
        }
    }

    private static void RegisterClient(WebSocket ws, string role)
    {
        lock (_clientsLock)
        {
            _clients.Add(ws);
        }
        _roleBySocket[ws] = role;

        var counts = GetRoleCounts();
        LogInfo($"[CONNECT] role={role} total={counts.total} egm={counts.egm} roulette={counts.roulette}");
    }

    private static void UnregisterClient(WebSocket ws)
    {
        lock (_clientsLock)
        {
            _clients.Remove(ws);
        }
        _roleBySocket.TryRemove(ws, out _);
        _sessionSentToRoulette.TryRemove(ws, out _);
    }

    private static (int total, int egm, int roulette) GetRoleCounts()
    {
        List<WebSocket> snap;
        lock (_clientsLock) snap = new List<WebSocket>(_clients);

        int egm = 0, rou = 0;
        foreach (var s in snap)
        {
            if (_roleBySocket.TryGetValue(s, out var r))
            {
                if (r.Equals(ROLE_EGM, StringComparison.OrdinalIgnoreCase)) egm++;
                else if (r.Equals(ROLE_ROULETTE, StringComparison.OrdinalIgnoreCase)) rou++;
            }
        }
        return (snap.Count, egm, rou);
    }

    // =========================================================
    // Queue processor: single consumer so logic is consistent
    // =========================================================
    private static async Task ProcessQueueAsync()
    {
        while (true)
        {
            while (_queue.TryDequeue(out var item))
            {
                var (sender, message) = item;

                if (!_roleBySocket.TryGetValue(sender, out var senderRole))
                {
                    // Socket already removed; ignore
                    continue;
                }

                try
                {
                    using var doc = JsonDocument.Parse(message);
                    var root = doc.RootElement;

                    // EGM uses "EventType" (PascalCase), roulette uses "@event" or "event"
                    var eventType = GetEventType(root);

                    if (string.IsNullOrWhiteSpace(eventType))
                    {
                        LogInfo($"[WARN] No eventType/event found. role={senderRole} msg={message}");
                        continue;
                    }

                    if (senderRole.Equals(ROLE_EGM, StringComparison.OrdinalIgnoreCase))
                    {
                        await HandleFromEgmAsync(sender, root, eventType, message);
                    }
                    else if (senderRole.Equals(ROLE_ROULETTE, StringComparison.OrdinalIgnoreCase))
                    {
                        await HandleFromRouletteAsync(sender, root, eventType, message);
                    }
                    else
                    {
                        LogInfo($"[WARN] Unknown role '{senderRole}'");
                    }
                }
                catch (Exception ex)
                {
                    LogInfo($"[PROCESS ERROR] {ex.Message} | msg={message}");
                }
            }

            await Task.Delay(10);
        }
    }

    // =========================================================
    // EGM -> Roulette translation (this is what your UI needs)
    // =========================================================
    private static async Task HandleFromEgmAsync(WebSocket sender, JsonElement root, string eventType, string rawMessage)
    {
        switch (eventType)
        {
            case "session_initialized":
                {
                    // This is EGM -> Adapter message (camelcase), not the roulette format.
                    // We generate roulette session_initialized and broadcast to roulette sockets.
                    decimal availableCredits = 0m;
                    if (root.TryGetProperty("payload", out var payload) &&
                        payload.TryGetProperty("availableCredits", out var ac))
                    {
                        availableCredits = SafeGetDecimal(ac);
                    }

                    _currentEgmBalance = availableCredits;
                    _sessionInitializedFromEgm = true;

                    await BroadcastSessionInitializedToRouletteAsync(availableCredits);
                    break;
                }

            case "BILL_INSERTED":
                {
                    decimal amount = GetDecimal(root, "amount", 0m);
                    decimal currentCredits = GetDecimal(root, "CurrentCredits", 0m);
                    string egmId = GetString(root, "egmId", "EGM-0441");

                    _currentEgmBalance = currentCredits;

                    // Map to roulette cash_event
                    var roundId = $"round-{DateTime.UtcNow.Ticks}";
                    var seq = NextSeq();

                    int denominationCents = (int)Math.Round(amount * 100m);

                    var cashEvent = new
                    {
                        @event = "cash_event",
                        roundId,
                        egmId,
                        sequence = seq,
                        sentAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                        nonce = Guid.NewGuid().ToString(),
                        payload = new
                        {
                            type = "bv_stack",
                            bvSerial = "BV-INTERNAL",
                            denomination = denominationCents,
                            currency = "ZAR",
                            bvStackerCount = 1,
                            sequenceNumber = (int)(DateTime.UtcNow.Ticks % 1_000_000),
                            meters = new
                            {
                                cashin = denominationCents,
                                credits = (int)currentCredits
                            }
                        }
                    };

                    await BroadcastToRoleAsync(ROLE_ROULETTE, JsonSerializer.Serialize(cashEvent), sender, "cash_event");
                    await SendBalanceSnapshotToRouletteAsync(sender, currentCredits, egmId);
                    break;
                }

            case "AFT_DEPOSIT":
                {
                    decimal amount = GetDecimal(root, "Amount", 0m);
                    decimal currentCredits = GetDecimal(root, "CurrentCredits", 0m);
                    string egmId = GetString(root, "egmId", "EGM-0441");
                    string aftReference = GetString(root, "AFTReference", "");

                    _currentEgmBalance = currentCredits;

                    string roundId = $"round-{DateTime.UtcNow.Ticks}";
                    long seq = NextSeq();

                    string aftTxnId = !string.IsNullOrWhiteSpace(aftReference) ? aftReference : $"AFT-{DateTime.UtcNow.Ticks}";
                    string authCode = !string.IsNullOrWhiteSpace(aftReference)
                        ? $"AUTH-{aftReference.Substring(Math.Max(0, aftReference.Length - 8))}"
                        : $"AUTH-{Guid.NewGuid():N}".Substring(0, 12).ToUpperInvariant();

                    var aftTransfer = new
                    {
                        @event = "aft_transfer",
                        roundId,
                        egmId,
                        sequence = seq,
                        sentAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                        nonce = Guid.NewGuid().ToString(),
                        payload = new
                        {
                            aftTxnId,
                            originHost = "EGM-LOCAL",
                            amount = (int)Math.Round(amount), // roulette expects int
                            authCode,
                            remainingBalance = (int)Math.Round(currentCredits)
                        }
                    };

                    await BroadcastToRoleAsync(ROLE_ROULETTE, JsonSerializer.Serialize(aftTransfer), sender, "aft_transfer");
                    await SendBalanceSnapshotToRouletteAsync(sender, currentCredits, egmId);
                    break;
                }

            case "AFT_CASHOUT":
                {
                    decimal amount = GetDecimal(root, "Amount", 0m);
                    decimal currentCredits = GetDecimal(root, "CurrentCredits", 0m); // expected 0
                    string egmId = GetString(root, "egmId", "EGM-0441");

                    decimal egmBalanceBefore = _currentEgmBalance;
                    decimal egmBalanceAfter = currentCredits;

                    _currentEgmBalance = currentCredits;

                    string roundId = $"round-{DateTime.UtcNow.Ticks}";
                    long seq = NextSeq();

                    var cashoutAck = new
                    {
                        @event = "cashout_ack",
                        roundId,
                        egmId,
                        sequence = seq,
                        sentAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                        nonce = Guid.NewGuid().ToString(),
                        payload = new
                        {
                            roundId,
                            accepted = true,
                            reason = (string?)null,
                            bet = 0,
                            win = 0,
                            egmBalanceBefore = (int)Math.Round(egmBalanceBefore),
                            egmBalanceAfter = (int)Math.Round(egmBalanceAfter)
                        }
                    };

                    await BroadcastToRoleAsync(ROLE_ROULETTE, JsonSerializer.Serialize(cashoutAck), sender, "cashout_ack");
                    await SendBalanceSnapshotToRouletteAsync(sender, currentCredits, egmId);
                    break;
                }

            case "ui_ping":
            case "UI_PING":
                {
                    // Frontend ping -> server must pong back to SAME roulette socket
                    string egmId = "EGM-0441";

                    // best-effort extract egmId if present
                    if (root.TryGetProperty("egmId", out var eid) && eid.ValueKind == JsonValueKind.String)
                        egmId = eid.GetString() ?? egmId;
                    else if (root.TryGetProperty("payload", out var p0) &&
                             p0.TryGetProperty("egmId", out var peid) &&
                             peid.ValueKind == JsonValueKind.String)
                        egmId = peid.GetString() ?? egmId;

                    long seq = NextSeq();

                    // echo frontend timestamp if provided
                    long clientTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    if (root.TryGetProperty("payload", out var payload) &&
                        payload.TryGetProperty("timestamp", out var ts) &&
                        ts.ValueKind == JsonValueKind.Number)
                    {
                        clientTimestamp = ts.GetInt64();
                    }

                    var uiPong = new
                    {
                        @event = "ui_pong",
                        egmId,
                        sequence = seq,
                        sentAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                        nonce = Guid.NewGuid().ToString(),
                        payload = new
                        {
                            timestamp = clientTimestamp,
                            serverTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                        }
                    };

                    // IMPORTANT: reply ONLY to sender (frontend)
                    await SendToSocketAsync(sender, JsonSerializer.Serialize(uiPong), "ROULETTE", "ui_pong");
                    break;
                }

            case "SPIN_COMPLETED":
                {
                    // Pass-through to roulette (and update _currentEgmBalance if present)
                    if (root.TryGetProperty("CurrentCredits", out var cc))
                        _currentEgmBalance = SafeGetDecimal(cc);
                    else if (root.TryGetProperty("currentCredits", out var cc2))
                        _currentEgmBalance = SafeGetDecimal(cc2);

                    await BroadcastToRoleAsync(ROLE_ROULETTE, rawMessage, sender, "SPIN_COMPLETED");
                    break;
                }

            default:
                // Unknown EGM event: if you want, pass through to roulette for logging/telemetry
                LogInfo($"[EGM->ADAPTER] Unhandled EventType={eventType} (not forwarded).");
                break;
        }
    }

    // =========================================================
    // Roulette -> EGM adapter (ONLY the two events you want)
    // =========================================================
    private static async Task HandleFromRouletteAsync(WebSocket sender, JsonElement root, string eventType, string rawMessage)
    {
        switch (eventType)
        {
            case "bet_commit":
                {
                    // Extract totalStake (cents)
                    int totalStakeCents = 0;
                    string roundId = "";
                    string egmId = "EGM-0441";

                    if (root.TryGetProperty("payload", out var payload))
                    {
                        if (payload.TryGetProperty("totalStake", out var ts) && ts.ValueKind == JsonValueKind.Number)
                            totalStakeCents = ts.GetInt32();

                        if (payload.TryGetProperty("roundId", out var rid))
                            roundId = rid.GetString() ?? "";

                        if (payload.TryGetProperty("egmId", out var eid))
                            egmId = eid.GetString() ?? egmId;
                    }

                    _pendingBetStakeCents = totalStakeCents;

                    // Send NO_MORE_BETS to EGM (BetValue in cents)
                    var noMoreBets = new
                    {
                        EventType = "NO_MORE_BETS",
                        Timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                        RoundId = roundId,
                        EgmId = egmId,
                        BetValue = totalStakeCents
                    };

                    await BroadcastToRoleAsync(ROLE_EGM, JsonSerializer.Serialize(noMoreBets), sender, "NO_MORE_BETS");
                    break;
                }

                case "ui_ping":
                case "UI_PING":
                {
                    LogInfo($"[PING] ui_ping received from roulette frontend");

                    string egmId = "EGM-0441";
                    if (root.TryGetProperty("egmId", out var eid) && eid.ValueKind == JsonValueKind.String)
                        egmId = eid.GetString() ?? egmId;
                    else if (root.TryGetProperty("payload", out var payload) &&
                             payload.TryGetProperty("egmId", out var peid) &&
                             peid.ValueKind == JsonValueKind.String)
                        egmId = peid.GetString() ?? egmId;

                    long seq = NextSeq();

                    long payloadTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    if (root.TryGetProperty("payload", out var payload2) &&
                        payload2.TryGetProperty("timestamp", out var ts) &&
                        ts.ValueKind == JsonValueKind.Number)
                    {
                        payloadTimestamp = ts.GetInt64();
                    }

                    var uiPong = new
                    {
                        @event = "ui_pong",
                        egmId,
                        sequence = seq,
                        sentAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                        nonce = Guid.NewGuid().ToString(),
                        payload = new
                        {
                            timestamp = payloadTimestamp
                        }
                    };

                    // IMPORTANT: reply back to the SAME socket that pinged us
                    await SendToSocketAsync(sender, JsonSerializer.Serialize(uiPong), "ROULETTE", "ui_pong");
                    break;
                }

            case "round_result":
                {
                    // Extract winAmount (cents)
                    int winAmountCents = 0;
                    string roundId = "";
                    string egmId = "EGM-0441";

                    if (root.TryGetProperty("payload", out var payload))
                    {
                        if (payload.TryGetProperty("winAmount", out var wa) && wa.ValueKind == JsonValueKind.Number)
                            winAmountCents = wa.GetInt32();

                        if (payload.TryGetProperty("roundId", out var rid))
                            roundId = rid.GetString() ?? "";

                        if (payload.TryGetProperty("egmId", out var eid))
                            egmId = eid.GetString() ?? egmId;
                    }

                    // ✅ Immediate duplicate guard (same roundId back-to-back)
                    if (!string.IsNullOrWhiteSpace(roundId) &&
                        string.Equals(_lastRoundResultRoundId, roundId, StringComparison.OrdinalIgnoreCase))
                    {
                        LogInfo($"[DEDUP] Ignoring immediate duplicate round_result for roundId={roundId}");

                        // Still ACK duplicates so Roulette stops retrying
                        var dupAck = new
                        {
                            @event = "round_result_ack",
                            roundId = roundId,
                            egmId = egmId,
                            sequence = NextSeq(),
                            sentAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                            nonce = Guid.NewGuid().ToString(),
                            payload = new { roundId = roundId, accepted = true }
                        };

                        await SendToSocketAsync(sender, JsonSerializer.Serialize(dupAck), "ROULETTE", "round_result_ack");
                        return;
                    }

                    // mark last processed
                    _lastRoundResultRoundId = roundId;

                    // Convert cents -> credits for EGM (EGM expects BetAmount/WinAmount in credits)
                    int betCredits = _pendingBetStakeCents / 100;
                    int winCredits = winAmountCents / 100;

                    var gameUpdate = new
                    {
                        EventType = "GAME_UPDATE",
                        BetAmount = betCredits,
                        WinAmount = winCredits,
                        Timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                        RoundId = roundId,
                        EgmId = egmId
                    };

                    await BroadcastToRoleAsync(ROLE_EGM, JsonSerializer.Serialize(gameUpdate), sender, "GAME_UPDATE");

                    // Reset pending bet after result
                    _pendingBetStakeCents = 0;

                    // Optional: ack back to the SAME roulette socket (helps your roulette logs)
                    var ack = new
                    {
                        @event = "round_result_akc",
                        roundId,
                        egmId,
                        sequence = NextSeq(),
                        sentAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                        nonce = Guid.NewGuid().ToString(),
                        payload = new { accepted = true }
                    };

                    await SendToSocketAsync(sender, JsonSerializer.Serialize(ack), "ROULETTE", "round_result_akc");
                    break;
                }

            default:
                // IMPORTANT: Do NOT forward roulette round_state/bet_commit/etc to EGM.
                // The roulette frontend should consume those directly from roulette backend, not via EGM.
                // We simply ignore/store here.
                break;
        }
    }

    // =========================================================
    // Session + Balance helpers
    // =========================================================
    private static async Task BroadcastSessionInitializedToRouletteAsync(decimal availableCredits)
    {
        string egmId = "EGM-0441";
        long seq = NextSeq();

        var sessionEvent = new
        {
            @event = "session_initialized",
            egmId,
            sequence = seq,
            sentAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            nonce = Guid.NewGuid().ToString(),
            payload = new
            {
                egmId,
                jurisdiction = "NV",
                currency = "ZAR",
                availableCredits = (int)Math.Round(availableCredits),
                aftAccountId = "AFT-12345",
                playerClass = "VIP",
                version = new { egmBackend = "1.0.0", sasDaemon = "2.1.0" }
            }
        };

        LogInfo($"[SESSION] Broadcast session_initialized -> roulette (credits={availableCredits})");
        await BroadcastToRoleAsync(ROLE_ROULETTE, JsonSerializer.Serialize(sessionEvent), excludeSender: null, eventType: "session_initialized");
    }

    private static async Task SendSessionInitializedToSingleRouletteAsync(WebSocket rouletteSocket, decimal availableCredits)
    {
        if (rouletteSocket == null || rouletteSocket.State != WebSocketState.Open) return;

        string egmId = "EGM-0441";
        long seq = NextSeq();

        var sessionEvent = new
        {
            @event = "session_initialized",
            egmId,
            sequence = seq,
            sentAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            nonce = Guid.NewGuid().ToString(),
            payload = new
            {
                egmId,
                jurisdiction = "NV",
                currency = "ZAR",
                availableCredits = (int)Math.Round(availableCredits),
                aftAccountId = "AFT-12345",
                playerClass = "Test",
                version = new { egmBackend = "1.0.0", sasDaemon = "2.1.0" }
            }
        };

        LogInfo($"[SESSION] Send session_initialized -> single roulette socket (credits={availableCredits})");
        await SendToSocketAsync(rouletteSocket, JsonSerializer.Serialize(sessionEvent), "ROULETTE", "session_initialized");
    }

    private static async Task SendBalanceSnapshotToRouletteAsync(WebSocket egmSender, decimal availableCredits, string egmId)
    {
        string roundId = $"round-{DateTime.UtcNow.Ticks}";
        long seq = NextSeq();

        var balanceSnapshot = new
        {
            @event = "balance_snapshot",
            roundId,
            egmId,
            sequence = seq,
            sentAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            nonce = Guid.NewGuid().ToString(),
            payload = new
            {
                availableCredits = (int)Math.Round(availableCredits),
                lockReason = (string?)null
            }
        };

        await BroadcastToRoleAsync(ROLE_ROULETTE, JsonSerializer.Serialize(balanceSnapshot), egmSender, "balance_snapshot");
    }

    // =========================================================
    // Networking helpers
    // =========================================================
    private static async Task BroadcastToRoleAsync(string role, string message, WebSocket? excludeSender, string eventType)
    {
        var bytes = Encoding.UTF8.GetBytes(message);

        List<WebSocket> snapshot;
        lock (_clientsLock)
            snapshot = new List<WebSocket>(_clients);

        int sent = 0, failed = 0;

        foreach (var client in snapshot)
        {
            if (client == null) continue;
            if (excludeSender != null && client == excludeSender) continue;
            if (client.State != WebSocketState.Open) continue;

            if (!_roleBySocket.TryGetValue(client, out var r)) continue;
            if (!r.Equals(role, StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                await client.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
                sent++;
                LogSent(role, eventType, message);
            }
            catch (Exception ex)
            {
                failed++;
                LogInfo($"[SEND FAIL] role={role} event={eventType} err={ex.Message}");
                // Remove dead socket safely
                UnregisterClient(client);
            }
        }

        LogInfo($"[BROADCAST] to={role} event={eventType} sent={sent} failed={failed}");
    }

    private static async Task SendToSocketAsync(WebSocket ws, string message, string who, string eventType)
    {
        if (ws == null || ws.State != WebSocketState.Open) return;

        var bytes = Encoding.UTF8.GetBytes(message);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        LogSent(who, eventType, message);
    }

    // =========================================================
    // JSON helpers
    // =========================================================
    private static string GetEventType(JsonElement root)
    {
        // EGM style
        if (root.TryGetProperty("EventType", out var et) && et.ValueKind == JsonValueKind.String)
            return et.GetString() ?? "";

        // Roulette style
        if (root.TryGetProperty("event", out var ev) && ev.ValueKind == JsonValueKind.String)
            return ev.GetString() ?? "";

        if (root.TryGetProperty("@event", out var ev2) && ev2.ValueKind == JsonValueKind.String)
            return ev2.GetString() ?? "";

        // EGM init style from your app
        if (root.TryGetProperty("eventType", out var et2) && et2.ValueKind == JsonValueKind.String)
            return et2.GetString() ?? "";

        return "";
    }

    private static string GetString(JsonElement root, string prop, string fallback)
    {
        if (root.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String)
            return v.GetString() ?? fallback;
        return fallback;
    }

    private static decimal GetDecimal(JsonElement root, string prop, decimal fallback)
    {
        if (!root.TryGetProperty(prop, out var v)) return fallback;
        return SafeGetDecimal(v, fallback);
    }

    private static decimal SafeGetDecimal(JsonElement v, decimal fallback = 0m)
    {
        try
        {
            return v.ValueKind switch
            {
                JsonValueKind.Number => v.GetDecimal(),
                JsonValueKind.String => decimal.TryParse(v.GetString(), out var d) ? d : fallback,
                _ => fallback
            };
        }
        catch { return fallback; }
    }

    private static long NextSeq() => Interlocked.Increment(ref _sequence);

    // =========================================================
    // Logging helpers
    // =========================================================
    private static void LogInfo(string line)
    {
        var msg = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} - {line}";
        Console.WriteLine(msg);
        try { File.AppendAllText(_logFile, msg + Environment.NewLine); } catch { /* ignore */ }
    }

    private static void LogReceived(string role, string json)
        => LogInfo($"[RECV][{role}] {json}");

    private static void LogSent(string role, string eventType, string json)
        => LogInfo($"[SENT][{role}][{eventType}] {json}");
}