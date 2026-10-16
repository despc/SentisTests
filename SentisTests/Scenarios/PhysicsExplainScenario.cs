using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Havok;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using SentisTests.Core;
using SentisTests.Game;
using VRage.Game.ModAPI;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// Why the world's dearest grid groups cost what SentisOptimisations lays to them (PhysicsCost.Share), as they are,
    /// nothing added. For <see cref="RunSeconds"/> every cluster is timed in turn every frame (<c>GapFrames</c> 1) and the
    /// step shared out each frame; then the five dearest groups are told grid by grid: blocks, awake, shapes, touches and
    /// the work of each, the bodies of their blocks awake (door leaves, connectors, piston and rotor tops), the grids
    /// docked to them that are not of the group, and their cluster: its measured step and the other groups in it.
    /// </summary>
    public sealed class PhysicsExplainScenario : TestScenario
    {
        public const string ScenarioName = "physics_explain";
        private const int RunSeconds = 15;
        private const int Top = 5;

        /// <summary>A grid told whatever it costs, a fake player put by it: the first dynamic one with this in its name.</summary>
        public const string Focus = "Cedar";

        private readonly Dictionary<HkWorld, List<double>> _measured = new Dictionary<HkWorld, List<double>>();
        private Type _cost;
        private Action<HkWorld, double> _handler;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 90;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            const BindingFlags any = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            _cost = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "SentisOptimisations")
                ?.GetType("Optimizer.Optimizations.PhysicsCost");
            var describe = _cost?.GetMethod("Describe", any);
            SkipUnless(describe != null && _cost.GetField("Sampled", any) != null, "SentisOptimisations has no description of the physics cost");
            var monitor = _cost.Assembly.GetType("Optimizer.Optimizations.PhysicsLoadMonitor");
            var share = _cost.GetMethod("Share", any);

            var focus = MyEntities.GetEntities().OfType<MyCubeGrid>()
                .Where(g => !g.IsStatic && (g.DisplayName ?? "").IndexOf(Focus, StringComparison.OrdinalIgnoreCase) >= 0 && !(g.DisplayName ?? "").StartsWith("["))
                .OrderByDescending(g => g.BlocksCount).FirstOrDefault();
            if (focus != null)
            {
                FakeClients.RemoveAll();
                var at = focus.PositionComp.GetPosition();
                var up = -Sandbox.Game.GameSystems.MyGravityProviderSystem.CalculateNaturalGravityInPoint(at);
                up = up.LengthSquared() > 1e-6 ? VRageMath.Vector3.Normalize(up) : VRageMath.Vector3.Up;
                FakeClients.Add(1, new FakeClients.NetworkProfile { RttMs = 30 }, i => (at + (VRageMath.Vector3D)up * 60, 0, 0), withCharacters: true);
                var arrive = WaitForSeconds(10, "a player by " + focus.DisplayName);
                while (arrive.MoveNext()) yield return arrive.Current;
            }

            _handler = (world, ms) =>
            {
                if (!_measured.TryGetValue(world, out var list)) _measured[world] = list = new List<double>();
                list.Add(ms);
            };
            _cost.GetField("Sampled", any).SetValue(null, _handler);
            _cost.GetField("GapFrames", any).SetValue(null, 1);
            var shares = new Dictionary<long, double>();
            var sum = new Dictionary<long, double>();
            var frames = 0;
            double steps = 0;
            var started = DateTime.UtcNow;
            while ((DateTime.UtcNow - started).TotalSeconds < RunSeconds)
            {
                yield return null;
                var stepMs = Convert.ToDouble(monitor.GetProperty("LastMs", any).GetValue(null));
                share.Invoke(null, new object[] { stepMs, shares });
                frames++;
                steps += stepMs;
                foreach (var pair in shares) sum[pair.Key] = (sum.TryGetValue(pair.Key, out var x) ? x : 0) + pair.Value;
            }
            StopSampling();
            Check(frames > 0 && sum.Count > 0, "nothing was shared out");

            double Median(List<double> xs) => xs == null || xs.Count == 0 ? 0 : xs.OrderBy(x => x).ElementAt(xs.Count / 2);
            Note($"EXPLAIN | {frames} frames, the step {steps / frames:0.000} ms; {sum.Count} groups, the {Top} dearest:");
            var rank = 0;
            var told = sum.OrderByDescending(p => p.Value).Take(Top).ToList();
            var focusKey = focus == null ? 0 : sum.Keys.FirstOrDefault(k => (MyCubeGridGroups.Static.GetGroups(GridLinkTypeEnum.Mechanical).GetGroupNodes(focus) ?? new List<MyCubeGrid> { focus }).Any(g => g.EntityId == k));
            if (focus != null && told.All(p => p.Key != focusKey))
                told.Add(new KeyValuePair<long, double>(focusKey != 0 ? focusKey : focus.EntityId, focusKey != 0 ? sum[focusKey] : 0));
            if (focus != null) Note($"EXPLAIN focus '{focus.DisplayName}' {focus.BlocksCount} blocks, awake {focus.Physics?.IsActive}, " + (focusKey == 0 ? "no share" : "share " + (sum[focusKey] / frames).ToString("0.000") + " ms a frame"));
            foreach (var pair in told)
            {
                rank++;
                if (!(MyEntities.GetEntityById(pair.Key) is MyCubeGrid key))
                {
                    Note($"EXPLAIN #{rank} | {pair.Key}: gone, {pair.Value / frames:0.000} ms a frame");
                    continue;
                }
                var mechanical = MyCubeGridGroups.Static.GetGroups(GridLinkTypeEnum.Mechanical).GetGroupNodes(key) ?? new List<MyCubeGrid> { key };
                var physical = MyCubeGridGroups.Static.GetGroups(GridLinkTypeEnum.Physical).GetGroupNodes(key) ?? new List<MyCubeGrid> { key };
                var world = key.Physics?.HavokWorld;
                var ids = new HashSet<long>(mechanical.Select(g => g.EntityId));
                // the bodies of the group's blocks awake, by kind, and the cluster's other groups
                var parts = new Dictionary<string, int>();
                var others = new Dictionary<long, double>();
                if (world != null)
                    foreach (var body in world.ActiveRigidBodies)
                    {
                        var entity = (body.UserObject as MyPhysicsBody)?.Entity;
                        if (entity == null || entity is MyCubeGrid) continue;
                        if (entity.GetTopMostParent() is MyCubeGrid owner && ids.Contains(owner.EntityId))
                        {
                            var kind = entity.GetType().Name + (entity.Parent is MyCubeBlock block ? " of " + block.BlockDefinition.Id.SubtypeName : "");
                            parts[kind] = (parts.TryGetValue(kind, out var n) ? n : 0) + 1;
                        }
                    }
                foreach (var other in sum)
                    if (other.Key != pair.Key && MyEntities.GetEntityById(other.Key) is MyCubeGrid g && g.Physics?.HavokWorld == world && world != null)
                        others[other.Key] = other.Value;
                List<double> ms = null;
                if (world != null) _measured.TryGetValue(world, out ms);
                Note($"EXPLAIN #{rank} | '{key.DisplayName}': {pair.Value / frames:0.000} ms a frame; the group {mechanical.Count} grids, {mechanical.Sum(g => g.BlocksCount)} blocks, " +
                     $"{mechanical.Count - 1} connections; its cluster {Median(ms):0.000} ms a step ({ms?.Count ?? 0} measured), other groups in it {others.Count}: " +
                     string.Join(", ", others.OrderByDescending(o => o.Value).Take(4).Select(o => $"{(MyEntities.GetEntityById(o.Key) as MyCubeGrid)?.DisplayName} {o.Value / frames:0.000}")));
                foreach (var g in mechanical.OrderByDescending(g => g.BlocksCount).Take(10))
                    Note($"EXPLAIN #{rank}   grid '{g.DisplayName}' {g.BlocksCount} blocks{(g.IsStatic ? ", static" : "")}: {describe.Invoke(null, new object[] { g })}; " +
                         $"v {g.Physics?.LinearVelocity.Length() ?? 0:0.00} m/s, w {g.Physics?.AngularVelocity.Length() ?? 0:0.000} rad/s; " +
                         string.Join(", ", g.GetFatBlocks().GroupBy(b => b.BlockDefinition.Id.TypeId.ToString().Replace("MyObjectBuilder_", "")).OrderByDescending(k => k.Count()).Take(5).Select(k => $"{k.Key} {k.Count()}")));
                if (mechanical.Count > 10) Note($"EXPLAIN #{rank}   ... and {mechanical.Count - 10} grids more");
                Note($"EXPLAIN #{rank}   bodies of its blocks awake: " + (parts.Count == 0 ? "none" : string.Join(", ", parts.OrderByDescending(p => p.Value).Select(p => $"{p.Key} {p.Value}"))) +
                     $"; docked, not of the group: " + (physical.Count == mechanical.Count ? "none" : string.Join(", ", physical.Where(g => !ids.Contains(g.EntityId)).Select(g => $"{g.DisplayName} {g.BlocksCount}"))));
            }
        }

        private void StopSampling()
        {
            if (_cost == null) return;
            const BindingFlags any = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var sampled = _cost.GetField("Sampled", any);
            if (sampled != null && ReferenceEquals(sampled.GetValue(null), _handler)) sampled.SetValue(null, null);
            _cost.GetField("GapFrames", any)?.SetValue(null, _cost.GetField("MeasureGapFrames", any)?.GetRawConstantValue() ?? 30);
        }

        public override void Cleanup()
        {
            try
            {
                StopSampling();
                FakeClients.RemoveAll();
            }
            finally { base.Cleanup(); }
        }
    }
}
