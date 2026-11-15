using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using DrawingColor = System.Drawing.Color;
using MediaColor = System.Windows.Media.Color;

namespace DesktopIsland.Services;

public record ScreenTranslateResult(
    bool Success,
    string SourceLang,
    string TargetLang,
    string OriginalText,
    string TranslatedText,
    string? Error = null);

public static class ScreenCaptureOcrService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    static ScreenCaptureOcrService()
    {
        try
        {
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) DesktopIsland/1.5");
        }
        catch { }
    }

    public static string ScreenshotsDirectory
    {
        get
        {
            string pics = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (string.IsNullOrWhiteSpace(pics))
                pics = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string dir = Path.Combine(pics, "DesktopIsland");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static Rectangle GetVirtualBounds() => Forms.SystemInformation.VirtualScreen;

    public static Bitmap CaptureVirtualScreen()
    {
        var vs = GetVirtualBounds();
        var bmp = new Bitmap(Math.Max(1, vs.Width), Math.Max(1, vs.Height), PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(vs.Left, vs.Top, 0, 0, vs.Size, CopyPixelOperation.SourceCopy);
        return bmp;
    }

    public static Bitmap CropFromVirtual(Bitmap virtualBmp, Rectangle localRect)
    {
        var clamped = Rectangle.Intersect(localRect, new Rectangle(0, 0, virtualBmp.Width, virtualBmp.Height));
        if (clamped.Width < 2 || clamped.Height < 2)
            clamped = new Rectangle(0, 0, Math.Min(2, virtualBmp.Width), Math.Min(2, virtualBmp.Height));

        var crop = new Bitmap(clamped.Width, clamped.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(crop);
        g.DrawImage(virtualBmp, new Rectangle(0, 0, clamped.Width, clamped.Height), clamped, GraphicsUnit.Pixel);
        return crop;
    }

    public static BitmapSource ToBitmapSource(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        ms.Position = 0;
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.StreamSource = ms;
        bi.EndInit();
        bi.Freeze();
        return bi;
    }

    public static (string FilePath, BitmapSource Preview) SaveAndCopyBitmap(Bitmap bmp, string prefix = "Shot")
    {
        string name = $"{prefix}_{DateTime.Now:yyyyMMdd_HHmmss}.png";
        string path = Path.Combine(ScreenshotsDirectory, name);
        bmp.Save(path, ImageFormat.Png);

        var bs = ToBitmapSource(bmp);
        try
        {
            System.Windows.Clipboard.SetImage(bs);
        }
        catch { }

        return (path, bs);
    }

    public static (string FilePath, BitmapSource Preview) CaptureScreenAndCopy(Forms.Screen screen)
    {
        var b = screen.Bounds;
        using var bmp = new Bitmap(Math.Max(1, b.Width), Math.Max(1, b.Height), PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(b.Left, b.Top, 0, 0, b.Size, CopyPixelOperation.SourceCopy);
        }
        return SaveAndCopyBitmap(bmp, "Screen");
    }

    public static async Task<string> RecognizeTextRuEnAsync(Bitmap rawCrop)
    {
        using var prepared = PrepareForOcr(rawCrop);
        using var ms = new MemoryStream();
        prepared.Save(ms, ImageFormat.Png);
        byte[] pngBytes = ms.ToArray();

        using var ras = new InMemoryRandomAccessStream();
        await ras.WriteAsync(pngBytes.AsBuffer());
        ras.Seek(0);

        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(ras);
        using var swBmp = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

        var langs = OcrEngine.AvailableRecognizerLanguages;
        OcrEngine? ruEngine = langs.FirstOrDefault(l => l.LanguageTag.StartsWith("ru", StringComparison.OrdinalIgnoreCase)) is Language ruLang
            ? OcrEngine.TryCreateFromLanguage(ruLang)
            : null;
        OcrEngine? enEngine = langs.FirstOrDefault(l => l.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase)) is Language enLang
            ? OcrEngine.TryCreateFromLanguage(enLang)
            : null;

        if (ruEngine == null && enEngine == null)
            ruEngine = OcrEngine.TryCreateFromUserProfileLanguages();

        string ruText = string.Empty;
        string enText = string.Empty;

        if (ruEngine != null)
            ruText = FormatOcrResult(await ruEngine.RecognizeAsync(swBmp));
        if (enEngine != null)
            enText = FormatOcrResult(await enEngine.RecognizeAsync(swBmp));

        if (string.IsNullOrWhiteSpace(ruText)) return enText.Trim();
        if (string.IsNullOrWhiteSpace(enText)) return ruText.Trim();

        int cyrInRu = CountCyrillic(ruText);
        int latInEn = CountLatin(enText);

        // Russian OCR engine in Windows also handles Latin digits/words well; prefer ruText when clear Cyrillic exists
        if (cyrInRu >= 3 && cyrInRu >= latInEn / 2)
            return ruText.Trim();

        return latInEn > cyrInRu ? enText.Trim() : ruText.Trim();
    }

    private static Bitmap PrepareForOcr(Bitmap src)
    {
        int scale = (src.Width < 700 || src.Height < 250) ? 2 : 1;
        int pad = 20;
        int w = src.Width * scale + pad * 2;
        int h = src.Height * scale + pad * 2;
        var outBmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(outBmp);
        g.Clear(DrawingColor.Black);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(src, new Rectangle(pad, pad, src.Width * scale, src.Height * scale));
        return outBmp;
    }

    private static string FormatOcrResult(OcrResult? res)
    {
        if (res == null || res.Lines == null || res.Lines.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        foreach (var line in res.Lines)
        {
            if (string.IsNullOrWhiteSpace(line.Text)) continue;
            if (sb.Length > 0) sb.AppendLine();
            sb.Append(line.Text.Trim());
        }
        return sb.ToString();
    }

    public static int CountCyrillic(string s)
        => s.Count(c => (c >= 'а' && c <= 'я') || (c >= 'А' && c <= 'Я') || c == 'ё' || c == 'Ё');

    public static int CountLatin(string s)
        => s.Count(c => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'));

    public static async Task<ScreenTranslateResult> TranslateAutoRuEnAsync(string text, string directionMode = "Auto")
    {
        string clean = text.Trim();
        if (string.IsNullOrWhiteSpace(clean))
            return new ScreenTranslateResult(false, "", "", "", "", "Текст не найден в выделенной области");

        int cyr = CountCyrillic(clean);
        int lat = CountLatin(clean);

        string srcLang;
        string tgtLang;
        if (string.Equals(directionMode, "EnToRu", StringComparison.OrdinalIgnoreCase))
        {
            srcLang = "en";
            tgtLang = "ru";
        }
        else if (string.Equals(directionMode, "RuToEn", StringComparison.OrdinalIgnoreCase))
        {
            srcLang = "ru";
            tgtLang = "en";
        }
        else
        {
            bool mostlyRu = cyr >= lat && cyr > 0;
            srcLang = mostlyRu ? "ru" : "en";
            tgtLang = mostlyRu ? "en" : "ru";
        }

        // Primary: Google GTX lightweight translate endpoint
        try
        {
            string url = $"https://translate.googleapis.com/translate_a/single?client=gtx&sl={srcLang}&tl={tgtLang}&dt=t&q={Uri.EscapeDataString(clean)}";
            string json = await Http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
            {
                var segs = doc.RootElement[0];
                if (segs.ValueKind == JsonValueKind.Array)
                {
                    var sb = new StringBuilder();
                    foreach (var seg in segs.EnumerateArray())
                    {
                        if (seg.ValueKind == JsonValueKind.Array && seg.GetArrayLength() > 0 && seg[0].ValueKind == JsonValueKind.String)
                            sb.Append(seg[0].GetString());
                    }
                    string translated = sb.ToString().Trim();
                    if (!string.IsNullOrWhiteSpace(translated))
                        return new ScreenTranslateResult(true, srcLang, tgtLang, clean, translated);
                }
            }
        }
        catch { }

        // Fallback: MyMemory public translation API
        try
        {
            string query = clean.Length > 480 ? clean[..480] : clean;
            string url = $"https://api.mymemory.translated.net/get?q={Uri.EscapeDataString(query)}&langpair={srcLang}|{tgtLang}";
            string json = await Http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("responseData", out var rd) &&
                rd.TryGetProperty("translatedText", out var tt) &&
                tt.ValueKind == JsonValueKind.String)
            {
                string translated = tt.GetString()?.Trim() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(translated))
                    return new ScreenTranslateResult(true, srcLang, tgtLang, clean, translated);
            }
        }
        catch (Exception ex)
        {
            return new ScreenTranslateResult(false, srcLang, tgtLang, clean, string.Empty, ex.Message);
        }

        return new ScreenTranslateResult(false, srcLang, tgtLang, clean, string.Empty, "Сервис перевода временно недоступен");
    }

    public static string ToHex(MediaColor c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
