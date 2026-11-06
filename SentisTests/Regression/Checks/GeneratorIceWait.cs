using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// An O2/H2 generator on a conveyor line with no ice anywhere: its pulls find none and, with SentisOptimisations'
    /// GeneratorIcePull, it stops asking every 100 frames and waits (prod 06.11.2026: the empty pulls walked every
    /// inventory of the network, 4.4 ms a second). Ice put into the cargo container then: it is taken at once, not after
    /// the wait - ore stored into a sending inventory ends it.
    /// </summary>
    public sealed class GeneratorIceWaitCheck : RegressionCheck
    {
        public override string Name => Prefix + "generator_ice_wait";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 8, -8));
            var cargoPart = new Part("CargoContainer/LargeBlockSmallContainer", 0, 0, 0);
            Part generatorPart = null;
            yield return ConveyorNeighbour(cargoPart, "OxygenGenerator/", null, p => generatorPart = p);
            var taken = new HashSet<Vector3I>(Cells(cargoPart).Concat(Cells(generatorPart)));
            var free = taken.SelectMany(c => Base6Directions.IntDirections.Select(d => c + d)).Where(c => !taken.Contains(c)).Take(1).ToList();
            var grid = Build("ice-wait", Site.Origin, true, cargoPart, generatorPart,
                new Part("BatteryBlock/LargeBlockBatteryBlock", free[0].X, free[0].Y, free[0].Z).With(b => Motion.Charged(b)));
            var cargo = Inventory(Block<MyCargoContainer>(grid));
            var generator = Block<MyGasGenerator>(grid);
            Check(Amount(Inventory(generator), "Ore/Ice") == 0, "the generator has ice of its own");

            // no ice: the pulls find nothing, past the few the wait starts after (the conveyor paths known by then)
            yield return Seconds(20);
            var waiting = Waiting(generator);
            Note("20 s with no ice: " + (waiting == null ? "no SentisOptimisations GeneratorIcePull" : waiting.Value > 0 ? $"waiting {waiting} frames more" : "not waiting"));
            Check(waiting == null || waiting.Value > 0 || EverWaited(generator), "the generator does not wait on a network with no ice: " + State(generator));

            var put = DateTime.UtcNow;
            Put(cargo, "Ore/Ice", 1000);
            yield return Until(() => Amount(Inventory(generator), "Ore/Ice"), v => v > 0, "ice pulled into the generator, kg", 5, () => State(generator));
            var after = (DateTime.UtcNow - put).TotalSeconds;
            Result($"no ice: the generator waited; ice put in: taken {F(Amount(Inventory(generator), "Ore/Ice"))} kg in {F(after)} s");
        }

        private static object Backoff(MyGasGenerator generator)
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("Optimizer.Optimizations.GeneratorIcePull")).FirstOrDefault(t => t != null);
            var table = type?.GetField("States", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)?.GetValue(null);
            if (table == null) return null;
            var args = new object[] { generator, null };
            return (bool)table.GetType().GetMethod("TryGetValue").Invoke(table, args) ? args[1] : new object();
        }

        /// <summary>Frames the generator still waits before its next pull; null without GeneratorIcePull.</summary>
        private static long? Waiting(MyGasGenerator generator)
        {
            var state = Backoff(generator);
            if (state == null) return null;
            var until = state.GetType().GetField("_skipUntilFrame", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(state);
            if (until == null) return 0;
            return Math.Max(0, (long)until - (long)Sandbox.Game.World.MySession.Static.GameplayFrameCounter);
        }

        /// <summary>Whether its wait has been set at all (it may have just run out at the moment it is asked).</summary>
        private static bool EverWaited(MyGasGenerator generator)
        {
            var state = Backoff(generator);
            var wait = state?.GetType().GetField("_waitFrames", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(state);
            return wait is long w && w > 0;
        }
    }
}
