using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.WebSockets;

class Program
{
    private static readonly List<WebSocket> clients = new();
    private static readonly ConcurrentQueue<string> messageQueue = new();

    static async Task Main(string[] args)
    {
        Console.WriteLine("Starting Game WebSocket server on ws://0.0.0.0:5000/ws...");

        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls("http://0.0.0.0:5000");
        builder.Services.AddWebSockets(_ => { });

        var app = builder.Build();
        app.UseWebSockets();

        // Start background task for processing messages
        _ = Task.Run(ProcessMessageQueueAsync);

        app.Use(async (context, next) =>
        {
            if (context.Request.Path == "/ws")
            {
                if (context.WebSockets.IsWebSocketRequest)
                {
                    using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
                    await HandleWebSocketAsync(webSocket);
                }
                else
                {
                    context.Response.StatusCode = 400;
                }
            }
            else
            {
                await next();
            }
        });

        await app.RunAsync();
    }

    static async Task HandleWebSocketAsync(WebSocket webSocket)
    {
        var buffer = new byte[1024 * 4];
        Console.WriteLine("Game client connected.");
        clients.Add(webSocket);

        try
        {
            while (webSocket.State == WebSocketState.Open)
            {
                var result = await webSocket.ReceiveAsync(
                    new ArraySegment<byte>(buffer),
                    CancellationToken.None
                );

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    Console.WriteLine($"Received: {message}");
                    messageQueue.Enqueue(message);
                }
                else if (result.MessageType == WebSocketMessageType.Close)
                {
                    Console.WriteLine("Game client disconnected.");
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WebSocket error: {ex.Message}");
        }
        finally
        {
            clients.Remove(webSocket);
            await webSocket.CloseAsync(
                WebSocketCloseStatus.NormalClosure,
                "Closing",
                CancellationToken.None
            );
        }
    }

    static async Task ProcessMessageQueueAsync()
    {
        while (true)
        {
            if (messageQueue.TryDequeue(out string message))
            {
                try
                {
                    // Parse the message as a dynamic object to access properties directly
                    var data = JsonSerializer.Deserialize<JsonElement>(message);

                    // Check if it's a SPIN message
                    if (data.TryGetProperty("eventType", out var eventType) &&
                        eventType.GetString() == "SPIN" &&
                        data.TryGetProperty("betAmount", out var betAmount) &&
                        data.TryGetProperty("winAmount", out var winAmount))
                    {
                        // Create response with the actual values
                        var response = new GameResponse
                        {
                            EventType = "GAME_UPDATE",
                            BetAmount = betAmount.GetInt32(),
                            WinAmount = winAmount.GetInt32(),
                            Timestamp = DateTime.UtcNow
                        };

                        var jsonResponse = JsonSerializer.Serialize(response);
                        await BroadcastMessageAsync(jsonResponse);
                    }

                    // Handle SPIN_COMPLETED message from EGM
                    else if (data.TryGetProperty("EventType", out var eventTypeSpinCompleted) &&
                             eventTypeSpinCompleted.GetString() == "SPIN_COMPLETED")
                    {
                        // Forward the spin completion message to clients
                        await BroadcastMessageAsync(message);
                        Console.WriteLine($"Spin completed: {message}");
                    }

                    // Handle AFT_DEPOSIT
                    else if (data.TryGetProperty("EventType", out var eventType2) &&
                             eventType2.GetString() == "AFT_DEPOSIT")
                    {
                        // Simply forward the credit update message
                        await BroadcastMessageAsync(message);
                    }

                    // Handle BILL_INSERTED
                    else if (data.TryGetProperty("EventType", out var eventType3) &&
                             eventType3.GetString() == "BILL_INSERTED")
                    {
                        // Simply forward the credit update message
                        await BroadcastMessageAsync(message);
                    }

                    // Handle AFT_CASHOUT
                    else if (data.TryGetProperty("EventType", out var eventType4) &&
                             eventType4.GetString() == "AFT_CASHOUT")
                    {
                        // Forward credit updates
                        await BroadcastMessageAsync(message);
                    }

                    // Handle connection test messages
                    else if (data.TryGetProperty("eventType", out var eventTypeTest) &&
                             (eventTypeTest.GetString() == "CONNECTION_TEST" ||
                              eventTypeTest.GetString() == "TEST_RESPONSE"))
                    {
                        // Respond to connection tests
                        var testResponse = new
                        {
                            eventType = "CONNECTION_TEST_RESPONSE",
                            message = "Server is alive and connected",
                            timestamp = DateTime.UtcNow,
                            originalMessage = message
                        };

                        var testJson = JsonSerializer.Serialize(testResponse);
                        await BroadcastMessageAsync(testJson);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error processing message: {ex.Message}");
                    // Don't re-queue malformed messages to avoid infinite loops
                    Console.WriteLine($"Dropping malformed message: {message}");
                }
            }
            await Task.Delay(100);
        }
    }

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
                    await client.SendAsync(
                        new ArraySegment<byte>(bytes),
                        WebSocketMessageType.Text,
                        true,
                        CancellationToken.None
                    );
                    Console.WriteLine($"Broadcasted: {message}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error broadcasting to client: {ex.Message}");
                    clients.Remove(client);
                }
            }
        }
    }

    // Helper method to send spin completion messages (can be called from EGM)
    public static async Task SendSpinCompletedAsync(int betAmount, int winAmount, decimal currentCredits, string status = "SUCCESS")
    {
        var spinCompleted = new SpinCompletedMessage
        {
            EventType = "SPIN_COMPLETED",
            BetAmount = betAmount,
            WinAmount = winAmount,
            CurrentCredits = currentCredits,
            Timestamp = DateTime.UtcNow,
            Status = status
        };

        var jsonMessage = JsonSerializer.Serialize(spinCompleted);
        await BroadcastMessageAsync(jsonMessage);
    }
}

// Message models
public class GameMessage
{
    public string EventType { get; set; }
    public int BetAmount { get; set; }
    public int WinAmount { get; set; }
}

public class GameResponse
{
    public string EventType { get; set; }
    public int BetAmount { get; set; }
    public int WinAmount { get; set; }
    public DateTime Timestamp { get; set; }
}

//  model for spin completion messages
public class SpinCompletedMessage
{
    public string EventType { get; set; }
    public int BetAmount { get; set; }
    public int WinAmount { get; set; }
    public decimal CurrentCredits { get; set; }
    public DateTime Timestamp { get; set; }
    public string Status { get; set; }
}

// Model for credit updates
public class CreditUpdateMessage
{
    public string EventType { get; set; } = "CREDIT_UPDATE";
    public decimal CurrentCredits { get; set; }
    public DateTime Timestamp { get; set; }
}