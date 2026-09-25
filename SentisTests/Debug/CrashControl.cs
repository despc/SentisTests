using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace SentisTests.Debug
{
    /// <summary>
    /// The plugin's tab in Torch: buttons that bring the server down the way a native crash does, to check that
    /// crash dumps get written (tools/crash_dumps.ps1). Neither is anything .NET can catch or Torch can log:
    /// the process is simply gone, and Windows Error Reporting writes its dump.
    ///
    ///  * a stack overflow (0xC00000FD) - what took the test server down in Havok;
    ///  * an access violation (0xC0000005) in native code: a native thread reading memory a process may not
    ///    touch, with no managed frame anywhere on it for the runtime to turn it into an exception.
    /// </summary>
    public sealed class CrashControl : UserControl
    {
        public CrashControl()
        {
            var panel = new StackPanel { Margin = new Thickness(10) };
            panel.Children.Add(new TextBlock
            {
                Text = "Проверка дампов при падении: кнопки роняют сервер нативной ошибкой, которую .NET не ловит и " +
                       "Torch не логирует. Мир не сохраняется. Дамп должен появиться в папке, заданной " +
                       "tools/crash_dumps.ps1 (проверить: crash_dumps.ps1 -Status).",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10),
            });
            panel.Children.Add(CrashButton("Уронить сервер", "переполнение стека (0xC00000FD)", StackOverflow));
            panel.Children.Add(CrashButton("Уронить иначе", "чтение запрещённой памяти в нативном коде (0xC0000005)", AccessViolation));
            Content = panel;
        }

        /// <summary>
        /// A button that asks first on itself - the second press within five seconds does it - rather than in a
        /// message box: the stand's crash watchdog takes any dialog of the server's for a crash and ends the process.
        /// </summary>
        private static UIElement CrashButton(string idle, string what, Action crash)
        {
            const string armed = "Точно уронить? Нажми ещё раз";
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            var button = new Button { Content = idle, Width = 260 };
            var disarm = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            disarm.Tick += (s, e) => { disarm.Stop(); button.Content = idle; };
            button.Click += (s, e) =>
            {
                if ((string)button.Content != armed)
                {
                    button.Content = armed;
                    disarm.Start();
                    return;
                }
                disarm.Stop();
                button.Content = "Роняю...";
                button.IsEnabled = false;
                crash();
            };
            row.Children.Add(button);
            row.Children.Add(new TextBlock { Text = what, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) });
            return row;
        }

        /// <summary>A stack overflow on a thread of its own: the whole process goes down with it at once.</summary>
        public static void StackOverflow()
        {
            Announce("a stack overflow");
            var thread = new Thread(() => Deeper(0), 256 * 1024) { Name = "SentisTests crash", IsBackground = true };
            thread.Start();
        }

        // not a tail call (the addition after it), not inlined: every level takes its own frame
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Deeper(int depth) => Deeper(depth + 1) + depth;

        /// <summary>
        /// A native thread whose very first instruction reads memory no process may read: the C runtime's strlen,
        /// started directly by CreateThread, on an address in the kernel's half of the address space. There is
        /// no managed code on that thread, so the runtime never sees the fault - it goes straight to Windows.
        /// </summary>
        public static void AccessViolation()
        {
            var strlen = GetProcAddress(LoadLibrary("msvcrt.dll"), "strlen");
            if (strlen == IntPtr.Zero) throw new InvalidOperationException("strlen not found in msvcrt.dll");
            Announce("an access violation in native code");
            var forbidden = new IntPtr(unchecked((long)0xFFFF800000000000UL));
            CreateThread(IntPtr.Zero, UIntPtr.Zero, strlen, forbidden, 0, out _);
        }

        private static void Announce(string how)
        {
            SentisTestsPlugin.Log.Warn($"SentisTests: bringing the server down on purpose ({how}) to check crash dumps");
            NLog.LogManager.Flush();
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string name);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateThread(IntPtr attributes, UIntPtr stackSize, IntPtr start, IntPtr parameter, uint flags, out uint threadId);
    }
}
