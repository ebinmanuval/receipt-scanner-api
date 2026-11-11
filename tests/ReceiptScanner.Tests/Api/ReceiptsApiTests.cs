using System.Net;
using System.Text.Json;
using ImageMagick;
using Microsoft.EntityFrameworkCore;
using ReceiptScanner.Infrastructure.Data.Entities;

namespace ReceiptScanner.Tests.Api;

/// <summary>Phase 2: POST/GET/DELETE /api/receipts/ against API.md.</summary>
public class ReceiptsApiTests(ApiFactory factory) : ApiTestBase(factory)
{
    private static object Coles(string? clientUuid = null) => new
    {
        shop = "Coles",
        total = 20.10,
        date = "20/12/2025",
        client_uuid = clientUuid,
        items = new object[]
        {
            new { item = "Bananas", qty = 1, price = 8.10 },
            new { item = "Milk", qty = "2", price = "12.00" },
        },
    };

    [Fact]
    public async Task Create_returns_the_API_md_shape()
    {
        var client = await ClientAsync();

        var body = await Expect(await client.PostAsync("/api/receipts/", Json(Coles())), HttpStatusCode.Created);

        Assert.Equal("success", body.GetProperty("status").GetString());
        Assert.Equal(1, body.GetProperty("receipt_rows").GetInt32());
        Assert.Equal(2, body.GetProperty("item_rows").GetInt32());
        Assert.False(body.GetProperty("image_saved").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("image_note").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("bank_match").ValueKind);

        var totals = body.GetProperty("totals");
        Assert.Equal("20.10", totals.GetProperty("stated").GetString());
        Assert.Equal("20.10", totals.GetProperty("items_sum").GetString());
        Assert.Equal("0.00", totals.GetProperty("discrepancy").GetString());
        Assert.True(totals.GetProperty("reconciles").GetBoolean());

        var receipt = body.GetProperty("receipt");
        Assert.Equal(body.GetProperty("receipt_id").GetInt64(), receipt.GetProperty("id").GetInt64());
        Assert.Equal("20.10", receipt.GetProperty("total").GetString());
        Assert.Equal("2025-12-20", receipt.GetProperty("date").GetString());
        Assert.Equal(JsonValueKind.Null, receipt.GetProperty("image_url").ValueKind);
        Assert.Equal("", receipt.GetProperty("review_note").GetString());
        Assert.Equal(JsonValueKind.Null, receipt.GetProperty("category").ValueKind);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$", receipt.GetProperty("created_at").GetString());

        var milk = receipt.GetProperty("items")[1];
        Assert.Equal("Milk", milk.GetProperty("item").GetString());
        Assert.Equal("2.000", milk.GetProperty("qty").GetString());
        Assert.Equal("12.00", milk.GetProperty("price").GetString());
        Assert.Equal("6.0000", milk.GetProperty("unit_price").GetString());
    }

    [Fact]
    public async Task Items_that_dont_add_up_are_saved_and_flagged()
    {
        var client = await ClientAsync();
        var body = await Expect(await client.PostAsync("/api/receipts/", Json(new
        {
            shop = "Aldi", total = "84.04", date = "2026-01-02",
            items = new[] { new { item = "a", price = "50.00" }, new { item = "b", price = "43.38" } },
        })), HttpStatusCode.Created);

        var totals = body.GetProperty("totals");
        Assert.False(totals.GetProperty("reconciles").GetBoolean());
        Assert.Equal("Items sum to 93.38, total says 84.04 (9.34 more than the total).", totals.GetProperty("note").GetString());
        Assert.True(body.GetProperty("receipt").GetProperty("needs_review").GetBoolean());
    }

    [Fact]
    public async Task The_legacy_data_wrapper_with_code_fences_still_works()
    {
        var client = await ClientAsync();
        var inner = JsonSerializer.Serialize(Coles());
        var r = await client.PostAsync("/api/receipts/", Json(new { data = $"```json\n{inner}\n```" }));
        await Expect(r, HttpStatusCode.Created);
    }

