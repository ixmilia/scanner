using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Scanner.Tests;

public class BasicTests
{
    [Fact]
    public async Task GetRoot_ReturnsHelloWorld()
    {
        await using var application = new WebApplicationFactory<Program>();
        using var client = application.CreateClient();

        var response = await client.GetAsync("/", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Hello, World!", content);
    }
}
