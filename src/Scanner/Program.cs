using SkiaSharp;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorPages();

var app = builder.Build();

app.MapRazorPages();

app.MapGet("/api/images", (IConfiguration config) =>
{
    var imageDirectory = config["ImageDirectory"] ?? Environment.GetEnvironmentVariable("IMAGES_DIR") ?? string.Empty;
    if (string.IsNullOrEmpty(imageDirectory) || !Directory.Exists(imageDirectory))
    {
        return Results.Ok(Array.Empty<string>());
    }

    var images = Directory.EnumerateFiles(imageDirectory)
        .Where(f => Path.GetExtension(f).Equals(".png", StringComparison.OrdinalIgnoreCase))
        .Select(Path.GetFileName)
        .ToArray();
    return Results.Ok(images);
});

app.MapGet("/api/images/{fileName}/thumbnail", (string fileName, int? rotation, IConfiguration config) =>
{
    rotation ??= 0;
    var imageDirectory = config["ImageDirectory"] ?? Environment.GetEnvironmentVariable("IMAGES_DIR") ?? string.Empty;
    if (string.IsNullOrEmpty(imageDirectory))
    {
        return Results.NotFound();
    }

    // Prevent path traversal
    if (fileName.Contains("..") || Path.GetFileName(fileName) != fileName)
    {
        return Results.BadRequest();
    }

    // Validate rotation
    if (rotation != 0 && rotation != 90 && rotation != 180 && rotation != 270)
    {
        return Results.BadRequest();
    }

    var filePath = Path.Combine(imageDirectory, fileName);
    if (!File.Exists(filePath))
    {
        return Results.NotFound();
    }

    using var bitmap = SKBitmap.Decode(filePath);
    if (bitmap is null)
    {
        return Results.StatusCode(500);
    }

    const int thumbSize = 400;
    var ratioX = (double)thumbSize / bitmap.Width;
    var ratioY = (double)thumbSize / bitmap.Height;
    var ratio = Math.Min(ratioX, ratioY);
    var newWidth = (int)(bitmap.Width * ratio);
    var newHeight = (int)(bitmap.Height * ratio);

    using var resized = bitmap.Resize(new SKImageInfo(newWidth, newHeight), SKSamplingOptions.Default);

    SKBitmap finalBitmap;
    if (rotation == 0)
    {
        finalBitmap = resized;
    }
    else
    {
        var rotatedInfo = rotation is 90 or 270
            ? new SKImageInfo(resized.Height, resized.Width)
            : new SKImageInfo(resized.Width, resized.Height);
        finalBitmap = new SKBitmap(rotatedInfo);
        using var canvas = new SKCanvas(finalBitmap);
        canvas.Translate(rotatedInfo.Width / 2f, rotatedInfo.Height / 2f);
        canvas.RotateDegrees(rotation.Value);
        canvas.Translate(-resized.Width / 2f, -resized.Height / 2f);
        canvas.DrawBitmap(resized, 0, 0);
    }

    using var image = SKImage.FromBitmap(finalBitmap);
    using var data = image.Encode(SKEncodedImageFormat.Jpeg, 80);

    if (rotation != 0)
    {
        finalBitmap.Dispose();
    }

    return Results.File(data.ToArray(), "image/jpeg");
});

app.Run();
