using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.WebSockets;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

class Program
{

    // Track which clients have already received session_initialized (send once per Roulette connection)
    private static readonly ConcurrentDictionary<WebSocket, byte> sessionSentToClients = new();
    private static readonly List<WebSocket> clients = new();
    private static readonly ConcurrentQueue<(string message, WebSocket sender)> messageQueue = new();
    private static readonly ConcurrentDictionary<WebSocket, string> clientTypes = new(); // Track EGM vs Roulette
    private static bool _sessionInitialized = false; // Track if session was initialized

    // --- STORAGE FOR ROULETTE MESSAGES ---
    // Store all Roulette messages (except round_result which triggers GAME_UPDATE to EGM)
    private static readonly ConcurrentQueue<(string eventType, string message, DateTime timestamp)> storedRouletteMessages = new();

    // --- STATE MANAGEMENT ---
    // Stores the bet amount from 'bet_commit' until 'round_result' arrives
    private static int _pendingBetStake = 0;
    private static bool _isRoundActive = true;
    private static long _sequenceCounter = 0; // Sequence counter for events
    private static decimal _currentEgmBalance = 0; // Track current EGM balance
    private static int _coinsIn = 0; // Track coins in meter
    private static int _gamesPlayed = 0; // Track games played meter

    static async Task Main(string[] args)
    {
        Console.WriteLine("Starting Adapter WebSocket Server on ws://0.0.0.0:5000/ws...");
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        Console.WriteLine($"WebSocket log file: {Path.Combine(desktop, "ws", "websocket.txt")}");
        WebSocketFileLogger.LogInfo("=== WebSocket Adapter Server started ===");

        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls("http://localhost:5000");
        builder.Services.AddWebSockets(_ => { });

        var app = builder.Build();
        app.UseWebSockets();

        // Start background task for processing messages
        _ = Task.Run(ProcessMessageQueueAsync);
        String ClientType = "";
        app.Use(async (context, next) =>
        {
            if (context.Request.Path == "/ws/roulette")
            {
                if (context.WebSockets.IsWebSocketRequest)
                {
                    using var ws = await context.WebSockets.AcceptWebSocketAsync();
                    await HandleWebSocketAsync(ws, "roulette"); // ✅ correct
                    return;
                }
                context.Response.StatusCode = 400;
                return;
            }

            if (context.Request.Path == "/ws/egm")
            {
                if (context.WebSockets.IsWebSocketRequest)
                {
                    using var ws = await context.WebSockets.AcceptWebSocketAsync();
                    await HandleWebSocketAsync(ws, "egm"); // ✅ correct
                    return;
                }
                context.Response.StatusCode = 400;
                return;
            }

            await next();
        });

        await app.RunAsync();
    }

