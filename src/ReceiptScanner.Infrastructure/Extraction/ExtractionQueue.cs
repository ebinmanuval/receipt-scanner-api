using System.Globalization;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReceiptScanner.Core.Extraction;

namespace ReceiptScanner.Infrastructure.Extraction;

public sealed class ExtractionOptions
{
    public const string Section = "Extraction";

    /// <summary>Queue scans for extraction as soon as they're uploaded (Django's SCAN_AUTO_EXTRACT).</summary>
    public bool AutoExtract { get; set; } = true;

    /// <summary>
    /// The Celery broker, in the same form as Django's CELERY_BROKER_URL:
    /// <c>redis://[:password@]host:port/db</c>. Use a Redis database of its
    /// own (e.g. /1) so the Django worker never sees .NET's scan ids.
    /// </summary>
    public string Broker { get; set; } = "redis://localhost:6379/0";

    /// <summary>The worker's queue (<c>celery worker -Q extract</c>).</summary>
    public string Queue { get; set; } = "extract";

    /// <summary>A processing scan older than this may be re-run (EXTRACT_STALE_MINUTES).</summary>
    public int StaleMinutes { get; set; } = 30;

    public TimeSpan StaleAfter => TimeSpan.FromMinutes(StaleMinutes);
}

/// <summary>Hands a scan to the extraction worker.</summary>
public interface IExtractionQueue
{
    /// <summary>
    /// Queue <c>receipts.tasks.extract_scan(scanId)</c>. Returns the Celery
    /// task id, or null if the broker couldn't be reached. Never throws: the
    /// scan stays "queued" and can be re-run, as in the Django app.
    /// </summary>
    Task<string?> EnqueueAsync(long scanId, CancellationToken ct = default);
}

/// <summary>
/// Publishes to Redis with one LPUSH, which is all kombu does. A minimal
/// RESP client rather than a Redis library: one command, no pooling needed,
/// and a short timeout so an outage can't hang an upload.
/// </summary>
public sealed class CeleryExtractionQueue(IOptions<ExtractionOptions> options, ILogger<CeleryExtractionQueue> logger) : IExtractionQueue
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);
    private readonly ExtractionOptions _options = options.Value;

    public async Task<string?> EnqueueAsync(long scanId, CancellationToken ct = default)
    {
        var taskId = Guid.NewGuid().ToString();
        var message = CeleryMessage.ExtractScan(scanId, _options.Queue, taskId, $"dotnet@{Environment.MachineName}");
        try
        {
            var broker = RedisBroker.Parse(_options.Broker);
            await RedisBroker.LPushAsync(broker, _options.Queue, message, Timeout, ct);
            logger.LogInformation("Queued extraction for scan {ScanId} (task {TaskId})", scanId, taskId);
            return taskId;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Could not queue extraction for scan {ScanId}; it stays queued and can be re-run.", scanId);
            return null;
        }
    }
}

/// <summary>Just enough of the Redis protocol for AUTH, SELECT and LPUSH.</summary>
public static class RedisBroker
{
    public sealed record Endpoint(string Host, int Port, int Database, string? User, string? Password);

    /// <summary>Parse <c>redis://[[user]:password@]host[:port][/db]</c>.</summary>
    public static Endpoint Parse(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "redis")
            throw new FormatException($"Not a redis:// URL: {url}");

        string? user = null, password = null;
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var parts = uri.UserInfo.Split(':', 2);
            user = parts.Length == 2 && parts[0].Length > 0 ? Uri.UnescapeDataString(parts[0]) : null;
            password = Uri.UnescapeDataString(parts.Length == 2 ? parts[1] : parts[0]);
        }

        var path = uri.AbsolutePath.Trim('/');
        var db = path.Length == 0 ? 0 : int.Parse(path, CultureInfo.InvariantCulture);
        return new Endpoint(uri.Host, uri.Port > 0 ? uri.Port : 6379, db, user, password);
    }

    public static async Task<long> LPushAsync(Endpoint ep, string key, string value, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        using var client = new TcpClient();
        await client.ConnectAsync(ep.Host, ep.Port, cts.Token);
        await using var stream = client.GetStream();

        if (ep.Password is not null)
            await CommandAsync(stream, cts.Token, ep.User is null ? new[] { "AUTH", ep.Password } : new[] { "AUTH", ep.User, ep.Password });
        if (ep.Database != 0)
            await CommandAsync(stream, cts.Token, "SELECT", ep.Database.ToString(CultureInfo.InvariantCulture));

        var reply = await CommandAsync(stream, cts.Token, "LPUSH", key, value);
        return long.Parse(reply, CultureInfo.InvariantCulture);
    }

    /// <summary>Send one command; return the reply line's payload. Throws on an error reply.</summary>
    private static async Task<string> CommandAsync(NetworkStream stream, CancellationToken ct, params string[] args)
    {
        var sb = new StringBuilder();
        sb.Append('*').Append(args.Length).Append("\r\n");
        foreach (var arg in args)
            sb.Append('$').Append(Encoding.UTF8.GetByteCount(arg)).Append("\r\n").Append(arg).Append("\r\n");
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        await stream.WriteAsync(bytes, ct);

        var line = await ReadLineAsync(stream, ct);
        if (line.Length == 0) throw new IOException("Empty reply from Redis.");
        return line[0] switch
        {
            '+' or ':' => line[1..],
            '-' => throw new IOException($"Redis error: {line[1..]}"),
            _ => throw new IOException($"Unexpected Redis reply: {line}"),
        };
    }

    private static async Task<string> ReadLineAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            if (await stream.ReadAsync(one, ct) == 0) throw new IOException("Redis closed the connection.");
            if (one[0] == '\n' && buffer.Count > 0 && buffer[^1] == '\r')
            {
                buffer.RemoveAt(buffer.Count - 1);
                return Encoding.UTF8.GetString([.. buffer]);
            }
            buffer.Add(one[0]);
            if (buffer.Count > 4096) throw new IOException("Redis reply too long.");
        }
    }
}
