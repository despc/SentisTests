using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Game.Entities;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// SentisOptimisations' share of the physics step within one cluster (PhysicsCost.Share) goes by what the grids cost,
    /// not by their blocks. In one cluster on a planet, by a fake player: a cube of 1000 armor blocks kept awake on a
    /// static slab (a grid on a grid: 0.07-0.2 ms by <see cref="PhysicsCalibrationScenario"/>), and 130 m off a stick of 152
    /// (2 x 2 x 38) put upright and tipped, kept awake as it falls and lies on the voxels (0.45 ms lying). The step is
    /// shared out every frame for <see cref="RunSeconds"/>. Passes when the stick got more than <see cref="Ratio"/> times
    /// the cube's share once it lay on the ground (by blocks it would have got a fifth of it), and the two together
    /// were measured in one cluster.
    /// </summary>
    public sealed class PhysicsShareScenario : TestScenario
    {
        public const string ScenarioName = "physics_share";
        private const string Prefix = "pshare-";
        private const string Armor = "LargeBlockArmorBlock";
        private const int RunSeconds = 30;
        private const double Ratio = 2;

        private readonly ConfigOverride _config = new ConfigOverride();
        private readonly List<MyCubeGrid> _grids = new List<MyCubeGrid>();

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 180;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            const BindingFlags any = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "SentisOptimisations");
            var cost = assembly?.GetType("Optimizer.Optimizations.PhysicsCost");
            var monitor = assembly?.GetType("Optimizer.Optimizations.PhysicsLoadMonitor");
            var share = cost?.GetMethod("Share", any);
            SkipUnless(share != null && monitor != null && cost.GetField("VoxelTouchWork", any) != null, "SentisOptimisations has no share of the physics by contacts");
            _config.Set("FreezerEnabled", false);
            _config.Set("EnablePhysicsGuard", false);

            // ------------------------------------------------------------- a flat place with nobody near
            var planet = MyEntities.GetEntities().OfType<MyPlanet>().Where(p => p.HasAtmosphere).OrderByDescending(p => p.MaximumRadius).FirstOrDefault();
            SkipUnless(planet != null, "no planet with an atmosphere");
            var centre = planet.PositionComp.GetPosition();
            var others = MyEntities.GetEntities().OfType<MyCubeGrid>().Select(g => g.PositionComp.GetPosition()).ToList();
            Vector3D Surface(Vector3D point) => planet.GetClosestSurfacePointGlobal(ref point);
            Vector3D spot = Vector3D.Zero, up = Vector3D.Zero;
            const int n = 600;
            for (var i = 0; i < n && spot == Vector3D.Zero; i++)
            {
                var y = 1 - 2.0 * (i + 0.5) / n;
                var r = Math.Sqrt(1 - y * y);
                var a = i * Math.PI * (3 - Math.Sqrt(5));
                var dir = new Vector3D(Math.Cos(a) * r, y, Math.Sin(a) * r);
                var g = Surface(centre + dir * planet.MaximumRadius);
                if (others.Any(o => Vector3D.DistanceSquared(o, g) < 15000.0 * 15000.0)) continue;
                var height = Vector3D.Distance(g, centre);
                var side = Vector3D.CalculatePerpendicularVector(dir);
                var flat = new[] { side, -side, Vector3D.Cross(dir, side), -Vector3D.Cross(dir, side) }
                    .All(d => Math.Abs(Vector3D.Distance(Surface(g + d * 60 + dir * 50), centre) - height) < 3);
                if (!flat) continue;
                spot = g;
                up = Vector3D.Normalize(g - centre);
            }
            Check(spot != Vector3D.Zero, "no flat place without grids within 15 km");
            var forward = Vector3D.CalculatePerpendicularVector(up);
            var across = Vector3D.Cross(up, forward);

            FakeClients.RemoveAll();
            FakeClients.Add(1, new FakeClients.NetworkProfile { RttMs = 30 }, i => (Surface(spot - across * 80 + up * 50) + up * 3, 0, 0), withCharacters: true);
            var arrive = WaitForSeconds(6, "the player arrives");
            while (arrive.MoveNext()) yield return arrive.Current;

            // ------------------------------------------------------------- the slab and the cube on it; the stick
            MyObjectBuilder_CubeGrid Box(string name, Vector3D at, int x, int y, int z, bool isStatic)
            {
                var blocks = new List<BlockSpec>();
                for (var i = 0; i < x; i++)
                    for (var j = 0; j < y; j++)
                        for (var l = 0; l < z; l++)
                            blocks.Add(new BlockSpec(Armor, new Vector3I(i - x / 2, j, l - z / 2)));
                var ob = WorldApi.GridOb(WorldApi.EntityPrefix + Prefix + name, MyCubeSize.Large, isStatic, at, blocks, (Vector3)forward, (Vector3)up);
                ob.DampenersEnabled = false;
                ob.DestructibleBlocks = false;
                return ob;
            }
            MyCubeGrid Spawn(MyObjectBuilder_CubeGrid ob)
            {
                var grid = WorldApi.SpawnGrid(ob);
                Track(grid);
                _grids.Add(grid);
                return grid;
            }
            var bottom = spot + up * 1.75;
            Spawn(Box("slab", bottom, 20, 2, 20, true));
            var cube = Spawn(Box("cube", bottom + up * 5, 10, 10, 10, false));
            var stick = Spawn(Box("stick", Surface(spot + across * 130 + up * 50) + up * 1.75, 2, 38, 2, false));
            foreach (var g in new[] { cube, stick }) g.Physics.RigidBody.EnableDeactivation = false;
            stick.Physics.AngularVelocity = (Vector3)(forward * 0.15);
            Check(cube.Physics.HavokWorld == stick.Physics.HavokWorld, "the cube and the stick are not in one cluster");

            // ------------------------------------------------------------- the step shared out, every frame
            var shares = new Dictionary<long, double>();
            double cubeMs = 0, stickMs = 0, cubeDown = 0, stickDown = 0, stepDown = 0;
            int frames = 0, down = 0;
            var started = DateTime.UtcNow;
            var lastNote = started;
            while ((DateTime.UtcNow - started).TotalSeconds < RunSeconds)
            {
                yield return null;
                var stepMs = Convert.ToDouble(monitor.GetProperty("LastMs", any).GetValue(null));
                share.Invoke(null, new object[] { stepMs, shares });
                shares.TryGetValue(cube.EntityId, out var c);
                shares.TryGetValue(stick.EntityId, out var s);
                frames++;
                cubeMs += c;
                stickMs += s;
                // lying: the stick turned past 60 degrees from upright
                var lying = Math.Abs(Vector3D.Dot(stick.WorldMatrix.Up, up)) < 0.5;
                if (lying)
                {
                    down++;
                    cubeDown += c;
                    stickDown += s;
                    stepDown += stepMs;
                }
                if ((DateTime.UtcNow - lastNote).TotalSeconds >= 5)
                {
                    lastNote = DateTime.UtcNow;
                    Note($"sharing ({(lastNote - started).TotalSeconds:0}/{RunSeconds} s): the step {stepMs:0.000} ms, the cube {c:0.000}, the stick {s:0.000}{(lying ? ", lying" : "")}");
                }
            }
            Note($"PHYSICS SHARE | {frames} frames: the cube {cubeMs / frames:0.000} ms a frame, the stick {stickMs / frames:0.000}; " +
                 $"{down} frames with the stick lying: the step {(down > 0 ? stepDown / down : 0):0.000} ms, the cube {(down > 0 ? cubeDown / down : 0):0.000}, the stick {(down > 0 ? stickDown / down : 0):0.000}");
            Check(down > 120, $"the stick lay only {down} frames");
            Check(stickDown > cubeDown * Ratio, $"lying, the stick got {stickDown / down:0.000} ms a frame and the cube {cubeDown / down:0.000}: under {Ratio} times");
        }

        public override void Cleanup()
        {
            try
            {
                foreach (var grid in _grids)
                    if (!grid.Closed) grid.Close();
                FakeClients.RemoveAll();
                _config.Restore();
            }
            finally { base.Cleanup(); }
        }
    }
}
