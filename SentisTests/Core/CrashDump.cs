using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using NLog;

namespace SentisTests.Core
{
    /// <summary>
    /// A full dump of the server at the moment of a fatal managed exception.
    ///
    /// Torch handles an unhandled exception itself (Torch.Server.Initializer.HandleException): it logs it, shows the
    /// "Torch Fatal Error" box and kills its own process. Windows error reporting never sees such a crash, so its
    /// dump folder stays empty, and the box is closed by the crash watcher before Torch's own minidump is of any use.
    /// A prefix on that handler runs procdump on this process and waits for the dump, while the throwing thread
    /// still holds the exception (<c>!pe</c> in the dump) and every other thread is where it was.
    /// Native crashes (access violations in Havok, clr) do go to Windows error reporting: crash_dumps.ps1.
    /// </summary>
    public static class CrashDump
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private static readonly Harmony Harmony = new Harmony("SentisTests.CrashDump");
        private const int KeepDumps = 3;
        private static int _taken;

        public static string Folder => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CrashDumps");

        public static void Install()
        {
            try
            {
                var initializer = Type.GetType("Torch.Server.Initializer, Torch.Server", true);
                var handler = initializer.GetMethod("HandleException", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                              ?? throw new MissingMethodException(initializer.FullName, "HandleException");
                Harmony.Patch(handler, prefix: new HarmonyMethod(typeof(CrashDump).GetMethod(nameof(HandleExceptionPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
                Log.Info("CrashDump: a fatal exception is dumped to " + Folder + " by " + (ProcDump() ?? "procdump (not found)"));
            }
            catch (Exception e)
            {
                Log.Error(e, "CrashDump could not be installed; fatal exceptions will not be dumped");
            }
        }

        private static void HandleExceptionPrefix(UnhandledExceptionEventArgs e)
        {
            // One dump per process: a second fatal exception while the first is handled adds nothing.
            if (System.Threading.Interlocked.Exchange(ref _taken, 1) != 0) return;
            try
            {
                Take("crash", (e.ExceptionObject as Exception)?.GetType().Name ?? "unknown");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "CrashDump: the dump failed");
            }
        }

        /// <summary>Writes a full dump of this process and waits for it; returns its path, or null.</summary>
        public static string Take(string prefix, string reason)
        {
            var procdump = ProcDump();
            if (procdump == null)
            {
                Log.Error("CrashDump: procdump64.exe not found, no dump");
                return null;
            }
            Directory.CreateDirectory(Folder);
            Prune(prefix);
            var path = Path.Combine(Folder, prefix + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".dmp");
            Log.Fatal("CrashDump: " + reason + ", writing a full dump to " + path);
            LogManager.Flush();
            var started = Stopwatch.StartNew();
            using (var process = Process.Start(new ProcessStartInfo(procdump,
                       "-accepteula -ma " + Process.GetCurrentProcess().Id + " \"" + path + "\"")
                   {
                       UseShellExecute = false,
                       CreateNoWindow = true,
                       RedirectStandardOutput = true,
                       RedirectStandardError = true
                   }))
            {
                process.StandardOutput.ReadToEnd();
                process.WaitForExit();
            }
            Log.Fatal("CrashDump: " + (File.Exists(path) ? "dump written" : "no dump written") + " in " +
                      started.Elapsed.TotalSeconds.ToString("F0") + " s");
            LogManager.Flush();
            return File.Exists(path) ? path : null;
        }

        private static string ProcDump()
        {
            var candidates = new[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "procdump64.exe"),
                @"C:\SE\tools\procdump64.exe"
            };
            return candidates.FirstOrDefault(File.Exists);
        }

        // A full dump is the size of the heap (15-20 GB): only the latest few are kept.
        private static void Prune(string prefix)
        {
            foreach (var old in new DirectoryInfo(Folder).GetFiles(prefix + "_*.dmp")
                         .OrderByDescending(f => f.CreationTimeUtc).Skip(KeepDumps - 1))
            {
                try { old.Delete(); }
                catch (Exception ex) { Log.Warn("CrashDump: could not delete " + old.Name + ": " + ex.Message); }
            }
        }
    }
}
