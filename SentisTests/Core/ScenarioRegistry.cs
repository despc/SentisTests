using System;
using System.Collections.Generic;
using System.Linq;

namespace SentisTests.Core
{
    /// <summary>What the stand knows about a registered scenario, without creating it.</summary>
    public sealed class ScenarioInfo
    {
        public string Name;
        public Func<TestScenario> Factory;

        /// <summary>Optional plugins this scenario drives; empty when the game and Torch are enough.</summary>
        public string[] Requires = new string[0];

        /// <summary>Why it cannot run here, or null when it can.</summary>
        public string UnavailableReason => Integrations.MissingReason(Requires);

        public bool Available => Requires.Length == 0 || Integrations.Missing(Requires).Count == 0;
    }

    public static class ScenarioRegistry
    {
        private static readonly Dictionary<string, ScenarioInfo> _scenarios =
            new Dictionary<string, ScenarioInfo>(StringComparer.OrdinalIgnoreCase);

        /// <param name="requires">
        /// Names of the optional plugins the scenario drives (see <see cref="Integrations"/>). A
        /// scenario that asks for one is SKIPPED, not failed, when that plugin is not loaded, and
        /// left out of "run all".
        /// </param>
        public static void Register(string name, Func<TestScenario> factory, params string[] requires)
        {
            _scenarios[name] = new ScenarioInfo
            {
                Name = name,
                Factory = factory,
                Requires = requires ?? new string[0],
            };
        }

        /// <summary>
        /// Says which optional plugins the named scenarios drive. Call it after registering them:
        /// a scenario that cannot say anything about a plugin that is not installed is then SKIPPED
        /// with that plugin's name, instead of failing on a type that was never there.
        /// Unknown names are a mistake in the stand and are written to the log.
        /// </summary>
        public static void Needs(string[] plugins, params string[] names)
        {
            var unknown = names.Where(n => !_scenarios.ContainsKey(n)).ToList();
            if (unknown.Count > 0)
                SentisTestsPlugin.Log.Error("scenario requirements name unknown scenarios: " + string.Join(", ", unknown));

            foreach (var name in names)
            {
                ScenarioInfo info;
                if (!_scenarios.TryGetValue(name, out info)) continue;
                info.Requires = info.Requires.Concat(plugins ?? new string[0])
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            }
        }

        public static void Needs(string plugin, params string[] names) => Needs(new[] { plugin }, names);

        public static IReadOnlyList<string> Names
        {
            get { return _scenarios.Keys.OrderBy(x => x).ToList(); }
        }

        public static IReadOnlyList<ScenarioInfo> All
        {
            get { return _scenarios.Values.OrderBy(x => x.Name).ToList(); }
        }

        /// <summary>Scenarios that can run on this server as it stands.</summary>
        public static IReadOnlyList<string> AvailableNames
        {
            get { return _scenarios.Values.Where(s => s.Available).Select(s => s.Name).OrderBy(x => x).ToList(); }
        }

        /// <summary>Scenarios whose plugins are missing: name and what it is waiting for.</summary>
        public static IReadOnlyList<KeyValuePair<string, string>> Unavailable
        {
            get
            {
                return _scenarios.Values
                    .Where(s => !s.Available)
                    .Select(s => new KeyValuePair<string, string>(s.Name, s.UnavailableReason))
                    .OrderBy(p => p.Key)
                    .ToList();
            }
        }

        public static bool Contains(string name)
        {
            return _scenarios.ContainsKey(name);
        }

        /// <summary>Why the scenario cannot run here, or null when it can. Null also for an unknown name.</summary>
        public static string UnavailableReason(string name)
        {
            ScenarioInfo info;
            return _scenarios.TryGetValue(name, out info) ? info.UnavailableReason : null;
        }

        public static TestScenario Create(string name)
        {
            ScenarioInfo info;
            if (!_scenarios.TryGetValue(name, out info))
                throw new ArgumentException("unknown scenario: " + name + " (known: " + string.Join(", ", Names) + ")");
            return info.Factory();
        }
    }
}
