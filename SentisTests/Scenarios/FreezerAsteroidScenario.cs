using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Havok;
using Sandbox.Definitions;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.GameSystems;
using Sandbox.Game.World;
using SentisTests.Core;
using SentisTests.Game;
using VRage.Game.Entity;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// A dynamic grid with dynamic subgrids resting against an asteroid in empty space, frozen and thawed by the
    /// SentisOptimisations freezer as its player leaves and comes back. Reported from the live server (17.10.2026): such
    /// a grid, hanging all but touching an asteroid, got a kick when its player logged out and in, and broke on the rock.
    ///
    /// The group is the freezer test's (<see cref="FreezerPhysicsScenario.ResourceName"/>: a chassis with wheels, pistons
    /// and a hinge, its static grid left out, the gear released) with a working assembler added, brought down onto an
    /// asteroid of its own until it touches, then let settle with a fake player by it. Before every leave its pistons and
    /// hinges are set moving. Phases of <see cref="Cycles"/> leave-and-return cycles each: the freezer in logic only;
    /// the same with wake-ups while nobody is there (MinWakeUpIntervalInSec <see cref="WakeInterval"/>: a group with
    /// production is woken for 10 s, players or not); the same with FreezePhysics; the same with a second player 2 km
    /// off (the world stepped while the group is frozen); the freezer off (with selective physics updates the world
    /// only stops being stepped). After every return the fastest grid, the biggest change of speed in one frame, the
    /// blocks' damage and how far each grid went are noted, and the asteroid's collision meshes. A phase is quiet when
    /// no change of speed in a frame is over <see cref="MaxKick"/> and nothing is damaged; passes when every phase is.
    /// </summary>
    public sealed class FreezerAsteroidScenario : TestScenario
    {
        public const string ScenarioName = "freezer_asteroid";
        private const string Prefix = "frzast-";
        private const string AsteroidPrefab = "Asteroid256-001";
        private const int Cycles = 2;
        private const int FreezeDistance = 500;
        private const double AwaySeconds = 80, WatchSeconds = 6, SettleSeconds = 12, WaitStateSeconds = 40;
        /// <summary>The joints move by design; a change of speed of more than this in one frame (180 m/s², 18 g) is a kick.</summary>
        private const double MaxKick = 3;
        private const int WakeInterval = 15;

        private enum Mode { FreezePhysics, LogicOnly, FreezerOff }

        private sealed class Phase
        {
            public Mode Mode;
            /// <summary>A second player 2 km off, beyond the freeze distance: the group's world is stepped while it is frozen.</summary>
            public bool Stepped;
            /// <summary>The asteroid's meshes dropped while nobody is there, as the game drops them when nothing moving is by it.</summary>
            public bool Discard;
            /// <summary>
            /// MinWakeUpIntervalInSec short, so the group with a working assembler is woken while nobody is there (the
            /// freezer wakes a group with production for 10 s every MinWakeUpIntervalInSec, players or not).
            /// </summary>
            public bool Wake;
            public int Wakes;
            public double MaxKick;
            public string Name => Mode + (Stepped ? ", world stepped" : ", world not stepped") + (Discard ? ", rock meshes dropped" : "") + (Wake ? ", woken while away" : "");
            public double MaxSpeed, MaxAngular, MaxTouch, MaxMoved, Damage;
            public int BlocksLost, TopsLost;
            public string MaxSpeedWhat = "";
            public readonly List<string> Cycles = new List<string>();
        }

        private readonly List<MyCubeGrid> _grids = new List<MyCubeGrid>();
        private readonly ConfigOverride _config = new ConfigOverride();
        private MyVoxelBase _asteroid;
        private Vector3D _playerAt, _up;
        // the hardest touch with the asteroid since it was last read: separating velocity of a contact point, m/s
        private double _touch;
        private bool _touched;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 5 * Cycles * (int)(AwaySeconds + WatchSeconds + 2 * WaitStateSeconds + 10) + 300;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            // ------------------------------------------------------------- a place in empty space, nobody near
            var planets = MyEntities.GetEntities().OfType<MyPlanet>().ToList();
            var others = MyEntities.GetEntities().Where(e => e is MyCubeGrid || e is MyVoxelBase && !(e is MyPlanet))
                .Select(e => e.PositionComp.GetPosition()).ToList();
            Vector3D? spot = null;
            for (var i = 0; i < 400 && spot == null; i++)
            {
                var probe = new Vector3D(820000 - (i % 20) * 83000, 610000 - (i / 20) * 61000, 270000 - (i % 7) * 90000);
                if (MyGravityProviderSystem.CalculateNaturalGravityInPoint(probe).LengthSquared() > 1e-6) continue;
                if (planets.Any(p => Vector3D.Distance(p.PositionComp.GetPosition(), probe) < p.MaximumRadius + 20000)) continue;
                if (others.Any(o => Vector3D.DistanceSquared(o, probe) < 15000.0 * 15000.0)) continue;
                spot = probe;
            }
            Check(spot.HasValue, "no place in empty space");

            // the freezer stays off until the group is down on the rock
            _config.Set("FreezerEnabled", false);
            _config.Set("FreezeDistanceDynamic", FreezeDistance);
            _config.Set("FreezeDistanceStatic", FreezeDistance);

            // the asteroid gets its physics only with somebody by it
            FakeClients.RemoveAll();
            FakeClients.Add(1, new FakeClients.NetworkProfile { RttMs = 50 }, _ => (spot.Value + Vector3D.Up * 300, 0, 0), withCharacters: true);
            var arrive = WaitForSeconds(8, "the player arrives");
            while (arrive.MoveNext()) yield return arrive.Current;

            var prefab = MyDefinitionManager.Static.GetVoxelMapStorageDefinitions().Select(d => d.Id.SubtypeName)
                .FirstOrDefault(n => n == AsteroidPrefab) ?? MyDefinitionManager.Static.GetVoxelMapStorageDefinitions()
                .Select(d => d.Id.SubtypeName).FirstOrDefault(n => n.StartsWith("Asteroid256"));
            Check(prefab != null, "no asteroid prefab of 256");
            _asteroid = MyWorldGenerator.AddAsteroidPrefab(prefab, spot.Value, WorldApi.EntityPrefix + Prefix + "asteroid");
            Check(_asteroid != null, "the asteroid " + prefab + " was not made");
            Track(_asteroid);
            yield return WaitForTicks(120);

            // ------------------------------------------------------------- the group above the rock
            var group = WorldApi.LoadAuthoredGroup(FreezerPhysicsScenario.ResourceName, WorldApi.EntityPrefix + Prefix + "rig");
            var chassisOb = group.First(ob => !ob.IsStatic);
            var authored = chassisOb.PositionAndOrientation.Value.GetMatrix();
            var authoredPlanet = MyGamePruningStructure.GetClosestPlanet(authored.Translation);
            var up = authoredPlanet != null ? Vector3D.Normalize(authored.Translation - authoredPlanet.PositionComp.GetPosition()) : authored.Up;
            var middle = _asteroid.PositionComp.WorldAABB.Center;
            var rock = Surface(middle + up * 400, -up, 800);
            Check(rock.HasValue, "no rock under the place for the group");
            var shift = rock.Value + up * 20 - authored.Translation;
            foreach (var ob in group)
            {
                if (ob.IsStatic) continue;
                var m = ob.PositionAndOrientation.Value.GetMatrix();
                m.Translation += shift;
                ob.PositionAndOrientation = new VRage.MyPositionAndOrientation(m);
                foreach (var gear in ob.CubeBlocks.OfType<Sandbox.Common.ObjectBuilders.MyObjectBuilder_LandingGear>())
                {
                    gear.AutoLock = false;
                    gear.IsLocked = false;
                    gear.LockMode = SpaceEngineers.Game.ModAPI.Ingame.LandingGearMode.Unlocked;
                    gear.AttachedEntityId = null;
                }
                ob.DampenersEnabled = false;
                if (ob == chassisOb)
                {
                    // on the chassis' top row of armour (y 4, x -3..-21, z 0), clear of the cockpit, piston, gyro and hinge
                    var assembler = WorldApi.MakeBlockOb("BasicAssembler");
                    assembler.Min = new VRage.SerializableVector3I(-10, 5, 0);
                    assembler.BlockOrientation = new VRage.Game.SerializableBlockOrientation(Base6Directions.Direction.Forward, Base6Directions.Direction.Up);
                    assembler.Owner = WorldApi.PlayerIdentityId();
                    assembler.BuiltBy = assembler.Owner;
                    ob.CubeBlocks.Add(assembler);
                }
                var grid = WorldApi.SpawnGrid(ob);
                Track(grid);
                _grids.Add(grid);
            }
            foreach (var grid in _grids) WorldApi.ChargeBatteries(grid);
            _up = up;
            _playerAt = rock.Value + up * 20 + Vector3D.CalculatePerpendicularVector(up) * 40;
            FakeClients.RemoveAll();
            FakeClients.Add(1, new FakeClients.NetworkProfile { RttMs = 50 }, _ => (_playerAt, 0, 0), withCharacters: true);
            yield return WaitForTicks(60);
            foreach (var grid in _grids) Listen(grid);

            // down onto the rock at 2 m/s until it touches, then let it come to rest there
            var approach = DateTime.UtcNow;
            while (!_touched && (DateTime.UtcNow - approach).TotalSeconds < 30)
            {
                foreach (var g in _grids.Where(g => g.Physics != null))
                {
                    g.Physics.LinearVelocity = (Vector3)(-up * 2);
                    g.Physics.AngularVelocity = Vector3.Zero;
                }
                yield return null;
            }
            Check(_touched, "the group did not reach the rock");
            foreach (var g in _grids.Where(g => g.Physics != null))
            {
                g.Physics.LinearVelocity = Vector3.Zero;
                g.Physics.AngularVelocity = Vector3.Zero;
            }
            var settle = WaitForSeconds(SettleSeconds, "the group settles on the rock");
            while (settle.MoveNext()) yield return settle.Current;
            var production = _grids.SelectMany(g => g.GetFatBlocks().OfType<Sandbox.Game.Entities.Cube.MyProductionBlock>()).ToList();
            Check(production.Count == 1 && production[0].IsWorking, "the assembler on the chassis is not working: " + production.Count);
            Note("group: " + _grids.Count + " grids, " + RigParts.Blocks(_grids) + " blocks, tops attached " + RigParts.Attached(_grids) +
                 ", speed now " + _grids.Max(g => g.Physics?.LinearVelocity.Length() ?? 0).ToString("F3") + " m/s, " + Describe());

            // ------------------------------------------------------------- the phases
            var phases = new List<Phase>();
            foreach (var phase in new[]
                     {
                         new Phase { Mode = Mode.LogicOnly }, new Phase { Mode = Mode.LogicOnly, Wake = true },
                         new Phase { Mode = Mode.FreezePhysics, Wake = true }, new Phase { Mode = Mode.LogicOnly, Wake = true, Stepped = true },
                         new Phase { Mode = Mode.FreezerOff },
                     })
            {
                var mode = phase.Mode;
                phases.Add(phase);
                _config.Set("FreezePhysics", mode == Mode.FreezePhysics);
                _config.Set("FreezerEnabled", mode != Mode.FreezerOff);
                _config.Set("MinWakeUpIntervalInSec", phase.Wake ? WakeInterval : 1000);
                var awake = Wait(() => _grids.All(g => g.Closed || !FreezerState.IsFrozen(g)), mode + ": the group awake", (int)WaitStateSeconds);
                while (awake.MoveNext()) yield return awake.Current;
                var calm = WaitForSeconds(5, mode + ": calm before");
                while (calm.MoveNext()) yield return calm.Current;
                for (var cycle = 1; cycle <= Cycles; cycle++)
                {
                    var cycleRun = RunCycle(phase, cycle);
                    while (cycleRun.MoveNext()) yield return cycleRun.Current;
                }
            }

            Note("FREEZER ASTEROID RESULT | " + string.Join(" | ", phases.Select(p => p.Name + ": woken " + p.Wakes + ", max speed " + p.MaxSpeed.ToString("F2") + " m/s (" +
                 p.MaxSpeedWhat + "), kick " + p.MaxKick.ToString("F2") + " m/s in a frame" +
                 ", angular " + p.MaxAngular.ToString("F2") + " rad/s, hardest touch " + p.MaxTouch.ToString("F2") + " m/s, moved " +
                 p.MaxMoved.ToString("F2") + " m, damage " + p.Damage.ToString("F0") + ", blocks lost " + p.BlocksLost + ", tops lost " + p.TopsLost)));
            var loud = phases.Where(p => p.MaxKick > MaxKick || p.Damage > 1 || p.BlocksLost > 0 || p.TopsLost > 0).ToList();
            Check(loud.Count == 0, "kicked on return: " + string.Join(", ", loud.Select(p => p.Name + " kick " + p.MaxKick.ToString("F1") + " m/s in a frame, damage " + p.Damage.ToString("F0"))));
        }

        private IEnumerator RunCycle(Phase phase, int cycle)
        {
            var label = phase.Name + " cycle " + cycle;
            var poses = _grids.Select(g => g.PositionComp.GetPosition()).ToList();
            var integrity = RigParts.Integrity(_grids);
            var blocks = RigParts.Blocks(_grids);
            var tops = RigParts.Attached(_grids);

            RigParts.StartMotors(_grids);
            foreach (var piston in RigParts.Tops(_grids).OfType<Sandbox.ModAPI.IMyPistonBase>())
                piston.Velocity = cycle % 2 == 0 ? -0.3f : 0.3f;
            foreach (var stator in RigParts.Tops(_grids).OfType<Sandbox.ModAPI.IMyMotorStator>().Where(t => !(t is Sandbox.Game.Entities.Cube.MyMotorSuspension)))
                stator.TargetVelocityRPM = cycle % 2 == 0 ? -2f : 2f;

            // the player leaves (and another stays 2 km off)
            FakeClients.RemoveAll();
            if (phase.Stepped) AddFar();
            var left = DateTime.UtcNow;
            if (phase.Mode != Mode.FreezerOff)
            {
                var frozen = Wait(() => _grids.All(g => g.Closed || FreezerState.IsFrozen(g)), label + ": frozen", (int)WaitStateSeconds);
                while (frozen.MoveNext()) yield return frozen.Current;
            }
            var frozenAfter = (DateTime.UtcNow - left).TotalSeconds;
            var physicsFrozen = _grids.Count(FreezerState.IsPhysicsFrozen);
            var fixedBodies = _grids.Count(FreezerState.HasFixedBody);
            var meshesAtFreeze = Describe();
            // nobody there: wake-ups counted, and the speed the bodies gather while the world stands
            var steppedAway = "";
            var dropped = "";
            var wakes = 0;
            var wasFrozen = true;
            double gathered = 0;
            var awayFrom = DateTime.UtcNow;
            var noted = 0;
            while ((DateTime.UtcNow - awayFrom).TotalSeconds < AwaySeconds)
            {
                var seconds = (DateTime.UtcNow - awayFrom).TotalSeconds;
                if (steppedAway == "" && seconds > AwaySeconds / 2)
                {
                    steppedAway = Stepped();
                    if (phase.Discard) dropped = Discard();
                }
                var frozenNow = phase.Mode == Mode.FreezerOff || _grids.All(g => g.Closed || FreezerState.IsFrozen(g));
                if (wasFrozen && !frozenNow) wakes++;
                wasFrozen = frozenNow;
                foreach (var g in _grids)
                    if (!g.Closed && g.Physics != null) gathered = Math.Max(gathered, g.Physics.LinearVelocity.Length());
                if ((int)seconds / 10 > noted)
                {
                    noted = (int)seconds / 10;
                    Note(label + ": nobody there (" + (int)seconds + "/" + AwaySeconds + "s), woken " + wakes);
                }
                yield return null;
            }
            phase.Wakes += wakes;
            var meshesAway = Describe();
            var movedAway = _grids.Select((g, i) => Vector3D.Distance(g.PositionComp.GetPosition(), poses[i])).Max();

            // the player comes back
            FakeClients.RemoveAll();
            FakeClients.Add(1, new FakeClients.NetworkProfile { RttMs = 50 }, _ => (_playerAt, 0, 0), withCharacters: true);
            if (phase.Stepped) AddFar();
            var back = DateTime.UtcNow;
            if (phase.Mode != Mode.FreezerOff)
            {
                var thawed = Wait(() => _grids.All(g => g.Closed || !FreezerState.IsFrozen(g)), label + ": thawed", (int)WaitStateSeconds);
                while (thawed.MoveNext()) yield return thawed.Current;
            }
            var thawAfter = (DateTime.UtcNow - back).TotalSeconds;
            var meshesThaw = Describe();
            _touch = 0;
            double speed = 0, angular = 0, kick = 0;
            string what = "", kickWhat = "";
            var last = _grids.Select(g => g.Physics?.LinearVelocity ?? Vector3.Zero).ToList();
            var watch = DateTime.UtcNow;
            while ((DateTime.UtcNow - watch).TotalSeconds < WatchSeconds)
            {
                for (var i = 0; i < _grids.Count; i++)
                {
                    var g = _grids[i];
                    if (g.Closed || g.Physics == null) continue;
                    var dv = (g.Physics.LinearVelocity - last[i]).Length();
                    last[i] = g.Physics.LinearVelocity;
                    if (dv > kick)
                    {
                        kick = dv;
                        kickWhat = "grid " + i + " " + (DateTime.UtcNow - watch).TotalSeconds.ToString("F2") + " s after";
                    }
                    var v = g.Physics.LinearVelocity.Length();
                    angular = Math.Max(angular, g.Physics.AngularVelocity.Length());
                    if (v <= speed) continue;
                    speed = v;
                    what = "grid " + i + " (" + g.BlocksCount + " blocks) " + (DateTime.UtcNow - watch).TotalSeconds.ToString("F2") + " s after the thaw";
                }
                yield return null;
            }
            var moved = _grids.Select((g, i) => g.Closed ? 0 : Vector3D.Distance(g.PositionComp.GetPosition(), poses[i])).Max();
            var damage = integrity - RigParts.Integrity(_grids);
            var lostBlocks = blocks - RigParts.Blocks(_grids);
            var lostTops = tops - RigParts.Attached(_grids);
            if (speed > phase.MaxSpeed) { phase.MaxSpeed = speed; phase.MaxSpeedWhat = label + ", " + what; }
            phase.MaxAngular = Math.Max(phase.MaxAngular, angular);
            phase.MaxKick = Math.Max(phase.MaxKick, kick);
            phase.MaxTouch = Math.Max(phase.MaxTouch, _touch);
            phase.MaxMoved = Math.Max(phase.MaxMoved, moved);
            phase.Damage += Math.Max(0, damage);
            phase.BlocksLost += Math.Max(0, lostBlocks);
            phase.TopsLost += Math.Max(0, lostTops);
            Note(label + ": frozen " + frozenAfter.ToString("F1") + " s after the leave, world stepped while away " + steppedAway + ", woken " + wakes +
                 " times while away, speed gathered while away " + gathered.ToString("F2") + " m/s, physics-frozen " + physicsFrozen + "/" + _grids.Count + ", fixed bodies " + fixedBodies + ", moved while away " +
                 movedAway.ToString("F3") + " m; thawed " + thawAfter.ToString("F1") + " s after the return; max speed " + speed.ToString("F2") +
                 " m/s (" + what + "), kick " + kick.ToString("F2") + " m/s in a frame (" + kickWhat + "), angular " + angular.ToString("F2") + ", hardest touch " + _touch.ToString("F2") + " m/s, moved " + moved.ToString("F2") +
                 " m, damage " + damage.ToString("F0") + ", blocks lost " + lostBlocks + ", tops lost " + lostTops +
                 " | rock meshes: at the freeze " + meshesAtFreeze + "; " + dropped + "away " + meshesAway + "; at the thaw " + meshesThaw);
            var rest = WaitForSeconds(5, label + ": awake");
            while (rest.MoveNext()) yield return rest.Current;
        }

        private void AddFar() =>
            FakeClients.Add(1, new FakeClients.NetworkProfile { RttMs = 50 }, _ => (_playerAt + Vector3D.CalculatePerpendicularVector(_up) * 2000, 0, 0), withCharacters: true);

        /// <summary>Whether MyPhysics steps the world the group's chassis is in (selective physics updates).</summary>
        private string Stepped()
        {
            var world = _grids[0].Physics?.HavokWorld;
            if (world == null) return "no world";
            var physics = MySession.Static.GetComponent<MyPhysics>();
            var active = typeof(MyPhysics).GetMethod("IsClusterActive", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(int), typeof(int) }, null);
            foreach (var cluster in MyPhysics.Clusters.GetClusters())
                if (cluster.UserData == world)
                    return ((bool)active.Invoke(physics, new object[] { cluster.ClusterId, world.CharacterRigidBodies.Count })).ToString();
            return "no cluster";
        }

        /// <summary>
        /// What MyVoxelPhysicsBody.CheckAndDiscardShapes does when nothing that moves is near the rock (m_nearbyEntities
        /// empty) and nothing asked for its meshes for a while: the meshes of the cells dropped.
        /// </summary>
        private string Discard()
        {
            var physics = _asteroid?.Physics as MyPhysicsBody;
            if (physics?.RigidBody == null) return "no rock body to drop; ";
            var nearby = physics.GetType().GetField("m_nearbyEntities", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(physics) as List<MyEntity>;
            var before = Describe();
            ((HkUniformGridShape)physics.RigidBody.GetShape()).DiscardLargeData();
            return "dropped (moving things by the rock for the game: " + (nearby?.Count.ToString() ?? "?") + ") " + before + " -> " + Describe() + "; ";
        }

        /// <summary>The asteroid's collision meshes: cells with a mesh in its LOD0 and LOD1 bodies.</summary>
        private string Describe()
        {
            var physics = _asteroid?.Physics as MyPhysicsBody;
            if (physics == null) return "no physics";
            string Count(HkRigidBody body) => body == null ? "-" : ((HkUniformGridShape)body.GetShape()).ShapeCount.ToString();
            return "cells " + Count(physics.RigidBody) + "/" + Count(physics.RigidBody2);
        }

        private void Listen(MyCubeGrid grid)
        {
            var body = grid.Physics?.RigidBody;
            if (body == null) return;
            var asteroid = _asteroid;
            body.ContactPointCallback += (ref HkContactPointEvent e) =>
            {
                var a = e.Base.BodyA?.UserObject as MyPhysicsBody;
                var b = e.Base.BodyB?.UserObject as MyPhysicsBody;
                if (a?.Entity != asteroid && b?.Entity != asteroid) return;
                _touched = true;
                var v = Math.Abs(e.SeparatingVelocity);
                if (v > _touch) _touch = v;
            };
            body.ContactPointCallbackEnabled = true;
        }

        private static Vector3D? Surface(Vector3D from, Vector3D dir, double length)
        {
            var hits = new List<MyPhysics.HitInfo>();
            MyPhysics.CastRay(from, from + dir * length, hits, MyPhysics.CollisionLayers.CollideWithStaticLayer);
            foreach (var hit in hits)
                if (hit.HkHitInfo.GetHitEntity() is MyVoxelBase) return hit.Position;
            return null;
        }

        public override void Cleanup()
        {
            try
            {
                FakeClients.RemoveAll();
                _config.Restore();
            }
            finally { base.Cleanup(); }
        }

        public override void CleanupLeftovers()
        {
            try { _config.Restore(); }
            finally { base.CleanupLeftovers(); }
        }
    }
}
