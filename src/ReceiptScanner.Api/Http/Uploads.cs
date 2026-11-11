using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.StaticFiles;

namespace ReceiptScanner.Api.Http;

/// <summary>Request and response helpers shared by the receipt and scan endpoints.</summary>
public static class Uploads
{
    // Names a client might use for the photo (views.UPLOAD_FIELD_PREFIXES).
    private static readonly string[] Prefixes = ["image", "photo", "file", "receipt", "picture", "scan"];

    /// <summary>
    /// The uploaded photo, whatever the client called it. iOS Shortcuts
    /// appends the filename to the field name ("imageIMG_7020"), so match by
    /// prefix, then fall back to the only file present (views.find_upload).
    /// </summary>
    public static IFormFile? Find(IFormFileCollection files)
    {
        if (files.Count == 0) return null;

        foreach (var name in Prefixes)
            if (files.GetFile(name) is { } exact) return exact;

        foreach (var file in files)
            if (Prefixes.Any(p => file.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase))) return file;

        return files.Count == 1 ? files[0] : null;
    }

    public static async Task<byte[]> ReadAllAsync(IFormFile file, CancellationToken ct)
    {
        using var buffer = new MemoryStream((int)Math.Min(file.Length, int.MaxValue));
        await file.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }

    /// <summary>The request body as JSON. Empty body → null. Throws JsonException on bad JSON.</summary>
    public static async Task<JsonNode?> ReadJsonAsync(HttpRequest request, CancellationToken ct)
    {
        using var reader = new StreamReader(request.Body);
        var text = await reader.ReadToEndAsync(ct);
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }

    /// <summary>
    /// A JSON object body, a form as an object of strings, or {} when there's
    /// no body or it isn't an object (as DRF's request.data does). Bad JSON is
    /// 400 malformed_payload.
    /// </summary>
    public static async Task<JsonObject> ReadObjectAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.HasFormContentType)
        {
            var form = await request.ReadFormAsync(ct);
            var obj = new JsonObject();
            foreach (var (key, value) in form) obj[key] = value.ToString();
            return obj;
        }
        try
        {
            return await ReadJsonAsync(request, ct) as JsonObject ?? new JsonObject();
        }
        catch (JsonException ex)
        {
            throw Errors.ApiException.BadRequest("malformed_payload", $"Body is not valid JSON: {ex.Message}");
        }
    }

    /// <summary>A string field from a JSON object, or null. Numbers come back as their text.</summary>
    public static string? Text(JsonObject body, string key) => body[key] switch
    {
        JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
        JsonValue v when v.GetValueKind() is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.ToJsonString(),
        _ => null,
    };

    /// <summary>Python truthiness of a form/JSON flag: "1", "true", "yes", "on" (any case), or true.</summary>
    public static bool IsTruthy(JsonNode? node) => node switch
    {
        JsonValue v when v.GetValueKind() == JsonValueKind.True => true,
        JsonValue v when v.GetValueKind() is JsonValueKind.String or JsonValueKind.Number =>
            (v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : v.ToJsonString()).Trim().ToLowerInvariant()
                is "1" or "true" or "yes" or "on",
        _ => false,
    };

    private static readonly FileExtensionContentTypeProvider ContentTypes = new()
    {
        Mappings = { [".heic"] = "image/heic", [".heif"] = "image/heif", [".avif"] = "image/avif" },
    };

    public static string ContentTypeFor(string path) =>
        ContentTypes.TryGetContentType(path, out var type) ? type : "application/octet-stream";
}
