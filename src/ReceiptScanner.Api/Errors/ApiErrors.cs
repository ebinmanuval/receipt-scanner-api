using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using ReceiptScanner.Api.Json;

namespace ReceiptScanner.Api.Errors;

/// <summary>
/// The one error shape (API.md#error-handling):
/// <c>{"status":"error","code":"…","message":"…","errors":{field:[strings]}|null}</c>.
/// <see cref="Extra"/> adds context fields alongside, e.g. the existing
/// receipt's id on a duplicate. Clients can ignore them.
/// </summary>
public sealed class ErrorEnvelope
{
    public string Status => "error";
    public required string Code { get; init; }
    public required string Message { get; init; }
    public IDictionary<string, string[]>? Errors { get; init; }

    [JsonExtensionData]
    public IDictionary<string, object?>? Extra { get; init; }
}

/// <summary>Throw from anywhere to return an error in the standard shape.</summary>
public class ApiException(
    int statusCode,
    string code,
    string message,
    IDictionary<string, string[]>? errors = null,
    IDictionary<string, object?>? extra = null) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
    public IDictionary<string, string[]>? Errors { get; } = errors;
    public IDictionary<string, object?>? Extra { get; } = extra;

    public static ApiException NotFound(string message = "Not found.") =>
        new(StatusCodes.Status404NotFound, "not_found", message);

    public static ApiException Validation(IDictionary<string, string[]> errors, string? message = null) =>
        new(StatusCodes.Status422UnprocessableEntity, "validation_error",
            message ?? ApiErrors.FirstMessage(errors) ?? ApiErrors.FallbackMessage(422), errors);

    public static ApiException BadRequest(string code, string message) =>
        new(StatusCodes.Status400BadRequest, code, message);

    public static ApiException Conflict(string code, string message, IDictionary<string, object?>? extra = null) =>
        new(StatusCodes.Status409Conflict, code, message, extra: extra);
}

public static class ApiErrors
{
    // Ported from config/api.py.
    private static readonly Dictionary<int, string> Fallback = new()
    {
        [400] = "The request was malformed.",
        [401] = "Authentication is required.",
        [403] = "You don't have permission to do that.",
        [404] = "Not found.",
        [405] = "That method isn't allowed here.",
        [413] = "That upload is too large.",
        [415] = "Unsupported media type.",
        [422] = "Some fields didn't validate.",
        [429] = "Too many requests — slow down.",
        [500] = "Something went wrong on the server.",
    };

    private static readonly Dictionary<int, string> Codes = new()
    {
        [400] = "bad_request",
        [401] = "not_authenticated",
        [403] = "permission_denied",
        [404] = "not_found",
        [405] = "method_not_allowed",
        [406] = "not_acceptable",
        [413] = "request_entity_too_large",
        [415] = "unsupported_media_type",
        [422] = "validation_error",
        [429] = "throttled",
        [500] = "server_error",
    };

    public static string FallbackMessage(int status) => Fallback.GetValueOrDefault(status, "Something went wrong.");
    public static string CodeFor(int status) => Codes.GetValueOrDefault(status, "error");

    /// <summary>"field: first message", or the bare message for non-field errors.</summary>
    public static string? FirstMessage(IDictionary<string, string[]> errors)
    {
        foreach (var (field, messages) in errors)
            if (messages.Length > 0)
                return field == "non_field_errors" ? messages[0] : $"{field}: {messages[0]}";
        return null;
    }

    public static Task WriteAsync(HttpContext http, int status, ErrorEnvelope body)
    {
        http.Response.StatusCode = status;
        http.Response.ContentType = "application/json";
        return http.Response.WriteAsync(JsonSerializer.Serialize(body, ApiJsonOptions.Shared));
    }

    public static Task WriteAsync(HttpContext http, int status, string? code = null, string? message = null) =>
        WriteAsync(http, status, new ErrorEnvelope
        {
            Code = code ?? CodeFor(status),
            Message = message ?? FallbackMessage(status),
        });

