using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace WpfVisualTreeMcp.Inspector;

/// <summary>
/// Diagnostic log for the injected Inspector.
///
/// All entries are queued to a single background writer thread — the calling thread
/// (including the UI Dispatcher thread) never touches the file system. The log lives
/// at %TEMP%\WpfInspector_Debug_{pid}.log and is capped: at ~5 MB it is rotated to
/// a .old file (overwriting the previous one), so unbounded growth is impossible.
///
/// Set WPF_INSPECTOR_DEBUG=0 (in the target process environment) to disable writing
/// entirely; entries are then dropped at enqueue time at near-zero cost.
/// </summary>
public static class InspectorLog
{
    private const long MaxBytes = 5 * 1024 * 1024;
    private const int RotationCheckInterval = 256;

    private static readonly string? LogPath;
    private static readonly BlockingCollection<string>? Queue;
    private static int _writesSinceRotationCheck;

    static InspectorLog()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("WPF_INSPECTOR_DEBUG"), "0", StringComparison.Ordinal))
        {
            return;
        }

        var pid = GetCurrentProcessId();
        LogPath = Path.Combine(Path.GetTempPath(), $"WpfInspector_Debug_{pid}.log");
        Queue = new BlockingCollection<string>(boundedCapacity: 8192);
        var writer = new Thread(WriterLoop)
        {
            IsBackground = true,
            Name = "WpfInspectorLog"
        };
        writer.Start();
        Enqueue($"=== Inspector logging started (pid {pid}) ===");
    }

    /// <summary>Queues an entry for background writing. Safe (and cheap) from any thread.</summary>
    public static void Debug(string message)
    {
        if (Queue == null)
        {
            return;
        }

        Enqueue(message);
    }

    private static void Enqueue(string message)
    {
        try
        {
            Queue!.TryAdd($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}");
        }
        catch (Exception)
        {
            // Full queue (writer stalled) or completing collection: drop the entry.
            // Logging must never break inspection.
        }
    }

    private static void WriterLoop()
    {
        foreach (var entry in Queue!.GetConsumingEnumerable())
        {
            try
            {
                File.AppendAllText(LogPath!, entry + Environment.NewLine);
            }
            catch (Exception)
            {
                // Transient sharing violations etc.: the next entry retries the file.
            }

            if (Interlocked.Increment(ref _writesSinceRotationCheck) >= RotationCheckInterval)
            {
                _writesSinceRotationCheck = 0;
                RotateIfLarge();
            }
        }
    }

    private static void RotateIfLarge()
    {
        try
        {
            if (new FileInfo(LogPath!).Length <= MaxBytes)
            {
                return;
            }

            var old = LogPath + ".old";
            if (File.Exists(old))
            {
                File.Delete(old);
            }

            File.Move(LogPath!, old);
        }
        catch (Exception)
        {
            // Rotation is best-effort; keep writing to the current file on failure.
        }
    }

    private static int GetCurrentProcessId()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        return process.Id;
    }
}
