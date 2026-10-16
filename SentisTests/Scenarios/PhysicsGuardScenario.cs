using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.GameSystems;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game.ModAPI;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// SentisOptimisations' physics guard (PhysicsGuard: the step's average from PhysicsLoadMonitor shared out by
    /// PhysicsCost - the clusters timed, a cluster stepped alone every 30th frame, within a cluster by work - the worst
    /// groups alerted, punished, turned into stations). A stack of fourteen pistons
    /// (<see cref="PistonStackScenario.ResourceName"/>, its base made dynamic) floats in space by a fake player; the
    /// guard is switched on with its thresholds put under the stack's share (counted beforehand by PhysicsCost.Share, as
    /// the guard counts; the stack among the five worst groups), and one check before the punishment. What the guard
    /// does to a group that is not the stack is held back (by Harmony, on Punisher: no station, no message to its owner)
    /// and noted. Passes when PhysicsLoadMonitor kept measuring, the clusters were timed all along, and the guard turned
    /// the stack into a station within <see cref="PunishWithinSeconds"/>.
    /// </summary>
    public sealed class PhysicsGuardScenario : TestScenario
    {
        public const string ScenarioName = "physics_guard";
        private const string Prefix = "pguard-";
        private const int PunishWithinSeconds = 60;

        private readonly ConfigOverride _config = new ConfigOverride();
        private readonly List<MyCubeGrid> _grids = new List<MyCubeGrid>();
        private static HarmonyLib.Harmony _harmony;
        private static readonly List<string> HeldBack = new List<string>();
        private static int _ours;
        private MethodInfo _share;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 240;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            const BindingFlags any = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type T(string assembly, string type) => AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == assembly)?.GetType(type);
            var monitor = T("SentisOptimisations", "Optimizer.Optimizations.PhysicsLoadMonitor");
            var punisher = T("SentisOptimisations", "SentisOptimisationsPlugin.Punisher");
            var guard = T("SentisOptimisations", "SentisOptimisationsPlugin.PhysicsGuard");
            var cost = T("SentisOptimisations", "Optimizer.Optimizations.PhysicsCost");
            SkipUnless(monitor != null && punisher != null && guard != null && cost != null, "SentisOptimisations has no physics guard");
            var waiting = (bool)guard.GetMethod("Waiting", any).Invoke(null, new object[] { DateTime.UtcNow, guard.GetField("_loadedAt", any)?.GetValue(null) });
            SkipUnless(!waiting, "the guard still waits for the world to settle");
            double AverageMs() => Convert.ToDouble(monitor.GetProperty("AverageMs", any).GetValue(null));
            long Frames() => Convert.ToInt64(monitor.GetProperty("Frames", any).GetValue(null));
            long Read(string field) => (long)cost.GetField(field, any).GetValue(null);
            _share = cost.GetMethod("Share", any);
            _config.Set("FreezerEnabled", false);
            _config.Set("EnablePhysicsGuard", false);

            // ------------------------------------------------------------- what the guard does to others: held back
            HeldBack.Clear();
            _ours = 0;
            _harmony = new HarmonyLib.Harmony("SentisTests.PhysicsGuard");
            var hold = new HarmonyLib.HarmonyMethod(typeof(PhysicsGuardScenario).GetMethod(nameof(PunisherPrefix), BindingFlags.Static | BindingFlags.NonPublic));
            foreach (var name in new[] { "AlertPlayerGrid", "PunishPlayerGrid", "PunishPlayerGridImmediately" })
                _harmony.Patch(punisher.GetMethod(name, any), prefix: hold);

            // ------------------------------------------------------------- a place in space, a player, the stack
            var planets = MyEntities.GetEntities().OfType<MyPlanet>().ToList();
            var spot = Vector3D.Zero;
            for (var i = 0; i < 60 && spot == Vector3D.Zero; i++)
            {
                var probe = new Vector3D(420000 + i * 20000, -310000 + i * 15000, 150000 - i * 10000);
                if (MyGravityProviderSystem.CalculateNaturalGravityInPoint(probe).LengthSquared() > 1e-6) continue;
                if (planets.Any(p => Vector3D.Distance(p.PositionComp.GetPosition(), probe) < p.MaximumRadius + 10000)) continue;
                spot = probe;
            }
            Check(spot != Vector3D.Zero, "no place in space without gravity");
            FakeClients.RemoveAll();
            FakeClients.Add(1, new FakeClients.NetworkProfile { RttMs = 30 }, p => (spot + new Vector3D(0, 60, 40), 0, 0), withCharacters: true);
            var arrive = WaitForSeconds(5, "the player arrives");
            while (arrive.MoveNext()) yield return arrive.Current;

            var group = WorldApi.LoadAuthoredGroup(PistonStackScenario.ResourceName, WorldApi.EntityPrefix + Prefix + "stack");
            var base0 = group[0].PositionAndOrientation.Value.GetMatrix();
            var move = MatrixD.CreateTranslation(spot - base0.Translation);
            foreach (var ob in group)
                ob.PositionAndOrientation = new MyPositionAndOrientation(ob.PositionAndOrientation.Value.GetMatrix() * move);
            group[0].IsStatic = false;
            foreach (var grid in group.Select(WorldApi.SpawnGrid))
            {
                Track(grid);
                _grids.Add(grid);
            }
            var settle = WaitForSeconds(8, "the stack settles");
            while (settle.MoveNext()) yield return settle.Current;
            // the stack kept awake: a sleeping body costs the guard nothing
            foreach (var grid in _grids) grid.Physics?.RigidBody?.Activate();

            // ------------------------------------------------------------- the guard's shares, by PhysicsCost
            var wait = WaitForSeconds(3, "the clusters are timed");
            while (wait.MoveNext()) yield return wait.Current;
            var stepMs = AverageMs();
            var shares = new Dictionary<long, double>();
            _share.Invoke(null, new object[] { stepMs, shares });
            var stack = new HashSet<long>(_grids.Select(g => g.EntityId));
            var ours = shares.Where(p => stack.Contains(p.Key)).Select(p => p.Value).DefaultIfEmpty(0).Sum();
            Check(ours > 0, "the stack has no share of the physics");
            var ranked = shares.OrderByDescending(p => p.Value).ToList();
            var rank = ranked.FindIndex(p => stack.Contains(p.Key));
            string NameOf(long id) => (MyEntities.GetEntityById(id) as MyCubeGrid)?.DisplayName ?? id.ToString();
            Note($"GUARD SHARES | the step {stepMs:0.00} ms: the stack {ours:0.000} ms, #{rank + 1} of {shares.Count} groups; the top: " +
                 string.Join(", ", ranked.Take(5).Select(p => $"{NameOf(p.Key)} {p.Value:0.000}")));
            // the guard looks at its five worst groups: the stack has to be one; the ones over it are held back
            SkipUnless(rank >= 0 && rank < 5, $"the stack is #{rank + 1} of the groups the guard counts: the guard looks at five");
            // under the stack's share with room for the step's average to move
            var threshold = ours * 0.85;
            _config.Set("PhysicsMsToAlert", (float)threshold);
            _config.Set("PhysicsMsToPunish", (float)threshold);
            _config.Set("PhysicsMsToPunishImmediately", 1000f);
            _config.Set("PhysicsChecksBeforePunish", 1);

            // ------------------------------------------------------------- on, with the clusters timed
            var measured0 = Read("ClustersMeasured");
            var frames0 = Frames();
            _config.Set("EnablePhysicsGuard", true);
            Note($"the guard on: alert and punish over {threshold:0.000} ms, at once over 1000 ms, one check before the punishment");
            var started = DateTime.UtcNow;
            var punished = Wait(() => _grids.Any(g => g.IsStatic), "the guard turns the stack into a station", PunishWithinSeconds);
            while (punished.MoveNext()) yield return punished.Current;
            var took = (DateTime.UtcNow - started).TotalSeconds;
            _config.Set("EnablePhysicsGuard", false);
            var averageMs = AverageMs();
            var frames = Frames() - frames0;
            var measured = Read("ClustersMeasured") - measured0;

            var statics = _grids.Count(g => g.IsStatic);
            Note($"PHYSICS GUARD | the stack a station in {took:0} s ({statics} of {_grids.Count} grids static, {_ours} actions on it); " +
                 $"PhysicsLoadMonitor {frames} frames, {averageMs:0.00} ms; clusters timed {measured}; held back for others {HeldBack.Count}" +
                 (HeldBack.Count > 0 ? ": " + string.Join("; ", HeldBack.Distinct().Take(5)) : ""));
            Check(frames > took * 50, $"PhysicsLoadMonitor measured {frames} frames in {took:0} s");
            Check(averageMs > 0 && averageMs < 50, $"PhysicsLoadMonitor's average {averageMs:0.00} ms");
            Check(measured >= took * 60 / 30 / 2, $"only {measured} clusters timed in {took:0} s");
            Check(statics == _grids.Count, $"{statics} of the stack's {_grids.Count} grids are stations");
        }

        /// <summary>The guard acts on the stack; on any other group the action is noted and held back.</summary>
        private static bool PunisherPrefix(List<IMyCubeGrid> grids, MethodBase __originalMethod)
        {
            if (grids != null && grids.Any(g => g.Name != null && g.Name.StartsWith(WorldApi.EntityPrefix + Prefix)))
            {
                _ours++;
                return true;
            }
            lock (HeldBack) HeldBack.Add(__originalMethod.Name + " " + string.Join(", ", grids?.Select(g => g.DisplayName) ?? Enumerable.Empty<string>()));
            return false;
        }

        public override void Cleanup()
        {
            try
            {
                _config.Restore();
                _harmony?.UnpatchAll("SentisTests.PhysicsGuard");
                _harmony = null;
                foreach (var grid in _grids)
                    if (!grid.Closed) grid.Close();
                FakeClients.RemoveAll();
            }
            finally { base.Cleanup(); }
        }
    }
}
