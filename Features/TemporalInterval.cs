using System.Globalization;
using System.Text.RegularExpressions;

namespace PgDataHub.Features;

public sealed record TemporalInterval(DateTimeOffset? Start, DateTimeOffset? End)
{
    public static TemporalInterval Parse(string value)
    {
        static DateTimeOffset? Instant(string part)
        {
            if (part is "" or "..") return null;
            if (!Regex.IsMatch(part, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)) ||
                !DateTimeOffset.TryParse(part, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var instant))
                throw new ArgumentException("datetime must contain RFC3339 timestamps with a timezone, optionally separated by '/'.");
            return instant.ToUniversalTime();
        }
        var parts = value.Split('/');
        if (parts.Length is < 1 or > 2) throw new ArgumentException("Invalid datetime interval.");
        var start = Instant(parts[0]);
        var end = parts.Length == 1 ? start : Instant(parts[1]);
        if (start is null && end is null || start > end) throw new ArgumentException("Invalid or reversed datetime interval.");
        return new(start, end);
    }
}
