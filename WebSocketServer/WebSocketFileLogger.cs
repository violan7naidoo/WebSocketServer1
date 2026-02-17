using System;
using System.IO;

/// <summary>
/// Thread-safe file logger for WebSocket traffic. Writes to Desktop\ws\websocket.txt.
/// Creates the ws folder on Desktop if missing; uses a single .txt file and appends at the bottom.
/// </summary>
public static class WebSocketFileLogger
{
    private static readonly object _lock = new object();
    private static string? _logPath;

    private static string LogPath
    {
        get
        {
            if (_logPath != null) return _logPath;
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string wsDir = Path.Combine(desktop, "ws");
            try
            {
                if (!Directory.Exists(wsDir))
                    Directory.CreateDirectory(wsDir);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LOGGER] Failed to create Desktop\\ws folder: {ex.Message}");
                wsDir = desktop;
            }
            _logPath = Path.Combine(wsDir, "websocket.txt");
            return _logPath;
        }
    }

    public static void LogReceived(string fromClient, string message)
    {
        Write("RECV", fromClient, null, message);
    }

    public static void LogSent(string toTarget, string? eventType, string message)
    {
        Write("SEND", toTarget, eventType, message);
    }

    public static void LogInfo(string message)
    {
        lock (_lock)
        {
            try
            {
                var line = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff} UTC | INFO | {message}{Environment.NewLine}";
                File.AppendAllText(LogPath, line);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LOGGER] Write failed: {ex.Message}");
            }
        }
    }

    private static void Write(string direction, string clientOrTarget, string? eventType, string payload)
    {
        lock (_lock)
        {
            try
            {
                var et = string.IsNullOrEmpty(eventType) ? "" : $" | {eventType}";
                var line = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff} UTC | {direction} | {clientOrTarget}{et} | {payload}{Environment.NewLine}";
                File.AppendAllText(LogPath, line);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LOGGER] Write failed: {ex.Message}");
            }
        }
    }
}
