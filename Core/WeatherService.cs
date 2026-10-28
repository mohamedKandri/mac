using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace MacShell.Core;

// Weather for the island, from Open-Meteo (https://open-meteo.com): free, no account or key.
// Sends only the city name (once, to turn it into coordinates) and then those coordinates. Location stays off:
// the city comes from config.json (island.weatherCity). Refreshed at most every 30 minutes; never throws.
public sealed class WeatherService
{
    public readonly record struct Hour(DateTime Time, int Temperature, int Code);

    // Code = WMO weather code (0 clear ... 95+ thunderstorm); see Describe.
    public sealed record Forecast(string City, int Temperature, int Code, int High, int Low, IReadOnlyList<Hour> Hours, DateTime FetchedAt);

    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(30);
    // Created on first use: networking isn't loaded until weather is asked for.
    private static readonly Lazy<HttpClient> Http = new(() =>
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MacShell/1.0");
        return client;
    });

    private string _city = "";
    private (double Lat, double Lon, string Name)? _place;
    private Task<Forecast?>? _pending;

    public Forecast? Latest { get; private set; }

    // The cached forecast if it's fresh, else a new one (null if the city is unknown or the network is down).
    public Task<Forecast?> GetAsync(string city)
    {
        city = city.Trim();
        if (!city.Equals(_city, StringComparison.OrdinalIgnoreCase))
        {
            _city = city;
            _place = null;
            Latest = null;
        }
        if (city.Length == 0) return Task.FromResult<Forecast?>(null);
        if (Latest is not null && DateTime.UtcNow - Latest.FetchedAt < MaxAge) return Task.FromResult<Forecast?>(Latest);
        return _pending ??= FetchAsync(city).ContinueWith(t =>
        {
            _pending = null;
            if (t.Result is not null) Latest = t.Result;
            return Latest;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private async Task<Forecast?> FetchAsync(string city)
    {
        try
        {
            if (_place is null)
            {
                using var geo = await Get($"https://geocoding-api.open-meteo.com/v1/search?count=1&name={Uri.EscapeDataString(city)}");
                if (geo is null || !geo.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0) return null;
                var r = results[0];
                _place = (r.GetProperty("latitude").GetDouble(), r.GetProperty("longitude").GetDouble(), r.GetProperty("name").GetString() ?? city);
            }
            var (lat, lon, name) = _place.Value;
            string url = string.Format(CultureInfo.InvariantCulture,
                "https://api.open-meteo.com/v1/forecast?latitude={0}&longitude={1}&current=temperature_2m,weather_code" +
                "&hourly=temperature_2m,weather_code&daily=temperature_2m_max,temperature_2m_min&timezone=auto&forecast_days=2",
                lat, lon);
            using var doc = await Get(url);
            if (doc is null) return null;
            var root = doc.RootElement;
            var current = root.GetProperty("current");
            var daily = root.GetProperty("daily");
            var hourly = root.GetProperty("hourly");

            // Times come in the city's own time zone ("2026-10-28T15:00"); keep the next five hours after now there.
            string nowLocal = current.GetProperty("time").GetString() ?? "";
            var times = hourly.GetProperty("time").EnumerateArray().Select(t => t.GetString() ?? "").ToList();
            var temps = hourly.GetProperty("temperature_2m").EnumerateArray().Select(t => t.GetDouble()).ToList();
            var codes = hourly.GetProperty("weather_code").EnumerateArray().Select(t => t.GetInt32()).ToList();
            var hours = new List<Hour>();
            for (int i = 0; i < times.Count && hours.Count < 5; i++)
            {
                if (string.CompareOrdinal(times[i], nowLocal) <= 0) continue;
                hours.Add(new Hour(DateTime.Parse(times[i], CultureInfo.InvariantCulture), (int)Math.Round(temps[i]), codes[i]));
            }
            return new Forecast(name,
                (int)Math.Round(current.GetProperty("temperature_2m").GetDouble()), current.GetProperty("weather_code").GetInt32(),
                (int)Math.Round(daily.GetProperty("temperature_2m_max")[0].GetDouble()), (int)Math.Round(daily.GetProperty("temperature_2m_min")[0].GetDouble()),
                hours, DateTime.UtcNow);
        }
        catch (Exception) { return null; }   // offline, blocked, timed out, unexpected JSON
    }

    private static async Task<JsonDocument?> Get(string url)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var response = await Http.Value.GetAsync(url, timeout.Token);
        if (!response.IsSuccessStatusCode) return null;
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
    }

    // WMO code -> short description and icon kind ("sun", "partly", "cloud", "fog", "rain", "snow", "storm").
    public static (string Text, string Icon) Describe(int code) => code switch
    {
        0 => ("Clear", "sun"),
        1 => ("Mostly clear", "partly"),
        2 => ("Partly cloudy", "partly"),
        3 => ("Cloudy", "cloud"),
        45 or 48 => ("Fog", "fog"),
        >= 51 and <= 57 => ("Drizzle", "rain"),
        >= 61 and <= 67 => ("Rain", "rain"),
        >= 71 and <= 77 => ("Snow", "snow"),
        >= 80 and <= 82 => ("Showers", "rain"),
        85 or 86 => ("Snow showers", "snow"),
        >= 95 => ("Thunderstorm", "storm"),
        _ => ("—", "cloud"),
    };
}
