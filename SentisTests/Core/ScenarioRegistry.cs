using System;
using System.Collections.Generic;
using System.Linq;

namespace SentisTests.Core
{
    public static class ScenarioRegistry
    {
        private static readonly Dictionary<string, Func<TestScenario>> _factories =
            new Dictionary<string, Func<TestScenario>>(StringComparer.OrdinalIgnoreCase);

        public static void Register(string name, Func<TestScenario> factory)
        {
            _factories[name] = factory;
        }

        public static IReadOnlyList<string> Names
        {
            get { return _factories.Keys.OrderBy(x => x).ToList(); }
        }

        public static bool Contains(string name)
        {
            return _factories.ContainsKey(name);
        }

        public static TestScenario Create(string name)
        {
            Func<TestScenario> factory;
            if (!_factories.TryGetValue(name, out factory))
                throw new ArgumentException("unknown scenario: " + name + " (known: " + string.Join(", ", Names) + ")");
            return factory();
        }
    }
}
