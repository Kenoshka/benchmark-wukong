using System.Text.RegularExpressions;

namespace WukongAutoBench.Config;

/// <summary>
/// Строка <c>UISettingData=(("Key", "Value"),("Key2", "Value2"),...)</c> из GameUserSettings.ini —
/// это значения, которые показывает меню бенчмарка. При старте игра применяет именно их,
/// поэтому их нужно менять синхронно с [ScalabilityGroups].
/// Формат (пробелы, кавычки) берётся из исходной строки и сохраняется при записи.
/// </summary>
internal sealed partial class UiSettingData
{
    [GeneratedRegex("""\(\s*"(?<k>[^"]*)"(?<sep>\s*,\s*)(?<q>"?)(?<v>[^")]*)\k<q>\s*\)""")]
    private static partial Regex PairRegex();

    private readonly List<(string Key, string Value)> _pairs;
    private readonly string _keyValueSeparator;
    private readonly string _pairSeparator;
    private readonly string _quote;

    private UiSettingData(List<(string, string)> pairs, string keyValueSeparator, string pairSeparator, string quote)
    {
        _pairs = pairs;
        _keyValueSeparator = keyValueSeparator;
        _pairSeparator = pairSeparator;
        _quote = quote;
    }

    public static UiSettingData Parse(string raw)
    {
        var matches = PairRegex().Matches(raw);
        var pairs = matches.Select(m => (m.Groups["k"].Value, m.Groups["v"].Value)).ToList();

        var kvSep = matches.Count > 0 ? matches[0].Groups["sep"].Value : ", ";
        var quote = matches.Count > 0 ? matches[0].Groups["q"].Value : "\"";
        var pairSep = matches.Count > 1
            ? raw[(matches[0].Index + matches[0].Length)..matches[1].Index]
            : ",";
        if (pairSep.Trim() != ",") pairSep = ",";

        return new UiSettingData(pairs, kvSep, pairSep, quote);
    }

    public IReadOnlyList<(string Key, string Value)> Pairs => _pairs;

    public string? this[string key]
    {
        get
        {
            var i = IndexOf(key);
            return i < 0 ? null : _pairs[i].Value;
        }
    }

    public bool Contains(string key) => IndexOf(key) >= 0;

    /// <summary>Ставит значение; если ключа нет и <paramref name="addIfMissing"/> — добавляет пару в конец.</summary>
    public string? Set(string key, string value, bool addIfMissing = true)
    {
        var i = IndexOf(key);
        if (i >= 0)
        {
            var old = _pairs[i].Value;
            _pairs[i] = (_pairs[i].Key, value);
            return old;
        }
        if (addIfMissing) _pairs.Add((key, value));
        return null;
    }

    public string Serialize() =>
        "(" + string.Join(_pairSeparator,
            _pairs.Select(p => $"(\"{p.Key}\"{_keyValueSeparator}{_quote}{p.Value}{_quote})")) + ")";

    private int IndexOf(string key) =>
        _pairs.FindIndex(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
}
