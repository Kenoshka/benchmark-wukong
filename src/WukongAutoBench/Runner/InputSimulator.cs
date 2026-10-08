using System.Runtime.InteropServices;
using WukongAutoBench.Native;

namespace WukongAutoBench.Runner;

/// <summary>
/// Нажатия и клики через SendInput. Клавиши шлются скан-кодами: UE читает ввод через WM_KEYDOWN/Raw Input,
/// и скан-коды надёжнее виртуальных кодов. Ввод отправляется только когда окно игры на переднем плане,
/// чтобы случайно не нажать что-то в другом приложении.
/// </summary>
internal static class InputSimulator
{
    public static void PressKey(ushort virtualKey)
    {
        var scan = (ushort)Win32.MapVirtualKey(virtualKey, 0);
        Send(KeyInput(scan, Win32.KEYEVENTF_SCANCODE));
        Thread.Sleep(60);
        Send(KeyInput(scan, Win32.KEYEVENTF_SCANCODE | Win32.KEYEVENTF_KEYUP));
    }

    /// <summary>Клик левой кнопкой в точке экрана; перед кликом курсор «подводится», чтобы кнопка UMG получила hover.</summary>
    public static void Click(int screenX, int screenY)
    {
        Win32.SetCursorPos(screenX - 2, screenY - 2);
        Thread.Sleep(80);
        Send(MouseInput(Win32.MOUSEEVENTF_MOVE, 2, 2));
        Thread.Sleep(250);
        Send(MouseInput(Win32.MOUSEEVENTF_LEFTDOWN));
        Thread.Sleep(80);
        Send(MouseInput(Win32.MOUSEEVENTF_LEFTUP));
    }

    /// <summary>
    /// Выводит окно на передний план. Windows разрешает SetForegroundWindow только «активному» процессу,
    /// поэтому сначала шлём нажатие Alt — это стандартный обход ограничения.
    /// </summary>
    public static bool TryActivate(IntPtr hwnd)
    {
        if (Win32.GetForegroundWindow() == hwnd) return true;
        if (Win32.IsIconic(hwnd)) Win32.ShowWindow(hwnd, Win32.SW_RESTORE);

        Send(KeyInput(0, 0, Win32.VK_MENU));
        Send(KeyInput(0, Win32.KEYEVENTF_KEYUP, Win32.VK_MENU));
        Win32.SetForegroundWindow(hwnd);
        Win32.BringWindowToTop(hwnd);
        Thread.Sleep(400);
        return Win32.GetForegroundWindow() == hwnd;
    }

    private static Win32.INPUT KeyInput(ushort scan, uint flags, ushort vk = 0) => new()
    {
        type = Win32.INPUT_KEYBOARD,
        U = new Win32.InputUnion { ki = new Win32.KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags } },
    };

    private static Win32.INPUT MouseInput(uint flags, int dx = 0, int dy = 0) => new()
    {
        type = Win32.INPUT_MOUSE,
        U = new Win32.InputUnion { mi = new Win32.MOUSEINPUT { dx = dx, dy = dy, dwFlags = flags } },
    };

    private static void Send(Win32.INPUT input) =>
        Win32.SendInput(1, [input], Marshal.SizeOf<Win32.INPUT>());
}
