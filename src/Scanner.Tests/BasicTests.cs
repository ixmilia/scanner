using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using Xunit;

namespace Scanner.Tests;

public class IntegrationTests : IDisposable
{
    private readonly string _imageDir;

    public IntegrationTests()
    {
        _imageDir = Path.Combine(Path.GetTempPath(), $"scanner-tests-{Guid.NewGuid()}");
        Directory.CreateDirectory(_imageDir);
        CreateTestPng("test.png", 100, 100);
    }

    public void Dispose()
    {
        if (Directory.Exists(_imageDir))
            Directory.Delete(_imageDir, true);
    }

    private WebApplicationFactory<Program> CreateFactory(string? imageDir = null)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ImageDirectory"] = imageDir ?? _imageDir
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.Configure<RazorPagesOptions>(options =>
                {
                    options.Conventions.ConfigureFilter(new IgnoreAntiforgeryTokenAttribute());
                });
            });
        });
    }

    private void CreateTestPng(string name, int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        using var paint = new SKPaint { Color = SKColors.Black };
        canvas.DrawRect(10, 10, Math.Max(1, width - 20), Math.Max(1, height - 20), paint);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(Path.Combine(_imageDir, name), data.ToArray());
    }

    private static async Task<HttpResponseMessage> PostGeneratePdf(HttpClient client, object body)
    {
        return await client.PostAsJsonAsync("/?handler=GeneratePdf", body, TestContext.Current.CancellationToken);
    }

    private static async Task AssertIsPdfResponse(HttpResponseMessage response, string expectedFileName)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        var bytes = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        Assert.True(bytes.Length > 4);
        // PDF files start with "%PDF"
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        var disposition = response.Content.Headers.ContentDisposition;
        Assert.NotNull(disposition);
        Assert.Equal(expectedFileName, disposition.FileNameStar ?? disposition.FileName);
    }

    // ── Smoke ──

    [Fact]
    public async Task GetRoot_ReturnsOk()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── Input validation ──

    [Fact]
    public async Task GeneratePdf_EmptyImages_ReturnsBadRequest()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await PostGeneratePdf(client, new { fileName = "out", images = Array.Empty<object>() });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GeneratePdf_PathTraversal_ReturnsBadRequest()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await PostGeneratePdf(client, new
        {
            fileName = "out",
            images = new[] { new { fileName = "../secret.png", imageType = "photo", rotation = 0 } }
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GeneratePdf_NonPngExtension_ReturnsBadRequest()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await PostGeneratePdf(client, new
        {
            fileName = "out",
            images = new[] { new { fileName = "photo.jpg", imageType = "photo", rotation = 0 } }
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GeneratePdf_MissingFile_ReturnsBadRequest()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await PostGeneratePdf(client, new
        {
            fileName = "out",
            images = new[] { new { fileName = "nonexistent.png", imageType = "photo", rotation = 0 } }
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GeneratePdf_NoImageDirectory_ReturnsBadRequest()
    {
        await using var factory = CreateFactory(imageDir: Path.Combine(Path.GetTempPath(), "does-not-exist"));
        using var client = factory.CreateClient();

        var response = await PostGeneratePdf(client, new
        {
            fileName = "out",
            images = new[] { new { fileName = "test.png", imageType = "photo", rotation = 0 } }
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Successful PDF generation ──

    [Fact]
    public async Task GeneratePdf_PhotoType_ReturnsPdf()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await PostGeneratePdf(client, new
        {
            fileName = "scan",
            images = new[] { new { fileName = "test.png", imageType = "photo", rotation = 0 } }
        });

        await AssertIsPdfResponse(response, "scan.pdf");
    }

    [Fact]
    public async Task GeneratePdf_GrayscaleType_ReturnsPdf()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await PostGeneratePdf(client, new
        {
            fileName = "doc",
            images = new[] { new { fileName = "test.png", imageType = "grayscale", rotation = 0 } }
        });

        await AssertIsPdfResponse(response, "doc.pdf");
    }

    [Fact]
    public async Task GeneratePdf_DocumentType_ReturnsPdf()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await PostGeneratePdf(client, new
        {
            fileName = "doc",
            images = new[] { new { fileName = "test.png", imageType = "document", rotation = 0 } }
        });

        await AssertIsPdfResponse(response, "doc.pdf");
    }

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task GeneratePdf_WithRotation_ReturnsPdf(int rotation)
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await PostGeneratePdf(client, new
        {
            fileName = "rotated",
            images = new[] { new { fileName = "test.png", imageType = "photo", rotation } }
        });

        await AssertIsPdfResponse(response, "rotated.pdf");
    }

    [Fact]
    public async Task GeneratePdf_MultipleImages_ReturnsPdf()
    {
        CreateTestPng("page2.png", 80, 120);
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await PostGeneratePdf(client, new
        {
            fileName = "multi",
            images = new[]
            {
                new { fileName = "test.png", imageType = "photo", rotation = 0 },
                new { fileName = "page2.png", imageType = "document", rotation = 90 },
            }
        });

        await AssertIsPdfResponse(response, "multi.pdf");
    }

    // ── Images API ──

    [Fact]
    public async Task GetApiImages_ReturnsPngFileNames()
    {
        CreateTestPng("a.png", 10, 10);
        CreateTestPng("b.png", 10, 10);
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var files = await client.GetFromJsonAsync<string[]>("/api/images", TestContext.Current.CancellationToken);

        Assert.NotNull(files);
        Assert.Contains("a.png", files);
        Assert.Contains("b.png", files);
        Assert.Contains("test.png", files);
    }

    [Fact]
    public async Task GetApiImages_NoDirectory_ReturnsEmptyArray()
    {
        await using var factory = CreateFactory(imageDir: Path.Combine(Path.GetTempPath(), "does-not-exist"));
        using var client = factory.CreateClient();

        var files = await client.GetFromJsonAsync<string[]>("/api/images", TestContext.Current.CancellationToken);

        Assert.NotNull(files);
        Assert.Empty(files);
    }
}
