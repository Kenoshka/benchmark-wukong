using System.Text;

namespace WukongAutoBench.Config;

/// <summary>
/// Минимальный редактор ini в стиле Unreal Engine, который меняет только нужные строки:
/// порядок, комментарии, кодировка (с BOM или без) и переводы строк остаются как были.
/// Ключи сравниваются без учёта регистра, как в UE.
/// </summary>
internal sealed class IniDocument
{
    private readonly List<string> _lines;
    private readonly byte[] _preamble;
    private readonly Encoding _encoding;
    private readonly string _newLine;
    private readonly bool _trailingNewLine;

    private IniDocument(List<string> lines, Encoding encoding, byte[] preamble, string newLine, bool trailingNewLine)
    {
        _lines = lines;
        _encoding = encoding;
        _preamble = preamble;
        _newLine = newLine;
        _trailingNewLine = trailingNewLine;
    }

    public static IniDocument Load(string path) => FromBytes(File.ReadAllBytes(path));

    public static IniDocument FromBytes(byte[] bytes)
    {
        Encoding encoding;
        int bomLength;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            (encoding, bomLength) = (new UTF8Encoding(false), 3);
        else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            (encoding, bomLength) = (new UnicodeEncoding(false, false), 2);
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            (encoding, bomLength) = (new UnicodeEncoding(true, false), 2);
        else
            (encoding, bomLength) = (new UTF8Encoding(false), 0);

        var text = encoding.GetString(bytes, bomLength, bytes.Length - bomLength);
        return Parse(text, encoding, bytes[..bomLength]);
    }

    public static IniDocument Parse(string text) => Parse(text, new UTF8Encoding(false), []);

    private static IniDocument Parse(string text, Encoding encoding, byte[] preamble)
    {
        var newLine = text.Contains("\r\n") ? "\r\n" : "\n";
        var trailing = text.EndsWith('\n');
        var body = trailing ? text[..^(text.EndsWith("\r\n") ? 2 : 1)] : text;
        var lines = body.Length == 0 ? new List<string>() : body.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        return new IniDocument(lines, encoding, preamble, newLine, trailing);
    }

    public byte[] ToBytes() => [.. _preamble, .. _encoding.GetBytes(ToString())];

    public void Save(string path) => File.WriteAllBytes(path, ToBytes());

    public override string ToString() =>
        string.Join(_newLine, _lines) + (_trailingNewLine ? _newLine : "");

    public IEnumerable<string> Sections =>
        _lines.Select(TryGetSectionName).Where(s => s != null).Select(s => s!);

    /// <summary>Значение ключа в секции (первое вхождение) или null.</summary>
    public string? Get(string section, string key)
    {
        foreach (var (index, _) in KeysInSection(section, key))
            return ValueOf(_lines[index]);
        return null;
    }

    /// <summary>Первое вхождение ключа в любой секции.</summary>
    public (string Section, string Value)? Find(string key)
    {
        string? current = null;
        foreach (var line in _lines)
        {
            var header = TryGetSectionName(line);
            if (header != null) { current = header; continue; }
            if (current != null && KeyOf(line) is { } k && k.Equals(key, StringComparison.OrdinalIgnoreCase))
                return (current, ValueOf(line));
        }
        return null;
    }

    /// <summary>
    /// Ставит значение ключа в секции. Если ключа нет и <paramref name="addIfMissing"/> — дописывает его
    /// в конец секции (секция создаётся при необходимости). Возвращает прежнее значение (null, если ключа не было).
    /// </summary>
    public string? Set(string section, string key, string value, bool addIfMissing = true)
    {
        foreach (var (index, actualKey) in KeysInSection(section, key))
        {
            var old = ValueOf(_lines[index]);
            _lines[index] = $"{actualKey}={value}";
            return old;
        }

        if (!addIfMissing) return null;

        var range = FindSection(section);
        if (range == null)
        {
            if (_lines.Count > 0 && _lines[^1].Trim().Length > 0) _lines.Add("");
            _lines.Add($"[{section}]");
            _lines.Add($"{key}={value}");
            return null;
        }

        // Вставляем после последней непустой строки секции, чтобы не разрывать её пустой строкой.
        var insertAt = range.Value.End;
        while (insertAt > range.Value.Header + 1 && _lines[insertAt - 1].Trim().Length == 0) insertAt--;
        _lines.Insert(insertAt, $"{key}={value}");
        return null;
    }

    /// <summary>Меняет значение ключа во всех секциях, где он встречается. Возвращает число изменённых строк.</summary>
    public int SetEverywhere(string key, string value)
    {
        var count = 0;
        for (var i = 0; i < _lines.Count; i++)
        {
            if (KeyOf(_lines[i]) is { } k && k.Equals(key, StringComparison.OrdinalIgnoreCase)
                && TryGetSectionName(_lines[i]) == null)
            {
                _lines[i] = $"{k}={value}";
                count++;
            }
        }
        return count;
    }

    public IEnumerable<KeyValuePair<string, string>> GetSection(string section)
    {
        var range = FindSection(section);
        if (range == null) yield break;
        for (var i = range.Value.Header + 1; i < range.Value.End; i++)
            if (KeyOf(_lines[i]) is { } k)
                yield return new(k, ValueOf(_lines[i]));
    }

    private IEnumerable<(int Index, string Key)> KeysInSection(string section, string key)
    {
        var range = FindSection(section);
        if (range == null) yield break;
        for (var i = range.Value.Header + 1; i < range.Value.End; i++)
            if (KeyOf(_lines[i]) is { } k && k.Equals(key, StringComparison.OrdinalIgnoreCase))
                yield return (i, k);
    }

    private (int Header, int End)? FindSection(string section)
    {
        for (var i = 0; i < _lines.Count; i++)
        {
            if (!string.Equals(TryGetSectionName(_lines[i]), section, StringComparison.OrdinalIgnoreCase)) continue;
            var end = i + 1;
            while (end < _lines.Count && TryGetSectionName(_lines[end]) == null) end++;
            return (i, end);
        }
        return null;
    }

    private static string? TryGetSectionName(string line)
    {
        var t = line.Trim();
        return t.Length >= 2 && t[0] == '[' && t[^1] == ']' ? t[1..^1].Trim() : null;
    }

    private static string? KeyOf(string line)
    {
        var t = line.TrimStart();
        if (t.Length == 0 || t[0] is ';' or '#' or '[') return null;
        var eq = t.IndexOf('=');
        return eq <= 0 ? null : t[..eq].Trim();
    }

    private static string ValueOf(string line)
    {
        var eq = line.IndexOf('=');
        return eq < 0 ? "" : line[(eq + 1)..];
    }
}
