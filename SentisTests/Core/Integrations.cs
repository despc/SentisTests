using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace SentisTests.Core
{
    /// <summary>
    /// The optional plugins this stand can drive. Nothing here needs any of them: the stand runs,
    /// lists its scenarios and executes the ones that touch only the game and Torch. A scenario
    /// that exists to check another plugin says so (<see cref="ScenarioRegistry.Register(string, Func{TestScenario}, string[])"/>)
    /// and is SKIPPED with the plugin's name when that plugin is not loaded, instead of failing on
    /// a missing type.
    ///
    /// Everything is resolved by reflection on purpose - the stand is compiled without a single
    /// reference to the plugins it tests, so any of them can be absent, older, or newer.
    /// </summary>
    public static class Integrations
    {
        /// <summary>SentisOptimisations: the freezer, physics and save patches, replication indexes. Its assembly also holds the <c>Optimizer.Optimizations.*</c> types.</summary>
        public const string Optimisations = "SentisOptimisations";

        /// <summary>SentisGameplayImprovements: gameplay patches, asteroid fields, background sweeps.</summary>
        public const string Gameplay = "SentisGameplayImprovements";

        /// <summary>SentisAi: the bots the stand plays against and drives.</summary>
        public const string Ai = "SentisAi";

        /// <summary>SentisWatcher: the recorder the stand writes events for and reads back.</summary>
        public const string Watcher = "SentisWatcher";

        /// <summary>SentisAdventures: NPC spawns, contracts, world sweeps.</summary>
        public const string Adventures = "SentisAdventures";

        /// <summary>VirtualGarage: grid save/load roundtrip.</summary>
        public const string Garage = "VirtualGarage";

        /// <summary>Profiler: the method timings the load tests read.</summary>
        public const string Profiler = "Profiler";

        /// <summary>Every optional plugin, in the order of the start-up log line.</summary>
        public static readonly IReadOnlyList<string> Known = new List<string>
        {
            Optimisations, Gameplay, Ai, Watcher, Adventures, Garage, Profiler,
        };

        // Types are resolved once per session and kept; a plugin that is not loaded yet is looked
        // up again next time (plugin load order is not ours to choose).
        private static readonly Dictionary<string, Type> TypeCache = new Dictionary<string, Type>(StringComparer.Ordinal);
        private static readonly object Gate = new object();

        /// <summary>The loaded assembly of an optional plugin, or null when it is not loaded.</summary>
        public static Assembly AssemblyOf(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            try
            {
                return AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a =>
                    string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static bool IsLoaded(string name) => AssemblyOf(name) != null;

        /// <summary>A type of an optional plugin; null when the plugin or the type is not there. Never throws.</summary>
        public static Type TypeOf(string assemblyName, string typeName)
        {
            var key = assemblyName + "!" + typeName;
            lock (Gate)
            {
                Type cached;
                if (TypeCache.TryGetValue(key, out cached)) return cached;
            }

            var found = AssemblyOf(assemblyName)?.GetType(typeName, false);
            if (found != null)
            {
                lock (Gate) TypeCache[key] = found;
            }

            return found;
        }

        /// <summary>
        /// A type by its full name, in whichever loaded assembly holds it. For the types the stand
        /// hooks but does not care who owns (a plugin's internals move between assemblies between
        /// releases). Null when nobody has it.
        /// </summary>
        public static Type TypeAnywhere(string typeName)
        {
            var key = "*" + typeName;
            lock (Gate)
            {
                Type cached;
                if (TypeCache.TryGetValue(key, out cached)) return cached;
            }

            Type found = null;
            try
            {
                found = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => SafeGetType(a, typeName))
                    .FirstOrDefault(t => t != null);
            }
            catch (Exception)
            {
                return null;
            }

            if (found != null)
            {
                lock (Gate) TypeCache[key] = found;
            }

            return found;
        }

        private static Type SafeGetType(Assembly assembly, string typeName)
        {
            try
            {
                return assembly?.GetType(typeName, false);
            }
            catch (Exception)
            {
                // A reflection-only or unloadable assembly, or a type whose dependencies are missing.
                return null;
            }
        }

        /// <summary>The optional plugins a scenario needs that are not loaded; empty when it can run.</summary>
        public static List<string> Missing(IEnumerable<string> names)
        {
            var missing = new List<string>();
            if (names == null) return missing;
            foreach (var name in names)
            {
                if (string.IsNullOrEmpty(name)) continue;
                if (!IsLoaded(name) && !missing.Contains(name, StringComparer.OrdinalIgnoreCase))
                    missing.Add(name);
            }

            return missing;
        }

        /// <summary>Why something needs a plugin that is not here, or null when it is loaded.</summary>
        public static string MissingReason(string name) =>
            IsLoaded(name) ? null : "needs plugin " + name + " - not loaded";

        /// <summary>Why the scenario cannot run here, or null when every plugin it needs is loaded.</summary>
        public static string MissingReason(IEnumerable<string> names)
        {
            var missing = Missing(names);
            if (missing.Count == 0) return null;
            return "needs plugin " + string.Join(", ", missing) + " - not loaded";
        }

        /// <summary>Skips the calling scenario when one of these plugins is not loaded.</summary>
        public static void Require(params string[] names)
        {
            var reason = MissingReason(names);
            if (reason != null)
                throw new ScenarioSkippedException(reason);
        }

        /// <summary>One line for the log: which of the optional plugins the stand found.</summary>
        public static string StatusLine()
        {
            return string.Join(", ", Known.Select(n => n + (IsLoaded(n) ? " yes" : " no")));
        }
    }
}