    /// <summary>
    /// [ApiController] model-state failures. A body that couldn't be parsed at
    /// all is 400 malformed_payload; one that parsed but didn't validate is
    /// 422 validation_error with snake_case field names.
    /// </summary>
    public static IActionResult FromModelState(ActionContext context)
    {
        var state = context.ModelState;
        // "$" is where System.Text.Json reports a body it couldn't read as the
        // expected object at all (bad syntax at the root, empty body, an array
        // where an object was expected). Errors at "$.field" are per-field.
        var malformed = state.TryGetValue("$", out var root) && root.Errors.Count > 0
            || state.Values.SelectMany(v => v.Errors)
                .Any(e => e.ErrorMessage.StartsWith("A non-empty request body is required", StringComparison.Ordinal));

        // When the body fails to deserialise, MVC also reports the action
        // parameter itself as "required". That's noise next to the real
        // field error, so drop it whenever a field error exists.
        var parameterNames = context.ActionDescriptor.Parameters.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hasFieldErrors = state.Any(kv => kv.Value!.ValidationState == ModelValidationState.Invalid && !parameterNames.Contains(kv.Key));

        var errors = new Dictionary<string, string[]>();
        foreach (var (key, entry) in state)
        {
            if (entry.ValidationState != ModelValidationState.Invalid) continue;
            if (hasFieldErrors && parameterNames.Contains(key)) continue;
            var field = FieldName(key);
            var messages = entry.Errors
                .Select(e => string.IsNullOrEmpty(e.ErrorMessage) ? e.Exception?.Message ?? "Invalid value." : e.ErrorMessage)
                .ToArray();
            errors[field] = errors.TryGetValue(field, out var existing) ? [.. existing, .. messages] : messages;
        }

        var status = malformed ? 400 : 422;
        var body = new ErrorEnvelope
        {
            Code = malformed ? "malformed_payload" : "validation_error",
            Message = malformed ? "The request body couldn't be parsed." : FirstMessage(errors) ?? FallbackMessage(422),
            Errors = malformed ? null : errors,
        };
        return new ObjectResult(body) { StatusCode = status };
    }

    /// <summary>"$.items[0].Qty" / "Items[0].Qty" → "items[0].qty"; "" or "$" → non_field_errors.</summary>
    internal static string FieldName(string key)
    {
        var k = key.StartsWith("$.", StringComparison.Ordinal) ? key[2..] : key;
        if (k.Length == 0 || k == "$") return "non_field_errors";
        return string.Join('.', k.Split('.').Select(part =>
        {
            var bracket = part.IndexOf('[');
            var name = bracket < 0 ? part : part[..bracket];
            var rest = bracket < 0 ? "" : part[bracket..];
            return JsonNamingPolicy.SnakeCaseLower.ConvertName(name) + rest;
        }));
    }
}

internal static class ApiJsonOptions
{
    public static readonly JsonSerializerOptions Shared = ApiJson.Create();
}

/// <summary>
/// Turns every exception into the standard envelope. <see cref="ApiException"/>
/// carries its own status and code; anything else is a logged 500.
/// </summary>
public sealed class ApiErrorMiddleware(RequestDelegate next, ILogger<ApiErrorMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext http)
    {
        try
        {
            await next(http);
        }
        catch (ApiException ex) when (!http.Response.HasStarted)
        {
            await ApiErrors.WriteAsync(http, ex.StatusCode, new ErrorEnvelope
            {
                Code = ex.Code,
                Message = ex.Message,
                Errors = ex.Errors,
                Extra = ex.Extra,
            });
        }
        catch (BadHttpRequestException ex) when (!http.Response.HasStarted)
        {
            // Kestrel limits: body too large, bad framing, etc. Every upload in
            // this API is a photo, so a body over the limit is image_too_large
            // (API.md: "downscale and retry").
            var status = ex.StatusCode;
            var code = status == StatusCodes.Status413PayloadTooLarge ? "image_too_large" : ApiErrors.CodeFor(status);
            await ApiErrors.WriteAsync(http, status, code, ApiErrors.FallbackMessage(status));
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
            // Client went away; nothing to send.
        }
        catch (Exception ex) when (!http.Response.HasStarted)
        {
            logger.LogError(ex, "Unhandled exception for {Method} {Path}", http.Request.Method, http.Request.Path);
            await ApiErrors.WriteAsync(http, 500, "server_error", ApiErrors.FallbackMessage(500));
        }
    }
}

/// <summary>
/// Bodyless 4xx/5xx responses (unknown route, wrong method, auth challenge)
/// get the envelope too, so a client never has to handle a second shape.
/// </summary>
public static class ApiStatusCodePages
{
    public static Task WriteAsync(StatusCodeContext context) =>
        ApiErrors.WriteAsync(context.HttpContext, context.HttpContext.Response.StatusCode);
}
