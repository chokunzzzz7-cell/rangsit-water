// Reads the Rangsit camera snapshot and writes status.json.
// Run from the repo root:
//   dotnet run --project monitor -c Release                (normal)
//   dotnet run --project monitor -c Release -- --debug     (print the numbers only, write nothing)
//   dotnet run --project monitor -c Release -- --file x.png --debug   (test on a saved screenshot)
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace RangsitMonitor;

public class Band
{
    [JsonPropertyName("box")] public int[] Box { get; set; } = new int[4];
    [JsonPropertyName("hue")] public double[][] Hue { get; set; } = Array.Empty<double[]>();
    [JsonPropertyName("sat")] public double Sat { get; set; }
    [JsonPropertyName("val")] public double Val { get; set; }
}

public class Config
{
    [JsonPropertyName("camera_page")] public string CameraPage { get; set; } = "";
    [JsonPropertyName("snapshot_url")] public string SnapshotUrl { get; set; } = "";
    [JsonPropertyName("ref_width")] public int RefWidth { get; set; } = 1920;
    [JsonPropertyName("night_min_brightness")] public double NightMinBrightness { get; set; } = 45;
    [JsonPropertyName("min_fraction")] public double MinFraction { get; set; } = 0.35;
    [JsonPropertyName("bands")] public Dictionary<string, Band> Bands { get; set; } = new();
}

public class HistoryEntry
{
    [JsonPropertyName("t")] public string T { get; set; } = "";
    [JsonPropertyName("level")] public string Level { get; set; } = "";
}

// Same layout as before, so index.html keeps working.
public class Status
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("note")] public string Note { get; set; } = "";
    [JsonPropertyName("level")] public string? Level { get; set; }
    [JsonPropertyName("pending")] public string? Pending { get; set; }
    [JsonPropertyName("changed_at")] public string? ChangedAt { get; set; }
    [JsonPropertyName("checked_at")] public string? CheckedAt { get; set; }
    [JsonPropertyName("fractions")] public Dictionary<string, double>? Fractions { get; set; }
    [JsonPropertyName("history")] public List<HistoryEntry> History { get; set; } = new();
}

public static class Program
{
    const string StatusFile = "status.json";
    static readonly Dictionary<string, string> Label = new()
    {
        ["green"] = "Normal", ["yellow"] = "Warning", ["orange"] = "High", ["red"] = "Critical",
    };
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    static readonly JsonSerializerOptions JsonOut = new() { WriteIndented = true };
    static Config Cfg = new();

    public static async Task<int> Main(string[] args)
    {
        Cfg = JsonSerializer.Deserialize<Config>(File.ReadAllText("config.json"))!;
        string? path = Array.IndexOf(args, "--file") is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        bool debug = args.Contains("--debug");

        string now = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'+00:00'");
        Status old = LoadStatus();

        Image<Rgb24> img;
        try { img = await LoadImage(path); }
        catch (Exception e)
        {
            // camera unreachable: record it, keep the last known level
            Console.WriteLine("error: " + e.Message);
            if (!debug)
            {
                old.Ok = false; old.Note = $"Camera error: {e.Message}"; old.CheckedAt = now;
                Save(old);
            }
            return 0;   // not a build failure; the page shows the warning
        }

        using (img)
        {
            if (debug)
            {
                var (lv, f) = ReadLevel(img);
                Console.WriteLine($"level: {lv}  visible: {string.Join(", ", f.Select(kv => $"{kv.Key} {kv.Value:0.00}"))}  brightness: {Math.Round(Brightness(img))}");
                return 0;
            }

            var st = old;
            try
            {
                st.Ok = true;
                if (Brightness(img) < Cfg.NightMinBrightness)
                {
                    st.Ok = false; st.Note = "Image too dark to read"; st.CheckedAt = now;
                }
                else
                {
                    var (level, fr) = ReadLevel(img);
                    string? confirmed = old.Level;

                    // a new level must be seen on two runs in a row before it counts
                    if (level == confirmed) st.Pending = null;
                    else if (old.Pending == level || confirmed == null)
                    {
                        st.Level = level; st.Pending = null; st.ChangedAt = now;
                        if (confirmed != null)
                            await Notify($"Rangsit water level: {Label[level]} ({level})");
                    }
                    else st.Pending = level;

                    st.CheckedAt = now; st.Fractions = fr; st.Note = "";
                    st.History.Add(new HistoryEntry { T = now, Level = st.Level ?? level });
                    if (st.History.Count > 288) st.History = st.History.TakeLast(288).ToList();
                }
            }
            catch (Exception e)
            {
                st.Ok = false; st.Note = $"Error: {e.Message}"; st.CheckedAt = now;
            }
            Save(st);
            Console.WriteLine($"{st.Level} {st.Note}");
        }
        return 0;
    }

