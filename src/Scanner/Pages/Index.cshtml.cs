using System.Text;
using IxMilia.Pdf;
using IxMilia.Pdf.Encoders;
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

        // Inject DecodeParms into the PDF dictionary via the filter name
        public string DisplayName => $"CCITTFaxDecode]\r\n  /DecodeParms [<</K -1 /Columns {_width} /Rows {_height} /BlackIs1 false>>";

        public byte[] Encode(byte[] data) => EncodeGroup4(data, _width, _height);
    }

    private static byte[] EncodeGroup4(byte[] data, int width, int height)
    {
        var rowBytes = (width + 7) / 8;
        var output = new List<byte>();
        int bitPos = 0;
        byte currentByte = 0;

        void WriteBits(int code, int length)
        {
            for (var i = length - 1; i >= 0; i--)
            {
                if (((code >> i) & 1) == 1)
                {
                    currentByte |= (byte)(1 << (7 - bitPos));
                }

                bitPos++;
                if (bitPos == 8)
                {
                    output.Add(currentByte);
                    currentByte = 0;
                    bitPos = 0;
                }
            }
        }

        bool GetPixel(byte[] row, int x)
        {
            if (x >= width) return false;
            return (row[x / 8] & (1 << (7 - (x % 8)))) == 0; // 0=black=true, 1=white=false
        }

        int NextChanging(byte[] row, int x, bool currentColor)
        {
            for (var i = x; i < width; i++)
            {
                if (GetPixel(row, i) != currentColor) return i;
            }
            return width;
        }

        int FindB1(byte[] row, int a0, bool a0Color)
        {
            // b1 is the first changing element on the reference line to the right of a0
            // whose color is opposite to a0Color.
            var start = a0 + 1;
            if (start >= width) return width;

            // Find the color of the reference line at 'start'
            var refColor = GetPixel(row, start);
            if (refColor != a0Color)
            {
                // Already opposite color; b1 is either 'start' itself (if it's a
                // changing element) or the position where this opposite run began.
                // We need the first changing element, so check if it just changed here.
                if (start == 0 || GetPixel(row, start - 1) != refColor)
                    return start;
                // The opposite run started before 'start', so find the next change
                // (which will flip back to a0Color) then find the next opposite again.
                var next = NextChanging(row, start, refColor);
                if (next >= width) return width;
                return NextChanging(row, next, a0Color);
            }
            else
            {
                // Same color as a0Color; find where it changes to opposite
                return NextChanging(row, start, a0Color);
            }
        }

        void WriteRunLength(int length, bool isBlack)
        {
            // Makeup codes for runs >= 64
            while (length >= 2560)
            {
                WriteMakeupCode(2560, isBlack);
                length -= 2560;
            }
            if (length >= 64)
            {
                var makeup = (length / 64) * 64;
                WriteMakeupCode(makeup, isBlack);
                length -= makeup;
            }
            // Terminating code for remaining 0-63
            WriteTerminatingCode(length, isBlack);
        }

        void WriteMakeupCode(int length, bool isBlack)
        {
            var (code, bits) = GetMakeupCode(length, isBlack);
            WriteBits(code, bits);
        }

        void WriteTerminatingCode(int length, bool isBlack)
        {
            var (code, bits) = GetTerminatingCode(length, isBlack);
            WriteBits(code, bits);
        }

        // Pass mode: 0001
        void WritePass() => WriteBits(0b0001, 4);

        // Horizontal mode prefix: 001
        void WriteHorizontalPrefix() => WriteBits(0b001, 3);

        // Vertical mode codes
        void WriteVertical(int offset)
        {
            switch (offset)
            {
                case 0: WriteBits(0b1, 1); break;        // V(0)
                case 1: WriteBits(0b011, 3); break;       // VR(1)
                case -1: WriteBits(0b010, 3); break;      // VL(1)
                case 2: WriteBits(0b000011, 6); break;    // VR(2)
                case -2: WriteBits(0b000010, 6); break;   // VL(2)
                case 3: WriteBits(0b0000011, 7); break;   // VR(3)
                case -3: WriteBits(0b0000010, 7); break;  // VL(3)
            }
        }

        var refRow = new byte[rowBytes]; // starts as all-white (0xFF = white in our encoding)
        Array.Fill(refRow, (byte)0xFF);

        for (var y = 0; y < height; y++)
        {
            var codingRow = new byte[rowBytes];
            Array.Copy(data, y * rowBytes, codingRow, 0, rowBytes);

            var a0 = -1;
            var a0Color = false; // white

            while (a0 < width)
            {
                var a0Pos = Math.Max(a0, 0);
                var a1 = NextChanging(codingRow, a0Pos, a0Color);
                if (a0 == -1 && a1 == 0 && GetPixel(codingRow, 0) != a0Color)
                    a1 = 0; // first pixel is different from the imaginary white start
                else if (a0 == -1)
                    a1 = NextChanging(codingRow, 0, a0Color);
                var b1 = FindB1(refRow, a0, a0Color);
                var b2 = b1 < width ? NextChanging(refRow, b1, !a0Color) : width;

                if (b2 < a1)
                {
                    // Pass mode
                    WritePass();
                    a0 = b2;
                }
                else if (a1 - b1 >= -3 && a1 - b1 <= 3)
                {
                    // Vertical mode
                    WriteVertical(a1 - b1);
                    a0 = a1;
                    a0Color = !a0Color;
                }
                else
                {
                    // Horizontal mode
                    WriteHorizontalPrefix();
                    var a2 = NextChanging(codingRow, a1, !a0Color);
                    var firstRunLength = a0 == -1 ? a1 : a1 - a0;
                    WriteRunLength(firstRunLength, a0Color != false); // a0Color false=white, so isBlack when a0Color is true
                    WriteRunLength(a2 - a1, a0Color == false); // opposite color
                    a0 = a2;
                }
            }

            Array.Copy(codingRow, 0, refRow, 0, rowBytes);
        }

        // EOFB: two consecutive EOL codes (000000000001 each)
        WriteBits(0b000000000001, 12);
        WriteBits(0b000000000001, 12);

        // Flush remaining bits
        if (bitPos > 0)
        {
            output.Add(currentByte);
        }

        output.Add((byte)'\r');
        output.Add((byte)'\n');

        return output.ToArray();
    }

    private static (int code, int bits) GetTerminatingCode(int length, bool isBlack)
    {
        if (isBlack)
        {
            return length switch
            {
                0 => (0b0000110111, 10),
                1 => (0b010, 3),
                2 => (0b11, 2),
                3 => (0b10, 2),
                4 => (0b011, 3),
                5 => (0b0011, 4),
                6 => (0b0010, 4),
                7 => (0b00011, 5),
                8 => (0b000101, 6),
                9 => (0b000100, 6),
                10 => (0b0000100, 7),
                11 => (0b0000101, 7),
                12 => (0b0000111, 7),
                13 => (0b00000100, 8),
                14 => (0b00000111, 8),
                15 => (0b00011000, 8),
                16 => (0b0000010111, 10),
                17 => (0b0000011000, 10),
                18 => (0b0000001000, 10),
                19 => (0b00001100111, 11),
                20 => (0b00001101000, 11),
                21 => (0b00001101100, 11),
                22 => (0b00000110111, 11),
                23 => (0b00000101000, 11),
                24 => (0b00000010111, 11),
                25 => (0b00000011000, 11),
                26 => (0b000011001010, 12),
                27 => (0b000011001011, 12),
                28 => (0b000011001100, 12),
                29 => (0b000011001101, 12),
                30 => (0b000001101000, 12),
                31 => (0b000001101001, 12),
                32 => (0b000001101010, 12),
                33 => (0b000001101011, 12),
                34 => (0b000011010010, 12),
                35 => (0b000011010011, 12),
                36 => (0b000011010100, 12),
                37 => (0b000011010101, 12),
                38 => (0b000011010110, 12),
                39 => (0b000011010111, 12),
                40 => (0b000001101100, 12),
                41 => (0b000001101101, 12),
                42 => (0b000011011010, 12),
                43 => (0b000011011011, 12),
                44 => (0b000001010100, 12),
                45 => (0b000001010101, 12),
                46 => (0b000001010110, 12),
                47 => (0b000001010111, 12),
                48 => (0b000001100100, 12),
                49 => (0b000001100101, 12),
                50 => (0b000001010010, 12),
                51 => (0b000001010011, 12),
                52 => (0b000000100100, 12),
                53 => (0b000000110111, 12),
                54 => (0b000000111000, 12),
                55 => (0b000000100111, 12),
                56 => (0b000000101000, 12),
                57 => (0b000001011000, 12),
                58 => (0b000001011001, 12),
                59 => (0b000000101011, 12),
                60 => (0b000000101100, 12),
                61 => (0b000001011010, 12),
                62 => (0b000001100110, 12),
                63 => (0b000001100111, 12),
                _ => throw new ArgumentOutOfRangeException(nameof(length)),
            };
        }
        else
        {
            return length switch
            {
                0 => (0b00110101, 8),
                1 => (0b000111, 6),
                2 => (0b0111, 4),
                3 => (0b1000, 4),
                4 => (0b1011, 4),
                5 => (0b1100, 4),
                6 => (0b1110, 4),
                7 => (0b1111, 4),
                8 => (0b10011, 5),
                9 => (0b10100, 5),
                10 => (0b00111, 5),
                11 => (0b01000, 5),
                12 => (0b001000, 6),
                13 => (0b000011, 6),
                14 => (0b110100, 6),
                15 => (0b110101, 6),
                16 => (0b101010, 6),
                17 => (0b101011, 6),
                18 => (0b0100111, 7),
                19 => (0b0001100, 7),
                20 => (0b0001000, 7),
                21 => (0b0010111, 7),
                22 => (0b0000011, 7),
                23 => (0b0000100, 7),
                24 => (0b0101000, 7),
                25 => (0b0101011, 7),
                26 => (0b0010011, 7),
                27 => (0b0100100, 7),
                28 => (0b0011000, 7),
                29 => (0b00000010, 8),
                30 => (0b00000011, 8),
                31 => (0b00011010, 8),
                32 => (0b00011011, 8),
                33 => (0b00010010, 8),
                34 => (0b00010011, 8),
                35 => (0b00010100, 8),
                36 => (0b00010101, 8),
                37 => (0b00010110, 8),
                38 => (0b00010111, 8),
                39 => (0b00101000, 8),
                40 => (0b00101001, 8),
                41 => (0b00101010, 8),
                42 => (0b00101011, 8),
                43 => (0b00101100, 8),
                44 => (0b00101101, 8),
                45 => (0b00000100, 8),
                46 => (0b00000101, 8),
                47 => (0b00001010, 8),
                48 => (0b00001011, 8),
                49 => (0b01010010, 8),
                50 => (0b01010011, 8),
                51 => (0b01010100, 8),
                52 => (0b01010101, 8),
                53 => (0b00100100, 8),
                54 => (0b00100101, 8),
                55 => (0b01011000, 8),
                56 => (0b01011001, 8),
                57 => (0b01011010, 8),
                58 => (0b01011011, 8),
                59 => (0b01001010, 8),
                60 => (0b01001011, 8),
                61 => (0b00110010, 8),
                62 => (0b00110011, 8),
                63 => (0b00110100, 8),
                _ => throw new ArgumentOutOfRangeException(nameof(length)),
            };
        }
    }

    private static (int code, int bits) GetMakeupCode(int length, bool isBlack)
    {
        if (isBlack)
        {
            return length switch
            {
                64 => (0b0000001111, 10),
                128 => (0b000011001000, 12),
                192 => (0b000011001001, 12),
                256 => (0b000001011011, 12),
                320 => (0b000000110011, 12),
                384 => (0b000000110100, 12),
                448 => (0b000000110101, 12),
                512 => (0b0000001101100, 13),
                576 => (0b0000001101101, 13),
                640 => (0b0000001001010, 13),
                704 => (0b0000001001011, 13),
                768 => (0b0000001001100, 13),
                832 => (0b0000001001101, 13),
                896 => (0b0000001110010, 13),
                960 => (0b0000001110011, 13),
                1024 => (0b0000001110100, 13),
                1088 => (0b0000001110101, 13),
                1152 => (0b0000001110110, 13),
                1216 => (0b0000001110111, 13),
                1280 => (0b0000001010010, 13),
                1344 => (0b0000001010011, 13),
                1408 => (0b0000001010100, 13),
                1472 => (0b0000001010101, 13),
                1536 => (0b0000001011010, 13),
                1600 => (0b0000001011011, 13),
                1664 => (0b0000001100100, 13),
                1728 => (0b0000001100101, 13),
                1792 => (0b00000001000, 11),
                1856 => (0b00000001100, 11),
                1920 => (0b00000001101, 11),
                1984 => (0b000000010010, 12),
                2048 => (0b000000010011, 12),
                2112 => (0b000000010100, 12),
                2176 => (0b000000010101, 12),
                2240 => (0b000000010110, 12),
                2304 => (0b000000010111, 12),
                2368 => (0b000000011100, 12),
                2432 => (0b000000011101, 12),
                2496 => (0b000000011110, 12),
                2560 => (0b000000011111, 12),
                _ => throw new ArgumentOutOfRangeException(nameof(length)),
            };
        }
        else
        {
            return length switch
            {
                64 => (0b11011, 5),
                128 => (0b10010, 5),
                192 => (0b010111, 6),
                256 => (0b0110111, 7),
                320 => (0b00110110, 8),
                384 => (0b00110111, 8),
                448 => (0b01100100, 8),
                512 => (0b01100101, 8),
                576 => (0b01101000, 8),
                640 => (0b01100111, 8),
                704 => (0b011001100, 9),
                768 => (0b011001101, 9),
                832 => (0b011010010, 9),
                896 => (0b011010011, 9),
                960 => (0b011010100, 9),
                1024 => (0b011010101, 9),
                1088 => (0b011010110, 9),
                1152 => (0b011010111, 9),
                1216 => (0b011011000, 9),
                1280 => (0b011011001, 9),
                1344 => (0b011011010, 9),
                1408 => (0b011011011, 9),
                1472 => (0b010011000, 9),
                1536 => (0b010011001, 9),
                1600 => (0b010011010, 9),
                1664 => (0b011000, 6),
                1728 => (0b010011011, 9),
                1792 => (0b00000001000, 11),
                1856 => (0b00000001100, 11),
                1920 => (0b00000001101, 11),
                1984 => (0b000000010010, 12),
                2048 => (0b000000010011, 12),
                2112 => (0b000000010100, 12),
                2176 => (0b000000010101, 12),
                2240 => (0b000000010110, 12),
                2304 => (0b000000010111, 12),
                2368 => (0b000000011100, 12),
                2432 => (0b000000011101, 12),
                2496 => (0b000000011110, 12),
                2560 => (0b000000011111, 12),
                _ => throw new ArgumentOutOfRangeException(nameof(length)),
            };
        }
    }
}
