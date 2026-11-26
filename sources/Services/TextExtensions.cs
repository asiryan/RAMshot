namespace RAMshot.Services;

internal static class TextExtensions
{
    // English pluralization for counts: "1 image" / "2 images".
    public static string Declension(this long number, string singular, string plural) =>
        $"{number} {(number == 1 ? singular : plural)}";

    public static string Declension(this int number, string singular, string plural) =>
        ((long)number).Declension(singular, plural);
}
