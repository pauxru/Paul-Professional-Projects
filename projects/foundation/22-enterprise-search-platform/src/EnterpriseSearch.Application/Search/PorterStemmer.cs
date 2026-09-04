namespace EnterpriseSearch.Application.Search;

public static class PorterStemmer
{
    public static string Stem(string word)
    {
        if (string.IsNullOrWhiteSpace(word) || word.Length < 3)
        {
            return word;
        }

        var value = word.ToLowerInvariant();
        value = Step1A(value);
        value = Step1B(value);
        value = Step1C(value);
        value = ReplaceSuffix(value, Step2, 1);
        value = ReplaceSuffix(value, Step3, 1);
        value = Step4(value);
        return Step5(value);
    }

    private static string Step1A(string value)
    {
        if (value.EndsWith("sses", StringComparison.Ordinal)) return value[..^2];
        if (value.EndsWith("ies", StringComparison.Ordinal)) return value[..^2];
        if (value.EndsWith("ss", StringComparison.Ordinal)) return value;
        return value.EndsWith('s') ? value[..^1] : value;
    }

    private static string Step1B(string value)
    {
        if (value.EndsWith("eed", StringComparison.Ordinal))
        {
            var eedStem = value[..^3];
            return Measure(eedStem) > 0 ? eedStem + "ee" : value;
        }

        string stem;
        if (value.EndsWith("ed", StringComparison.Ordinal)) stem = value[..^2];
        else if (value.EndsWith("ing", StringComparison.Ordinal)) stem = value[..^3];
        else return value;

        if (!ContainsVowel(stem)) return value;
        if (stem.EndsWith("at", StringComparison.Ordinal) || stem.EndsWith("bl", StringComparison.Ordinal) || stem.EndsWith("iz", StringComparison.Ordinal)) return stem + "e";
        if (EndsDoubleConsonant(stem) && stem[^1] is not ('l' or 's' or 'z')) return stem[..^1];
        return Measure(stem) == 1 && IsCvc(stem) ? stem + "e" : stem;
    }

    private static string Step1C(string value) => value.EndsWith('y') && ContainsVowel(value[..^1]) ? value[..^1] + "i" : value;

    private static readonly (string Suffix, string Replacement)[] Step2 =
    [
        ("ational", "ate"), ("tional", "tion"), ("enci", "ence"), ("anci", "ance"), ("izer", "ize"), ("abli", "able"),
        ("alli", "al"), ("entli", "ent"), ("eli", "e"), ("ousli", "ous"), ("ization", "ize"), ("ation", "ate"),
        ("ator", "ate"), ("alism", "al"), ("iveness", "ive"), ("fulness", "ful"), ("ousness", "ous"), ("aliti", "al"),
        ("iviti", "ive"), ("biliti", "ble")
    ];

    private static readonly (string Suffix, string Replacement)[] Step3 =
    [
        ("icate", "ic"), ("ative", ""), ("alize", "al"), ("iciti", "ic"), ("ical", "ic"), ("ful", ""), ("ness", "")
    ];

    private static string ReplaceSuffix(string value, IEnumerable<(string Suffix, string Replacement)> rules, int minimumMeasure)
    {
        foreach (var (suffix, replacement) in rules.OrderByDescending(rule => rule.Suffix.Length))
        {
            if (!value.EndsWith(suffix, StringComparison.Ordinal)) continue;
            var stem = value[..^suffix.Length];
            return Measure(stem) > minimumMeasure - 1 ? stem + replacement : value;
        }
        return value;
    }

    private static string Step4(string value)
    {
        foreach (var suffix in new[] { "al", "ance", "ence", "er", "ic", "able", "ible", "ant", "ement", "ment", "ent", "ion", "ou", "ism", "ate", "iti", "ous", "ive", "ize" }.OrderByDescending(s => s.Length))
        {
            if (!value.EndsWith(suffix, StringComparison.Ordinal)) continue;
            var stem = value[..^suffix.Length];
            if (suffix == "ion" && stem.Length > 0 && stem[^1] is not ('s' or 't')) return value;
            return Measure(stem) > 1 ? stem : value;
        }
        return value;
    }

    private static string Step5(string value)
    {
        if (value.EndsWith('e'))
        {
            var stem = value[..^1];
            var measure = Measure(stem);
            if (measure > 1 || (measure == 1 && !IsCvc(stem))) value = stem;
        }
        if (value.EndsWith("ll", StringComparison.Ordinal) && Measure(value) > 1) value = value[..^1];
        return value;
    }

    private static bool ContainsVowel(string value) => value.Select((character, index) => IsVowel(value, index)).Any(vowel => vowel);

    private static bool IsVowel(string value, int index)
    {
        var character = value[index];
        if (character is 'a' or 'e' or 'i' or 'o' or 'u') return true;
        return character == 'y' && index > 0 && !IsVowel(value, index - 1);
    }

    private static int Measure(string value)
    {
        var count = 0;
        var wasVowel = false;
        foreach (var index in Enumerable.Range(0, value.Length))
        {
            var vowel = IsVowel(value, index);
            if (wasVowel && !vowel) count++;
            wasVowel = vowel;
        }
        return count;
    }

    private static bool EndsDoubleConsonant(string value) => value.Length > 1 && value[^1] == value[^2] && !IsVowel(value, value.Length - 1);

    private static bool IsCvc(string value)
    {
        if (value.Length < 3) return false;
        var last = value.Length - 1;
        return !IsVowel(value, last - 2) && IsVowel(value, last - 1) && !IsVowel(value, last) && value[last] is not ('w' or 'x' or 'y');
    }
}
