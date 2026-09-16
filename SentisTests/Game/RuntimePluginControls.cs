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
                return (bool)Config(type).GetType().GetProperty("FreezerEnabled").GetValue(Config(type));
            }
        }

        public static void SetFreezerEnabled(bool enabled)
        {
            var type = PluginType("SentisOptimisations",
                "SentisOptimisationsPlugin.SentisOptimisationsPlugin");
            var config = Config(type);
            config.GetType().GetProperty("FreezerEnabled").SetValue(config, enabled);
            var save = type.GetMethod("SaveConfig", BindingFlags.Public | BindingFlags.Static);
            if (save == null)
                throw new InvalidOperationException(type.FullName + ".SaveConfig() is unavailable");
            save.Invoke(null, null);
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
