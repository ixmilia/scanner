using IxMilia.Pdf;
using IxMilia.Pdf.Encoders;
using BitMiracle.LibTiff.Classic;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SkiaSharp;

namespace Scanner.Pages;

public class IndexModel : PageModel
{
    private const int Dpi = 300;
    private const double PageWidthInches = 8.5;
    private const double PageHeightInches = 11.0;

    private readonly IConfiguration _config;

    public IndexModel(IConfiguration config)
    {
        _config = config;
    }

    public void OnGet()
    {
    }

    public record SelectedImage(string FileName, string ImageType, int Rotation);

    public IActionResult OnPostGeneratePdf([FromBody] SelectedImage[] images)
    {
        if (images is null || images.Length == 0)
        {
            return BadRequest();
        }

        var fileNames = images.Select(i => i.FileName).ToArray();

        var imageDirectory = _config["ImageDirectory"] ?? Environment.GetEnvironmentVariable("IMAGES_DIR") ?? string.Empty;
        if (string.IsNullOrEmpty(imageDirectory) || !Directory.Exists(imageDirectory))
        {
            return BadRequest();
        }

        foreach (var fileName in fileNames)
        {
            // Prevent path traversal
            if (fileName.Contains("..") || Path.GetFileName(fileName) != fileName)
            {
                return BadRequest();
            }

            if (!Path.GetExtension(fileName).Equals(".png", StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest();
            }

            var filePath = Path.Combine(imageDirectory, fileName);
            if (!System.IO.File.Exists(filePath))
            {
                return BadRequest();
            }
        }

        var pdf = new PdfFile();
        foreach (var selectedImage in images)
        {
            var filePath = Path.Combine(imageDirectory, selectedImage.FileName);
            using var originalBitmap = SKBitmap.Decode(filePath);

            var cropWidth = Math.Min((int)(PageWidthInches * Dpi), originalBitmap.Width);
            var cropHeight = Math.Min((int)(PageHeightInches * Dpi), originalBitmap.Height);
            using var croppedBitmap = new SKBitmap(cropWidth, cropHeight);
            using (var canvas = new SKCanvas(croppedBitmap))
            {
                canvas.DrawBitmap(originalBitmap, SKRectI.Create(0, 0, cropWidth, cropHeight), new SKRect(0, 0, cropWidth, cropHeight));
            }

            using var bitmap = selectedImage.Rotation switch
            {
                90 => RotateBitmap(croppedBitmap, SKEncodedOrigin.RightTop),
                180 => RotateBitmap(croppedBitmap, SKEncodedOrigin.BottomRight),
                270 => RotateBitmap(croppedBitmap, SKEncodedOrigin.LeftBottom),
                _ => RotateBitmap(croppedBitmap, SKEncodedOrigin.TopLeft),
            };

            var targetWidth = bitmap.Width;
            var targetHeight = bitmap.Height;
            var isLandscape = selectedImage.Rotation is 90 or 270;
            var pageWidthInches = isLandscape ? PageHeightInches : PageWidthInches;
            var pageHeightInches = isLandscape ? PageWidthInches : PageHeightInches;

            using var trimmedImage = SKImage.FromBitmap(bitmap);

            PdfImageObject imageObject;
            if (selectedImage.ImageType is "document" or "fax")
            {
                var bwData = new byte[(targetWidth + 7) / 8 * targetHeight];
                using var trimmedBitmap = SKBitmap.FromImage(trimmedImage);
                var pixels = trimmedBitmap.GetPixelSpan();
                var bytesPerPixel = trimmedBitmap.BytesPerPixel;
                for (var y = 0; y < targetHeight; y++)
                {
                    for (var x = 0; x < targetWidth; x++)
                    {
                        var offset = (y * targetWidth + x) * bytesPerPixel;
                        var r = pixels[offset];
                        var g = pixels[offset + 1];
                        var b = pixels[offset + 2];
                        var luminance = (0.299 * r + 0.587 * g + 0.114 * b) / 255.0;
                        if (luminance >= 0.75)
                        {
                            var byteIndex = y * ((targetWidth + 7) / 8) + x / 8;
                            var bitIndex = 7 - (x % 8);
                            bwData[byteIndex] |= (byte)(1 << bitIndex);
                        }
                    }
                }

                IPdfEncoder encoder = selectedImage.ImageType == "fax"
                    ? new CcittGroup4Encoder(targetWidth, targetHeight)
                    : new FlateEncoder();
                imageObject = new PdfImageObject(targetWidth, targetHeight, PdfColorSpace.DeviceGray, 1, bwData, encoder);
            }
            else
            {
                using var jpegData = trimmedImage.Encode(SKEncodedImageFormat.Jpeg, 90);
                var jpegBytes = jpegData.ToArray();
                imageObject = new PdfImageObject(targetWidth, targetHeight, PdfColorSpace.DeviceRGB, 8, jpegBytes, new CustomPassThroughEncoder("DCTDecode"));
            }

            var page = new PdfPage(PdfMeasurement.Inches(pageWidthInches), PdfMeasurement.Inches(pageHeightInches));
            var pageWidthPoints = PdfMeasurement.Inches(pageWidthInches).AsPoints();
            var pageHeightPoints = PdfMeasurement.Inches(pageHeightInches).AsPoints();
            var transform = PdfMatrix.ScaleThenTranslate(pageWidthPoints, pageHeightPoints, 0, 0);
            page.Items.Add(new PdfImageItem(imageObject, transform));

            pdf.Pages.Add(page);
        }

        var ms = new MemoryStream();
        pdf.Save(ms);
        ms.Position = 0;

        return File(ms, "application/pdf", "scan.pdf");
    }

    private static SKBitmap RotateBitmap(SKBitmap original, SKEncodedOrigin origin)
    {
        var rotated = new SKBitmap(original.Info);
        using var canvas = new SKCanvas(rotated);
        canvas.Clear();

        switch (origin)
        {
            case SKEncodedOrigin.RightTop: // 90° clockwise
                rotated = new SKBitmap(original.Height, original.Width, original.ColorType, original.AlphaType);
                using (var c = new SKCanvas(rotated))
                {
                    c.Translate(rotated.Width, 0);
                    c.RotateDegrees(90);
                    c.DrawBitmap(original, 0, 0);
                }
                break;
            case SKEncodedOrigin.BottomRight: // 180°
                using (var c = new SKCanvas(rotated))
                {
                    c.Translate(rotated.Width, rotated.Height);
                    c.RotateDegrees(180);
                    c.DrawBitmap(original, 0, 0);
                }
                break;
            case SKEncodedOrigin.LeftBottom: // 270° clockwise
                rotated = new SKBitmap(original.Height, original.Width, original.ColorType, original.AlphaType);
                using (var c = new SKCanvas(rotated))
                {
                    c.Translate(0, rotated.Height);
                    c.RotateDegrees(270);
                    c.DrawBitmap(original, 0, 0);
                }
                break;
            default: // 0° — no rotation
                using (var c = new SKCanvas(rotated))
                {
                    c.DrawBitmap(original, 0, 0);
                }
                break;
        }

        return rotated;
    }

    private sealed class CcittGroup4Encoder : IPdfEncoder
    {
        private readonly int _width;
        private readonly int _height;

        public CcittGroup4Encoder(int width, int height)
        {
            _width = width;
            _height = height;
        }

        public string DisplayName => $"CCITTFaxDecode]\r\n  /DecodeParms [<</K -1 /Columns {_width} /Rows {_height} /BlackIs1 true>>";

        public byte[] Encode(byte[] data)
        {
            var rowBytes = (_width + 7) / 8;

            // Write a single-strip TIFF with CCITT Group 4 compression,
            // then read back the raw compressed strip data via LibTiff.
            // LibTiff closes the underlying stream when the Tiff object is
            // disposed, so we snapshot the bytes between write and read.
            byte[] tiffBytes;
            using (var ms = new MemoryStream())
            {
                using (var tiff = Tiff.ClientOpen("in-memory", "w", ms, new TiffStream()))
                {
                    tiff.SetField(TiffTag.IMAGEWIDTH, _width);
                    tiff.SetField(TiffTag.IMAGELENGTH, _height);
                    tiff.SetField(TiffTag.SAMPLESPERPIXEL, 1);
                    tiff.SetField(TiffTag.BITSPERSAMPLE, 1);
                    tiff.SetField(TiffTag.COMPRESSION, Compression.CCITTFAX4);
                    tiff.SetField(TiffTag.PHOTOMETRIC, Photometric.MINISWHITE);
                    tiff.SetField(TiffTag.ROWSPERSTRIP, _height);
                    tiff.SetField(TiffTag.FILLORDER, FillOrder.MSB2LSB);

                    var row = new byte[rowBytes];
                    for (var y = 0; y < _height; y++)
                    {
                        Buffer.BlockCopy(data, y * rowBytes, row, 0, rowBytes);
                        tiff.WriteScanline(row, y);
                    }
                }

                tiffBytes = ms.ToArray();
            }

            using var readMs = new MemoryStream(tiffBytes);
            using var readTiff = Tiff.ClientOpen("in-memory", "r", readMs, new TiffStream());
            var stripSize = (int)readTiff.RawStripSize(0);
            var rawStrip = new byte[stripSize];
            readTiff.ReadRawStrip(0, rawStrip, 0, stripSize);
            return rawStrip;
        }
    }
}
