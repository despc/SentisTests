using System;
using System.Text;
using System.Reflection;
using HarmonyLib;
using NLog;
using Sandbox.Game.Entities;
using VRage.Game.Entity;

namespace SentisTests.Core
{
    /// <summary>
    /// Diagnostic: something on the server closes freshly spawned ST-* grids within
    /// a second, silently. This patches MyEntity.Close() and dumps the caller stack
    /// for every test entity being closed, so the culprit subsystem gets a name.
    /// </summary>
    public static class KillerTrace
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private static bool _installed;

        public static void Install()
        {
            if (_installed) return;
            _installed = true;
            try
            {
                var instance = new Harmony("sentis.tests.killertrace");
                var close = AccessTools.Method(typeof(MyEntity), "Close");
                var prefix = AccessTools.Method(typeof(ClosePatch), "Prefix");
                instance.Patch(close, new HarmonyMethod(prefix), null);
                Log.Info("[STKILL] MyEntity.Close tracing installed");
            }
            catch (Exception e)
            {
                Log.Error(e, "[STKILL] install failed");
            }
        }

        [HarmonyPatch(typeof(MyEntity), "Close")]
        public static class ClosePatch
        {
            static void Prefix(MyEntity __instance)
            {
                try
                {
                    if (__instance.MarkedForClose) return;      // only the first Close matters
                    var name = __instance.DisplayName;
                    if (string.IsNullOrEmpty(name) || !name.StartsWith("ST-47351-")) return;

                    var sb = new StringBuilder();
                    sb.Append("[STKILL] Close(").Append(name).Append(") id=").Append(__instance.EntityId);
                    sb.Append(" type=").Append(__instance.GetType().Name).AppendLine();
                    var trace = Environment.StackTrace;
                    var lines = trace.Split('\n');
                    for (int i = 3; i < lines.Length && i < 28; i++)
                        sb.Append(lines[i]);
                    Log.Info(sb.ToString());
                }
                catch
                {
                    // tracing must never be the thing that breaks the server
                }
            }
        }
    }
}
