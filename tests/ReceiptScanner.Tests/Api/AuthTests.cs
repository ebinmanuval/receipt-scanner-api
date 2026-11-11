using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace ReceiptScanner.Tests.Api;

/// <summary>Phase 1: the auth endpoints against API.md#authentication.</summary>
public class AuthTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private static StringContent Json(object body) =>
        new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private static async Task<JsonElement> Body(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();

    private async Task<JsonElement> LoginAsync(string username, string password = ApiFactory.Password)
    {
        var r = await _client.PostAsync("/api/auth/login/", Json(new { username, password }));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await Body(r);
    }

    private HttpRequestMessage Authed(HttpMethod method, string url, string access, object? body = null)
    {
        var req = new HttpRequestMessage(method, url) { Content = body is null ? null : Json(body) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        return req;
    }

    private static void AssertError(JsonElement body, string code)
    {
        Assert.Equal("error", body.GetProperty("status").GetString());
        Assert.Equal(code, body.GetProperty("code").GetString());
        Assert.True(body.TryGetProperty("message", out _));
        Assert.True(body.TryGetProperty("errors", out _));
    }

    // --- login ----------------------------------------------------------------

    [Fact]
    public async Task Login_returns_the_API_md_shape()
    {
        var user = await factory.AddUserAsync("alice");

        var body = await LoginAsync("alice");

        Assert.Equal("success", body.GetProperty("status").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("access").GetString()));
        Assert.False(string.IsNullOrEmpty(body.GetProperty("refresh").GetString()));
        Assert.Equal(3600, body.GetProperty("access_expires_in").GetInt32());
        Assert.Equal(2592000, body.GetProperty("refresh_expires_in").GetInt32());
        Assert.Equal("Bearer", body.GetProperty("token_type").GetString());

        var u = body.GetProperty("user");
        Assert.Equal(user.Id, u.GetProperty("id").GetInt32());
        Assert.Equal("alice", u.GetProperty("username").GetString());
        Assert.Equal("alice@example.com", u.GetProperty("email").GetString());
        Assert.Equal("", u.GetProperty("first_name").GetString());
        Assert.Equal("", u.GetProperty("last_name").GetString());
        Assert.True(u.GetProperty("is_staff").GetBoolean());
        Assert.Equal(6, u.EnumerateObject().Count());   // nothing else leaks (no password, no is_superuser)
    }

    [Fact]
    public async Task Login_sets_last_login_and_upgrades_an_old_hash()
    {
        var user = await factory.AddUserAsync("bob");   // 1000 iterations

        await LoginAsync("bob");

        var stored = await factory.WithDbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id));
        Assert.NotNull(stored.LastLogin);
        Assert.StartsWith("pbkdf2_sha256$870000$", stored.Password);
    }

    [Fact]
    public async Task A_hash_made_by_Django_logs_in()
    {
        // Generated with Python: hashlib.pbkdf2_hmac('sha256', 'pässwörd', 'abc123SALTxyz', 1000)
        await factory.AddUserAsync("django-user",
            hash: "pbkdf2_sha256$1000$abc123SALTxyz$LLucqgHKVOwi51DCM3mlQHCIMMD6AuTghk/8ofAInD0=");

        await LoginAsync("django-user", "pässwörd");
    }

    [Theory]
    [InlineData("carol", "wrong password")]
    [InlineData("nobody-by-this-name", ApiFactory.Password)]
    public async Task Wrong_password_and_unknown_user_look_the_same(string username, string password)
    {
        await factory.AddUserAsync("carol");

        var r = await _client.PostAsync("/api/auth/login/", Json(new { username, password }));

        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        var body = await Body(r);
        AssertError(body, "invalid_credentials");
        Assert.Equal("Incorrect username or password.", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task An_inactive_user_cannot_log_in()
    {
        await factory.AddUserAsync("dave", active: false);

        var r = await _client.PostAsync("/api/auth/login/", Json(new { username = "dave", password = ApiFactory.Password }));

        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        AssertError(await Body(r), "invalid_credentials");
    }

    [Fact]
    public async Task Missing_fields_are_400_with_per_field_errors()
    {
        var r = await _client.PostAsync("/api/auth/login/", Json(new { username = "  " }));

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        var body = await Body(r);
        AssertError(body, "validation_error");
        Assert.Equal("This field may not be blank.", body.GetProperty("errors").GetProperty("username")[0].GetString());
        Assert.Equal("This field is required.", body.GetProperty("errors").GetProperty("password")[0].GetString());
    }

    // --- me / bearer ------------------------------------------------------------

    [Fact]
    public async Task Me_without_a_token_is_401_not_authenticated()
    {
        var r = await _client.GetAsync("/api/auth/me/");

        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        AssertError(await Body(r), "not_authenticated");
        Assert.Contains("Bearer", r.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task Me_with_an_access_token_returns_the_user()
    {
        await factory.AddUserAsync("erin");
        var login = await LoginAsync("erin");

        var r = await _client.SendAsync(Authed(HttpMethod.Get, "/api/auth/me/", login.GetProperty("access").GetString()!));

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await Body(r);
        Assert.Equal("success", body.GetProperty("status").GetString());
        Assert.Equal("erin", body.GetProperty("user").GetProperty("username").GetString());
    }

    [Theory]
    [InlineData("not-a-jwt")]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJ0b2tlbl90eXBlIjoiYWNjZXNzIiwidXNlcl9pZCI6MX0.c2lnbmF0dXJl")]
    public async Task A_bad_token_is_401_not_authenticated(string token)
    {
        var r = await _client.SendAsync(Authed(HttpMethod.Get, "/api/auth/me/", token));

        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        AssertError(await Body(r), "not_authenticated");
    }

    [Fact]
    public async Task A_refresh_token_is_not_accepted_as_a_bearer_token()
    {
        await factory.AddUserAsync("frank");
        var login = await LoginAsync("frank");

        var r = await _client.SendAsync(Authed(HttpMethod.Get, "/api/auth/me/", login.GetProperty("refresh").GetString()!));

        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task A_user_deactivated_after_login_is_locked_out()
    {
        var user = await factory.AddUserAsync("gina");
        var login = await LoginAsync("gina");
        await factory.WithDbAsync(db => db.Users.Where(u => u.Id == user.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false)));

        var r = await _client.SendAsync(Authed(HttpMethod.Get, "/api/auth/me/", login.GetProperty("access").GetString()!));

        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    // --- refresh ------------------------------------------------------------------

    [Fact]
    public async Task Refresh_rotates_and_the_old_token_stops_working()
    {
        await factory.AddUserAsync("henry");
        var login = await LoginAsync("henry");
        var oldRefresh = login.GetProperty("refresh").GetString()!;

        var first = await _client.PostAsync("/api/auth/refresh/", Json(new { refresh = oldRefresh }));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var body = await Body(first);
        Assert.Equal("success", body.GetProperty("status").GetString());
        Assert.Equal("Bearer", body.GetProperty("token_type").GetString());
        var newRefresh = body.GetProperty("refresh").GetString()!;
        Assert.NotEqual(oldRefresh, newRefresh);

        // The new access token works.
        var me = await _client.SendAsync(Authed(HttpMethod.Get, "/api/auth/me/", body.GetProperty("access").GetString()!));
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);

        // Reusing the old refresh token fails...
        var reuse = await _client.PostAsync("/api/auth/refresh/", Json(new { refresh = oldRefresh }));
        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
        AssertError(await Body(reuse), "token_not_valid");

        // ...and the new one still works.
        var again = await _client.PostAsync("/api/auth/refresh/", Json(new { refresh = newRefresh }));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    [Fact]
    public async Task An_access_token_cannot_be_used_to_refresh()
    {
        await factory.AddUserAsync("ivy");
        var login = await LoginAsync("ivy");

        var r = await _client.PostAsync("/api/auth/refresh/", Json(new { refresh = login.GetProperty("access").GetString() }));

        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        AssertError(await Body(r), "token_not_valid");
    }

    [Fact]
    public async Task Refresh_without_a_token_is_400()
    {
        var r = await _client.PostAsync("/api/auth/refresh/", Json(new { }));

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        AssertError(await Body(r), "missing_refresh_token");
    }

    // --- logout -------------------------------------------------------------------

    [Fact]
    public async Task Logout_revokes_the_refresh_token()
    {
        await factory.AddUserAsync("jack");
        var login = await LoginAsync("jack");
        var access = login.GetProperty("access").GetString()!;
        var refresh = login.GetProperty("refresh").GetString()!;

        var r = await _client.SendAsync(Authed(HttpMethod.Post, "/api/auth/logout/", access, new { refresh }));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await Body(r);
        Assert.Equal("success", body.GetProperty("status").GetString());
        Assert.Equal("Logged out.", body.GetProperty("message").GetString());

        var after = await _client.PostAsync("/api/auth/refresh/", Json(new { refresh }));
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);

        // Logging out twice is still a success.
        var twice = await _client.SendAsync(Authed(HttpMethod.Post, "/api/auth/logout/", access, new { refresh }));
        Assert.Equal(HttpStatusCode.OK, twice.StatusCode);
    }

    [Fact]
    public async Task Logout_cannot_revoke_someone_elses_token()
    {
        await factory.AddUserAsync("kate");
        await factory.AddUserAsync("liam");
        var kate = await LoginAsync("kate");
        var liam = await LoginAsync("liam");

        await _client.SendAsync(Authed(HttpMethod.Post, "/api/auth/logout/", kate.GetProperty("access").GetString()!,
            new { refresh = liam.GetProperty("refresh").GetString() }));

        var r = await _client.PostAsync("/api/auth/refresh/", Json(new { refresh = liam.GetProperty("refresh").GetString() }));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Logout_needs_an_access_token_and_a_refresh_token()
    {
        var anonymous = await _client.PostAsync("/api/auth/logout/", Json(new { refresh = "x" }));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        await factory.AddUserAsync("mia");
        var login = await LoginAsync("mia");
        var missing = await _client.SendAsync(Authed(HttpMethod.Post, "/api/auth/logout/", login.GetProperty("access").GetString()!, new { }));
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        AssertError(await Body(missing), "missing_refresh_token");
    }

    [Fact]
    public async Task Health_stays_open()
    {
        var r = await _client.GetAsync("/api/health/");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }
}