    // ---------- image ----------

    static async Task<Image<Rgb24>> LoadImage(string? path)
    {
        if (path != null) return Image.Load<Rgb24>(path);
        using var req = new HttpRequestMessage(HttpMethod.Get, Cfg.SnapshotUrl);
        req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
        req.Headers.TryAddWithoutValidation("Referer", Cfg.CameraPage);
        using var res = await Http.SendAsync(req);
        res.EnsureSuccessStatusCode();
        return Image.Load<Rgb24>(await res.Content.ReadAsByteArrayAsync());
    }

    static Rectangle Scaled(int[] box, Image img)
    {
        double k = (double)img.Width / Cfg.RefWidth;
        int x1 = (int)(box[0] * k), y1 = (int)(box[1] * k);
        int x2 = (int)(box[2] * k), y2 = (int)(box[3] * k);
        return Rectangle.Intersect(new Rectangle(x1, y1, x2 - x1, y2 - y1),
                                   new Rectangle(0, 0, img.Width, img.Height));
    }

    static (double h, double s, double v) ToHsv(Rgb24 c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double d = max - min, h = 0;
        if (d > 0)
        {
            if (max == r) h = ((g - b) / d) % 6;
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h *= 60;
            if (h < 0) h += 360;
        }
        return (h, max == 0 ? 0 : d / max, max);
    }

    static double Fraction(Image<Rgb24> img, Band band)
    {
        var area = Scaled(band.Box, img);
        if (area.Width <= 0 || area.Height <= 0) return 0;
        using var crop = img.Clone(c => c.Crop(area).Resize(40, 40, KnownResamplers.Bicubic));
        int ok = 0;
        for (int y = 0; y < 40; y++)
            for (int x = 0; x < 40; x++)
            {
                var (h, s, v) = ToHsv(crop[x, y]);
                if (s >= band.Sat && v >= band.Val && band.Hue.Any(r => h >= r[0] && h <= r[1])) ok++;
            }
        return ok / 1600.0;
    }

    static (string level, Dictionary<string, double> fractions) ReadLevel(Image<Rgb24> img)
    {
        var fr = Cfg.Bands.ToDictionary(kv => kv.Key, kv => Math.Round(Fraction(img, kv.Value), 2));
        var hidden = fr.Where(kv => kv.Value < Cfg.MinFraction).Select(kv => kv.Key).ToHashSet();
        // the highest band that is no longer visible is where the water is
        foreach (var name in new[] { "red", "orange", "yellow" })
            if (hidden.Contains(name)) return (name, fr);
        return ("green", fr);
    }

    static double Brightness(Image<Rgb24> img)
    {
        using var small = img.Clone(c => c.Resize(32, 18, KnownResamplers.Bicubic));
        double sum = 0;
        for (int y = 0; y < 18; y++)
            for (int x = 0; x < 32; x++)
            {
                var p = small[x, y];
                sum += 0.299 * p.R + 0.587 * p.G + 0.114 * p.B;
            }
        return sum / (32 * 18);
    }

    // ---------- files / telegram ----------

    static Status LoadStatus()
    {
        try { return JsonSerializer.Deserialize<Status>(File.ReadAllText(StatusFile)) ?? new Status(); }
        catch { return new Status(); }
    }

    static void Save(Status st) => File.WriteAllText(StatusFile, JsonSerializer.Serialize(st, JsonOut));

    static async Task Notify(string text)
    {
        string? token = Environment.GetEnvironmentVariable("TELEGRAM_TOKEN");
        string? chat = Environment.GetEnvironmentVariable("TELEGRAM_CHAT_ID");
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(chat)) return;
        try
        {
            using var body = new FormUrlEncodedContent(new Dictionary<string, string>
                { ["chat_id"] = chat, ["text"] = text });
            await Http.PostAsync($"https://api.telegram.org/bot{token}/sendMessage", body);
        }
        catch (Exception e) { Console.WriteLine("telegram failed: " + e.Message); }
    }
}
