namespace WukongAutoBench.Config;

/// <summary>
/// Резервная копия, применение профилей и восстановление GameUserSettings.ini.
/// Каждый профиль накладывается на исходный файл пользователя, а не на результат предыдущего прохода.
/// Копия лежит рядом с ini (*.wukongautobench.bak), поэтому, если утилиту убили посреди прогона,
/// исходные настройки восстановятся при следующем запуске.
/// </summary>
internal sealed class GameConfigFiles
{
    private const string BackupSuffix = ".wukongautobench.bak";
    private readonly Dictionary<string, byte[]> _originals = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private bool _restored = true;

    public GameConfigFiles(IReadOnlyList<string> paths) => Paths = paths;

    public IReadOnlyList<string> Paths { get; }

    /// <summary>Восстанавливает ini из копий, оставшихся после аварийно прерванного запуска.</summary>
    public static IReadOnlyList<string> RecoverStaleBackups(IEnumerable<string> candidatePaths)
    {
        var recovered = new List<string>();
        foreach (var path in candidatePaths)
        {
            var backup = path + BackupSuffix;
            if (!File.Exists(backup)) continue;
            File.Copy(backup, path, overwrite: true);
            File.Delete(backup);
            recovered.Add(path);
        }
        return recovered;
    }

    public void Backup()
    {
        lock (_lock)
        {
            foreach (var path in Paths)
            {
                var bytes = File.ReadAllBytes(path);
                _originals[path] = bytes;
                File.WriteAllBytes(path + BackupSuffix, bytes);
            }
            _restored = false;
        }
    }

    public byte[] Original(string path) => _originals.TryGetValue(path, out var b) ? b : File.ReadAllBytes(path);

    /// <summary>Пишет профиль во все найденные копии конфига. Возвращает изменения по первому (основному) файлу.</summary>
    public IReadOnlyList<SettingChange> Apply(BenchmarkProfile profile)
    {
        IReadOnlyList<SettingChange>? primary = null;
        foreach (var path in Paths)
        {
            var doc = IniDocument.FromBytes(Original(path));
            var changes = SettingsApplier.Apply(doc, profile);
            primary ??= changes;
            var attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.ReadOnly))
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            doc.Save(path);
        }
        return primary ?? [];
    }

    /// <summary>Возвращает исходные файлы. Безопасно вызывать повторно (в т.ч. из обработчика выхода процесса).</summary>
    public void Restore()
    {
        lock (_lock)
        {
            if (_restored) return;
            foreach (var (path, bytes) in _originals)
            {
                File.WriteAllBytes(path, bytes);
                File.Delete(path + BackupSuffix);
            }
            _restored = true;
        }
    }
}
