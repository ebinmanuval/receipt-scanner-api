using Microsoft.AspNetCore.Http;
using ReceiptScanner.Api.Errors;
using ReceiptScanner.Api.Pagination;

namespace ReceiptScanner.Tests.Api;

public class PaginationTests
{
    [Theory]
    [InlineData(286, null, null, 1, 20, 15)]
    [InlineData(286, "15", null, 15, 20, 15)]
    [InlineData(286, "last", null, 15, 20, 15)]
    [InlineData(286, "2", "50", 2, 50, 6)]
    [InlineData(286, "1", "500", 1, 100, 3)]     // capped at 100
    [InlineData(286, "1", "0", 1, 20, 15)]       // not positive → default
    [InlineData(286, "1", "abc", 1, 20, 15)]
    [InlineData(0, null, null, 1, 20, 1)]        // an empty list still has page 1
    public void Resolves_like_DRF(int count, string? page, string? size, int number, int pageSize, int pages)
    {
        var w = PageWindow.Resolve(count, page, size);
        Assert.Equal(number, w.Number);
        Assert.Equal(pageSize, w.Size);
        Assert.Equal(pages, w.Pages);
    }

    [Theory]
    [InlineData("16")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("two")]
    [InlineData("2.0")]
    public void A_page_past_the_end_or_not_an_integer_is_404(string page)
    {
        var ex = Assert.Throws<ApiException>(() => PageWindow.Resolve(286, page, null));
        Assert.Equal(404, ex.StatusCode);
        Assert.Equal("not_found", ex.Code);
    }

    [Fact]
    public void Builds_links_keeping_filters_and_dropping_page_for_page_one()
    {
        var http = new DefaultHttpContext();
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("accounts.ebinmanuval.in");
        http.Request.Path = "/api/receipts/list/";
        http.Request.QueryString = new QueryString("?shop=coles&page=2");

        var page = Paginator.Build(PageWindow.Resolve(286, "2", null), Array.Empty<int>(), http.Request);

        Assert.Equal("https://accounts.ebinmanuval.in/api/receipts/list/?shop=coles&page=3", page.Next);
        Assert.Equal("https://accounts.ebinmanuval.in/api/receipts/list/?shop=coles", page.Previous);
        Assert.True(page.HasNext);
        Assert.True(page.HasPrevious);
    }
}
