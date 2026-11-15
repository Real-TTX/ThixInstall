namespace ThixInstall.Shared;

/// <summary>
/// Compares versions of apps as their vendors write them ("133.0", "24.08", "1.95.3.0", "2.3.4-beta"): the numbers are
/// compared one by one, missing numbers count as 0, a text after the numbers ("-beta", "rc1") is older than none.
/// Used by the cloud (newest release of a package) and the device (is an update available?).
/// </summary>
public static class AppVersion
{
    /// <summary>Less than 0: <paramref name="left"/> is older; 0: the same; greater than 0: newer. Null is the oldest.</summary>
    public static int Compare(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return string.IsNullOrWhiteSpace(left) ? (string.IsNullOrWhiteSpace(right) ? 0 : -1) : 1;
        }

        var (leftNumbers, leftSuffix) = Split(left);
        var (rightNumbers, rightSuffix) = Split(right);
        var count = Math.Max(leftNumbers.Count, rightNumbers.Count);
        for (var index = 0; index < count; index++)
        {
            var a = index < leftNumbers.Count ? leftNumbers[index] : 0;
            var b = index < rightNumbers.Count ? rightNumbers[index] : 0;
            if (a != b)
            {
                return a.CompareTo(b);
            }
        }

        if (leftSuffix.Length == 0 || rightSuffix.Length == 0)
        {
            return rightSuffix.Length.CompareTo(leftSuffix.Length) switch
            {
                > 0 => 1,
                < 0 => -1,
                _ => 0,
            };
        }

        return string.Compare(leftSuffix, rightSuffix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when <paramref name="available"/> is newer than <paramref name="installed"/> (both known).</summary>
    public static bool IsNewer(string? available, string? installed) =>
        !string.IsNullOrWhiteSpace(available) && !string.IsNullOrWhiteSpace(installed) && HasNumber(installed) && Compare(available, installed) > 0;

    private static bool HasNumber(string value) => value.Any(char.IsAsciiDigit);

    private static (List<long> Numbers, string Suffix) Split(string version)
    {
        var text = version.Trim().TrimStart('v', 'V');
        var numbers = new List<long>();
        var position = 0;
        while (position < text.Length)
        {
            var start = position;
            while (position < text.Length && char.IsAsciiDigit(text[position]))
            {
                position++;
            }

            if (position == start)
            {
                break;
            }

            numbers.Add(long.TryParse(text.AsSpan(start, Math.Min(position - start, 18)), out var number) ? number : long.MaxValue);
            if (position < text.Length && (text[position] == '.' || text[position] == '_' || text[position] == ','))
            {
                position++;
                continue;
            }

            break;
        }

        return (numbers, text[position..].Trim('-', '+', ' ', '.'));
    }
}
