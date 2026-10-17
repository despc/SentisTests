using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Havok;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.Weapons;
using SentisTests.Core;
using SentisTests.Game;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// A real drill ship of the world drilling its real asteroid, as a player would: what the physics step as a whole
    /// does, against what PhysicsCost lays to the ship. The ship (<see cref="ShipName"/> in its display name, or the
    /// grid DebugBridge's test call names) is put with its drills' heads a metre short of the rock ahead of them (or, with
    /// none, turned to the middle of the nearest asteroid), a fake player 60 m off, and it goes round <see cref="Cycles"/> times: held still, drilling - its drills
    /// on and the ship pushed along them at <see cref="DrillSpeed"/> m/s, as a pilot holding "forward" - and held still
    /// again; <see cref="StateSeconds"/> a state, the first <see cref="SettleSeconds"/> of it not counted. Short states
    /// taken in turn, so the step's drift (the bots, the stand warming up after a restart) falls on each alike. For each
    /// state: the frame's physics step on average; on the frames the ship's cluster was timed alone the step and that
    /// cluster's part, and so what the cluster adds to a step (the plain step less the others' part of a timed one); and
    /// what PhysicsCost.Share lays to the ship a frame (the guard's and SentisWatcher's number, the clusters timed as
    /// always). The ship stays where it drilled to, its drills off. The numbers are for reading; passes when every state
    /// was measured.
    /// </summary>
    public sealed class PhysicsDrillLiveScenario : TestScenario
    {
        public const string ScenarioName = "physics_drill_live";
        public const string ShipName = "Бур Физика";
        private const int Cycles = 6;
        private const double StateSeconds = 8, SettleSeconds = 2, DrillSpeed = 0.5;

        private readonly ConfigOverride _config = new ConfigOverride();
        private Type _cost;
        private Action<HkWorld, double> _handler;
        private HkWorld _world;
        private bool _counting;
        private readonly Dictionary<string, List<double>> _alone = new Dictionary<string, List<double>>();
        private string _state;
        private bool _sampledNow;
        private MyCubeGrid _ship;
        private List<MyShipDrill> _drills = new List<MyShipDrill>();

        private sealed class Totals
        {
            public double Steps, Laid, Voxels, Moved, TimedSteps, TimedAlone;
            public int Frames, Timed;
        }

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 420;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            const BindingFlags any = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            _cost = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "SentisOptimisations")
                ?.GetType("Optimizer.Optimizations.PhysicsCost");
            var share = _cost?.GetMethod("Share", any);
            var describe = _cost?.GetMethod("Describe", any);
            var monitor = _cost?.Assembly.GetType("Optimizer.Optimizations.PhysicsLoadMonitor");
            SkipUnless(share != null && describe != null && monitor != null && _cost.GetField("Sampled", any) != null, "SentisOptimisations has no physics cost for tests");
            _config.Set("FreezerEnabled", false);
            _config.Set("EnablePhysicsGuard", false);

            var name = TestRunner.Focus ?? ShipName;
            _ship = MyEntities.GetEntities().OfType<MyCubeGrid>().Where(g => !g.IsStatic && !g.MarkedForClose && (g.DisplayName ?? "").IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderByDescending(g => g.BlocksCount).FirstOrDefault();
            Check(_ship != null, "no dynamic grid named " + name);
            _drills = WorldApi.FindFunctionals<MyShipDrill>(_ship);
            Check(_drills.Count > 0, _ship.DisplayName + " has no drills");
            var into = (Vector3D)_ship.WorldMatrix.GetDirectionVector(_drills[0].Orientation.Forward);
            Note($"SHIP '{_ship.DisplayName}' {_ship.BlocksCount} blocks, {_drills.Count} drills along {into:F2}; " + describe.Invoke(null, new object[] { _ship }));
            _handler = (w, ms) =>
            {
                if (w != _world || !_counting) return;
                if (!_alone.TryGetValue(_state, out var l)) _alone[_state] = l = new List<double>();
                l.Add(ms);
                _sampledNow = true;
            };
            _cost.GetField("Sampled", any).SetValue(null, _handler);

            // room for the ore: a drill with its cargo full cuts nothing (the ore it drilled in these runs goes)
            double volume = 0, max = 0, ore = 0;
            foreach (var block in _ship.GetFatBlocks())
                for (var i = 0; i < block.InventoryCount; i++)
                {
                    var inventory = (VRage.Game.Entity.MyInventoryBase)block.GetInventoryBase(i);
                    if (!(inventory is Sandbox.Game.MyInventory inv)) continue;
                    volume += (double)inv.CurrentVolume;
                    max += (double)inv.MaxVolume;
                    foreach (var item in inv.GetItems().ToList())
                        if (item.Content is VRage.Game.MyObjectBuilder_Ore)
                        {
                            ore += (double)item.Amount;
                            inv.RemoveItems(item.ItemId);
                        }
                }
            Note($"the ship's inventories {volume:0.0} of {max:0.0} m3 full; {ore / 1000:0.0} t of ore taken out");
            // ahead of the drills, else the rock of the nearest asteroid: the ship turned to face it, the drills' heads a metre
            // short of it
            var heads = _drills.Select(d => d.PositionComp.WorldAABB.Center).Aggregate((x, y) => x + y) / _drills.Count + into * 2.5;
            var rock = Rock(heads, into, 300);
            if (rock == null)
            {
                var asteroid = MyEntities.GetEntities().OfType<MyVoxelBase>().Where(v => !(v is MyPlanet) && v.RootVoxel == v)
                    .OrderBy(v => Vector3D.DistanceSquared(v.PositionComp.WorldAABB.Center, heads)).FirstOrDefault();
                Check(asteroid != null, "no asteroid");
                var towards = Vector3D.Normalize(asteroid.PositionComp.WorldAABB.Center - heads);
                rock = Rock(heads, towards, 3000);
                Check(rock != null, "no rock between the drills and the middle of the nearest asteroid");
                // the ship turned about its drills' heads so that they face the rock
                var axis = Vector3D.Cross(into, towards);
                var turn = axis.LengthSquared() < 1e-9 ? MatrixD.Identity : MatrixD.CreateFromAxisAngle(Vector3D.Normalize(axis), Math.Acos(MathHelper.Clamp(Vector3D.Dot(into, towards), -1, 1)));
                var m = _ship.WorldMatrix;
                var offset = heads - m.Translation;
                var turned = m.GetOrientation() * turn;
                turned.Translation = heads - Vector3D.TransformNormal(offset, turn);
                _ship.Teleport(turned);
                Note($"turned {MathHelper.ToDegrees(Math.Acos(MathHelper.Clamp(Vector3D.Dot(into, towards), -1, 1))):0} degrees to the asteroid {asteroid.StorageName}");
                into = towards;
            }
            var face = _ship.WorldMatrix;
            face.Translation += into * (rock.Value - 1);
            _ship.Teleport(face);
            Note($"to the rock: {rock.Value - 1:0.0} m on");
            var near = _ship.PositionComp.WorldAABB.Center - into * 60;
            FakeClients.RemoveAll();
            FakeClients.Add(1, new FakeClients.NetworkProfile { RttMs = 30 }, i => (near, 0, 0), withCharacters: true);
            var arrive = WaitForSeconds(8, "a player by the ship");
            while (arrive.MoveNext()) yield return arrive.Current;
            if (_ship.Physics?.RigidBody != null) _ship.Physics.RigidBody.EnableDeactivation = false;

            const string still = "the player by it, held still", drilling = "drilling", after = "held still after drilling";
            var totals = new Dictionary<string, Totals>();
            var shares = new Dictionary<long, double>();
            for (var cycle = 0; cycle < Cycles; cycle++)
                foreach (var state in new[] { still, drilling, after })
                {
                    _state = state;
                    foreach (var d in _drills) d.Enabled = state == drilling;
                    var began = DateTime.UtcNow;
                    var from = _ship.PositionComp.GetPosition();
                    if (!totals.TryGetValue(state, out var t)) totals[state] = t = new Totals();
                    while ((DateTime.UtcNow - began).TotalSeconds < StateSeconds)
                    {
                        if (_ship.Physics != null)
                        {
                            _ship.Physics.LinearVelocity = state == drilling ? (Vector3)(into * DrillSpeed) : Vector3.Zero;
                            _ship.Physics.AngularVelocity = Vector3.Zero;
                        }
                        yield return null;
                        _world = _ship.Physics?.HavokWorld;
                        _counting = (DateTime.UtcNow - began).TotalSeconds >= SettleSeconds;
                        if (!_counting) continue;
                        var stepMs = Convert.ToDouble(monitor.GetProperty("LastMs", any).GetValue(null));
                        share.Invoke(null, new object[] { stepMs, shares });
                        t.Laid += shares.TryGetValue(_ship.EntityId, out var x) ? x : 0;
                        t.Steps += stepMs;
                        if (_sampledNow) { t.Timed++; t.TimedSteps += stepMs; t.TimedAlone += _alone[_state].Last(); }
                        _sampledNow = false;
                        if (((string)describe.Invoke(null, new object[] { _ship })).Contains("on the voxels")) t.Voxels++;
                        t.Frames++;
                    }
                    _counting = false;
                    t.Moved += Vector3D.Distance(from, _ship.PositionComp.GetPosition());
                }
            foreach (var d in _drills) d.Enabled = false;

            foreach (var pair in totals)
            {
                var t = pair.Value;
                var step = t.Steps / Math.Max(1, t.Frames);
                var alone = _alone.TryGetValue(pair.Key, out var l) ? l.OrderBy(v => v).ToList() : new List<double>();
                Note($"LIVE {pair.Key} | {t.Frames} frames: the physics step {step:0.000} ms, laid to the ship {t.Laid / Math.Max(1, t.Frames):0.000} ms a frame; " +
                     "its cluster alone " + (alone.Count == 0 ? "not timed" : $"median {alone[alone.Count / 2]:0.000} ms ({alone.Count} times)") +
                     (t.Timed > 0 ? $"; the frames it was timed alone: the step {t.TimedSteps / t.Timed:0.000} ms, of it alone {t.TimedAlone / t.Timed:0.000} - what it adds to a step {step - (t.TimedSteps - t.TimedAlone) / t.Timed:0.000} ms" : "") +
                     $"; on the voxels {100 * t.Voxels / Math.Max(1, t.Frames):0}% of the frames; moved {t.Moved:0.0} m");
            }
            Note("LIVE now: " + describe.Invoke(null, new object[] { _ship }));
            Check(totals.Values.All(t => t.Frames > 0), "not every state measured");
        }

        /// <summary>How far along the line the first voxel is, or null.</summary>
        private static double? Rock(Vector3D from, Vector3D dir, double length)
        {
            var hits = new List<MyPhysics.HitInfo>();
            MyPhysics.CastRay(from, from + dir * length, hits, MyPhysics.CollisionLayers.CollideWithStaticLayer);
            return hits.Where(h => h.HkHitInfo.GetHitEntity() is MyVoxelBase).Select(h => (double?)Vector3D.Distance(from, h.Position)).FirstOrDefault();
        }

        public override void Cleanup()
        {
            try
            {
                const BindingFlags any = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                var sampled = _cost?.GetField("Sampled", any);
                if (sampled != null && ReferenceEquals(sampled.GetValue(null), _handler)) sampled.SetValue(null, null);
                foreach (var d in _drills) d.Enabled = false;
                if (_ship?.Physics != null)
                {
                    _ship.Physics.LinearVelocity = Vector3.Zero;
                    _ship.Physics.AngularVelocity = Vector3.Zero;
                    if (_ship.Physics.RigidBody != null) _ship.Physics.RigidBody.EnableDeactivation = true;
                }
                FakeClients.RemoveAll();
                _config.Restore();
            }
            finally { base.Cleanup(); }
        }
    }
}
