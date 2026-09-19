using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using SentisTests.Core;
using SentisTests.Game;
using SpaceEngineers.Game.Entities.Blocks;
using VRage;
using VRage.Game;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// Freezer physics freeze/unfreeze (SentisOptimisations, FreezePhysics) on the operator's
    /// FREEZER_TEST_WITH_SUBGRIDS: a chassis on six wheels with piston chains and a hinge (all
    /// subgrids) and a landing gear locked to a static grid. Four copies: on the planet and in
    /// open space, each once as built (gear locked to its static grid - the freezer must not freeze
    /// the physics of a group with a static grid) and once without the static grid, gear released
    /// (the whole group of subgrids gets its physics frozen).
    /// The freezer runs for real with a 500 m distance; a fake player with a character is added
    /// next to the structures (they wake up) and removed (they freeze), <see cref="Cycles"/> times.
    /// After every thaw each structure is compared with the moment it froze: movement of every
    /// grid, subgrids still attached, gears still locked, blocks damaged or lost, speed kicks,
    /// grids under the ground. While frozen, the scenario records which grids the freezer froze and
    /// whether their bodies were really fixed.
    /// </summary>
    public sealed class FreezerPhysicsScenario : TestScenario
    {
        public const string ScenarioName = "freezer_physics";
        private const string Prefix = "frz-";
        private const int Cycles = 5;
        private const int FreezeDistance = 500;
        private const double AwakeSeconds = 10;
        private const double FrozenSeconds = 10;
        private const double WaitStateSeconds = 40;
        private const double SettleSeconds = 10;
        private const double KickWatchSeconds = 3;
        private const double SpaceHeightM = 150000;
        internal const string ResourceName = "SentisTests.Resources.FreezerTest.xml";

        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };

        private sealed class Rig
        {
            public string Name;
            public List<MyCubeGrid> Grids = new List<MyCubeGrid>();
            public Vector3D Site;
            public bool OnPlanet;
            // At the moment the rig froze: world matrices, attached tops, locked gears, integrity.
            public List<MatrixD> FrozenPoses;
            // When this rig's own thaw was seen, and its poses KickWatchSeconds after it.
            public DateTime? ThawedAt;
            public List<Vector3D> AfterPoses;
            public List<Vector3D> ThawPoses;
            public int FrozenAttached, FrozenLocked;
            public double FrozenIntegrity;
            public int FrozenBlocks;
            public List<string> Problems = new List<string>();
            public double MaxKick;
            // Speed of the chassis (the first grid) right after a thaw: moving tops excluded.
            public double MaxChassisKick;
            // Grids whose body vanilla keeps fixed (a landing gear locked to a static grid or voxels).
            public HashSet<long> VanillaFixed = new HashSet<long>();
            public int PhysicsFrozenCycles, LogicOnlyCycles, NotFrozenCycles;
        }

        private readonly List<Rig> _rigs = new List<Rig>();
        private MyPlanet _planet;
        private PlanetGround _ground;
        private readonly ConfigOverride _config = new ConfigOverride();

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => (int)(Cycles * (AwakeSeconds + FrozenSeconds + 2 * WaitStateSeconds) + 300);

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            var anchorM = WorldApi.LoadAuthoredGroup(WheelPerfScenario.ResourceName, WorldApi.EntityPrefix + Prefix + "anchor")[0]
                .PositionAndOrientation.Value.GetMatrix();
            _planet = MyGamePruningStructure.GetClosestPlanet(anchorM.Translation);
            Check(_planet != null, "no planet");
            _ground = new PlanetGround(_planet);
            var planetCentre = _planet.PositionComp.GetPosition();
            var up = Vector3D.Normalize(anchorM.Translation - planetCentre);
            var east = Vector3D.Normalize(anchorM.Forward - up * Vector3D.Dot(anchorM.Forward, up));
            var north = Vector3D.Cross(up, east);
            var planetSite = anchorM.Translation + east * 600;
            var spaceSite = planetCentre + up * ((anchorM.Translation - planetCentre).Length() + SpaceHeightM);

            // Copies of FREEZER_TEST_WITH_SUBGRIDS, each moved rigidly from where it was built.
            var authored = WorldApi.LoadAuthoredGroup(ResourceName, WorldApi.EntityPrefix + Prefix + "authored")[0]
                .PositionAndOrientation.Value.GetMatrix().Translation;
            _rigs.Add(BuildCopy("planet, gear on static", authored, planetSite, withStatic: true, onPlanet: true));
            yield return null;
            _rigs.Add(BuildCopy("planet, free", authored, planetSite + north * 120, withStatic: false, onPlanet: true));
            yield return null;
            _rigs.Add(BuildCopy("space, gear on static", authored, spaceSite, withStatic: true, onPlanet: false));
            yield return null;
            _rigs.Add(BuildCopy("space, free", authored, spaceSite + north * 150, withStatic: false, onPlanet: false));
            yield return null;
            // The test grid locks its gear sideways onto a static wall; a plate on a gear locked to
            // the ground covers the voxel constraint (GroupContainsFixedGrid, voxel branch).
            _rigs.Add(BuildGearOnGround("planet, gear on ground", planetSite - north * 120, up, east));
            yield return null;

            var settle = WaitForSeconds(SettleSeconds, "structures settle, gears lock");
            while (settle.MoveNext()) yield return settle.Current;
            foreach (var rig in _rigs) RigParts.StartMotors(rig.Grids);
            foreach (var rig in _rigs)
                Note("rig " + rig.Name + ": " + rig.Grids.Count + " grids, tops attached " + RigParts.Attached(rig.Grids) + ", gears locked " + RigParts.Locked(rig.Grids));
            // Only the copies with the static grid have their gear locked; the free ones are released.
            var unlocked = _rigs.Where(r => (r.Grids.Any(g => g.IsStatic) || r.Name.Contains("on ground")) && RigParts.Locked(r.Grids) != RigParts.Gears(r.Grids).Count).ToList();
            Check(unlocked.Count == 0, "landing gear not locked to the static grid: " +
                  string.Join(", ", unlocked.Select(r => r.Name + " " + RigParts.Locked(r.Grids) + "/" + RigParts.Gears(r.Grids).Count)));

            // Before the freezer has touched anything: which bodies are fixed in vanilla.
            foreach (var rig in _rigs)
            {
                foreach (var g in rig.Grids.Where(g => !g.IsStatic && g.Physics?.RigidBody != null && g.Physics.RigidBody.IsFixed))
                    rig.VanillaFixed.Add(g.EntityId);
                if (rig.VanillaFixed.Count > 0) Note("vanilla keeps " + rig.VanillaFixed.Count + " grids of " + rig.Name + " with a fixed body");
            }

            // Freezer on, for real, with a short distance so the fake player decides.
            _config.Set("FreezeDistanceDynamic", FreezeDistance);
            _config.Set("FreezeDistanceStatic", FreezeDistance);
            _config.Set("FreezePhysics", true);
            _config.Set("FreezerEnabled", true);
            Note("freezer on: physics freeze, " + FreezeDistance + " m");
            TickMetrics.Take();
            FrameProbe.Take();

            for (var cycle = 1; cycle <= Cycles; cycle++)
            {
                // Nobody near: wait until every rig is frozen, then hold.
                FakeClients.RemoveAll();
                var frozenWait = Wait(() => _rigs.All(r => r.Grids.All(g => g.Closed || FreezerState.IsFrozen(g))), "all structures frozen", (int)WaitStateSeconds);
                while (frozenWait.MoveNext()) yield return frozenWait.Current;
                foreach (var rig in _rigs) CaptureFrozen(rig);
                var hold = WaitForSeconds(FrozenSeconds, "cycle " + cycle + " frozen");
                while (hold.MoveNext())
                {
                    foreach (var rig in _rigs) CheckStillFrozen(rig, cycle);
                    yield return hold.Current;
                }

                // A player comes: wait for the thaw, watch the first seconds for kicks, compare.
                FakeClients.Add(2, Network, index => (index == 0 ? planetSite + north * 100 + up * 3 : spaceSite + north * 40, 0, 0), withCharacters: true);
                var thawStart = DateTime.UtcNow;
                var thaw = WatchThaw("cycle " + cycle);
                while (thaw.MoveNext()) yield return thaw.Current;
                foreach (var rig in _rigs) CompareAfterThaw(rig, cycle);
                Note("cycle " + cycle + " chassis: " + string.Join(" | ", _rigs.Select(r => r.Name + " thawed after " +
                     (r.ThawedAt.Value - thawStart).TotalSeconds.ToString("F1") + " s, frozen->thaw " +
                     Vector3D.Distance(r.ThawPoses[0], r.FrozenPoses[0].Translation).ToString("F2") + " m, thaw->+3s " +
                     Vector3D.Distance(r.AfterPoses[0], r.ThawPoses[0]).ToString("F2") + " m")));
                Note("cycle " + cycle + ": " + string.Join(" | ", _rigs.Select(Summary)));
                var awake = WaitForSeconds(AwakeSeconds, "cycle " + cycle + " awake");
                while (awake.MoveNext()) yield return awake.Current;
            }
            // FreezePhysics switched off and on while everything is frozen (UpdateFreezePhysics):
            // off must hand every body back to dynamic, on must freeze the free groups again.
            FakeClients.RemoveAll();
            var toggleWait = Wait(() => _rigs.All(r => r.Grids.All(g => g.Closed || FreezerState.IsFrozen(g))), "all frozen before the toggle", (int)WaitStateSeconds);
            while (toggleWait.MoveNext()) yield return toggleWait.Current;
            foreach (var rig in _rigs) CaptureFrozen(rig);
            _config.Set("FreezePhysics", false);
            var off = WaitForSeconds(3, "FreezePhysics off");
            while (off.MoveNext()) yield return off.Current;
            foreach (var rig in _rigs)
            {
                CheckNoFixedBodies(rig, "FreezePhysics off");
                if (rig.Grids.Any(FreezerState.IsPhysicsFrozen)) rig.Problems.Add("FreezePhysics off: still listed physics-frozen");
                if (!rig.Grids.All(g => g.Closed || FreezerState.IsFrozen(g))) rig.Problems.Add("FreezePhysics off: logic freeze dropped");
            }
            _config.Set("FreezePhysics", true);
            var on = WaitForSeconds(3, "FreezePhysics on");
            while (on.MoveNext()) yield return on.Current;
            foreach (var rig in _rigs)
            {
                var free = !rig.Grids.Any(g => g.IsStatic) && !rig.Name.Contains("on ground");
                var physicsFrozen = rig.Grids.Count(FreezerState.IsPhysicsFrozen);
                if (free && physicsFrozen != rig.Grids.Count)
                    rig.Problems.Add("FreezePhysics on: " + physicsFrozen + "/" + rig.Grids.Count + " physics-frozen");
                if (!free && physicsFrozen > 0)
                    rig.Problems.Add("FreezePhysics on: physics frozen on a fixed group");
            }
            Note("toggle: " + string.Join(" | ", _rigs.Select(r => r.Name + " physics-frozen " + r.Grids.Count(FreezerState.IsPhysicsFrozen) + "/" + r.Grids.Count)));
            FakeClients.Add(2, Network, index => (index == 0 ? planetSite + north * 100 + up * 3 : spaceSite + north * 40, 0, 0), withCharacters: true);
            var lastThaw = WatchThaw("after the toggle");
            while (lastThaw.MoveNext()) yield return lastThaw.Current;
            foreach (var rig in _rigs) CompareAfterThaw(rig, Cycles + 1);
            FakeClients.RemoveAll();

            Note("FREEZER RESULT | " + string.Join(" | ", _rigs.Select(r => r.Name + ": physics frozen " + r.PhysicsFrozenCycles + ", logic only " +
                 r.LogicOnlyCycles + ", not frozen " + r.NotFrozenCycles + ", max speed after thaw " + r.MaxKick.ToString("F1") + " m/s (chassis " + r.MaxChassisKick.ToString("F2") + "), problems " +
                 (r.Problems.Count == 0 ? "none" : string.Join("; ", r.Problems.Distinct().Take(6))))) + " | " + TickMetrics.Take().Format() + " | " + FrameProbe.Take());
            Check(_rigs.All(r => r.Problems.Count == 0), "problems: " + string.Join(" | ", _rigs.Where(r => r.Problems.Count > 0)
                .Select(r => r.Name + ": " + string.Join("; ", r.Problems.Distinct().Take(4)))));
        }

        /// <summary>
        /// Waits for every rig to thaw, then watches each for <see cref="KickWatchSeconds"/> from its
        /// own thaw: the fastest grid, the chassis speed, poses at the thaw and after the watch.
        /// </summary>
        private IEnumerator WatchThaw(string when)
        {
            // Rigs thaw at different moments (a world is stepped only once the player's client has it
            // with selective physics updates), so each rig is watched from its own thaw.
            foreach (var rig in _rigs)
            {
                rig.ThawedAt = null;
                rig.AfterPoses = null;
            }
            var thawStart = DateTime.UtcNow;
            while (_rigs.Any(r => r.AfterPoses == null))
            {
                var now = DateTime.UtcNow;
                if ((now - thawStart).TotalSeconds > WaitStateSeconds + KickWatchSeconds)
                    throw new ScenarioFailedException(when + ": not thawed: " + string.Join(", ", _rigs.Where(r => r.ThawedAt == null).Select(r => r.Name)));
                foreach (var rig in _rigs)
                {
                    if (rig.ThawedAt == null && rig.Grids.All(g => g.Closed || !FreezerState.IsFrozen(g)))
                    {
                        rig.ThawedAt = now;
                        rig.ThawPoses = rig.Grids.Select(g => g.PositionComp.GetPosition()).ToList();
                    }
                    if (rig.ThawedAt == null || rig.AfterPoses != null) continue;
                    foreach (var g in rig.Grids.Where(g => !g.Closed && g.Physics != null))
                        rig.MaxKick = Math.Max(rig.MaxKick, g.Physics.LinearVelocity.Length());
                    var chassis = rig.Grids[0];
                    if (!chassis.Closed && chassis.Physics != null)
                        rig.MaxChassisKick = Math.Max(rig.MaxChassisKick, chassis.Physics.LinearVelocity.Length());
                    if ((now - rig.ThawedAt.Value).TotalSeconds >= KickWatchSeconds)
                        rig.AfterPoses = rig.Grids.Select(g => g.PositionComp.GetPosition()).ToList();
                }
                yield return null;
            }
        }

        // ------------------------------------------------------------------ checks

        private void CaptureFrozen(Rig rig)
        {
            rig.FrozenPoses = rig.Grids.Select(g => g.WorldMatrix).ToList();
            rig.FrozenAttached = RigParts.Attached(rig.Grids);
            rig.FrozenLocked = RigParts.Locked(rig.Grids);
            rig.FrozenIntegrity = rig.Grids.Where(g => !g.Closed).Sum(g => g.CubeBlocks.Sum(b => (double)b.Integrity));
            rig.FrozenBlocks = rig.Grids.Where(g => !g.Closed).Sum(g => g.CubeBlocks.Count);
            var fixedBodies = rig.Grids.Count(g => g.Physics?.RigidBody != null && g.Physics.RigidBody.IsFixed);
            var physicsFrozen = rig.Grids.Count(FreezerState.IsPhysicsFrozen);
            var hasStatic = rig.Grids.Any(g => g.IsStatic) || rig.Name.Contains("on ground");
            if (hasStatic && physicsFrozen > 0)
                rig.Problems.Add("physics frozen on " + physicsFrozen + " grids of a group with a static grid");
            if (physicsFrozen == rig.Grids.Count) rig.PhysicsFrozenCycles++;
            else if (rig.Grids.All(FreezerState.IsFrozen)) rig.LogicOnlyCycles++;
            else rig.NotFrozenCycles++;
            if (!hasStatic && physicsFrozen > 0 && physicsFrozen < rig.Grids.Count)
                rig.Problems.Add("only " + physicsFrozen + "/" + rig.Grids.Count + " grids physics-frozen");
            if (physicsFrozen > 0 && fixedBodies < physicsFrozen)
                rig.Problems.Add("physics-frozen but body not fixed on " + (physicsFrozen - fixedBodies) + " grids");
        }

        private void CheckStillFrozen(Rig rig, int cycle)
        {
            for (var i = 0; i < rig.Grids.Count; i++)
            {
                var g = rig.Grids[i];
                if (g.Closed) continue;
                var moved = Vector3D.Distance(g.PositionComp.GetPosition(), rig.FrozenPoses[i].Translation);
                if (FreezerState.IsPhysicsFrozen(g) && moved > 0.05)
                    rig.Problems.Add("cycle " + cycle + ": grid " + i + " moved " + moved.ToString("F2") + " m while physics-frozen");
            }
        }

        private void CompareAfterThaw(Rig rig, int cycle)
        {
            var planetCentre = _planet.PositionComp.GetPosition();
            for (var i = 0; i < rig.Grids.Count; i++)
            {
                var g = rig.Grids[i];
                if (g.Closed)
                {
                    rig.Problems.Add("cycle " + cycle + ": grid " + i + " closed");
                    continue;
                }
                // A free rig in space drifts on its own pistons and hinge (nothing holds it): for it
                // only a jump at the moment of the thaw counts, the drift is watched by the chassis speed.
                var drifts = !rig.OnPlanet && !rig.Grids.Any(x => x.IsStatic);
                var jumpOnly = drifts && rig.ThawPoses != null;
                var moved = jumpOnly
                    ? Vector3D.Distance(rig.ThawPoses[i], rig.FrozenPoses[i].Translation)
                    : Vector3D.Distance(rig.AfterPoses != null ? rig.AfterPoses[i] : g.PositionComp.GetPosition(), rig.FrozenPoses[i].Translation);
                var limit = jumpOnly ? 0.5 : 1.0;
                // Moving tops (rotor, hinge, piston) move by design; bases and gear-locked ships must not.
                var isTop = rig.Grids[0] != g && RigParts.Tops(rig.Grids).Any(t => t.TopGrid == g) && !(rig.Name.Contains("WHEEL"));
                if (!isTop && moved > limit)
                    rig.Problems.Add("cycle " + cycle + ": grid " + i + (jumpOnly ? " jumped " : " moved ") + moved.ToString("F1") + " m after thaw");
                if (rig.OnPlanet && _ground.UnderGround(g))
                    rig.Problems.Add("cycle " + cycle + ": grid " + i + " under the ground");
            }
            var attached = RigParts.Attached(rig.Grids);
            if (attached < rig.FrozenAttached) rig.Problems.Add("cycle " + cycle + ": tops attached " + attached + "/" + rig.FrozenAttached);
            var locked = RigParts.Locked(rig.Grids);
            if (locked < rig.FrozenLocked) rig.Problems.Add("cycle " + cycle + ": gears locked " + locked + "/" + rig.FrozenLocked);
            var integrity = rig.Grids.Where(g => !g.Closed).Sum(g => g.CubeBlocks.Sum(b => (double)b.Integrity));
            var blocks = rig.Grids.Where(g => !g.Closed).Sum(g => g.CubeBlocks.Count);
            if (blocks < rig.FrozenBlocks) rig.Problems.Add("cycle " + cycle + ": blocks " + blocks + "/" + rig.FrozenBlocks);
            if (integrity < rig.FrozenIntegrity - 1) rig.Problems.Add("cycle " + cycle + ": damage " + (rig.FrozenIntegrity - integrity).ToString("F0"));
            if (rig.MaxChassisKick > 1) rig.Problems.Add("cycle " + cycle + ": chassis speed " + rig.MaxChassisKick.ToString("F1") + " m/s right after thaw");
            CheckNoFixedBodies(rig, "cycle " + cycle + " thawed");
        }

        /// <summary>A grid that is not static and not physics-frozen must have a dynamic body.</summary>
        private void CheckNoFixedBodies(Rig rig, string when)
        {
            var stuck = rig.Grids.Where(g => !g.Closed && !g.IsStatic && !FreezerState.IsPhysicsFrozen(g) && !rig.VanillaFixed.Contains(g.EntityId) &&
                                             g.Physics?.RigidBody != null && g.Physics.RigidBody.IsFixed).ToList();
            var lost = rig.Grids.Where(g => !g.Closed && rig.VanillaFixed.Contains(g.EntityId) && g.Physics?.RigidBody != null && !g.Physics.RigidBody.IsFixed).ToList();
            if (lost.Count > 0) rig.Problems.Add(when + ": " + lost.Count + " gear-held grids no longer fixed");
            if (stuck.Count > 0) rig.Problems.Add(when + ": fixed body on " + string.Join(", ", stuck.Select(g =>
                "grid " + rig.Grids.IndexOf(g) + " (" + g.CubeBlocks.Count + " blocks, " +
                string.Join("/", g.GetFatBlocks().Select(b => b.BlockDefinition.Id.SubtypeName).Distinct().Take(3)) + ")")));
        }

        private string Summary(Rig rig) =>
            rig.Name + " attached " + RigParts.Attached(rig.Grids) + " locked " + RigParts.Locked(rig.Grids) + " max speed " + rig.MaxKick.ToString("F1") +
            " chassis " + rig.MaxChassisKick.ToString("F2");

        // ------------------------------------------------------------------ structures

        private sealed class Part
        {
            public string Subtype;
            public Vector3I Min;
            public Base6Directions.Direction Forward = Base6Directions.Direction.Forward;
            public Base6Directions.Direction Up = Base6Directions.Direction.Up;
        }

        private MyCubeGrid Spawn(string name, MatrixD world, IEnumerable<Part> parts)
        {
            var owner = WorldApi.PlayerIdentityId();
            var blocks = new List<MyObjectBuilder_CubeBlock>();
            foreach (var p in parts)
            {
                var block = WorldApi.MakeBlockOb(p.Subtype);
                block.Min = new SerializableVector3I(p.Min.X, p.Min.Y, p.Min.Z);
                block.BlockOrientation = new SerializableBlockOrientation(p.Forward, p.Up);
                block.Owner = owner;
                block.BuiltBy = owner;
                block.ShareMode = MyOwnershipShareModeEnum.Faction;
                if (block is Sandbox.Common.ObjectBuilders.MyObjectBuilder_LandingGear gear) gear.AutoLock = true;
                blocks.Add(block);
            }
            var grid = WorldApi.SpawnGrid(new MyObjectBuilder_CubeGrid
            {
                Name = WorldApi.EntityPrefix + Prefix + name.Replace(' ', '-'),
                DisplayName = WorldApi.EntityPrefix + Prefix + name.Replace(' ', '-'),
                GridSizeEnum = MyCubeSize.Large,
                IsStatic = false,
                PositionAndOrientation = new MyPositionAndOrientation(world),
                PersistentFlags = VRage.ObjectBuilders.MyPersistentEntityFlags2.InScene,
                CubeBlocks = blocks,
            });
            Track(grid);
            return grid;
        }

        private static IEnumerable<Part> Plate(int sizeX, int sizeZ, int y)
        {
            for (var x = 0; x < sizeX; x++)
            for (var z = 0; z < sizeZ; z++)
                yield return new Part { Subtype = "LargeBlockArmorBlock", Min = new Vector3I(x, y, z) };
        }

        /// <summary>
        /// A copy of the test group moved so its chassis is at <paramref name="site"/> (on the
        /// planet: standing on the real ground there the way it stood where it was built). Without
        /// the static grid the landing gear is released and autolock is off, so the group is free.
        /// </summary>
        private Rig BuildCopy(string name, Vector3D authored, Vector3D site, bool withStatic, bool onPlanet)
        {
            var group = WorldApi.LoadAuthoredGroup(ResourceName, WorldApi.EntityPrefix + Prefix + name.Replace(' ', '-').Replace(",", ""));
            var shift = site - authored;
            if (onPlanet)
            {
                var up = Vector3D.Normalize(site - _planet.PositionComp.GetPosition());
                shift += up * (Vector3D.Dot(_ground.Ground(site) - _ground.Ground(authored), up) - Vector3D.Dot(site - authored, up) + 0.3);
            }
            var rig = new Rig { Name = name, Site = site, OnPlanet = onPlanet };
            foreach (var ob in group)
            {
                if (ob.IsStatic && !withStatic) continue;
                var m = ob.PositionAndOrientation.Value.GetMatrix();
                m.Translation += shift;
                ob.PositionAndOrientation = new MyPositionAndOrientation(m);
                foreach (var gear in ob.CubeBlocks.OfType<Sandbox.Common.ObjectBuilders.MyObjectBuilder_LandingGear>())
                    if (!withStatic)
                    {
                        gear.AutoLock = false;
                        gear.IsLocked = false;
                        gear.LockMode = SpaceEngineers.Game.ModAPI.Ingame.LandingGearMode.Unlocked;
                        gear.AttachedEntityId = null;
                    }
                var grid = WorldApi.SpawnGrid(ob);
                Track(grid);
                rig.Grids.Add(grid);
            }
            foreach (var grid in rig.Grids.Where(g => !g.IsStatic)) WorldApi.ChargeBatteries(grid);
            return rig;
        }

        /// <summary>
        /// A 5x5 armor plate on one large landing gear (the orientation gear_probe found to lock
        /// downwards), dropped onto the ground with autolock on: a group held by a voxel constraint.
        /// </summary>
        private Rig BuildGearOnGround(string name, Vector3D site, Vector3D up, Vector3D east)
        {
            // The large gear is 1x2x3: it takes y 0..1 under the plate at y 2.
            var parts = Plate(5, 5, 2).Append(new Part { Subtype = "LargeBlockLandingGear", Min = new Vector3I(2, 0, 1) });
            var world = MatrixD.CreateWorld(_ground.Ground(site) + up * 3, Vector3D.Normalize(Vector3D.Cross(east, up)), up);
            var grid = Spawn("gear-on-ground", world, parts);
            var rig = new Rig { Name = name, Site = site, OnPlanet = true };
            rig.Grids.Add(grid);
            return rig;
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
            try
            {
                _config.Restore();
                foreach (var grid in MyEntities.GetEntities().OfType<MyCubeGrid>().ToList())
                {
                    if (grid == null || grid.MarkedForClose) continue;
                    if (!(grid.Name ?? "").StartsWith(WorldApi.EntityPrefix + Prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    Sandbox.ModAPI.MyAPIGateway.Entities.RemoveEntity(grid);
                    grid.Close();
                }
            }
            finally { base.CleanupLeftovers(); }
        }
    }
}
