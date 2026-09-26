using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DogdouSpec.Core.Transactions;

/// <summary>
/// Execution metadata recorded inside workspace lock file for diagnostic and conflict reporting.
/// </summary>
public sealed record LockMetadata(
    int Pid,
    string ProcessName,
    string CommandLine,
    DateTimeOffset AcquiredAtUtc,
    string MachineName)
{
    public static LockMetadata CreateCurrent()
    {
        var proc = Process.GetCurrentProcess();
        return new LockMetadata(
            Pid: proc.Id,
            ProcessName: proc.ProcessName,
            CommandLine: Environment.CommandLine,
            AcquiredAtUtc: DateTimeOffset.UtcNow,
            MachineName: Environment.MachineName);
    }

    public string ToJsonString()
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            var safeCmd = CommandLine.Length > 200 ? CommandLine[..197] + "..." : CommandLine;
            writer.WriteStartObject();
            writer.WriteNumber("pid", Pid);
            writer.WriteString("process", ProcessName);
            writer.WriteString("command", safeCmd);
            writer.WriteString("acquired_at", AcquiredAtUtc.ToString("o", CultureInfo.InvariantCulture));
            writer.WriteString("machine", MachineName);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static LockMetadata? FromJsonString(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var pid = root.TryGetProperty("pid", out var pidProp) && pidProp.TryGetInt32(out var p) ? p : 0;
            var proc = root.TryGetProperty("process", out var procProp) ? procProp.GetString() ?? string.Empty : string.Empty;
            var cmd = root.TryGetProperty("command", out var cmdProp) ? cmdProp.GetString() ?? string.Empty : string.Empty;
            var machine = root.TryGetProperty("machine", out var machProp) ? machProp.GetString() ?? string.Empty : string.Empty;
            var acquiredAtStr = root.TryGetProperty("acquired_at", out var acqProp) ? acqProp.GetString() : null;

            var acquiredAt = DateTimeOffset.TryParse(acquiredAtStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt)
                ? dt
                : DateTimeOffset.UtcNow;

            return new LockMetadata(pid, proc, cmd, acquiredAt, machine);
        }
        catch
        {
            return null;
        }
    }

    public static LockMetadata? TryReadFromFile(string lockFilePath)
    {
        try
        {
            if (!File.Exists(lockFilePath))
            {
                return null;
            }

            using var fs = new FileStream(
                lockFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            var content = reader.ReadToEnd();
            return FromJsonString(content);
        }
        catch
        {
            return null;
        }
    }

    public string FormatConflictDetails()
    {
        var elapsed = DateTimeOffset.UtcNow - AcquiredAtUtc;
        var elapsedSeconds = Math.Max(0, (int)elapsed.TotalSeconds);
        var timeStr = AcquiredAtUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

        var shortCommand = CommandLine;
        if (shortCommand.Length > 80)
        {
            shortCommand = shortCommand[..77] + "...";
        }

        return $"Lock currently held by PID {Pid} ('{ProcessName}', cmd: '{shortCommand}') on machine '{MachineName}' since {timeStr} ({elapsedSeconds}s ago).";
    }
}
