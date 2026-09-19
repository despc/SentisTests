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
        private readonly VRage.Voxels.MyStorageData _probe = new VRage.Voxels.MyStorageData(VRage.Voxels.MyStorageDataTypeFlags.Content);
        private readonly Dictionary<string, object> _savedConfig = new Dictionary<string, object>();

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => (int)(Cycles * (AwakeSeconds + FrozenSeconds + 2 * WaitStateSeconds) + 300);

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            var anchorM = WorldApi.LoadAuthoredGroup(WheelPerfScenario.ResourceName, WorldApi.EntityPrefix + Prefix + "anchor")[0]
                .PositionAndOrientation.Value.GetMatrix();
            _planet = MyGamePruningStructure.GetClosestPlanet(anchorM.Translation);
            Check(_planet != null, "no planet");
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
            StartMotors();
            foreach (var rig in _rigs)
                Note("rig " + rig.Name + ": " + rig.Grids.Count + " grids, tops attached " + Attached(rig) + ", gears locked " + Locked(rig));
            // Only the copies with the static grid have their gear locked; the free ones are released.
            var unlocked = _rigs.Where(r => (r.Grids.Any(g => g.IsStatic) || r.Name.Contains("on ground")) && Locked(r) != Gears(r).Count).ToList();
            Check(unlocked.Count == 0, "landing gear not locked to the static grid: " +
                  string.Join(", ", unlocked.Select(r => r.Name + " " + Locked(r) + "/" + Gears(r).Count)));

            // Before the freezer has touched anything: which bodies are fixed in vanilla.
            foreach (var rig in _rigs)
            {
                foreach (var g in rig.Grids.Where(g => !g.IsStatic && g.Physics?.RigidBody != null && g.Physics.RigidBody.IsFixed))
                    rig.VanillaFixed.Add(g.EntityId);
                if (rig.VanillaFixed.Count > 0) Note("vanilla keeps " + rig.VanillaFixed.Count + " grids of " + rig.Name + " with a fixed body");
            }

            // Freezer on, for real, with a short distance so the fake player decides.
            SetConfig("FreezeDistanceDynamic", FreezeDistance);
            SetConfig("FreezeDistanceStatic", FreezeDistance);
            SetConfig("FreezePhysics", true);
            SetConfig("FreezerEnabled", true);
            Note("freezer on: physics freeze, " + FreezeDistance + " m");
            TickMetrics.Take();
            FrameProbe.Take();

            for (var cycle = 1; cycle <= Cycles; cycle++)
            {
                // Nobody near: wait until every rig is frozen, then hold.
                FakeClients.RemoveAll();
                var frozenWait = Wait(() => _rigs.All(r => r.Grids.All(g => g.Closed || IsFrozen(g))), "all structures frozen", (int)WaitStateSeconds);
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
                var thawWait = Wait(() => _rigs.All(r => r.Grids.All(g => g.Closed || !IsFrozen(g))), "all structures thawed", (int)WaitStateSeconds);
                while (thawWait.MoveNext()) yield return thawWait.Current;
                var watch = DateTime.UtcNow;
                while ((DateTime.UtcNow - watch).TotalSeconds < KickWatchSeconds)
                {
                    foreach (var rig in _rigs)
                    {
                        foreach (var g in rig.Grids.Where(g => !g.Closed && g.Physics != null))
                            rig.MaxKick = Math.Max(rig.MaxKick, g.Physics.LinearVelocity.Length());
                        var chassis = rig.Grids[0];
                        if (!chassis.Closed && chassis.Physics != null)
                            rig.MaxChassisKick = Math.Max(rig.MaxChassisKick, chassis.Physics.LinearVelocity.Length());
                    }
                    yield return null;
                }
                foreach (var rig in _rigs) CompareAfterThaw(rig, cycle);
                Note("cycle " + cycle + ": " + string.Join(" | ", _rigs.Select(Summary)));
                var awake = WaitForSeconds(AwakeSeconds, "cycle " + cycle + " awake");
                while (awake.MoveNext()) yield return awake.Current;
            }
            // FreezePhysics switched off and on while everything is frozen (UpdateFreezePhysics):
            // off must hand every body back to dynamic, on must freeze the free groups again.
            FakeClients.RemoveAll();
            var toggleWait = Wait(() => _rigs.All(r => r.Grids.All(g => g.Closed || IsFrozen(g))), "all frozen before the toggle", (int)WaitStateSeconds);
            while (toggleWait.MoveNext()) yield return toggleWait.Current;
            foreach (var rig in _rigs) CaptureFrozen(rig);
            SetConfig("FreezePhysics", false);
            var off = WaitForSeconds(3, "FreezePhysics off");
            while (off.MoveNext()) yield return off.Current;
            foreach (var rig in _rigs)
            {
                CheckNoFixedBodies(rig, "FreezePhysics off");
                if (rig.Grids.Any(IsPhysicsFrozen)) rig.Problems.Add("FreezePhysics off: still listed physics-frozen");
                if (!rig.Grids.All(g => g.Closed || IsFrozen(g))) rig.Problems.Add("FreezePhysics off: logic freeze dropped");
            }
            SetConfig("FreezePhysics", true);
            var on = WaitForSeconds(3, "FreezePhysics on");
            while (on.MoveNext()) yield return on.Current;
            foreach (var rig in _rigs)
            {
                var free = !rig.Grids.Any(g => g.IsStatic) && !rig.Name.Contains("on ground");
                var physicsFrozen = rig.Grids.Count(IsPhysicsFrozen);
                if (free && physicsFrozen != rig.Grids.Count)
                    rig.Problems.Add("FreezePhysics on: " + physicsFrozen + "/" + rig.Grids.Count + " physics-frozen");
                if (!free && physicsFrozen > 0)
                    rig.Problems.Add("FreezePhysics on: physics frozen on a fixed group");
            }
            Note("toggle: " + string.Join(" | ", _rigs.Select(r => r.Name + " physics-frozen " + r.Grids.Count(IsPhysicsFrozen) + "/" + r.Grids.Count)));
            FakeClients.Add(2, Network, index => (index == 0 ? planetSite + north * 100 + up * 3 : spaceSite + north * 40, 0, 0), withCharacters: true);
            var lastThaw = Wait(() => _rigs.All(r => r.Grids.All(g => g.Closed || !IsFrozen(g))), "all structures thawed after the toggle", (int)WaitStateSeconds);
            while (lastThaw.MoveNext()) yield return lastThaw.Current;
            var settleLast = WaitForSeconds(2, "after the toggle");
            while (settleLast.MoveNext()) yield return settleLast.Current;
            foreach (var rig in _rigs) CompareAfterThaw(rig, Cycles + 1);
            FakeClients.RemoveAll();

            Note("FREEZER RESULT | " + string.Join(" | ", _rigs.Select(r => r.Name + ": physics frozen " + r.PhysicsFrozenCycles + ", logic only " +
                 r.LogicOnlyCycles + ", not frozen " + r.NotFrozenCycles + ", max speed after thaw " + r.MaxKick.ToString("F1") + " m/s (chassis " + r.MaxChassisKick.ToString("F2") + "), problems " +
                 (r.Problems.Count == 0 ? "none" : string.Join("; ", r.Problems.Distinct().Take(6))))) + " | " + TickMetrics.Take().Format() + " | " + FrameProbe.Take());
            Check(_rigs.All(r => r.Problems.Count == 0), "problems: " + string.Join(" | ", _rigs.Where(r => r.Problems.Count > 0)
                .Select(r => r.Name + ": " + string.Join("; ", r.Problems.Distinct().Take(4)))));
        }

        // ------------------------------------------------------------------ checks

        private void CaptureFrozen(Rig rig)
        {
            rig.FrozenPoses = rig.Grids.Select(g => g.WorldMatrix).ToList();
            rig.FrozenAttached = Attached(rig);
            rig.FrozenLocked = Locked(rig);
            rig.FrozenIntegrity = rig.Grids.Where(g => !g.Closed).Sum(g => g.CubeBlocks.Sum(b => (double)b.Integrity));
            rig.FrozenBlocks = rig.Grids.Where(g => !g.Closed).Sum(g => g.CubeBlocks.Count);
            var fixedBodies = rig.Grids.Count(g => g.Physics?.RigidBody != null && g.Physics.RigidBody.IsFixed);
            var physicsFrozen = rig.Grids.Count(IsPhysicsFrozen);
            var hasStatic = rig.Grids.Any(g => g.IsStatic) || rig.Name.Contains("on ground");
            if (hasStatic && physicsFrozen > 0)
                rig.Problems.Add("physics frozen on " + physicsFrozen + " grids of a group with a static grid");
            if (physicsFrozen == rig.Grids.Count) rig.PhysicsFrozenCycles++;
            else if (rig.Grids.All(IsFrozen)) rig.LogicOnlyCycles++;
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
                if (IsPhysicsFrozen(g) && moved > 0.05)
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
                var moved = Vector3D.Distance(g.PositionComp.GetPosition(), rig.FrozenPoses[i].Translation);
                // Moving tops (rotor, hinge, piston) move by design; bases and gear-locked ships must not.
                var isTop = rig.Grids[0] != g && Tops(rig).Any(t => t.TopGrid == g) && !(rig.Name.Contains("WHEEL"));
                if (!isTop && moved > 1.0)
                    rig.Problems.Add("cycle " + cycle + ": grid " + i + " moved " + moved.ToString("F1") + " m after thaw");
                if (rig.OnPlanet && UnderGround(g))
                    rig.Problems.Add("cycle " + cycle + ": grid " + i + " under the ground");
            }
            var attached = Attached(rig);
            if (attached < rig.FrozenAttached) rig.Problems.Add("cycle " + cycle + ": tops attached " + attached + "/" + rig.FrozenAttached);
            var locked = Locked(rig);
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
            var stuck = rig.Grids.Where(g => !g.Closed && !g.IsStatic && !IsPhysicsFrozen(g) && !rig.VanillaFixed.Contains(g.EntityId) &&
                                             g.Physics?.RigidBody != null && g.Physics.RigidBody.IsFixed).ToList();
            var lost = rig.Grids.Where(g => !g.Closed && rig.VanillaFixed.Contains(g.EntityId) && g.Physics?.RigidBody != null && !g.Physics.RigidBody.IsFixed).ToList();
            if (lost.Count > 0) rig.Problems.Add(when + ": " + lost.Count + " gear-held grids no longer fixed");
            if (stuck.Count > 0) rig.Problems.Add(when + ": fixed body on " + string.Join(", ", stuck.Select(g =>
                "grid " + rig.Grids.IndexOf(g) + " (" + g.CubeBlocks.Count + " blocks, " +
                string.Join("/", g.GetFatBlocks().Select(b => b.BlockDefinition.Id.SubtypeName).Distinct().Take(3)) + ")")));
        }

        private string Summary(Rig rig) =>
            rig.Name + " attached " + Attached(rig) + " locked " + Locked(rig) + " max speed " + rig.MaxKick.ToString("F1") +
            " chassis " + rig.MaxChassisKick.ToString("F2");

        private static List<MyMechanicalConnectionBlockBase> Tops(Rig rig) =>
            rig.Grids.Where(g => !g.Closed).SelectMany(g => g.GetFatBlocks().OfType<MyMechanicalConnectionBlockBase>()).ToList();

        private static int Attached(Rig rig) => Tops(rig).Count(t => t.TopGrid != null);

        private static List<MyLandingGear> Gears(Rig rig) =>
            rig.Grids.Where(g => !g.Closed).SelectMany(g => g.GetFatBlocks().OfType<MyLandingGear>()).ToList();

        private static int Locked(Rig rig) => Gears(rig).Count(g => g.LockMode == SpaceEngineers.Game.ModAPI.Ingame.LandingGearMode.Locked);

        // ------------------------------------------------------------------ freezer state

        private static Type FreezeLogicType => AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("SentisOptimisationsPlugin.Freezer.FreezeLogic")).FirstOrDefault(t => t != null)
            ?? throw new ScenarioFailedException("SentisOptimisations freezer is not loaded");

        private static bool InSet(string field, long id)
        {
            var set = FreezeLogicType.GetField(field, BindingFlags.Static | BindingFlags.Public).GetValue(null);
            return (bool)set.GetType().GetMethod("Contains").Invoke(set, new object[] { id });
        }

        private static bool IsFrozen(MyCubeGrid g) => InSet("FrozenGrids", g.EntityId);
        private static bool IsPhysicsFrozen(MyCubeGrid g) => InSet("FrozenPhysicsGrids", g.EntityId);

        private void SetConfig(string property, object value)
        {
            var plugin = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("SentisOptimisationsPlugin.SentisOptimisationsPlugin")).First(t => t != null);
            var config = plugin.GetProperty("Config", BindingFlags.Public | BindingFlags.Static).GetValue(null);
            var prop = config.GetType().GetProperty(property);
            if (!_savedConfig.ContainsKey(property)) _savedConfig[property] = prop.GetValue(config);
            prop.SetValue(config, value);
            plugin.GetMethod("SaveConfig", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
        }

        private void RestoreConfig()
        {
            // Freezer off first, so everything thaws; then the rest.
            if (_savedConfig.TryGetValue("FreezerEnabled", out var enabled)) SetConfig("FreezerEnabled", enabled);
            foreach (var pair in _savedConfig.ToList())
                if (pair.Key != "FreezerEnabled") SetConfig(pair.Key, pair.Value);
            _savedConfig.Clear();
        }

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
                shift += up * (Vector3D.Dot(Ground(site) - Ground(authored), up) - Vector3D.Dot(site - authored, up) + 0.3);
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
            var world = MatrixD.CreateWorld(Ground(site) + up * 3, Vector3D.Normalize(Vector3D.Cross(east, up)), up);
            var grid = Spawn("gear-on-ground", world, parts);
            var rig = new Rig { Name = name, Site = site, OnPlanet = true };
            rig.Grids.Add(grid);
            return rig;
        }

        private void StartMotors()
        {
            foreach (var rig in _rigs)
            foreach (var mech in Tops(rig))
            {
                switch (mech)
                {
                    case Sandbox.ModAPI.IMyPistonBase piston:
                        piston.Velocity = 0.3f;
                        break;
                    case Sandbox.ModAPI.IMyMotorStator stator when !(mech is MyMotorSuspension):
                        stator.TargetVelocityRPM = mech.BlockDefinition.Id.SubtypeName.Contains("Hinge") ? 2f : 5f;
                        break;
                }
            }
        }

        // ------------------------------------------------------------------ ground

        private byte ContentAt(Vector3D point)
        {
            var voxel = Vector3I.Floor(point - _planet.PositionLeftBottomCorner) + _planet.StorageMin;
            _probe.Resize(Vector3I.One);
            _planet.Storage.ReadRange(_probe, VRage.Voxels.MyStorageDataTypeFlags.Content, 0, voxel, voxel);
            return _probe.Content(0);
        }

        private Vector3D Ground(Vector3D point)
        {
            var generated = _planet.GetClosestSurfacePointGlobal(ref point);
            var up = Vector3D.Normalize(generated - _planet.PositionComp.GetPosition());
            for (var h = 40.0; h > -40.0; h -= 0.25)
                if (ContentAt(generated + up * h) >= 128)
                    return generated + up * (h + 0.25);
            return generated;
        }

        private bool UnderGround(MyCubeGrid grid)
        {
            var at = grid.PositionComp.WorldAABB.Center;
            var up = Vector3D.Normalize(at - _planet.PositionComp.GetPosition());
            return ContentAt(at + up * 1.5) >= 128;
        }

        public override void Cleanup()
        {
            try
            {
                FakeClients.RemoveAll();
                RestoreConfig();
            }
            finally { base.Cleanup(); }
        }

        public override void CleanupLeftovers()
        {
            try
            {
                RestoreConfig();
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
