using ReceiptScanner.Api.Errors;

namespace ReceiptScanner.Tests.Api;

public class ErrorFieldNameTests
{
    [Theory]
    [InlineData("$.total", "total")]
    [InlineData("$.items[0].Qty", "items[0].qty")]
    [InlineData("ClientUuid", "client_uuid")]
    [InlineData("", "non_field_errors")]
    [InlineData("$", "non_field_errors")]
    public void Model_state_keys_become_api_field_names(string key, string expected) =>
        Assert.Equal(expected, ApiErrors.FieldName(key));
}