    [Fact]
    public async Task The_shortcut_can_use_the_api_key_and_nothing_else_can()
    {
        var withKey = new HttpRequestMessage(HttpMethod.Post, "/api/receipts/") { Content = Json(Coles()) };
        withKey.Headers.Add("X-API-Key", ApiFactory.ApiKey);
        await Expect(await Anonymous.SendAsync(withKey), HttpStatusCode.Created);

        var wrongKey = new HttpRequestMessage(HttpMethod.Post, "/api/receipts/") { Content = Json(Coles()) };
        wrongKey.Headers.Add("X-API-Key", "nope");
        AssertError(await Expect(await Anonymous.SendAsync(wrongKey), HttpStatusCode.Unauthorized), "not_authenticated");

        AssertError(await Expect(await Anonymous.PostAsync("/api/receipts/", Json(Coles())), HttpStatusCode.Unauthorized), "not_authenticated");

        // The key opens only this endpoint.
        var list = new HttpRequestMessage(HttpMethod.Get, "/api/receipts/list/");
        list.Headers.Add("X-API-Key", ApiFactory.ApiKey);
        await Expect(await Anonymous.SendAsync(list), HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_repeated_client_uuid_is_409_duplicate_receipt()
    {
        var client = await ClientAsync();
        var uuid = Guid.NewGuid().ToString();
        await Expect(await client.PostAsync("/api/receipts/", Json(Coles(uuid))), HttpStatusCode.Created);

        var again = await Expect(await client.PostAsync("/api/receipts/", Json(Coles(uuid))), HttpStatusCode.Conflict);

        AssertError(again, "duplicate_receipt");
        Assert.Equal("receipt with this client uuid already exists.", again.GetProperty("errors").GetProperty("client_uuid")[0].GetString());
    }

    [Fact]
    public async Task Bad_fields_are_422_and_a_bad_body_is_400()
    {
        var client = await ClientAsync();

        var invalid = await Expect(await client.PostAsync("/api/receipts/", Json(new
        {
            shop = "Coles", total = "abc", date = "nope", items = new[] { new { item = "x", price = "zz" } },
        })), HttpStatusCode.UnprocessableEntity);
        AssertError(invalid, "validation_error");
        var errors = invalid.GetProperty("errors");
        Assert.Equal("Not a valid amount: 'abc'", errors.GetProperty("total")[0].GetString());
        Assert.StartsWith("Unrecognized date 'nope'", errors.GetProperty("date")[0].GetString());
        Assert.Equal("price: Not a valid amount: 'zz'", errors.GetProperty("items")[0].GetString());

        AssertError(await Expect(await client.PostAsync("/api/receipts/", RawJson("{bad")), HttpStatusCode.BadRequest), "malformed_payload");
        AssertError(await Expect(await client.PostAsync("/api/receipts/", Json(new { foo = 1 })), HttpStatusCode.BadRequest), "malformed_payload");
    }

    [Fact]
    public async Task Multipart_with_a_photo_stores_it_behind_the_token()
    {
        var client = await ClientAsync();
        var form = Form(MakeImage(), "receipt.jpg", "imageIMG_7020", ("data", JsonSerializer.Serialize(Coles())));

        var body = await Expect(await client.PostAsync("/api/receipts/", form), HttpStatusCode.Created);
        Assert.True(body.GetProperty("image_saved").GetBoolean());
        var url = body.GetProperty("receipt").GetProperty("image_url").GetString()!;
        Assert.EndsWith($"/api/receipts/{body.GetProperty("receipt_id").GetInt64()}/image/", url);

        var photo = await client.GetAsync(new Uri(url).PathAndQuery);
        Assert.Equal(HttpStatusCode.OK, photo.StatusCode);
        Assert.Equal("image/jpeg", photo.Content.Headers.ContentType?.MediaType);
        AssertLongPrivateCache(photo);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Anonymous.GetAsync(new Uri(url).PathAndQuery)).StatusCode);
    }

