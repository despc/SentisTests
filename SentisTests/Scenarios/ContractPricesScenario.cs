using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using Sandbox.Game.World.Generator;
using SentisTests.Core;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// SentisGameplayImprovements' contract reward multipliers on the running server: each of the game's reward
    /// methods (acquisition, escort, hauling - a package and a grid share it - and repair) is called with its
    /// multiplier at 1 and at <see cref="Multiplier"/>, and the reward must grow by that much. This shows the
    /// suffixes are on the game's methods and read the setting live.
    /// </summary>
    internal sealed class ContractPricesScenario : TestScenario
    {
        public const string ScenarioName = "contract_prices";
        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 30;

        private const double Multiplier = 7.5;
        private const BindingFlags Any = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private readonly ConfigOverride _gameplay = new ConfigOverride(ConfigOverride.Gameplay);

        public override IEnumerator Run()
        {
            var cases = new (string Setting, Type Type, string Method, object[] Args)[]
            {
                ("ContractAcquisitionMultiplier", typeof(MyContractTypeAcquisitionStrategy), "GetMoneyRewardForAcquisitionContract", new object[] { 20000L, 37 }),
                ("ContractEscortMultiplier", typeof(MyContractTypeEscortStrategy), "GetMoneyReward_Escort", new object[] { 30000L, 250000.0 }),
                ("ContractHaulingtMultiplier", typeof(MyContractTypeBaseStrategy), "GetHaulingMoneyReward", new object[] { 40000L, 100000.0, 2000 }),
                ("ContractRepairMultiplier", typeof(MyContractTypeRepairStrategy), "GetMoneyRewardForRepairContract", new object[] { 25000L, 8000.0, 1500000L, 0.1f }),
            };
            var failures = new List<string>();
            var results = new List<string>();
            foreach (var (setting, type, method, args) in cases)
            {
                var target = type.GetMethod(method, Any);
                Check(target != null, type.Name + "." + method + " is gone");
                var instance = target.IsStatic ? null : FormatterServices.GetUninitializedObject(type);

                _gameplay.Set(setting, 1.0);
                var plain = (long)target.Invoke(instance, args);
                _gameplay.Set(setting, Multiplier);
                var scaled = (long)target.Invoke(instance, args);
                var expected = (long)(plain * Multiplier);
                var line = method + ": " + plain + " at x1, " + scaled + " at x" + Multiplier + " (expected " + expected + ")";
                results.Add(line);
                Note(line);
                if (plain <= 0 || scaled != expected) failures.Add(line);
            }
            yield return null;
            Note("CONTRACT PRICES RESULT: " + string.Join(" || ", results));
            Check(failures.Count == 0, "rewards not multiplied: " + string.Join("; ", failures));
        }

        public override void Cleanup()
        {
            try { _gameplay.Restore(); }
            finally { base.Cleanup(); }
        }
    }
}
