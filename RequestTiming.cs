using System.Diagnostics;
using System.Globalization;

namespace PgDataHub;

/// <summary>Request-local stage timings exposed through the standard Server-Timing header.</summary>
public static class RequestTiming
{
    private const string StartKey = "PgDataHub.Timing.Start";
    private const string ValuesKey = "PgDataHub.Timing.Values";

    public static void Begin(HttpContext context) =>
        context.Items[StartKey] = Stopwatch.GetTimestamp();

    public static void Add(HttpContext context, string name, TimeSpan elapsed)
    {
        if (!context.Items.TryGetValue(ValuesKey, out var value) ||
            value is not Dictionary<string, double> values)
        {
            values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            context.Items[ValuesKey] = values;
        }

        values[name] = values.GetValueOrDefault(name) + elapsed.TotalMilliseconds;
    }

    public static string Format(HttpContext context)
    {
        var values = context.Items.TryGetValue(ValuesKey, out var value) &&
                     value is Dictionary<string, double> existing
            ? existing
            : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        if (context.Items.TryGetValue(StartKey, out var start) && start is long timestamp)
            values["app"] = Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds;

        return string.Join(", ", values.Select(item =>
            $"{Sanitize(item.Key)};dur={item.Value.ToString("0.###", CultureInfo.InvariantCulture)}"));
    }

    private static string Sanitize(string value) =>
        new(value.Where(c => char.IsLetterOrDigit(c) || c is '_' or '-').ToArray());
}