    [Fact]
    public async Task A_big_or_non_web_photo_becomes_a_2400px_jpeg()
    {
        var client = await ClientAsync();
        var png = MakeImage(3000, 1000, MagickFormat.Png);
        var form = Form(png, "big.png", fields: ("data", JsonSerializer.Serialize(Coles())));

        var body = await Expect(await client.PostAsync("/api/receipts/", form), HttpStatusCode.Created);
        Assert.Equal("PNG 3000x1000 → JPEG 2400x800", body.GetProperty("image_note").GetString());
    }

    [Fact]
    public async Task A_file_that_isnt_an_image_is_a_validation_error()
    {
        var client = await ClientAsync();
        var form = Form("not an image at all"u8.ToArray(), "x.jpg", fields: ("data", JsonSerializer.Serialize(Coles())));

        var body = await Expect(await client.PostAsync("/api/receipts/", form), HttpStatusCode.UnprocessableEntity);
        Assert.True(body.GetProperty("errors").TryGetProperty("image", out _));
    }

    [Fact]
    public async Task List_filters_and_paginates()
    {
        var client = await ClientAsync();
        await Expect(await client.PostAsync("/api/receipts/", Json(new
        {
            shop = "Woolworths Metro", total = "5.00", date = "2026-03-01",
            items = new[] { new { item = "Oat MILK 1L", price = "2.50" }, new { item = "milk chocolate", price = "2.50" } },
        })), HttpStatusCode.Created);

        // A receipt with two matching items comes back once.
        var byItem = await Expect(await client.GetAsync("/api/receipts/list/?item=MILK&since=2026-02-01"), HttpStatusCode.OK);
        Assert.Equal(1, byItem.GetProperty("count").GetInt32());
        Assert.Equal("Woolworths Metro", byItem.GetProperty("results")[0].GetProperty("shop").GetString());

        var byShop = await Expect(await client.GetAsync("/api/receipts/list/?shop=metro"), HttpStatusCode.OK);
        Assert.Equal(1, byShop.GetProperty("count").GetInt32());

        var q = await Expect(await client.GetAsync("/api/receipts/list/?q=chocolate"), HttpStatusCode.OK);
        Assert.Equal(1, q.GetProperty("count").GetInt32());

        var wildcard = await Expect(await client.GetAsync("/api/receipts/list/?shop=%25"), HttpStatusCode.OK);
        Assert.Equal(0, wildcard.GetProperty("count").GetInt32());   // % is literal, not "everything"

        var page = await Expect(await client.GetAsync("/api/receipts/list/?page_size=1"), HttpStatusCode.OK);
        Assert.Equal(1, page.GetProperty("results").GetArrayLength());
        Assert.True(page.GetProperty("count").GetInt32() >= 1);

        await Expect(await client.GetAsync("/api/receipts/list/?since=yesterday"), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Detail_and_delete()
    {
        var client = await ClientAsync();
        var id = (await Expect(await client.PostAsync("/api/receipts/", Json(Coles())), HttpStatusCode.Created))
            .GetProperty("receipt_id").GetInt64();

        // A scan pointing at it gets unlinked, not deleted.
        var scanId = await Factory.WithDbAsync(async db =>
        {
            var scan = new ReceiptScan { Image = "scans/x.jpg", Status = "accepted", ReceiptId = id };
            db.ReceiptScans.Add(scan);
            await db.SaveChangesAsync();
            return scan.Id;
        });

        var detail = await Expect(await client.GetAsync($"/api/receipts/{id}/"), HttpStatusCode.OK);
        Assert.Equal(id, detail.GetProperty("id").GetInt64());

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/receipts/{id}/")).StatusCode);
        AssertError(await Expect(await client.GetAsync($"/api/receipts/{id}/"), HttpStatusCode.NotFound), "not_found");

        var scanAfter = await Factory.WithDbAsync(db => db.ReceiptScans.AsNoTracking().SingleAsync(s => s.Id == scanId));
        Assert.Null(scanAfter.ReceiptId);
        Assert.Equal(0, await Factory.WithDbAsync(db => db.ReceiptItems.CountAsync(i => i.ReceiptId == id)));
    }
}
