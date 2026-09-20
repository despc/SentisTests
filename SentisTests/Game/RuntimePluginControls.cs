using System;
using System.Linq;
using System.Reflection;

namespace SentisTests.Game
{
    internal static class RuntimePluginControls
    {
        private static Type PluginType(string assemblyName, string typeName)
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a =>
                string.Equals(a.GetName().Name, assemblyName, StringComparison.Ordinal));
            return assembly?.GetType(typeName, true);
        }

        private static object Config(Type pluginType)
        {
            return pluginType.GetProperty("Config", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null) ?? throw new InvalidOperationException(pluginType.FullName + ".Config is unavailable");
        }

        public static bool FreezerEnabled
        {
            get
            {
                var type = PluginType("SentisOptimisations",
                    "SentisOptimisationsPlugin.SentisOptimisationsPlugin");
                if (type == null) return false; // plugin not loaded (isolation runs)
                return (bool)Config(type).GetType().GetProperty("FreezerEnabled").GetValue(Config(type));
            }
        }

        public static void SetFreezerEnabled(bool enabled)
        {
            var type = PluginType("SentisOptimisations",
                "SentisOptimisationsPlugin.SentisOptimisationsPlugin");
            if (type == null) return; // plugin not loaded (isolation runs)
            var config = Config(type);
            config.GetType().GetProperty("FreezerEnabled").SetValue(config, enabled);
            var save = type.GetMethod("SaveConfig", BindingFlags.Public | BindingFlags.Static);
            if (save == null)
                throw new InvalidOperationException(type.FullName + ".SaveConfig() is unavailable");
            save.Invoke(null, null);
        }

        public static string AntifreezeBlocksSubtypes
        {
            get
            {
                var type = PluginType("SentisOptimisations", "SentisOptimisationsPlugin.SentisOptimisationsPlugin");
                return (string)Config(type).GetType().GetProperty("AntifreezeBlocksSubtypes").GetValue(Config(type));
            }
        }

        public static void SetAntifreezeBlocksSubtypes(string subtypes)
        {
            var type = PluginType("SentisOptimisations", "SentisOptimisationsPlugin.SentisOptimisationsPlugin");
            var config = Config(type);
            config.GetType().GetProperty("AntifreezeBlocksSubtypes").SetValue(config, subtypes);
            type.GetMethod("SaveConfig", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
        }

        /// <summary>Public static field or property of the SentisOptimisations FrozenGridSaveCache.</summary>
        public static object FrozenGridSaveCacheStat(string name)
        {
            var type = PluginType("SentisOptimisations", "SentisOptimisationsPlugin.Freezer.FrozenGridSaveCache");
            return type.GetField(name, BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                   ?? throw new InvalidOperationException("FrozenGridSaveCache." + name + " is unavailable");
        }

        /// <summary>Grid stream builder cache counters since the last call (SentisOptimisations), or "-".</summary>
        public static string TakeGridStreamBuilderStats()
        {
            try
            {
                var type = PluginType("SentisOptimisations", "SentisOptimisationsPlugin.GridStreamBuilders");
                long Take(string name)
                {
                    var field = type.GetField(name, BindingFlags.Public | BindingFlags.Static);
                    if (field == null) return 0;
                    var value = (long)field.GetValue(null);
                    field.SetValue(null, 0L);
                    return value;
                }
                var ticksToMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                var built = Take("Builds");
                var reused = Take("Reuses");
                var blocks = Take("BlockRebuilds");
                var structure = Take("StructureRebuilds");
                var buildMs = Take("BuildTicks") * ticksToMs;
                var refreshMs = Take("RefreshTicks") * ticksToMs;
                var byEvent = Take("StaleByEvent");
                var byDirty = Take("StaleByDirtyBlocks");
                var indexMismatch = Take("IndexMismatches");
                return "grid builders built=" + built + " (" + buildMs.ToString("F0") + "ms, " + structure +
                       " rebuilt: " + byEvent + " changed, " + byDirty + " too many dirty blocks, " + indexMismatch +
                       " index mismatch) reused=" + reused + " (" + refreshMs.ToString("F0") + "ms, " + blocks + " blocks rebuilt)";
            }
            catch (Exception)
            {
                return "-";
            }
        }

        /// <summary>Inventory sync counters since the last call: updates written, updates left for a later frame.</summary>
        public static string TakeInventoryDeltaStats()
        {
            try
            {
                var type = PluginType("SentisOptimisations", "Optimizer.Optimizations.IdleInventorySync");
                long Take(string name)
                {
                    var field = type.GetField(name, BindingFlags.Public | BindingFlags.Static);
                    if (field == null) return 0;
                    var value = (long)field.GetValue(null);
                    field.SetValue(null, 0L);
                    return value;
                }
                return "inventory syncs delayed=" + Take("Delayed") + " kept queued=" + Take("Kept") + " normal=" + Take("Normal") + " woken up=" + Take("WokenUp");
            }
            catch (Exception)
            {
                return "-";
            }
        }

        /// <summary>Inventories of this client the server has queued, and how many are due within so many frames.</summary>
        public static (long Queued, long Due) InventoryQueueState(VRage.Network.MyClientStateBase state, long withinFrames)
        {
            var type = PluginType("SentisOptimisations", "Optimizer.Optimizations.IdleInventorySync");
            var method = type?.GetMethod("InventoryQueueState", BindingFlags.Public | BindingFlags.Static);
            if (method == null) return (0, 0); // plugin not loaded (isolation runs)
            var counts = (long[])method.Invoke(null, new object[] { state, withinFrames });
            return (counts[0], counts[1]);
        }

        /// <summary>Reads and resets IdleInventorySync's wake counter; 0 when the plugin is absent.</summary>
        public static long TakeIdleInventoryWake()
        {
            var type = PluginType("SentisOptimisations", "Optimizer.Optimizations.IdleInventorySync");
            var method = type?.GetMethod("TakeWokenUp", BindingFlags.Public | BindingFlags.Static);
            return method == null ? 0L : (long)method.Invoke(null, null);
        }

        /// <summary>Compares the state group client index with the vanilla scan (slow; test only).</summary>
        public static void SetStateGroupIndexVerify(bool verify)
        {
            var type = PluginType("SentisOptimisations", "Optimizer.Optimizations.StateGroupClients");
            if (type == null) return; // plugin not loaded (isolation runs)
            type.GetField("VerifyIndex", BindingFlags.Public | BindingFlags.Static).SetValue(null, verify);
            if (!verify) return;
            type.GetField("VerifyMismatches", BindingFlags.Public | BindingFlags.Static).SetValue(null, 0L);
            type.GetField("VerifyFirstDifference", BindingFlags.Public | BindingFlags.Static).SetValue(null, null);
        }

        public static (long Mismatches, string FirstDifference) StateGroupIndexVerifyResult()
        {
            var type = PluginType("SentisOptimisations", "Optimizer.Optimizations.StateGroupClients");
            if (type == null) return (0, null); // plugin not loaded (isolation runs)
            return ((long)type.GetField("VerifyMismatches", BindingFlags.Public | BindingFlags.Static).GetValue(null),
                (string)type.GetField("VerifyFirstDifference", BindingFlags.Public | BindingFlags.Static).GetValue(null));
        }

        /// <summary>Dirty group counters since the last call: queued, unique applied, client syncs scheduled.</summary>
        public static string TakeStateGroupStats()
        {
            try
            {
                var type = PluginType("SentisOptimisations", "Optimizer.Optimizations.StateGroupClients");
                long Take(string name)
                {
                    var field = type.GetField(name, BindingFlags.Public | BindingFlags.Static);
                    if (field == null) return 0;
                    var value = (long)field.GetValue(null);
                    field.SetValue(null, 0L);
                    return value;
                }
                return "dirty groups queued=" + Take("Queued") + " applied=" + Take("Applied") + " client syncs=" + Take("Scheduled");
            }
            catch (Exception)
            {
                return "-";
            }
        }

        /// <summary>Compares every reused grid builder with a fresh one (slow; test only).</summary>
        public static void SetGridStreamBuilderVerify(bool verify)
        {
            var type = PluginType("SentisOptimisations", "SentisOptimisationsPlugin.GridStreamBuilders");
            type.GetField("VerifyCachedBuilders", BindingFlags.Public | BindingFlags.Static).SetValue(null, verify);
            if (!verify) return;
            type.GetField("VerifyMismatches", BindingFlags.Public | BindingFlags.Static).SetValue(null, 0L);
            type.GetField("VerifyFirstDifference", BindingFlags.Public | BindingFlags.Static).SetValue(null, null);
            var elements = type.GetField("VerifyDifferentElements", BindingFlags.Public | BindingFlags.Static).GetValue(null);
            elements.GetType().GetMethod("Clear").Invoke(elements, null);
        }

        public static (long Mismatches, string FirstDifference) GridStreamBuilderVerifyResult()
        {
            var type = PluginType("SentisOptimisations", "SentisOptimisationsPlugin.GridStreamBuilders");
            var elements = (System.Collections.IEnumerable)type.GetField("VerifyDifferentElements", BindingFlags.Public | BindingFlags.Static).GetValue(null);
            if (elements == null) return (0, null);
            var names = new System.Collections.Generic.List<string>();
            foreach (string element in elements) names.Add(element);
            names.Sort();
            return ((long)type.GetField("VerifyMismatches", BindingFlags.Public | BindingFlags.Static).GetValue(null),
                (string)type.GetField("VerifyFirstDifference", BindingFlags.Public | BindingFlags.Static).GetValue(null) +
                (names.Count > 0 ? "; differing elements: " + string.Join(", ", names) : ""));
        }

        public static object FrozenGridSaveCacheStatOrNull(string name)
        {
            var type = PluginType("SentisOptimisations", "SentisOptimisationsPlugin.Freezer.FrozenGridSaveCache");
            return type.GetField(name, BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        }

        public static void SetFrozenGridSaveCacheVerify(bool verify)
        {
            var type = PluginType("SentisOptimisations", "SentisOptimisationsPlugin.Freezer.FrozenGridSaveCache");
            (type.GetField("VerifyPreparedBuilders", BindingFlags.Public | BindingFlags.Static)
             ?? throw new InvalidOperationException("FrozenGridSaveCache.VerifyPreparedBuilders is unavailable")).SetValue(null, verify);
        }

        public static bool IsGridFrozen(long gridId)
        {
            var type = PluginType("SentisOptimisations", "SentisOptimisationsPlugin.Freezer.FreezeLogic");
            var frozen = type.GetField("FrozenGrids", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            var contains = frozen?.GetType().GetMethod("Contains", new[] { typeof(long) });
            if (contains == null)
                throw new InvalidOperationException("FreezeLogic.FrozenGrids is unavailable");
            return (bool)contains.Invoke(frozen, new object[] { gridId });
        }

        public static int FrozenGridCount
        {
            get
            {
                var type = PluginType("SentisOptimisations", "SentisOptimisationsPlugin.Freezer.FreezeLogic");
                var frozen = type.GetField("FrozenGrids", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                var count = frozen?.GetType().GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
                if (count == null)
                    throw new InvalidOperationException("FreezeLogic.FrozenGrids.Count is unavailable");
                return (int)count.GetValue(frozen);
            }
        }

        /// <summary>The load the plugin charges to one programmable block, in ms of every frame.</summary>
        public static double PbLoadMsPerFrame(object programmableBlock)
        {
            var type = PluginType("SentisOptimisations", "SentisOptimisationsPlugin.PbLoad");
            if (type == null) return 0;
            var method = type.GetMethod("LoadMsPerFrame", BindingFlags.Public | BindingFlags.Static);
            if (method == null) throw new InvalidOperationException("PbLoad.LoadMsPerFrame is unavailable");
            return Convert.ToDouble(method.Invoke(null, new[] { programmableBlock }));
        }

        /// <summary>What every running script costs the server together, in ms of every frame.</summary>
        public static double PbTotalMsPerFrame()
        {
            var type = PluginType("SentisOptimisations", "SentisOptimisationsPlugin.PbLoad");
            if (type == null) return 0;
            var method = type.GetMethod("TotalMsPerFrame", BindingFlags.Public | BindingFlags.Static);
            if (method == null) throw new InvalidOperationException("PbLoad.TotalMsPerFrame is unavailable");
            return Convert.ToDouble(method.Invoke(null, null));
        }

        /// <summary>The plugin's own list of the heaviest programmable blocks.</summary>
        public static string PbTop(int count)
        {
            var type = PluginType("SentisOptimisations", "SentisOptimisationsPlugin.PbLoad");
            if (type == null) return "plugin not loaded";
            var method = type.GetMethod("Top", BindingFlags.Public | BindingFlags.Static);
            if (method == null) throw new InvalidOperationException("PbLoad.Top is unavailable");
            return (string)method.Invoke(null, new object[] { count });
        }

        /// <summary>
        /// What the plugin's physics load monitor measured: the average and the last physics step in
        /// milliseconds and how many frames it has seen. Zero frames means the patch never ran.
        /// </summary>
        public static (double AverageMs, double LastMs, double Frames) PhysicsStep
        {
            get
            {
                var type = PluginType("SentisOptimisations", "Optimizer.Optimizations.PhysicsLoadMonitor");
                if (type == null) return (0, 0, 0);
                double Read(string name) =>
                    Convert.ToDouble(type.GetProperty(name, BindingFlags.Public | BindingFlags.Static)?.GetValue(null) ?? 0.0);
                return (Read("AverageMs"), Read("LastMs"), Read("Frames"));
            }
        }

        public static float WelderRadiusMultiplier
        {
            get
            {
                var type = PluginType("SentisGameplayImprovements",
                    "SentisGameplayImprovements.SentisGameplayImprovementsPlugin");
                var config = Config(type);
                return (float)config.GetType().GetProperty("WelderRadiusMultiplier").GetValue(config);
            }
        }

        public static void SetWelderRadiusMultiplier(float multiplier)
        {
            var type = PluginType("SentisGameplayImprovements",
                "SentisGameplayImprovements.SentisGameplayImprovementsPlugin");
            var config = Config(type);
            config.GetType().GetProperty("WelderRadiusMultiplier").SetValue(config, multiplier);
            type.GetMethod("SaveConfig", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
        }
    }
}
