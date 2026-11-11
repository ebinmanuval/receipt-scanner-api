using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReceiptScanner.Core.Extraction;

/// <summary>
/// A Celery task message (protocol 2, JSON), as kombu's Redis transport stores
/// it. Pushing one onto the queue's Redis list is exactly what
/// <c>extract_scan.delay(scan_id)</c> does in the Django app, so the existing
/// Celery worker picks it up unchanged.
/// </summary>
/// <remarks>
/// Envelope: <c>{body, content-encoding, content-type, headers, properties}</c>.
/// The body is base64 of <c>[args, kwargs, embed]</c>. kombu publishes with
/// LPUSH and consumes with BRPOP on a list named after the queue.
/// </remarks>
public static class CeleryMessage
{
    public static string Build(string taskName, JsonArray args, string queue, string taskId, string origin)
    {
        var body = new JsonArray(
            args,
            new JsonObject(),
            new JsonObject
            {
                ["callbacks"] = null,
                ["errbacks"] = null,
                ["chain"] = null,
                ["chord"] = null,
            });

        var envelope = new JsonObject
        {
            ["body"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(body.ToJsonString())),
            ["content-encoding"] = "utf-8",
            ["content-type"] = "application/json",
            ["headers"] = new JsonObject
            {
                ["lang"] = "py",
                ["task"] = taskName,
                ["id"] = taskId,
                ["shadow"] = null,
                ["eta"] = null,
                ["expires"] = null,
                ["group"] = null,
                ["group_index"] = null,
                ["retries"] = 0,
                ["timelimit"] = new JsonArray(null, null),
                ["root_id"] = taskId,
                ["parent_id"] = null,
                ["argsrepr"] = ArgsRepr(args),
                ["kwargsrepr"] = "{}",
                ["origin"] = origin,
                ["ignore_result"] = true,
            },
            ["properties"] = new JsonObject
            {
                ["correlation_id"] = taskId,
                ["reply_to"] = "",
                ["delivery_mode"] = 2,
                ["delivery_info"] = new JsonObject { ["exchange"] = "", ["routing_key"] = queue },
                ["priority"] = 0,
                ["body_encoding"] = "base64",
                ["delivery_tag"] = Guid.NewGuid().ToString(),
            },
        };
        return envelope.ToJsonString();
    }

    /// <summary>The message for <c>receipts.tasks.extract_scan(scan_id)</c>.</summary>
    public static string ExtractScan(long scanId, string queue, string taskId, string origin) =>
        Build("receipts.tasks.extract_scan", new JsonArray(scanId), queue, taskId, origin);

    /// <summary>Python repr of the args tuple, for logs and Flower: "(5,)".</summary>
    private static string ArgsRepr(JsonArray args) =>
        args.Count == 1 ? $"({args[0]?.ToJsonString()},)" : $"({string.Join(", ", args.Select(a => a?.ToJsonString() ?? "None"))})";

    /// <summary>Decode a message's body back to [args, kwargs, embed]. For tests and debugging.</summary>
    public static JsonArray DecodeBody(string message)
    {
        var envelope = JsonNode.Parse(message)!.AsObject();
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(envelope["body"]!.GetValue<string>()));
        return JsonNode.Parse(json)!.AsArray();
    }
}
