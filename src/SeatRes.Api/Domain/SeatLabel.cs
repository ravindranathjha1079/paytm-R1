using System.Text.RegularExpressions;

namespace SeatRes.Api.Domain;

public static partial class SeatLabel
{
    [GeneratedRegex("^[A-Za-z0-9-]{1,16}$")]
    private static partial Regex Pattern();

    public static bool IsValid(string? label) => label is not null && Pattern().IsMatch(label);
}
