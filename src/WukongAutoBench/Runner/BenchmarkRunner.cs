using System.Diagnostics;
using WukongAutoBench.Native;
using WukongAutoBench.Results;
using WukongAutoBench.Steam;

namespace WukongAutoBench.Runner;

internal enum LaunchMode
{
    /// <summary>Напрямую exe из папки бенчмарка (Steam должен быть запущен).</summary>
    Exe,
    /// <summary>Через steam.exe -applaunch 3132990.</summary>
    Steam,
}

internal sealed record RunnerOptions
{
    public LaunchMode Mode { get; init; } = LaunchMode.Exe;
    public string LaunchArgs { get; init; } = "-dx12";
    public TimeSpan WindowTimeout { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan MenuDelay { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan PassTimeout { get; init; } = TimeSpan.FromMinutes(20);
    public int MaxLaunches { get; init; } = 3;

    /// <summary>Кнопка «Тест быстродействия» / «Benchmark» в главном меню (доли клиентской области окна).</summary>
    public (double X, double Y) StartButton { get; init; } = (0.11, 0.455);

    /// <summary>Кнопка «Подтвердить» / «Confirm» в диалоге запуска теста.</summary>
    public (double X, double Y) ConfirmButton { get; init; } = (0.39, 0.59);
}

internal sealed record PassRun(string ResultPath, BenchmarkResult Result, TimeSpan Duration, int Launches);

/// <summary>
/// Один проход: запуск Benchmark Tool → проход по меню → ожидание файла результата → закрытие процесса.
/// У бенчмарка нет ключа командной строки для автостарта теста, поэтому меню проходится эмуляцией ввода:
/// Enter (заставка «нажмите любую кнопку») → клик «Тест быстродействия» → клик «Подтвердить».
/// Последовательность повторяется, пока не появится результат: во время теста Enter и левый клик игнорируются,
/// а перед каждым действием проверяется, не готов ли уже результат (чтобы не нажать «повторить» на экране итогов).
/// </summary>
internal sealed class BenchmarkRunner(BenchmarkInstall install, RunnerOptions options, IReadOnlyList<string> resultDirs)
{
    private static readonly string[] ProcessNames = ["b1-Win64-Shipping", "b1"];

    public static bool IsRunning() => ForEachGameProcess(_ => { }) > 0;

    /// <summary>
    /// Принудительно завершает бенчмарк. Именно kill, а не мягкое закрытие: при штатном выходе игра
    /// перезаписывает GameUserSettings.ini, и это мешает вернуть исходный конфиг.
    /// </summary>
    public static void KillAll()
    {
        ForEachGameProcess(p =>
        {
            try
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit(15_000);
            }
            catch (InvalidOperationException) { } // уже завершился
            catch (System.ComponentModel.Win32Exception) { }
        });
    }

    public async Task<PassRun> RunAsync(string title, CancellationToken ct)
    {
        if (IsRunning())
            throw new InvalidOperationException("Benchmark Tool уже запущен. Закройте его и запустите утилиту снова.");

        var watcher = new ResultWatcher(resultDirs);
        watcher.Snapshot();
        var started = Stopwatch.StartNew();
        var launches = 0;

        try
        {
            while (true)
            {
                launches++;
                var hwnd = await LaunchAndWaitForWindowAsync(ct);
                Log.Info($"Окно бенчмарка найдено ({DescribeWindow(hwnd)}), жду загрузку меню {options.MenuDelay.TotalSeconds:0} с…");
                await Task.Delay(options.MenuDelay, ct);

                Log.Info($"[{title}] Прохожу меню: Enter → «Тест быстродействия» → «Подтвердить». Не трогайте мышь и клавиатуру.");
                var navigations = 0;
                var nextNavigation = TimeSpan.Zero;
                var sinceLaunch = Stopwatch.StartNew();

                while (IsRunning())
                {
                    ct.ThrowIfCancellationRequested();
                    if (started.Elapsed > options.PassTimeout)
                        throw new TimeoutException($"Бенчмарк не выдал результат за {options.PassTimeout.TotalMinutes:0} мин.");

                    if (watcher.TryGetNew() is { } found)
                        return new PassRun(found.Path, found.Result, started.Elapsed, launches);

                    if (sinceLaunch.Elapsed >= nextNavigation)
                    {
                        if (Navigate(() => watcher.TryGetNew() is not null))
                            navigations++;
                        // Сначала пробуем часто (меню может грузиться дольше ожидаемого), потом редко — тест уже идёт.
                        nextNavigation = sinceLaunch.Elapsed + (navigations < 6 ? TimeSpan.FromSeconds(12) : TimeSpan.FromSeconds(45));
                    }

                    await Task.Delay(1000, ct);
                }

                // Процесс завершился сам: результат мог успеть записаться, иначе это вылет или промах по кнопке «Выход».
                await Task.Delay(2000, ct);
                for (var i = 0; i < 3; i++)
                {
                    if (watcher.TryGetNew() is { } late)
                        return new PassRun(late.Path, late.Result, started.Elapsed, launches);
                    await Task.Delay(1000, ct);
                }

                if (launches >= options.MaxLaunches)
                    throw new InvalidOperationException(
                        $"Бенчмарк закрылся без результата {launches} раз подряд (возможен вылет при этих настройках).");
                Log.Warn("Бенчмарк закрылся, не записав результат. Перезапускаю…");
            }
        }
        finally
        {
            KillAll();
        }
    }

    /// <summary>
    /// Первый запуск: если GameUserSettings.ini ещё нет, запускаем бенчмарк, даём ему создать конфиг
    /// и закрываем штатно (WM_CLOSE), чтобы игра успела сохранить файл.
    /// </summary>
    public async Task CreateConfigAsync(Func<bool> configExists, CancellationToken ct)
    {
        if (IsRunning())
            throw new InvalidOperationException("Benchmark Tool уже запущен. Закройте его и запустите утилиту снова.");
        try
        {
            var hwnd = await LaunchAndWaitForWindowAsync(ct);
            for (var i = 0; i < 60 && !configExists(); i++)
                await Task.Delay(1000, ct);

            if (FindGameWindow() is var current && current != IntPtr.Zero) hwnd = current;
            Win32.PostMessage(hwnd, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            for (var i = 0; i < 30 && IsRunning(); i++)
                await Task.Delay(1000, ct);
        }
        finally
        {
            KillAll();
        }
    }

    private async Task<IntPtr> LaunchAndWaitForWindowAsync(CancellationToken ct)
    {
        Launch();
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < options.WindowTimeout)
        {
            ct.ThrowIfCancellationRequested();
            var hwnd = FindGameWindow();
            if (hwnd != IntPtr.Zero) return hwnd;
            await Task.Delay(1000, ct);
        }
        throw new TimeoutException($"Окно бенчмарка не появилось за {options.WindowTimeout.TotalMinutes:0} мин.");
    }

    private void Launch()
    {
        if (options.Mode == LaunchMode.Steam && install.SteamExe != null)
        {
            Log.Info($"Запуск через Steam: steam.exe -applaunch {SteamLocator.BenchmarkAppId} {options.LaunchArgs}");
            Process.Start(new ProcessStartInfo(install.SteamExe, $"-applaunch {SteamLocator.BenchmarkAppId} {options.LaunchArgs}")
            {
                UseShellExecute = true,
            });
            return;
        }

        var exe = install.FindExecutable()
                  ?? throw new FileNotFoundException($"Не найден исполняемый файл бенчмарка в {install.InstallDir}");
        Log.Info($"Запуск: {exe} {options.LaunchArgs}");
        Process.Start(new ProcessStartInfo(exe, options.LaunchArgs)
        {
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            UseShellExecute = true,
        });
    }

    /// <summary>
    /// Одна попытка пройти меню. Возвращает false, если окно не удалось вывести на передний план
    /// (тогда ввод не отправляется вовсе — он ушёл бы в чужое приложение).
    /// </summary>
    private bool Navigate(Func<bool> resultReady)
    {
        if (!TryAct(r => InputSimulator.PressKey(Win32.VK_RETURN))) return false;
        Thread.Sleep(2500);
        if (resultReady()) return true;

        if (!TryAct(r => InputSimulator.Click(r.X(options.StartButton.X), r.Y(options.StartButton.Y)))) return false;
        Thread.Sleep(2500);
        if (resultReady()) return true;

        TryAct(r => InputSimulator.Click(r.X(options.ConfirmButton.X), r.Y(options.ConfirmButton.Y)));
        return true;
    }

    private static bool TryAct(Action<ScreenRect> action)
    {
        var hwnd = FindGameWindow();
        if (hwnd == IntPtr.Zero || !InputSimulator.TryActivate(hwnd))
        {
            Log.Warn("Не удалось вывести окно бенчмарка на передний план — пропускаю шаг.");
            return false;
        }
        if (GetClientScreenRect(hwnd) is not { } rect || rect.Width < 320) return false;
        action(rect);
        return true;
    }

    private readonly record struct ScreenRect(int Left, int Top, int Width, int Height)
    {
        public int X(double fraction) => Left + (int)Math.Round(Width * fraction);
        public int Y(double fraction) => Top + (int)Math.Round(Height * fraction);
    }

    private static ScreenRect? GetClientScreenRect(IntPtr hwnd)
    {
        if (!Win32.GetClientRect(hwnd, out var rc)) return null;
        var origin = new Win32.POINT();
        if (!Win32.ClientToScreen(hwnd, ref origin)) return null;
        return new ScreenRect(origin.X, origin.Y, rc.Width, rc.Height);
    }

    private static string DescribeWindow(IntPtr hwnd)
    {
        var r = GetClientScreenRect(hwnd);
        var title = Win32.GetWindowTitle(hwnd);
        return r is { } rect ? $"«{title}», {rect.Width}x{rect.Height}" : $"«{title}»";
    }

    /// <summary>Самое большое видимое окно процессов бенчмарка (окно может пересоздаваться при смене режима экрана).</summary>
    private static IntPtr FindGameWindow()
    {
        var pids = new HashSet<uint>();
        ForEachGameProcess(p => pids.Add((uint)p.Id));
        if (pids.Count == 0) return IntPtr.Zero;

        var best = IntPtr.Zero;
        long bestArea = 0;
        Win32.EnumWindows((h, _) =>
        {
            Win32.GetWindowThreadProcessId(h, out var pid);
            if (!pids.Contains(pid) || !Win32.IsWindowVisible(h)) return true;
            if (!Win32.GetClientRect(h, out var rc)) return true;
            long area = (long)rc.Width * rc.Height;
            if (area > bestArea) (best, bestArea) = (h, area);
            return true;
        }, IntPtr.Zero);

        return bestArea >= 320 * 240 ? best : IntPtr.Zero;
    }

    private static int ForEachGameProcess(Action<Process> action)
    {
        var count = 0;
        foreach (var name in ProcessNames)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                using (p)
                {
                    count++;
                    action(p);
                }
            }
        }
        return count;
    }
}
