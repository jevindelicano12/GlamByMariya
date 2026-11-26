using System.Text.RegularExpressions;

namespace GlamByMariya.Core.BusinessRules;

// Shared profanity / offensive-language filter for customer/staff names and service addresses.
// Uses whole-word matching + light leetspeak normalization to avoid false positives on valid PH names and places.
public static partial class ProfanityFilter
{
    private static readonly HashSet<string> OffensiveWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // English profanity & slurs
        "fuck", "fucker", "fucking", "fucked", "fucks", "motherfucker", "mf",
        "shit", "shitty", "bullshit", "bitch", "bitches", "bastard",
        "asshole", "dumbass", "jackass", "dick", "dickhead", "cock", "pussy",
        "cunt", "whore", "slut", "hoe", "faggot", "fag", "nigger", "nigga",
        "retard", "retarded", "porn", "porno", "hentai", "sex", "sexy", "nude", "nudes",
        "rape", "rapist", "terrorist", "nazi", "hitler",

        // Filipino / Tagalog & Bisaya mura, bastos words & slurs
        "puta", "putang", "putangina", "putanginamo", "tangina", "tanginamo", "taena",
        "pota", "potang", "potangina", "potaena", "kingina", "kinginamo", "pukingina",
        "pukinangina", "amputa", "amputang", "pucha", "putcha", "punyeta", "pakyu",
        "pakshet", "pakshit", "gago", "gaga", "gagu", "ogag", "tanga", "bobo", "bubu",
        "vovo", "obob", "ulol", "ulul", "tarantado", "tarantada", "kupal", "hayop",
        "hayup", "hinayupak", "lintik", "leche", "letse", "letche", "peste", "piste",
        "pisti", "bwisit", "buwisit", "inutil", "ungas", "siraulo", "demonyo", "hudas",
        "walanghiya", "yawa", "giatay", "buang", "boang", "amaw", "bugo", "bogo",
        "burat", "titi", "tite", "etits", "oten", "utin", "bayag", "supot", "tamod",
        "puke", "puki", "pekpek", "puday", "bilat", "kepyas", "kiki",
        "kantot", "kantutan", "kantotan", "iyot", "iyutan", "iyotan", "hindot", "hindutan",
        "jakol", "jakulan", "jabol", "chupa", "chupaan", "tsupa", "tsupaan",
        "libog", "malibog", "bastos", "pokpok", "malandi", "animal"
    };

    private static readonly string[] OffensivePhrases =
    [
        "putang ina",
        "potang ina",
        "tang ina",
        "puking ina",
        "pukinang ina",
        "king ina",
        "anak ng puta",
        "ina mo",
        "walang hiya",
        "sira ulo",
        "fuck you",
        "son of a bitch",
        "mother fucker"
    ];

    [GeneratedRegex(@"[\p{L}\p{N}@$]+")]
    private static partial Regex WordTokens();

    private static string NormalizeToken(string token)
    {
        var lower = token.ToLowerInvariant();
        // Only apply leetspeak substitution when the token contains at least one letter (avoids house/ZIP numbers like 1600).
        if (!lower.Any(char.IsLetter)) return lower;
        return lower
            .Replace('@', 'a')
            .Replace('$', 's')
            .Replace('0', 'o')
            .Replace('1', 'i')
            .Replace('3', 'e')
            .Replace('4', 'a')
            .Replace('5', 's');
    }

    public static bool ContainsOffensiveWord(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var tokens = WordTokens().Matches(text).Select(m => NormalizeToken(m.Value)).ToArray();
        if (tokens.Length == 0) return false;
        foreach (var token in tokens)
        {
            if (OffensiveWords.Contains(token)) return true;
        }
        var joined = string.Join(' ', tokens);
        foreach (var phrase in OffensivePhrases)
        {
            if (joined.Contains(phrase, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    public static string? ValidateName(string? text, string fieldLabel = "name") =>
        ContainsOffensiveWord(text)
            ? $"Please enter an appropriate {fieldLabel} without offensive language."
            : null;

    public static string? ValidateAddress(string? text) =>
        ContainsOffensiveWord(text)
            ? "Please enter a valid address without offensive language."
            : null;
}
