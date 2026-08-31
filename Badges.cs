namespace ScweenSpit;

/// <summary>
/// How many things an application is waiting on you for, read from its window title.
///
/// Not from the taskbar overlay icon, and not from the Store badge: an application hands those to
/// the shell through ITaskbarList3 and the badge API, and the shell keeps them. Nothing public hands
/// them back — a taskbar that is not Explorer's cannot ask what Explorer was told.
///
/// What every one of those applications also does is put the count in its own title, because that is
/// how it shows in Alt+Tab and in a window list. "(3) Microsoft Teams", "(12) Slack", "(1) WhatsApp".
/// That is a title away, costs nothing, and is already read for the tooltip.
/// </summary>
internal static class Badges
{
    /// <summary>Beyond this it is not a notification count, it is a number that happens to be there.</summary>
    private const int Most = 999;

    /// <summary>
    /// The count an application is announcing, or null.
    ///
    /// Only at the very start of the title, and deliberately so. A number in brackets further in is
    /// far more often part of a name — "Document (2).docx", "screenshot (4).png" — and a badge that
    /// appears on every second File Explorer window is worse than no badge at all. Applications that
    /// are counting put it first, where it survives being truncated in a window list.
    /// </summary>
    public static int? Count(string? title)
    {
        if (string.IsNullOrEmpty(title)) return null;

        var text = title.AsSpan().TrimStart();
        if (text.Length < 3) return null;

        char opening = text[0];
        char closing = opening switch { '(' => ')', '[' => ']', _ => '\0' };
        if (closing == '\0') return null;

        int end = text.IndexOf(closing);
        if (end < 2) return null;                      // "()" is not a count

        var digits = text[1..end];
        if (digits.Length > 3) return null;            // longer than Most can be; not a count

        foreach (char c in digits)
            if (!char.IsAsciiDigit(c)) return null;

        if (!int.TryParse(digits, out int count) || count <= 0 || count > Most) return null;

        // Something has to follow it. A window actually called "(3)" is not announcing three of
        // anything, and neither is one whose title is a bare number in brackets.
        return text[(end + 1)..].TrimStart().Length == 0 ? null : count;
    }

    /// <summary>
    /// What to draw in the bubble. Two characters at most, because the bubble sits on the corner of
    /// an icon and a third would cover the icon rather than annotate it.
    /// </summary>
    public static string Text(int count) => count > 99 ? "99+" : count.ToString();
}
