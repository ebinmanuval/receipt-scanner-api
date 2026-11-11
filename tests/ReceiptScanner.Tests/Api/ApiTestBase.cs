using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ImageMagick;

namespace ReceiptScanner.Tests.Api;

/// <summary>A signed-in client plus small helpers for the API tests.</summary>
public abstract class ApiTestBase(ApiFactory factory) : IClassFixture<ApiFactory>
{
    protected ApiFactory Factory { get; } = factory;
    protected HttpClient Anonymous { get; } = factory.CreateClient();

    private HttpClient? _client;

    /// <summary>A client with a Bearer token for a staff user.</summary>
    protected async Task<HttpClient> ClientAsync()
    {
        if (_client is not null) return _client;
        await Factory.AddUserAsync("tester");
        var login = await Anonymous.PostAsync("/api/auth/login/", Json(new { username = "tester", password = ApiFactory.Password }));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var access = (await Body(login)).GetProperty("access").GetString();

        _client = Factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", access);
        return _client;
    }

    protected static StringContent Json(object body) =>
        new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    protected static StringContent RawJson(string json) => new(json, Encoding.UTF8, "application/json");

    protected static async Task<JsonElement> Body(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();

    protected static async Task<JsonElement> Expect(HttpResponseMessage r, HttpStatusCode status)
    {
        var text = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == status, $"Expected {(int)status}, got {(int)r.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>
    /// "private, max-age=31536000, immutable". Checked by meaning: HttpClient
    /// parses Cache-Control and prints the parts back in its own order.
    /// </summary>
    protected static void AssertLongPrivateCache(HttpResponseMessage r)
    {
        var cc = r.Headers.CacheControl;
        Assert.NotNull(cc);
        Assert.True(cc.Private);
        Assert.Equal(TimeSpan.FromSeconds(31536000), cc.MaxAge);
        Assert.Contains(cc.Extensions, e => e.Name == "immutable");
    }

    protected static void AssertError(JsonElement body, string code) =>
        Assert.Equal(code, body.GetProperty("code").GetString());

    /// <summary>A real image, encoded by the same library the app decodes with.</summary>
    protected static byte[] MakeImage(int width = 40, int height = 30, MagickFormat format = MagickFormat.Jpeg, string color = "red")
    {
        using var image = new MagickImage(new MagickColor(color), (uint)width, (uint)height);
        image.Format = format;
        return image.ToByteArray();
    }

    protected static MultipartFormDataContent Form(byte[]? image = null, string fileName = "IMG_0001.jpg",
        string field = "image", params (string Key, string Value)[] fields)
    {
        var form = new MultipartFormDataContent();
        if (image is not null)
        {
            var content = new ByteArrayContent(image);
            content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            form.Add(content, field, fileName);
        }
        foreach (var (key, value) in fields) form.Add(new StringContent(value), key);
        return form;
    }
}
