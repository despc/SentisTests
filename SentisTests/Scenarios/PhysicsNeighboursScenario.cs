using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Havok;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.GameSystems;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRage.Game.ModAPI;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// Who of a cluster's neighbours costs what, each on its own. The grid named <see cref="PhysicsExplainScenario.Focus"/>
    /// and every grid group within <see cref="Radius"/> m of it are copied (each with what is joined to it, programmable
    /// blocks off) to empty flat places of the same planet - each at its height over the ground and its tilt to the
    /// gravity, as it is - each with a fake player by it, and their clusters timed every frame for <see cref="RunSeconds"/>
    /// against an empty one: what each costs alone, and what PhysicsCost.Share lays to it. The copies are removed after.
    /// </summary>
    public sealed class PhysicsNeighboursScenario : TestScenario
    {
        public const string ScenarioName = "physics_neighbours";
        private const string Prefix = "pnear-";
        private const double Radius = 300;
        private const int RunSeconds = 20;

        private sealed class Copy
        {
            public string Name;
            public List<MyCubeGrid> Grids = new List<MyCubeGrid>();
            public HkWorld World;
            public readonly List<double> Ms = new List<double>();
            public double Share;
            public readonly Dictionary<string, int> Kinds = new Dictionary<string, int>();
        }

        private readonly ConfigOverride _config = new ConfigOverride();
        private readonly List<Copy> _copies = new List<Copy>();
        private readonly List<double> _empty = new List<double>();
        private HkWorld _emptyWorld;
        private Type _cost;
        private Action<HkWorld, double> _handler;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 180;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            const BindingFlags any = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            _cost = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "SentisOptimisations")
                ?.GetType("Optimizer.Optimizations.PhysicsCost");
            SkipUnless(_cost?.GetField("Sampled", any) != null, "SentisOptimisations has no timing of the physics clusters for tests");
            var monitor = _cost.Assembly.GetType("Optimizer.Optimizations.PhysicsLoadMonitor");
            var share = _cost.GetMethod("Share", any);
            _config.Set("FreezerEnabled", false);
            _config.Set("EnablePhysicsGuard", false);

            // ------------------------------------------------------------- the grid and its neighbours, by mechanical group
            var all = MyEntities.GetEntities().OfType<MyCubeGrid>().Where(g => !g.MarkedForClose && g.Physics != null).ToList();
            var focus = all.Where(g => !g.IsStatic && (g.DisplayName ?? "").IndexOf(PhysicsExplainScenario.Focus, StringComparison.OrdinalIgnoreCase) >= 0 && !(g.DisplayName ?? "").StartsWith("["))
                .OrderByDescending(g => g.BlocksCount).FirstOrDefault();
            SkipUnless(focus != null, "no grid named " + PhysicsExplainScenario.Focus);
            var planet = MyGamePruningStructure.GetClosestPlanet(focus.PositionComp.GetPosition());
            SkipUnless(planet != null, "the grid is not by a planet");
            var centre = planet.PositionComp.GetPosition();
            Vector3D Surface(Vector3D point) => planet.GetClosestSurfacePointGlobal(ref point);
            var groups = new List<List<MyCubeGrid>>();
            var seen = new HashSet<long>();
            foreach (var g in all.Where(g => Vector3D.Distance(g.PositionComp.GetPosition(), focus.PositionComp.GetPosition()) < Radius).OrderByDescending(g => g == focus).ThenByDescending(g => g.BlocksCount))
            {
                if (seen.Contains(g.EntityId)) continue;
                var nodes = MyCubeGridGroups.Static.GetGroups(GridLinkTypeEnum.Physical).GetGroupNodes(g) ?? new List<MyCubeGrid> { g };
                foreach (var node in nodes) seen.Add(node.EntityId);
                if (nodes.All(node => node.IsStatic && nodes.Count == 1)) continue;
                groups.Add(nodes.ToList());
            }
            Note($"NEIGHBOURS | '{focus.DisplayName}' and within {Radius:0} m: " + string.Join("; ", groups.Select(gs =>
                $"{gs.OrderByDescending(x => x.BlocksCount).First().DisplayName} ({gs.Count} grids, {gs.Sum(x => x.BlocksCount)} blocks, awake {gs.Count(x => x.Physics?.IsActive == true)})")));
            SkipUnless(groups.Count > 0, "nothing to copy");

            // ------------------------------------------------------------- empty flat places: one per group, and an empty one
            var positions = all.Select(g => g.PositionComp.GetPosition()).ToList();
            var spots = new List<Vector3D>();
            const int n = 1200;
            for (var i = 0; i < n && spots.Count < groups.Count + 1; i++)
            {
                var y = 1 - 2.0 * (i + 0.5) / n;
                var r = Math.Sqrt(1 - y * y);
                var a = i * Math.PI * (3 - Math.Sqrt(5));
                var dir = new Vector3D(Math.Cos(a) * r, y, Math.Sin(a) * r);
                var g = Surface(centre + dir * planet.MaximumRadius);
                // a cluster reaches 20 km and more: nobody's grid within 30 km
                if (positions.Any(o => Vector3D.DistanceSquared(o, g) < 30000.0 * 30000.0) || spots.Any(s => Vector3D.Distance(s, g) < 45000)) continue;
                var height = Vector3D.Distance(g, centre);
                var side = Vector3D.CalculatePerpendicularVector(dir);
                var other = Vector3D.Cross(dir, side);
                if (!new[] { side, -side, other, -other }.All(d => Math.Abs(Vector3D.Distance(Surface(g + d * 40 + dir * 50), centre) - height) < 3)) continue;
                spots.Add(g);
            }
            Check(spots.Count == groups.Count + 1, $"only {spots.Count} of {groups.Count + 1} flat places");

            // each group's frame: from where it stands (the ground under its biggest grid, the local up) to the spot's
            var obsByGroup = new List<List<MyObjectBuilder_CubeGrid>>();
            for (var k = 0; k < groups.Count; k++)
            {
                var main = groups[k].OrderByDescending(x => x.BlocksCount).First();
                var here = main.PositionComp.GetPosition();
                var groundHere = Surface(here);
                var upHere = Vector3D.Normalize(here - centre);
                var upThere = Vector3D.Normalize(spots[k] - centre);
                var turn = MatrixD.CreateFromQuaternion(QuaternionD.CreateFromTwoVectors(upHere, upThere));
                var obs = groups[k].Select(x => (MyObjectBuilder_CubeGrid)x.GetObjectBuilder(true)).ToList();
                foreach (var ob in obs)
                {
                    var m = ob.PositionAndOrientation.Value.GetMatrix();
                    var offset = m.Translation - groundHere;
                    var placed = MatrixD.CreateWorld(spots[k] + Vector3D.TransformNormal(offset, turn), Vector3D.TransformNormal(m.Forward, turn), Vector3D.TransformNormal(m.Up, turn));
                    ob.PositionAndOrientation = new MyPositionAndOrientation(placed);
                    ob.Name = WorldApi.EntityPrefix + Prefix + ob.EntityId;
                    ob.DisplayName = WorldApi.EntityPrefix + Prefix + ob.DisplayName;
                    foreach (var block in ob.CubeBlocks)
                        if (block is Sandbox.Common.ObjectBuilders.MyObjectBuilder_MyProgrammableBlock pb) pb.Enabled = false;
                }
                Sandbox.ModAPI.MyAPIGateway.Entities.RemapObjectBuilderCollection(obs);
                obsByGroup.Add(obs);
            }

            FakeClients.RemoveAll();
            FakeClients.Add(spots.Count, new FakeClients.NetworkProfile { RttMs = 30 }, i =>
            {
                var up = Vector3D.Normalize(spots[i] - centre);
                return (Surface(spots[i] + Vector3D.CalculatePerpendicularVector(up) * (Radius + 100) + up * 50) + up * 3, 0, 0);
            }, withCharacters: true);
            var arrive = WaitForSeconds(8, "the players arrive");
            while (arrive.MoveNext()) yield return arrive.Current;
            for (var k = 0; k < groups.Count; k++)
            {
                var copy = new Copy { Name = groups[k].OrderByDescending(x => x.BlocksCount).First().DisplayName };
                foreach (var ob in obsByGroup[k])
                {
                    var grid = WorldApi.SpawnGrid(ob);
                    Track(grid);
                    copy.Grids.Add(grid);
                }
                _copies.Add(copy);
            }
            var settle = WaitForSeconds(4, "the copies settle");
            while (settle.MoveNext()) yield return settle.Current;
            foreach (var copy in _copies)
                copy.World = copy.Grids.Select(g => g.Physics?.HavokWorld).FirstOrDefault(w => w != null);
            _emptyWorld = FakeClients.Character(groups.Count)?.Physics?.HavokWorld;

            // ------------------------------------------------------------- timed
            _handler = OnSampled;
            _cost.GetField("Sampled", any).SetValue(null, _handler);
            _cost.GetField("GapFrames", any).SetValue(null, 1);
            var shares = new Dictionary<long, double>();
            var frames = 0;
            var started = DateTime.UtcNow;
            while ((DateTime.UtcNow - started).TotalSeconds < RunSeconds)
            {
                yield return null;
                share.Invoke(null, new object[] { Convert.ToDouble(monitor.GetProperty("LastMs", any).GetValue(null)), shares });
                frames++;
                foreach (var copy in _copies)
                    copy.Share += copy.Grids.Sum(g => shares.TryGetValue(g.EntityId, out var x) ? x : 0);
            }
            StopSampling();
            double Median(List<double> xs) => xs.Count == 0 ? 0 : xs.OrderBy(x => x).ElementAt(xs.Count / 2);
            var empty = Median(_empty);
            Note($"NEIGHBOURS ALONE | an empty cluster on the planet {empty:0.000} ms ({_empty.Count} steps)");
            foreach (var copy in _copies)
                Note($"NEIGHBOURS ALONE | '{copy.Name}' ({copy.Grids.Count} grids, {copy.Grids.Sum(g => g.BlocksCount)} blocks, awake {copy.Grids.Count(g => g.Physics?.IsActive == true)}): " +
                     $"its cluster {Median(copy.Ms):0.000} ms, +{Median(copy.Ms) - empty:0.000} over the empty ({copy.Ms.Count} steps); PhysicsCost laid to it {copy.Share / Math.Max(1, frames):0.000} ms a frame" +
                     (_copies.Any(o => o != copy && o.World == copy.World) ? " (shares a cluster with another copy)" : "") +
                     "; awake in its cluster: " + (copy.Kinds.Count == 0 ? "nothing" : string.Join(", ", copy.Kinds.OrderByDescending(x => x.Value).Take(8).Select(x => $"{x.Key} {x.Value}"))) +
                     "; its blocks: " + string.Join(", ", copy.Grids.SelectMany(g => g.GetFatBlocks()).GroupBy(b => b.BlockDefinition.Id.TypeId.ToString().Replace("MyObjectBuilder_", "")).OrderByDescending(x => x.Count()).Take(6).Select(x => $"{x.Key} {x.Count()}")));
            Check(_copies.All(c => c.Ms.Count >= 20) && _empty.Count >= 20, "a cluster was not measured enough");
        }

        private void OnSampled(HkWorld world, double ms)
        {
            if (world == _emptyWorld) { _empty.Add(ms); return; }
            foreach (var copy in _copies)
                if (copy.World == world)
                {
                    copy.Ms.Add(ms);
                    // what is awake in it, by kind (the most of a kind seen in one step)
                    var kinds = new Dictionary<string, int>();
                    foreach (var body in world.ActiveRigidBodies)
                    {
                        var entity = (body.UserObject as MyPhysicsBody)?.Entity;
                        var kind = entity == null ? "no entity" : entity is MyCubeGrid g ? "grid " + (g.DisplayName ?? "").Replace(WorldApi.EntityPrefix + Prefix, "") + (g.IsStatic ? " (static)" : "")
                            : entity.GetType().Name + (entity.Parent is MyCubeBlock b ? " of " + b.BlockDefinition.Id.SubtypeName : "");
                        kinds[kind] = (kinds.TryGetValue(kind, out var k) ? k : 0) + 1;
                    }
                    foreach (var pair in kinds)
                        if (!copy.Kinds.TryGetValue(pair.Key, out var was) || was < pair.Value) copy.Kinds[pair.Key] = pair.Value;
                    return;
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
                foreach (var copy in _copies)
                    foreach (var g in copy.Grids)
                        if (!g.Closed) g.Close();
                FakeClients.RemoveAll();
                _config.Restore();
            }
            finally { base.Cleanup(); }
        }
    }
}