    static async Task HandleWebSocketAsync(WebSocket webSocket, string clientRoleFromPath)
    {
        var buffer = new byte[1024 * 4];

        // ✅ Thread-safe add
        lock (clients)
            clients.Add(webSocket);

        // ✅ IMPORTANT: Register role immediately from the URL path (/ws/egm or /ws/roulette)
        // This prevents mis-routing and "sending to both".
        clientTypes[webSocket] = clientRoleFromPath;

        // Connection validation and logging
        int egmCount = clientTypes.Values.Count(v => string.Equals(v, "egm", StringComparison.OrdinalIgnoreCase));
        int rouletteCount = clientTypes.Values.Count(v => string.Equals(v, "roulette", StringComparison.OrdinalIgnoreCase));

        Console.WriteLine($"╔════════════════════════════════════════════════════════════════╗");
        Console.WriteLine($"║  🔌 CLIENT CONNECTED                                          ║");
        Console.WriteLine($"╠════════════════════════════════════════════════════════════════╣");
        Console.WriteLine($"║  Role:           {clientRoleFromPath.PadRight(51)}║");
        Console.WriteLine($"║  Total Clients:  {clients.Count} (Expected: 2){"".PadRight(35)}║");
        Console.WriteLine($"║  EGM Clients:    {egmCount}{"".PadRight(45)}║");
        Console.WriteLine($"║  Roulette Clients:{rouletteCount}{"".PadRight(44)}║");

        if (clients.Count > 2)
            Console.WriteLine($"║  ⚠ WARNING: More than 2 clients connected!{"".PadRight(20)}║");

        Console.WriteLine($"╚════════════════════════════════════════════════════════════════╝");

        WebSocketFileLogger.LogInfo($"CLIENT CONNECTED | Role: {clientRoleFromPath} | Total: {clients.Count} | EGM: {egmCount} | Roulette: {rouletteCount}");

        try
        {
            while (webSocket.State == WebSocketState.Open)
            {
                var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var message = Encoding.UTF8.GetString(buffer, 0, result.Count);

                    // ✅ Send session_initialized to Roulette client once (if EGM already initialized session)
                    if (string.Equals(clientRoleFromPath, "roulette", StringComparison.OrdinalIgnoreCase) &&
                        _sessionInitialized &&
                        sessionSentToClients.TryAdd(webSocket, 0))
                    {
                        await SendSessionInitializedToRouletteClientAsync(webSocket);
                    }

                    Console.WriteLine($"[RECEIVED] From {clientRoleFromPath}: {message}");
                    WebSocketFileLogger.LogReceived(clientRoleFromPath, message);

                    // Queue processing
                    messageQueue.Enqueue((message, webSocket));
                }
                else if (result.MessageType == WebSocketMessageType.Close)
                {
                    Console.WriteLine("[DISCONNECTION] Client disconnected.");
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] WebSocket error: {ex.Message}");
        }
        finally
        {
            // Read role before we remove it
            string disconnectedType = clientTypes.TryGetValue(webSocket, out var dt) ? dt : "UNKNOWN";

            // ✅ Thread-safe remove
            lock (clients)
                clients.Remove(webSocket);

            clientTypes.TryRemove(webSocket, out _);
            sessionSentToClients.TryRemove(webSocket, out _);

            // ✅ If EGM disconnects, allow session re-init when it reconnects
            if (string.Equals(disconnectedType, "egm", StringComparison.OrdinalIgnoreCase))
                _sessionInitialized = false;

            egmCount = clientTypes.Values.Count(v => string.Equals(v, "egm", StringComparison.OrdinalIgnoreCase));
            rouletteCount = clientTypes.Values.Count(v => string.Equals(v, "roulette", StringComparison.OrdinalIgnoreCase));

            Console.WriteLine($"╔════════════════════════════════════════════════════════════════╗");
            Console.WriteLine($"║  🔌 CLIENT DISCONNECTED                                       ║");
            Console.WriteLine($"╠════════════════════════════════════════════════════════════════╣");
            Console.WriteLine($"║  Disconnected Type: {disconnectedType.PadRight(40)}║");
            Console.WriteLine($"║  Remaining Clients: {clients.Count}{"".PadRight(42)}║");
            Console.WriteLine($"║  EGM Clients:       {egmCount}{"".PadRight(45)}║");
            Console.WriteLine($"║  Roulette Clients:  {rouletteCount}{"".PadRight(45)}║");
            Console.WriteLine($"╚════════════════════════════════════════════════════════════════╝");

            WebSocketFileLogger.LogInfo($"CLIENT DISCONNECTED | Type: {disconnectedType} | Remaining: {clients.Count}");

            if (webSocket.State != WebSocketState.Closed)
            {
                try
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                }
                catch
                {
                    // ignore close failures
                }
            }
        }
    }

    static async Task ProcessMessageQueueAsync()
    {
        // Optional: define these once (static readonly) outside the loop if you prefer.
        // Keeping here for copy/paste clarity.
        var rouletteDefinitiveEvents = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "bet_commit", "round_result", "round_state", "cash_event", "aft_transfer",
        "AFT_CONFIRMED", "cashout", "round_summary", "refund", "round_void", "error", "ui_pong"
    };

        var egmDefinitiveEvents = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "BILL_INSERTED", "AFT_DEPOSIT", "AFT_CASHOUT", "SPIN_COMPLETED", "ui_ping", "UI_PING"
    };

        while (true)
        {
            if (messageQueue.TryDequeue(out var item))
            {
                var (message, sender) = item;
                try
                {
                    using var doc = JsonDocument.Parse(message);
                    var root = doc.RootElement;

                    // We check for "EventType" (PascalCase), "eventType" (camelCase), and "event" (lowercase)
                    string eventType = "";
                    if (root.TryGetProperty("EventType", out var et)) eventType = et.GetString();
                    else if (root.TryGetProperty("eventType", out var et2)) eventType = et2.GetString();
                    else if (root.TryGetProperty("event", out var ev)) eventType = ev.GetString();

                    // ============================================================
                    // ✅ GUARD: Once identified at connect-time, DON'T re-identify
                    // ============================================================
                    bool alreadyKnown = clientTypes.TryGetValue(sender, out var knownType) &&
                                        !string.IsNullOrWhiteSpace(knownType);

                    // Optional: if connect-time type is wrong, correct ONLY on definitive evidence
                    if (alreadyKnown)
                    {
                        // If socket is labeled "egm" but sends roulette-definitive events -> correct it
                        if (string.Equals(knownType, "egm", StringComparison.OrdinalIgnoreCase) &&
                            rouletteDefinitiveEvents.Contains(eventType))
                        {
                            Console.WriteLine($"[ROLE MISMATCH] Socket marked as EGM but sent Roulette event '{eventType}'. Correcting to 'roulette'.");
                            clientTypes[sender] = "roulette";
                            knownType = "roulette";
                        }
                        // If socket is labeled "roulette" but sends egm-definitive events -> correct it
                        else if (string.Equals(knownType, "roulette", StringComparison.OrdinalIgnoreCase) &&
                                 egmDefinitiveEvents.Contains(eventType))
                        {
                            Console.WriteLine($"[ROLE MISMATCH] Socket marked as Roulette but sent EGM event '{eventType}'. Correcting to 'egm'.");
                            clientTypes[sender] = "egm";
                            knownType = "egm";
                        }

                        // IMPORTANT: skip the old "first message identification" logic completely
                    }
                    else
                    {
                        // ============================================================
                        // Legacy identification for older connections (if any),
                        // but only when we truly don't know yet.
                        // ============================================================
                        if (rouletteDefinitiveEvents.Contains(eventType))
                        {
                            clientTypes[sender] = "roulette";
                            Console.WriteLine($"╔════════════════════════════════════════════════════════════════╗");
                            Console.WriteLine($"║  ✓ CLIENT IDENTIFIED: ROULETTE                                ║");
                            Console.WriteLine($"╠════════════════════════════════════════════════════════════════╣");
                            Console.WriteLine($"║  First Event: {eventType.PadRight(46)}║");
                            Console.WriteLine($"╚════════════════════════════════════════════════════════════════╝");
                        }
                        else if (egmDefinitiveEvents.Contains(eventType))
                        {
                            clientTypes[sender] = "egm";
                            Console.WriteLine($"╔════════════════════════════════════════════════════════════════╗");
                            Console.WriteLine($"║  ✓ CLIENT IDENTIFIED: EGM                                     ║");
                            Console.WriteLine($"╠════════════════════════════════════════════════════════════════╣");
                            Console.WriteLine($"║  First Event: {eventType.PadRight(46)}║");
                            Console.WriteLine($"╚════════════════════════════════════════════════════════════════╝");

                            // Extract credits only if present
                            decimal availableCredits = 0;
                            bool messageHasCredits = false;

                            if (root.TryGetProperty("CurrentCredits", out var credits))
                            { availableCredits = credits.GetDecimal(); messageHasCredits = true; }
                            else if (root.TryGetProperty("currentCredits", out var creditsCamel))
                            { availableCredits = creditsCamel.GetDecimal(); messageHasCredits = true; }
                            else if (root.TryGetProperty("payload", out var payload2) &&
                                     payload2.TryGetProperty("availableCredits", out var availCredits))
                            { availableCredits = availCredits.GetDecimal(); messageHasCredits = true; }

                            if (messageHasCredits)
                                _currentEgmBalance = availableCredits;
                        }
                        else if (eventType == "session_initialized" &&
                                 root.TryGetProperty("client", out var clientField) &&
                                 clientField.GetString() == "EGM_Application")
                        {
                            clientTypes[sender] = "egm";
                            Console.WriteLine($"[IDENTIFY] session_initialized from EGM_Application -> setting sender as EGM");
                        }
                    }

                    // ============================================================
                    // session_initialized handling (only from EGM socket)
                    // ============================================================
                    bool senderIsEgm = clientTypes.TryGetValue(sender, out var senderRole) &&
                                       string.Equals(senderRole, "egm", StringComparison.OrdinalIgnoreCase);

                    if (eventType == "session_initialized" &&
                        root.TryGetProperty("client", out var clientCheck) &&
                        clientCheck.GetString() == "EGM_Application" &&
                        senderIsEgm)
                    {
                        decimal availableCredits = 0;
                        if (root.TryGetProperty("payload", out var payloadEgm) &&
                            payloadEgm.TryGetProperty("availableCredits", out var ac))
                        {
                            availableCredits = ac.GetDecimal();
                            _currentEgmBalance = availableCredits;
                        }

                        await SendSessionInitializedAsync(sender, availableCredits);
                    }

                    // =======================================================================
                    //  1. OUTBOUND: EGM -> ROULETTE (Translation Layer)
                    // =======================================================================

                    if (eventType == "BILL_INSERTED")
                    {
                        // Extract Data from EGM Message
                        decimal amount = 0;
                        decimal currentCredits = 0;
                        string egmId = "EGM-0441"; // Default, can be extracted if available

                        if (root.TryGetProperty("amount", out var amt)) amount = amt.GetDecimal();
                        if (root.TryGetProperty("CurrentCredits", out var cc)) currentCredits = cc.GetDecimal();
                        if (root.TryGetProperty("egmId", out var egmIdProp)) egmId = egmIdProp.GetString();

                        Console.WriteLine($"[TRANSLATE] EGM -> Roulette: BILL_INSERTED received");
                        Console.WriteLine($"[TRANSLATE]   Amount: {amount} ZAR, Current Credits: {currentCredits}");

                        // Generate required fields
                        string roundId = $"round-{DateTime.UtcNow.Ticks}";
                        long sequence = Interlocked.Increment(ref _sequenceCounter);
                        string bvSerial = "BV-INTERNAL"; // Default, can be extracted if available
                        int denomination = (int)(amount * 100); // Convert to cents

                        // Update tracked EGM balance and meters
                        _currentEgmBalance = currentCredits;
                        _coinsIn += denomination; // Add to coins in meter
                        int bvStackerCount = 1; // Default, can be tracked if needed
                        int sequenceNumber = (int)(DateTime.UtcNow.Ticks % 1000000); // Bill validator sequence
                        int cashin = denomination; // Total cash in (for this transaction, could track cumulative)

                        // Create Roulette Spec "cash_event" matching expected format
                        var cashEvent = new
                        {
                            @event = "cash_event",
                            roundId = roundId,
                            egmId = egmId,
                            sequence = sequence,
                            sentAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                            nonce = Guid.NewGuid().ToString(),
                            payload = new
                            {
                                type = "bv_stack",
                                bvSerial = bvSerial,
                                denomination = denomination,
                                currency = "ZAR",
                                bvStackerCount = bvStackerCount,
                                sequenceNumber = sequenceNumber,
                                meters = new
                                {
                                    cashin = cashin,
                                    credits =0
                                }
                            }
                        };

                        string json = JsonSerializer.Serialize(cashEvent);
                        Console.WriteLine($"[TRANSLATE]   Converting to: cash_event");
                        Console.WriteLine($"[TRANSLATE]   RoundId: {roundId}, Denomination: {denomination} cents, Credits: {currentCredits}");
                        // Send to Roulette client only
                        await BroadcastToRouletteClientsAsync(json, sender, "cash_event");

                        // Send balance_snapshot after credit update
                        //await SendBalanceSnapshotAsync(sender, currentCredits, egmId);
                    }
                    else if (eventType == "AFT_DEPOSIT")
                    {
                        // Extract Data from EGM Message
                        decimal amount = 0;
                        decimal currentCredits = 0;
                        string aftReference = "";
                        string egmId = "EGM-0441"; // Default, can be extracted if available

                        if (root.TryGetProperty("Amount", out var amt)) amount = amt.GetDecimal();
                        if (root.TryGetProperty("CurrentCredits", out var cc)) currentCredits = cc.GetDecimal();
                        if (root.TryGetProperty("AFTReference", out var refProp)) aftReference = refProp.GetString();
                        if (root.TryGetProperty("egmId", out var egmIdProp)) egmId = egmIdProp.GetString();

                        // Update tracked EGM balance
                        _currentEgmBalance = currentCredits;

                        // Generate IDs if not provided
                        string aftTxnId = !string.IsNullOrEmpty(aftReference) ? aftReference : $"AFT-TXN-{DateTime.UtcNow.Ticks}";
                        string authCode = !string.IsNullOrEmpty(aftReference) ? $"AUTH-{aftReference.Substring(Math.Max(0, aftReference.Length - 8))}" : $"AUTH-{Guid.NewGuid().ToString().Substring(0, 8).ToUpper()}";
                        string roundId = $"round-{DateTime.UtcNow.Ticks}";
                        long sequence = Interlocked.Increment(ref _sequenceCounter); // Increment sequence counter

                        Console.WriteLine($"[TRANSLATE] EGM -> Roulette: AFT_DEPOSIT received");
                        Console.WriteLine($"[TRANSLATE]   Amount: {amount}, Current Credits: {currentCredits}, Reference: {aftReference}");

                        // Create Roulette Spec "aft_transfer" matching expected format
                        var aftEvent = new
                        {
                            @event = "aft_transfer",
                            roundId = roundId,
                            egmId = egmId,
                            sequence = sequence,
                            sentAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                            nonce = Guid.NewGuid().ToString(),
                            payload = new
                            {
                                aftTxnId = aftTxnId,
                                originHost = "EGM-LOCAL",
                                amount = (int)amount, // Convert to int as expected
                                authCode = authCode,
                                remainingBalance = (int)currentCredits // Convert to int as expected
                            }
                        };

                        string json = JsonSerializer.Serialize(aftEvent);
                        Console.WriteLine($"[TRANSLATE]   Converting to: aft_transfer");
                        Console.WriteLine($"[TRANSLATE]   RoundId: {roundId}, AFT TxnId: {aftTxnId}, AuthCode: {authCode}");
                        // Send to Roulette client only
                        await BroadcastToRouletteClientsAsync(json, sender, "aft_transfer");

                        // Send balance_snapshot after credit update
                        await SendBalanceSnapshotAsync(sender, currentCredits, egmId);
                    }
                    else if (eventType == "AFT_CASHOUT")
                    {
                        // Extract data from EGM Message
                        decimal currentCredits = 0;
                        decimal amount = 0;
                        string egmId = "EGM-0441";
                        int bet = 0;
                        int win = 0;

                        if (root.TryGetProperty("CurrentCredits", out var cc)) currentCredits = cc.GetDecimal();
                        if (root.TryGetProperty("Amount", out var amt)) amount = amt.GetDecimal();
                        if (root.TryGetProperty("egmId", out var egmIdProp)) egmId = egmIdProp.GetString();

                        // Try to extract bet and win if available in the message
                        if (root.TryGetProperty("BetAmount", out var betProp)) bet = betProp.GetInt32();
                        if (root.TryGetProperty("WinAmount", out var winProp)) win = winProp.GetInt32();

                        // Calculate balance before and after cashout
                        decimal egmBalanceBefore = _currentEgmBalance;
                        decimal egmBalanceAfter = 0; // After cashout, balance should be 0

                        // Generate roundId
                        string roundId = $"round-{DateTime.UtcNow.Ticks}";
                        long sequence = Interlocked.Increment(ref _sequenceCounter);

                        // Only store current credits from EGM (they send 0 on cashout)
                        _currentEgmBalance = currentCredits;

                        Console.WriteLine($"[TRANSLATE] EGM -> Roulette: AFT_CASHOUT received");
                        Console.WriteLine($"[TRANSLATE]   Amount: {amount}, Current Credits: {currentCredits}, Balance Before: {egmBalanceBefore}");

                        // Create cashout_ack event matching expected format
                        var cashoutAck = new
                        {
                            @event = "cashout_ack",
                            roundId = roundId,
                            egmId = egmId,
                            sequence = sequence,
                            sentAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                            nonce = Guid.NewGuid().ToString(),
                            payload = new
                            {
                                roundId = roundId,
                                accepted = true,
                                reason = (string)null, // null if accepted
                                bet = bet,
                                win = win,
                                egmBalanceBefore = (int)egmBalanceBefore,
                                egmBalanceAfter = (int)egmBalanceAfter
                            }
                        };

                        string json = JsonSerializer.Serialize(cashoutAck);
                        Console.WriteLine($"[TRANSLATE]   Converting to: cashout_ack");
                        Console.WriteLine($"[TRANSLATE]   RoundId: {roundId}, Bet: {bet}, Win: {win}, Balance Before: {egmBalanceBefore}, Balance After: {egmBalanceAfter}");
                        // Send to Roulette client only
                        await BroadcastToRouletteClientsAsync(json, sender, "cashout_ack");

                        // Send balance_snapshot after cashout (balance is now 0)
                        await SendBalanceSnapshotAsync(sender, 0, egmId);
                    }
                    else if (eventType == "ui_ping" || eventType == "UI_PING")
                    {
                        // Extract EGM ID if available
                        string egmId = "EGM-0441";
                        if (root.TryGetProperty("egmId", out var egmIdProp)) egmId = egmIdProp.GetString();

                        Console.WriteLine($"[PING] Received ui_ping from EGM");

                        // Translate to Roulette format
                        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        long sequence = Interlocked.Increment(ref _sequenceCounter);

                        // Extract timestamp from payload if available, otherwise use current time
                        long payloadTimestamp = timestamp;
                        if (root.TryGetProperty("payload", out var pingPayload))
                        {
                            if (pingPayload.TryGetProperty("timestamp", out var ts))
                            {
                                if (ts.ValueKind == JsonValueKind.Number)
                                    payloadTimestamp = ts.GetInt64();
                            }
                        }

                        var uiPong = new
                        {
                            @event = "ui_pong",
                            egmId = egmId,
                            sequence = sequence,
                            sentAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                            payload = new
                            {
                                timestamp = payloadTimestamp
                            },
                            nonce = Guid.NewGuid().ToString()
                        };

                        string json = JsonSerializer.Serialize(uiPong);
                        Console.WriteLine($"[PONG] Translated ui_pong for Roulette (Timestamp: {payloadTimestamp})");

                        // Send to all Roulette clients (exclude EGM sender)
                        await BroadcastToRouletteClientsAsync(json, sender, "ui_pong");
                    }

                    // =======================================================================
                    //  2. INBOUND: ROULETTE -> EGM (Game Logic Adapter)
                    // =======================================================================
                    // IMPORTANT: All Roulette messages are STORED in the WebSocket server.
                    // ONLY round_result triggers GAME_UPDATE to be sent to EGM.
                    // All other Roulette events (bet_commit, round_state, AFT_CONFIRMED, etc.)
                    // are stored but NOT forwarded to EGM.
                    // =======================================================================

                    else if (eventType == "bet_commit")
                    {
                        // Extract bet_commit data
                        string roundId = "";
                        string egmId = "EGM-0441";
                        int totalStake = 0;

                        if (root.TryGetProperty("roundId", out var rid)) roundId = rid.GetString();
                        if (root.TryGetProperty("egmId", out var eid)) egmId = eid.GetString();

                        if (root.TryGetProperty("payload", out var payload))
                        {
                            // Try different possible field names for stake
                            if (payload.TryGetProperty("totalStake", out var stake))
                                totalStake = stake.GetInt32();
                            else if (payload.TryGetProperty("localStake", out var localStake))
                                totalStake = localStake.GetInt32();

                            // Extract roundId from payload if not in root
                            if (string.IsNullOrEmpty(roundId) && payload.TryGetProperty("roundId", out var payloadRoundId))
                                roundId = payloadRoundId.GetString();

                            // Extract egmId from payload if not in root
                            if (egmId == "EGM-0441" && payload.TryGetProperty("egmId", out var payloadEgmId))
                                egmId = payloadEgmId.GetString();
                        }

                        Console.WriteLine($"[STORE] Roulette -> WebSocket: bet_commit received");
                        Console.WriteLine($"[STORE]   RoundId: {roundId}, EGM ID: {egmId}, Total Stake: {totalStake}");

                        // Store bet_commit message (do not forward to EGM)
                        storedRouletteMessages.Enqueue(("bet_commit", message, DateTime.UtcNow));
                        Console.WriteLine($"[STORE]   Stored bet_commit message (Total stored: {storedRouletteMessages.Count})");

                        if (totalStake > 0)
                        {
                            _pendingBetStake = totalStake;
                            _isRoundActive = true;
                            Console.WriteLine($"[ADAPTER] Bet Committed: {totalStake}. Waiting for result...");

                            // Generate roundId if not provided
                            if (string.IsNullOrEmpty(roundId))
                                roundId = $"round-{DateTime.UtcNow.Ticks}";

                            // Send bet_commit_ack to Roulette
                            // await SendBetCommitAckAsync(sender, roundId, egmId, totalStake);
                        }
                        else
                        {
                            Console.WriteLine($"[WARNING] bet_commit received but totalStake not found or invalid");
                        }
                        var NO_MORE_BETS_Events = new
                        {
                            EventType = "NO_MORE_BETS",
                            Timestamp = DateTime.UtcNow,
                            RoundId = roundId,
                            EgmId = egmId,
                            BetValue = totalStake // Include bet amount for better correlation in logs and potential future use in EGM
                        };
                        string eventJson = JsonSerializer.Serialize(NO_MORE_BETS_Events);
                        await BroadcastToEGMClientsAsync(eventJson, sender, "NO_MORE_BETS");
                    }
                    else if (eventType == "round_result")
                    {
                        Console.WriteLine($"[STORE] Roulette -> WebSocket: round_result received");

                        // Extract all round_result data
                        string roundId = "";
                        string egmId = "EGM-0441";
                        string layout = "";
                        int winningNumber = 0;
                        string sector = "";
                        string ballLandingTime = "";
                        int winAmount = 0;
                        int netWin = 0;
                        int balanceAfterWin = 0;
                        string rngAlgorithm = "";
                        string rngSeedHash = "";
                        string rngNonce = "";
                        int rngCallCount = 0;
                        string rngSignature = "";
                        string payoutBreakdownJson = "";

                        // Extract from root level
                        if (root.TryGetProperty("roundId", out var rid)) roundId = rid.GetString();
                        if (root.TryGetProperty("egmId", out var eid)) egmId = eid.GetString();

                        // Extract from payload
                        if (root.TryGetProperty("payload", out var payload))
                        {
                            // Extract roundId from payload if not in root
                            if (string.IsNullOrEmpty(roundId) && payload.TryGetProperty("roundId", out var payloadRoundId))
                                roundId = payloadRoundId.GetString();

                            // Extract egmId from payload if not in root
                            if (egmId == "EGM-0441" && payload.TryGetProperty("egmId", out var payloadEgmId))
                                egmId = payloadEgmId.GetString();

                            // Extract game result fields
                            if (payload.TryGetProperty("layout", out var layoutProp)) layout = layoutProp.GetString();
                            if (payload.TryGetProperty("winningNumber", out var wn)) winningNumber = wn.GetInt32();
                            if (payload.TryGetProperty("sector", out var sec)) sector = sec.GetString();
                            if (payload.TryGetProperty("ballLandingTime", out var blt)) ballLandingTime = blt.GetString();

                            // Extract win amounts
                            if (payload.TryGetProperty("winAmount", out var win)) winAmount = win.GetInt32();
                            if (payload.TryGetProperty("netWin", out var nw)) netWin = nw.GetInt32();
                            if (payload.TryGetProperty("balanceAfterWin", out var baw)) balanceAfterWin = baw.GetInt32();

                            // Extract RNG proof
                            if (payload.TryGetProperty("rngProof", out var rngProof))
                            {
                                if (rngProof.TryGetProperty("algorithm", out var alg)) rngAlgorithm = alg.GetString();
                                if (rngProof.TryGetProperty("seedHash", out var sh)) rngSeedHash = sh.GetString();
                                if (rngProof.TryGetProperty("nonce", out var rn)) rngNonce = rn.GetString();
                                if (rngProof.TryGetProperty("callCount", out var cc)) rngCallCount = cc.GetInt32();
                                if (rngProof.TryGetProperty("signature", out var sig)) rngSignature = sig.GetString();
                            }

                            // Extract payout breakdown
                            if (payload.TryGetProperty("payoutBreakdown", out var pb))
                            {
                                payoutBreakdownJson = pb.ToString();
                            }
                        }

                        // Log all extracted fields
                        Console.WriteLine($"[STORE]   RoundId: {roundId}, EGM ID: {egmId}");
                        Console.WriteLine($"[STORE]   Layout: {layout}, Winning Number: {winningNumber}, Sector: {sector}");
                        Console.WriteLine($"[STORE]   Ball Landing Time: {ballLandingTime}");
                        Console.WriteLine($"[STORE]   Win Amount: {winAmount}, Net Win: {netWin}, Balance After Win: {balanceAfterWin}");
                        if (!string.IsNullOrEmpty(rngAlgorithm))
                        {
                            string seedHashPreview = !string.IsNullOrEmpty(rngSeedHash) && rngSeedHash.Length > 20
                                ? rngSeedHash.Substring(0, 20) + "..."
                                : rngSeedHash ?? "";
                            Console.WriteLine($"[STORE]   RNG Proof: Algorithm={rngAlgorithm}, SeedHash={seedHashPreview}, Nonce={rngNonce}, CallCount={rngCallCount}");
                        }
                        if (!string.IsNullOrEmpty(payoutBreakdownJson))
                        {
                            Console.WriteLine($"[STORE]   Payout Breakdown: {payoutBreakdownJson}");
                        }

                        // Store round_result message
                        storedRouletteMessages.Enqueue(("round_result", message, DateTime.UtcNow));
                        Console.WriteLine($"[STORE]   Stored round_result message (Total stored: {storedRouletteMessages.Count})");

                        //if (_isRoundActive)
                        {
                            // COMBINE Saved Bet + New Win -> Standard EGM Format
                            // ONLY round_result triggers GAME_UPDATE to EGM
                            var gameUpdate = new GameResponse
                            {
                                EventType = "GAME_UPDATE",
                                BetAmount = _pendingBetStake,
                                WinAmount = winAmount,
                                Timestamp = DateTime.UtcNow
                            };

                            string json = JsonSerializer.Serialize(gameUpdate);

                            // Calculate EGM values (divide by 100 to convert from cents/smallest unit to actual credits)
                            int betAmountForEGM = _pendingBetStake / 100;
                            int winAmountForEGM = winAmount / 100;

                            // Enhanced logging for GAME_UPDATE
                            Console.WriteLine($"");
                            Console.WriteLine($"╔════════════════════════════════════════════════════════════════╗");
                            Console.WriteLine($"║  🎮 GAME_UPDATE SENT TO EGM                                    ║");
                            Console.WriteLine($"╠════════════════════════════════════════════════════════════════╣");
                            Console.WriteLine($"║  Event Type:    GAME_UPDATE{"".PadRight(40)}║");
                            Console.WriteLine($"║  Round ID:      {roundId.PadRight(47)}║");
                            Console.WriteLine($"║  EGM ID:        {egmId.PadRight(47)}║");
                            Console.WriteLine($"║  ──────────────────────────────────────────────────────────── ║");
                            Console.WriteLine($"║  Bet Amount:    {betAmountForEGM:N0} credits (raw: {_pendingBetStake:N0}){"".PadRight(20)}║");
                            Console.WriteLine($"║  Win Amount:    {winAmountForEGM:N0} credits (raw: {winAmount:N0}){"".PadRight(21)}║");
                            Console.WriteLine($"║  Net Result:    {(winAmountForEGM - betAmountForEGM):N0} credits{"".PadRight(34)}║");
                            Console.WriteLine($"║  ──────────────────────────────────────────────────────────── ║");
                            Console.WriteLine($"║  Game Result:   Number {winningNumber} ({sector}){"".PadRight(30)}║");
                            Console.WriteLine($"║  Layout:        {layout.PadRight(47)}║");
                            Console.WriteLine($"║  Timestamp:     {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff} UTC{"".PadRight(20)}║");
                            Console.WriteLine($"╚════════════════════════════════════════════════════════════════╝");
                            Console.WriteLine($"");

                            // Send GAME_UPDATE to EGM client only (this is the ONLY event sent to EGM from Roulette)
                            // Use PascalCase format (EventType, BetAmount, WinAmount) to match EGM expectations

                            var gameUpdateEvent = new
                            {
                                EventType = "GAME_UPDATE",
                                BetAmount = betAmountForEGM,
                                WinAmount = winAmountForEGM,
                                Timestamp = DateTime.UtcNow,
                                RoundId = roundId,
                                EgmId = egmId
                            };
                            string eventJson = JsonSerializer.Serialize(gameUpdateEvent);
                            await BroadcastToEGMClientsAsync(eventJson, sender, "GAME_UPDATE");

                            // Do not update _currentEgmBalance here — only EGM provides current credits (e.g. SPIN_COMPLETED)
                            _gamesPlayed++; // Increment games played meter

                            // Reset State
                            _pendingBetStake = 0;
                            _isRoundActive = false;
                        }
                        //else
                        {
                            //Console.WriteLine($"[WARNING] round_result received but no active round (_isRoundActive = false)");
                        }


                        long sequence = Interlocked.Increment(ref _sequenceCounter);
                        // Create cashout_ack event matching expected format
                        var roundresultsAck = new
                        {
                            @event = "round_result_akn",
                            roundId = roundId,
                            egmId = egmId,
                            sequence = sequence,
                            sentAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                            nonce = Guid.NewGuid().ToString(),
                            payload = new
                            {
                                roundId = roundId,
                                accepted = true                               
                            }
                        };

                        string json2 = JsonSerializer.Serialize(roundresultsAck);
                        Console.WriteLine($"[TRANSLATE]   Converting to: round_result_akn");

                        // Send to Roulette client only
                        // ✅ Send ack back to the SAME roulette socket that sent round_result
                        await SendToSocketAsync(sender, json2, "ROULETTE", "round_result_akn");

                    }
                    else if (eventType == "round_summary")
                    {
                        // Extract round_summary data
                        string roundId = "";
                        string egmId = "EGM-0441";
                        string tableId = "";
                        int totalPlayers = 0;
                        int totalWagered = 0;
                        int totalPaidOut = 0;
                        double houseEdge = 0.0;
                        int duration = 0;
                        string timestamp = "";

                        // Extract from root level
                        if (root.TryGetProperty("roundId", out var rid)) roundId = rid.GetString();
                        if (root.TryGetProperty("egmId", out var eid)) egmId = eid.GetString();

                        // Extract from payload
                        if (root.TryGetProperty("payload", out var payload))
                        {
                            // Extract roundId from payload if not in root
                            if (string.IsNullOrEmpty(roundId) && payload.TryGetProperty("roundId", out var payloadRoundId))
                                roundId = payloadRoundId.GetString();

                            // Extract egmId from payload if not in root
                            if (egmId == "EGM-0441" && payload.TryGetProperty("egmId", out var payloadEgmId))
                                egmId = payloadEgmId.GetString();

                            // Extract summary statistics
                            if (payload.TryGetProperty("tableId", out var tid)) tableId = tid.GetString();
                            if (payload.TryGetProperty("totalPlayers", out var tp)) totalPlayers = tp.GetInt32();
                            if (payload.TryGetProperty("totalWagered", out var tw)) totalWagered = tw.GetInt32();
                            if (payload.TryGetProperty("totalPaidOut", out var tpo)) totalPaidOut = tpo.GetInt32();
                            if (payload.TryGetProperty("houseEdge", out var he)) houseEdge = he.GetDouble();
                            if (payload.TryGetProperty("duration", out var dur)) duration = dur.GetInt32();
                            if (payload.TryGetProperty("timestamp", out var ts)) timestamp = ts.GetString();
                        }

                        Console.WriteLine($"[STORE] Roulette -> WebSocket: round_summary received");
                        Console.WriteLine($"[STORE]   RoundId: {roundId}, EGM ID: {egmId}, Table ID: {tableId}");
                        Console.WriteLine($"[STORE]   Total Players: {totalPlayers}, Duration: {duration}ms");
                        Console.WriteLine($"[STORE]   Total Wagered: {totalWagered:N0} credits, Total Paid Out: {totalPaidOut:N0} credits");
                        Console.WriteLine($"[STORE]   House Edge: {houseEdge:F2}%, Timestamp: {timestamp}");

                        // Store round_summary message (do not forward to EGM)
                        storedRouletteMessages.Enqueue(("round_summary", message, DateTime.UtcNow));
                        Console.WriteLine($"[STORE]   Stored round_summary message (Total stored: {storedRouletteMessages.Count})");


                    }
                    else if (eventType == "refund")
                    {
                        // Extract refund data
                        string roundId = "";
                        string egmId = "EGM-0441";
                        int refundAmount = 0;
                        string reason = "";
                        int originalStake = 0;

                        // Extract from root level
                        if (root.TryGetProperty("roundId", out var rid)) roundId = rid.GetString();
                        if (root.TryGetProperty("egmId", out var eid)) egmId = eid.GetString();

                        // Extract from payload
                        if (root.TryGetProperty("payload", out var payload))
                        {
                            // Extract roundId from payload if not in root
                            if (string.IsNullOrEmpty(roundId) && payload.TryGetProperty("roundId", out var payloadRoundId))
                                roundId = payloadRoundId.GetString();

                            // Extract egmId from payload if not in root
                            if (egmId == "EGM-0441" && payload.TryGetProperty("egmId", out var payloadEgmId))
                                egmId = payloadEgmId.GetString();

                            // Extract refund details
                            if (payload.TryGetProperty("refundAmount", out var ra)) refundAmount = ra.GetInt32();
                            if (payload.TryGetProperty("reason", out var rsn)) reason = rsn.GetString();
                            if (payload.TryGetProperty("originalStake", out var os)) originalStake = os.GetInt32();
                        }

                        Console.WriteLine($"[STORE] Roulette -> WebSocket: refund received");
                        Console.WriteLine($"[STORE]   RoundId: {roundId}, EGM ID: {egmId}");
                        Console.WriteLine($"[STORE]   Refund Amount: {refundAmount:N0} credits, Original Stake: {originalStake:N0} credits");
                        Console.WriteLine($"[STORE]   Reason: {reason}");

                        // Store refund message (do not forward to EGM)
                        storedRouletteMessages.Enqueue(("refund", message, DateTime.UtcNow));
                        Console.WriteLine($"[STORE]   Stored refund message (Total stored: {storedRouletteMessages.Count})");
                    }
                    else if (eventType == "round_void")
                    {
                        // Extract round_void data
                        string roundId = "";
                        string egmId = "EGM-0441";
                        string reason = "";
                        string affectedEgmsJson = "";

                        // Extract from root level
                        if (root.TryGetProperty("roundId", out var rid)) roundId = rid.GetString();
                        if (root.TryGetProperty("egmId", out var eid)) egmId = eid.GetString();

                        // Extract from payload
                        if (root.TryGetProperty("payload", out var payload))
                        {
                            // Extract roundId from payload if not in root
                            if (string.IsNullOrEmpty(roundId) && payload.TryGetProperty("roundId", out var payloadRoundId))
                                roundId = payloadRoundId.GetString();

                            // Extract egmId from payload if not in root
                            if (egmId == "EGM-0441" && payload.TryGetProperty("egmId", out var payloadEgmId))
                                egmId = payloadEgmId.GetString();

                            // Extract void details
                            if (payload.TryGetProperty("reason", out var rsn)) reason = rsn.GetString();

                            // Extract affected EGMs array
                            if (payload.TryGetProperty("affectedEgms", out var aegms))
                            {
                                affectedEgmsJson = aegms.ToString();
                            }
                        }

                        Console.WriteLine($"[STORE] Roulette -> WebSocket: round_void received");
                        Console.WriteLine($"[STORE]   RoundId: {roundId}, EGM ID: {egmId}");
                        Console.WriteLine($"[STORE]   Reason: {reason}");
                        Console.WriteLine($"[STORE]   Affected EGMs: {affectedEgmsJson}");

                        // Store round_void message (do not forward to EGM)
                        storedRouletteMessages.Enqueue(("round_void", message, DateTime.UtcNow));
                        Console.WriteLine($"[STORE]   Stored round_void message (Total stored: {storedRouletteMessages.Count})");
                    }
                    else if (eventType == "error")
                    {
                        // Extract error data
                        string roundId = "";
                        string egmId = "EGM-0441";
                        string code = "";
                        string errorMessage = "";
                        string severity = "";
                        string action = "";
                        int required = 0;
                        int available = 0;

                        // Extract from root level
                        if (root.TryGetProperty("roundId", out var rid)) roundId = rid.GetString();
                        if (root.TryGetProperty("egmId", out var eid)) egmId = eid.GetString();

                        // Extract from payload
                        if (root.TryGetProperty("payload", out var payload))
                        {
                            // Extract roundId from payload if not in root
                            if (string.IsNullOrEmpty(roundId) && payload.TryGetProperty("roundId", out var payloadRoundId))
                                roundId = payloadRoundId.GetString();

                            // Extract egmId from payload if not in root
                            if (egmId == "EGM-0441" && payload.TryGetProperty("egmId", out var payloadEgmId))
                                egmId = payloadEgmId.GetString();

                            // Extract error details
                            if (payload.TryGetProperty("code", out var codeProp)) code = codeProp.GetString();
                            if (payload.TryGetProperty("message", out var msgProp)) errorMessage = msgProp.GetString();
                            if (payload.TryGetProperty("severity", out var sevProp)) severity = sevProp.GetString();
                            if (payload.TryGetProperty("action", out var actProp)) action = actProp.GetString();

                            // Extract details object
                            if (payload.TryGetProperty("details", out var details))
                            {
                                if (details.TryGetProperty("required", out var req)) required = req.GetInt32();
                                if (details.TryGetProperty("available", out var avail)) available = avail.GetInt32();
                            }
                        }

                        Console.WriteLine($"[STORE] Roulette -> WebSocket: error received");
                        Console.WriteLine($"[STORE]   RoundId: {roundId}, EGM ID: {egmId}");
                        Console.WriteLine($"[STORE]   Error Code: {code}, Severity: {severity}");
                        Console.WriteLine($"[STORE]   Message: {errorMessage}");
                        Console.WriteLine($"[STORE]   Action: {action}");
                        if (required > 0 || available > 0)
                        {
                            Console.WriteLine($"[STORE]   Details: Required={required:N0} credits, Available={available:N0} credits");
                        }

                        // Store error message (do not forward to EGM)
                        storedRouletteMessages.Enqueue(("error", message, DateTime.UtcNow));
                        Console.WriteLine($"[STORE]   Stored error message (Total stored: {storedRouletteMessages.Count})");
                    }
                    else if (eventType == "ui_pong")
                    {
                        // Extract ui_pong data
                        string egmId = "EGM-0441";
                        long timestamp = 0;
                        long serverTime = 0;

                        // Extract from root level
                        if (root.TryGetProperty("egmId", out var eid)) egmId = eid.GetString();

                        // Extract from payload
                        if (root.TryGetProperty("payload", out var payload))
                        {
                            // Extract egmId from payload if not in root
                            if (egmId == "EGM-0441" && payload.TryGetProperty("egmId", out var payloadEgmId))
                                egmId = payloadEgmId.GetString();

                            // Extract timestamp and serverTime
                            if (payload.TryGetProperty("timestamp", out var ts)) timestamp = ts.GetInt64();
                            if (payload.TryGetProperty("serverTime", out var st)) serverTime = st.GetInt64();
                        }

                        Console.WriteLine($"[STORE] Roulette -> WebSocket: ui_pong received");
                        Console.WriteLine($"[STORE]   EGM ID: {egmId}");
                        Console.WriteLine($"[STORE]   Timestamp: {timestamp}, Server Time: {serverTime}");

                        // Store ui_pong message (do not forward to EGM)
                        storedRouletteMessages.Enqueue(("ui_pong", message, DateTime.UtcNow));
                        Console.WriteLine($"[STORE]   Stored ui_pong message (Total stored: {storedRouletteMessages.Count})");
                    }
                    else if (eventType == "round_state")
                    {
                        // Extract round_state data from Roulette
                        string roundId = "";
                        string egmId = "EGM-0441";
                        string tableId = "";
                        string state = "";
                        string stateExpiresAt = "";
                        int countdownMs = 0;
                        string layoutSeed = "";
                        string dealerId = "";

                        // Extract from root level
                        if (root.TryGetProperty("roundId", out var rid)) roundId = rid.GetString();
                        if (root.TryGetProperty("egmId", out var eid)) egmId = eid.GetString();

                        // Extract from payload
                        if (root.TryGetProperty("payload", out var payload))
                        {
                            if (payload.TryGetProperty("roundId", out var payloadRoundId)) roundId = payloadRoundId.GetString();
                            if (payload.TryGetProperty("tableId", out var tid)) tableId = tid.GetString();
                            if (payload.TryGetProperty("state", out var stateProp)) state = stateProp.GetString();
                            if (payload.TryGetProperty("stateExpiresAt", out var sea)) stateExpiresAt = sea.GetString();
                            if (payload.TryGetProperty("countdownMs", out var cd)) countdownMs = cd.GetInt32();
                            if (payload.TryGetProperty("layoutSeed", out var ls)) layoutSeed = ls.GetString();
                            if (payload.TryGetProperty("dealerId", out var did)) dealerId = did.GetString();
                        }

                        Console.WriteLine($"[STORE] Roulette -> WebSocket: round_state received");
                        Console.WriteLine($"[STORE]   RoundId: {roundId}, State: {state}, TableId: {tableId}");

                        // Store round_state message (do not forward to EGM)
                        storedRouletteMessages.Enqueue(("round_state", message, DateTime.UtcNow));
                        Console.WriteLine($"[STORE]   Stored round_state message (Total stored: {storedRouletteMessages.Count})");
                    }

                    // =======================================================================
                    //  3. EXISTING LOGIC (Pass-throughs)
                    // =======================================================================

                    // Pass-through SPIN_COMPLETED; update and push credits only from EGM
                    else if (eventType == "SPIN_COMPLETED")
                    {

                        bool hasCredits = root.TryGetProperty("CurrentCredits", out var spinCredits);
                        if (hasCredits)
                            _currentEgmBalance = spinCredits.GetDecimal();
                        else if (root.TryGetProperty("currentCredits", out var spinCreditsCamel))
                        {
                            _currentEgmBalance = spinCreditsCamel.GetDecimal();
                            hasCredits = true;
                        }
                        /*
                        if (hasCredits)
                        {
                            string egmId = "EGM-0441";
                            if (root.TryGetProperty("EgmId", out var eid)) egmId = eid.GetString();
                            await SendBalanceSnapshotAsync(sender, _currentEgmBalance, egmId);
                        }
                        */
                        Console.WriteLine($"[FORWARD] Passing through SPIN_COMPLETED");
                        await BroadcastToRouletteClientsAsync(message, sender, "SPIN_COMPLETED");
                    }
                    // Handle cashout - Store only, do not forward to EGM
                    else if (eventType == "cashout")
                    {
                        // Extract cashout data
                        string roundId = "";
                        string egmId = "EGM-0441";
                        string clientTs = "";

                        if (root.TryGetProperty("roundId", out var rid)) roundId = rid.GetString();
                        if (root.TryGetProperty("egmId", out var eid)) egmId = eid.GetString();

                        if (root.TryGetProperty("payload", out var payload))
                        {
                            // Extract roundId from payload if not in root
                            if (string.IsNullOrEmpty(roundId) && payload.TryGetProperty("roundId", out var payloadRoundId))
                                roundId = payloadRoundId.GetString();

                            // Extract egmId from payload if not in root
                            if (egmId == "EGM-0441" && payload.TryGetProperty("egmId", out var payloadEgmId))
                                egmId = payloadEgmId.GetString();

                            // Extract clientTs from payload
                            if (payload.TryGetProperty("clientTs", out var ct)) clientTs = ct.GetString();
                        }

                        Console.WriteLine($"[STORE] Roulette -> WebSocket: cashout received");
                        Console.WriteLine($"[STORE]   RoundId: {roundId}, EGM ID: {egmId}, ClientTs: {clientTs}");

                        // Store cashout message (do not forward to EGM)
                        storedRouletteMessages.Enqueue(("cashout", message, DateTime.UtcNow));
                        Console.WriteLine($"[STORE]   Stored cashout message (Total stored: {storedRouletteMessages.Count})");
                    }
                    // Handle Confirmation - Store only, do not forward to EGM
                    else if (eventType == "AFT_CONFIRMED")
                    {
                        bool confirmed = true;
                        string transferId = "";
                        // Basic parsing for the properties...
                        if (root.TryGetProperty("confirmed", out var cp)) confirmed = cp.GetBoolean();
                        if (root.TryGetProperty("transferId", out var tid)) transferId = tid.GetString();

                        Console.WriteLine($"[STORE] Roulette -> WebSocket: AFT_CONFIRMED received");
                        Console.WriteLine($"[STORE]   Confirmed: {confirmed}, TransferId: {transferId}");

                        // Store AFT_CONFIRMED message (do not forward to EGM)
                        storedRouletteMessages.Enqueue(("AFT_CONFIRMED", message, DateTime.UtcNow));
                        Console.WriteLine($"[STORE]   Stored AFT_CONFIRMED message (Total stored: {storedRouletteMessages.Count})");
                    }
                    // Store/Log other unknown events from Roulette
                    else
                    {
                        // Check if this is from a Roulette client
                        if (clientTypes.ContainsKey(sender) && clientTypes[sender] == "roulette")
                        {
                            Console.WriteLine($"[STORE] Roulette -> WebSocket: Unknown event '{eventType}' received");
                            storedRouletteMessages.Enqueue((eventType, message, DateTime.UtcNow));
                            Console.WriteLine($"[STORE]   Stored '{eventType}' message (Total stored: {storedRouletteMessages.Count})");
                        }
                        else
                        {
                            Console.WriteLine($"[STORE] Storing event: {eventType} for log/audit.");
                        }
                    }

                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error processing message: {ex.Message}");
                }
            }
            await Task.Delay(50);
        }
    }

    static async Task SendToSocketAsync(WebSocket ws, string message, string who, string eventType)
    {
        if (ws == null || ws.State != WebSocketState.Open) return;

        WebSocketFileLogger.LogSent(who, eventType, message);
        var bytes = Encoding.UTF8.GetBytes(message);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    // Broadcast message to all clients
    static async Task BroadcastMessageAsync(string message)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        var currentClients = new List<WebSocket>(clients);

        foreach (var client in currentClients)
        {
            if (client.State == WebSocketState.Open)
            {
                try
                {
                    await client.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
                }
                catch { clients.Remove(client); }
            }
        }
    }

    // Broadcast message to all clients EXCEPT the sender
    static async Task BroadcastToOthersAsync(string message, WebSocket sender, string eventType = "")
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        var currentClients = new List<WebSocket>(clients);
        int sentCount = 0;
        int failedCount = 0;

        foreach (var client in currentClients)
        {
            // Skip the sender
            if (client != sender && client.State == WebSocketState.Open)
            {
                try
                {
                    await client.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
                    sentCount++;
                }
                catch
                {
                    clients.Remove(client);
                    failedCount++;
                }
            }
        }

        // Enhanced logging
        if (!string.IsNullOrEmpty(eventType))
        {
            Console.WriteLine($"╔════════════════════════════════════════════════════════════════╗");
            Console.WriteLine($"║  ✓ EVENT SENT TO ROULETTE CLIENT(S)                          ║");
            Console.WriteLine($"╠════════════════════════════════════════════════════════════════╣");
            Console.WriteLine($"║  Event Type: {eventType.PadRight(47)}║");
            Console.WriteLine($"║  Sent To:    {sentCount} Roulette client(s){"".PadRight(35)}║");
            Console.WriteLine($"║  Timestamp:  {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff} UTC{"".PadRight(25)}║");
            if (failedCount > 0)
            {
                Console.WriteLine($"║  ⚠ WARNING:  Failed to send to {failedCount} client(s){"".PadRight(30)}║");
            }
            if (sentCount == 0)
            {
                Console.WriteLine($"║  ⚠ WARNING:  No clients received! (Total: {currentClients.Count}){"".PadRight(26)}║");
            }
            Console.WriteLine($"╚════════════════════════════════════════════════════════════════╝");
        }
        else
        {
            Console.WriteLine($"[SEND] Message sent to {sentCount} client(s)");
        }
    }

    // NEW: Broadcast only to clients (not to EGM) - Simplified to broadcast all for now as filtering requires client ID mapping
    static async Task BroadcastToClientsOnlyAsync(string message)
    {
        await BroadcastMessageAsync(message);
    }

    public static async Task SendAFTConfirmationToEGMAsync(bool confirmed, string transferId = "", WebSocket sender = null)
    {
        var aftConfirmation = new
        {
            EventType = "AFT_CONFIRMED",
            Confirmed = confirmed,
            TransferId = transferId,
            Timestamp = DateTime.UtcNow
        };
        var jsonMessage = JsonSerializer.Serialize(aftConfirmation);
        // Send to EGM client only (Roulette sent the confirmation)
        await BroadcastToEGMClientsAsync(jsonMessage, sender, "AFT_CONFIRMED");
    }

    // Send session_initialized event when EGM connects
    static async Task SendSessionInitializedAsync(WebSocket egmSender, decimal availableCredits = 0)
    {
        if (_sessionInitialized)
        {
            Console.WriteLine($"[SESSION] Session already initialized, skipping");
            return;
        }

        string egmId = "EGM-0441"; // Default, can be extracted from EGM if available
        long sequence = Interlocked.Increment(ref _sequenceCounter);

        var sessionEvent = new
        {
            @event = "session_initialized",
            egmId = egmId,
            sequence = sequence,
            sentAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            nonce = Guid.NewGuid().ToString(),
            payload = new
            {
                egmId = egmId,
                jurisdiction = "NV", // Default, can be configured
                currency = "ZAR", // Match your currency
                availableCredits = (int)availableCredits, // Credits from EGM if available
                aftAccountId = "AFT-12345", // Default, can be extracted if available
                playerClass = "VIP", // Default, can be configured
                version = new
                {
                    egmBackend = "1.0.0",
                    sasDaemon = "2.1.0"
                }
            }
        };

        string json = JsonSerializer.Serialize(sessionEvent);
        Console.WriteLine($"[SESSION] Sending session_initialized to Roulette clients (availableCredits: {availableCredits})");
        Console.WriteLine($"[SESSION]   EGM ID: {egmId}, Sequence: {sequence}");

        // Send to all Roulette clients (exclude EGM sender)
        //  await BroadcastToRouletteClientsAsync(json, egmSender, "session_initialized");
        _sessionInitialized = true;
    }

    // Send session_initialized to one Roulette client when they connect after EGM (so the URL gets current balance)
    static async Task SendSessionInitializedToRouletteClientAsync(WebSocket rouletteClient)
    {
        if (rouletteClient?.State != WebSocketState.Open) return;

        decimal availableCredits = _currentEgmBalance;
        string egmId = "EGM-0441";
        long sequence = Interlocked.Increment(ref _sequenceCounter);

        var sessionEvent = new
        {
            @event = "session_initialized",
            egmId = egmId,
            sequence = sequence,
            sentAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            nonce = Guid.NewGuid().ToString(),
            payload = new
            {
                egmId = egmId,
                jurisdiction = "NV",
                currency = "ZAR",
                availableCredits = (int)availableCredits,
                aftAccountId = "AFT-12345",
                playerClass = "Test",
                version = new { egmBackend = "1.0.0", sasDaemon = "2.1.0" }
            }
        };

        string json = JsonSerializer.Serialize(sessionEvent);
        Console.WriteLine($"[SESSION] Sending session_initialized to Roulette (URL loaded) (availableCredits: {availableCredits})");
        WebSocketFileLogger.LogSent("ROULETTE", "session_initialized", json);
        try
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            await rouletteClient.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SESSION] Failed to send session_initialized to Roulette: {ex.Message}");
        }
    }

    // Broadcast message only to Roulette clients
    static async Task BroadcastToRouletteClientsAsync(string message, WebSocket excludeSender, string eventType = "")
    {
        WebSocketFileLogger.LogSent("ROULETTE", eventType, message);
        var bytes = Encoding.UTF8.GetBytes(message);

        List<WebSocket> currentClients;
        lock (clients)
            currentClients = new List<WebSocket>(clients);

        int sentCount = 0, failedCount = 0;

        foreach (var client in currentClients)
        {
            if (client == null) continue;
            if (client == excludeSender) continue;
            if (client.State != WebSocketState.Open) continue;

            // ✅ Only send to roulette clients
            if (!clientTypes.TryGetValue(client, out var role) ||
                !string.Equals(role, "roulette", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                await client.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
                sentCount++;
            }
            catch (Exception ex)
            {
                failedCount++;
                lock (clients) clients.Remove(client);
                clientTypes.TryRemove(client, out _);
                Console.WriteLine($"[ROULETTE SEND ERROR] {eventType} -> removed client. Reason: {ex.Message}");
            }
        }

        Console.WriteLine($"[ROULETTE SEND] {eventType} -> {sentCount} sent, {failedCount} failed");
    }

    // Broadcast message only to EGM clients
    static async Task BroadcastToEGMClientsAsync(string message, WebSocket excludeSender, string eventType = "")
    {
        WebSocketFileLogger.LogSent("EGM", eventType, message);

        var bytes = Encoding.UTF8.GetBytes(message);

        // Snapshot to avoid collection-modified issues
        List<WebSocket> currentClients;
        lock (clients)
        {
            currentClients = new List<WebSocket>(clients);
        }

        int sentCount = 0;
        int failedCount = 0;

        foreach (var client in currentClients)
        {
            if (client == null) continue;
            if (client == excludeSender) continue;
            if (client.State != WebSocketState.Open) continue;

            // ✅ Only send to EGM clients (hard filter)
            if (!clientTypes.TryGetValue(client, out var role) || !string.Equals(role, "egm", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                await client.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
                sentCount++;
            }
            catch (Exception ex)
            {
                failedCount++;

                // Remove dead sockets safely
                lock (clients)
                {
                    clients.Remove(client);
                }
                clientTypes.TryRemove(client, out _);

                Console.WriteLine($"[EGM SEND ERROR] {eventType} -> removed client. Reason: {ex.Message}");
            }
        }

        // Enhanced logging
        if (!string.IsNullOrEmpty(eventType))
        {
            Console.WriteLine($"╔════════════════════════════════════════════════════════════════╗");
            Console.WriteLine($"║  ✓ EVENT SENT TO EGM CLIENT(S)                               ║");
            Console.WriteLine($"╠════════════════════════════════════════════════════════════════╣");
            Console.WriteLine($"║  Event Type: {eventType.PadRight(47)}║");
            Console.WriteLine($"║  Sent To:    {sentCount} EGM client(s){"".PadRight(37)}║");
            Console.WriteLine($"║  Timestamp:  {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff} UTC{"".PadRight(25)}║");
            if (failedCount > 0)
            {
                Console.WriteLine($"║  ⚠ WARNING:  Failed to send to {failedCount} client(s){"".PadRight(30)}║");
            }
            if (sentCount == 0)
            {
                Console.WriteLine($"║  ⚠ WARNING:  No EGM client received! (Total: {currentClients.Count}){"".PadRight(22)}║");
            }
            Console.WriteLine($"╚════════════════════════════════════════════════════════════════╝");
        }
        else
        {
            Console.WriteLine($"[EGM] Message sent to {sentCount} EGM client(s)");
            if (failedCount > 0)
            {
                Console.WriteLine($"[WARNING] Failed to send to {failedCount} client(s)");
            }
        }
    }

    // Send balance_snapshot event when EGM sends credit updates
    static async Task SendBalanceSnapshotAsync(WebSocket egmSender, decimal availableCredits, string egmId = "EGM-0441")
    {
        string roundId = $"round-{DateTime.UtcNow.Ticks}";
        long sequence = Interlocked.Increment(ref _sequenceCounter);

        var balanceSnapshot = new
        {
            @event = "balance_snapshot",
            roundId = roundId,
            egmId = egmId,
            sequence = sequence,
            sentAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            nonce = Guid.NewGuid().ToString(),
            payload = new
            {
                availableCredits = (int)availableCredits,
                lockReason = (string)null // null if not locked
            }
        };

        string json = JsonSerializer.Serialize(balanceSnapshot);
        Console.WriteLine($"[BALANCE] Sending balance_snapshot: {availableCredits} credits");
        Console.WriteLine($"[BALANCE]   RoundId: {roundId}, EGM ID: {egmId}, Sequence: {sequence}");

        // Send to all Roulette clients (exclude EGM sender)
        await BroadcastToRouletteClientsAsync(json, egmSender, "balance_snapshot");
    }

    // Send bet_commit_ack event after receiving bet_commit from Roulette
    static async Task SendBetCommitAckAsync(WebSocket rouletteSender, string roundId, string egmId, int totalStake)
    {
        /*
        long sequence = Interlocked.Increment(ref _sequenceCounter);
        
        // Balance from EGM only; ack uses current value and reserved amount for Roulette
        decimal egmBalanceBefore = _currentEgmBalance;
        decimal egmBalanceAfter = _currentEgmBalance - totalStake;

        var betCommitAck = new
        {
            
            @event = "bet_commit_ack",
            roundId = roundId,
            egmId = egmId,
            sequence = sequence,
            sentAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            nonce = Guid.NewGuid().ToString(),
            payload = new
            {
                roundId = roundId,
                accepted = true,
                reason = (string)null, // null if accepted
                egmBalanceBefore = (int)egmBalanceBefore,
                egmBalanceAfter = (int)egmBalanceAfter,
                meters = new
                {
                    coinsIn = _coinsIn, // Tracked meter
                    gamesPlayed = _gamesPlayed // Tracked meter
                }
            }
        };

        string json = JsonSerializer.Serialize(betCommitAck);
        
        // Send back to the Roulette client that sent bet_commit
        await SendToClientAsync(json, rouletteSender, "bet_commit_ack", roundId, totalStake, egmBalanceBefore, egmBalanceAfter);
        */
    }

    // Send message to a specific client
    static async Task SendToClientAsync(string message, WebSocket client, string eventType = "", string roundId = "", int stake = 0, decimal balanceBefore = 0, decimal balanceAfter = 0)
    {
        if (client == null || client.State != WebSocketState.Open)
        {
            Console.WriteLine($"[WARNING] Cannot send message to client - client is null or not open");
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(message);
        try
        {
            await client.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);

            // Enhanced logging for bet_commit_ack
            if (!string.IsNullOrEmpty(eventType) && eventType == "bet_commit_ack")
            {
                Console.WriteLine($"╔════════════════════════════════════════════════════════════════╗");
                Console.WriteLine($"║  ✓ EVENT SENT TO ROULETTE CLIENT(S)                          ║");
                Console.WriteLine($"╠════════════════════════════════════════════════════════════════╣");
                Console.WriteLine($"║  Event Type: {eventType.PadRight(47)}║");
                Console.WriteLine($"║  Sent To:    1 Roulette client{"".PadRight(38)}║");
                Console.WriteLine($"║  RoundId:    {roundId.PadRight(47)}║");
                Console.WriteLine($"║  Stake:      {stake} credits{"".PadRight(40)}║");
                Console.WriteLine($"║  Balance:    {balanceBefore} → {balanceAfter} credits{"".PadRight(26)}║");
                Console.WriteLine($"║  Timestamp:  {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff} UTC{"".PadRight(25)}║");
                Console.WriteLine($"╚════════════════════════════════════════════════════════════════╝");
            }
            else if (!string.IsNullOrEmpty(eventType))
            {
                Console.WriteLine($"╔════════════════════════════════════════════════════════════════╗");
                Console.WriteLine($"║  ✓ EVENT SENT TO ROULETTE CLIENT(S)                          ║");
                Console.WriteLine($"╠════════════════════════════════════════════════════════════════╣");
                Console.WriteLine($"║  Event Type: {eventType.PadRight(47)}║");
                Console.WriteLine($"║  Sent To:    1 Roulette client{"".PadRight(38)}║");
                Console.WriteLine($"║  Timestamp:  {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff} UTC{"".PadRight(25)}║");
                Console.WriteLine($"╚════════════════════════════════════════════════════════════════╝");
            }
            else
            {
                Console.WriteLine($"[SEND] Message sent to client successfully");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Failed to send message to client: {ex.Message}");
            clients.Remove(client);
            clientTypes.TryRemove(client, out _);
        }
    }
}

// Minimal EGM Response Model required for serialization
public class GameResponse
{
    public string EventType { get; set; }
    public int BetAmount { get; set; }
    public int WinAmount { get; set; }
    public DateTime Timestamp { get; set; }
}