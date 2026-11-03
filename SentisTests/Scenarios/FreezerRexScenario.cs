using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// Subgrids torn off by the freezer (prod, 03.11.2026): many copies of the player's "[ASMW] Rex-T24M-R6x8" standing
    /// side by side, frozen and thawed - the turrets come off. The ship (667 blocks, 96 thrusters, unlocked magnetic
    /// plates) carries 8 turrets, each a chain of three grids: an advanced rotor (no limits) on the hull, a 2-block grid
    /// with the rotor's head and a hinge (at its lower limit, sharing its inertia tensor), and a small grid on the
    /// hinge's head with 6 missile launchers.
    ///
    /// <see cref="Copies"/> copies float in open space, <see cref="Columns"/> to a row, about <see cref="SpacingM"/> from
    /// one to the next (some 200 m of empty space between two ships), a player at the middle of them (all within the
    /// freezer's distance). The SentisOptimisations freezer runs for real at <see cref="FreezeDistance"/> m
    /// with <c>FreezePhysics</c>, as on the server; <see cref="Cycles"/> times everything freezes (the fake
    /// players leave) and thaws - by turns a player coming back and the freezer's own scheduled wake-up (its
    /// <c>WakeUpDatas</c> brought forward, what a long wait comes to). Every frame of the whole run each copy's heads
    /// are counted: the first rotor or hinge found without its head is written out with where it happened (cycle,
    /// phase, frozen or not, physics frozen or not), the speed of the grids around it and the distance between the
    /// stator and the head; the run ends with every loss. Blocks lost, grids closed, kicks over
    /// <see cref="MaxThawSpeed"/> and grids under the ground are reported too.
    /// </summary>
    public sealed class FreezerRexScenario : TestScenario
    {
        public const string ScenarioName = "freezer_rex";
        /// <summary>As on the server for a while after a restart: no damage at all (SentisGameplayImprovements' DisableAnyDamageAfterStartTime) for a minute from each thaw.</summary>
        public const string InvulnerableName = "freezer_rex_invulnerable";
        /// <summary>
        /// Each copy hanging by its four magnetic plates (on top of the hull, facing up) from a static slab of a station: the
        /// game holds the hull fixed, so the freezer leaves the group's physics alone and freezes its logic only, the
        /// turrets' grids staying dynamic bodies.
        /// </summary>
        public const string DockedName = "freezer_rex_docked";
        private const int InvulnerableSeconds = 60;
        /// <summary>Copies left frozen in a saved world for <see cref="ReleaseName"/> after a restart.</summary>
        public const string SeedName = "freezer_rex_seed";
        /// <summary>After a restart: the seed's copies as loaded, then thawed by a player while no damage is taken, as on the server.</summary>
        public const string ReleaseName = "freezer_rex_release";
        /// <summary>The release with every weapon of the copies switched off before the thaw: their own missiles out of it.</summary>
        public const string ReleaseUnarmedName = "freezer_rex_release_unarmed";
        /// <summary>The release with damage on: no invulnerable minute after the restart.</summary>
        public const string ReleaseVulnerableName = "freezer_rex_release_vulnerable";
        /// <summary>The seed's grids: the runner leaves them when a session loads.</summary>
        public const string KeepPrefix = WorldApi.EntityPrefix + "rexkeep-";
        private const int ReleaseCycles = 3;

        public enum Mode { Cycles, Seed, Release }
        private const string Prefix = "frzrex-";
        internal const string ResourceName = "SentisTests.Resources.RexT24M.xml";
        private const int Copies = 12;
        private const int Columns = 4;
        private const double SpacingM = 235;
        private const int Cycles = 6;
        private const int FreezeDistance = 500;
        private const double SpaceHeightM = 150000;
        private const double WaitStateSeconds = 60;
        /// <summary>How long nobody is near while frozen: the copies' physics cluster is not stepped then.</summary>
        private const double FrozenSeconds = 60;
        private const double AwakeSeconds = 10;
        private const double SettleSeconds = 15;
        private const double MaxThawSpeed = 20;

        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };

        private sealed class Copy
        {
            public string Name;
            public bool OnPlanet;
            public readonly List<MyCubeGrid> Grids = new List<MyCubeGrid>();
            public int Heads, Blocks;
            /// <summary>Each grid's blocks when counting started: what is gone is told by name.</summary>
            public readonly Dictionary<MyCubeGrid, HashSet<MySlimBlock>> Known = new Dictionary<MyCubeGrid, HashSet<MySlimBlock>>();
            public readonly HashSet<long> Lost = new HashSet<long>();
            /// <summary>The station slab the copy hangs from (docked runs).</summary>
            public MyCubeGrid Station;
        }

        private readonly List<Copy> _copies = new List<Copy>();
        private readonly List<string> _losses = new List<string>();
        private readonly List<string> _blocksLost = new List<string>();
        private readonly List<string> _problems = new List<string>();
        private readonly ConfigOverride _config = new ConfigOverride();
        private MyPlanet _planet;
        private PlanetGround _ground;
        private Vector3D _planetSite, _spaceSite;
        private string _phase = "setup";
        /// <summary>Heads are counted as lost from here: right after a spawn the stators have not taken them yet.</summary>
        private bool _headsWatched;
        private double _maxKick;
        private string _maxKickWhat = "";

        private readonly bool _invulnerable;
        private readonly ConfigOverride _gameplay = new ConfigOverride(ConfigOverride.Gameplay);

        private readonly Mode _mode;

        private readonly bool _unarmed, _vulnerable, _docked;

        public FreezerRexScenario(bool invulnerable = false, Mode mode = Mode.Cycles, bool unarmed = false, bool vulnerable = false, bool docked = false)
        {
            _invulnerable = invulnerable;
            _mode = mode;
            _unarmed = unarmed;
            _vulnerable = vulnerable;
            _docked = docked;
        }

        public override string Name => _mode == Mode.Seed ? SeedName :
            _mode == Mode.Release ? (_unarmed ? ReleaseUnarmedName : _vulnerable ? ReleaseVulnerableName : ReleaseName) :
            _docked ? DockedName : _invulnerable ? InvulnerableName : ScenarioName;
        public override bool KeepsNewGrids => _mode == Mode.Seed;
        public override int TimeoutSeconds => (int)(Cycles * (FrozenSeconds + AwakeSeconds + 2 * WaitStateSeconds) + 600);

        public override IEnumerator Run() => _mode == Mode.Seed ? Seed() : _mode == Mode.Release ? Release() : CyclesRun();

        private int _lostBefore;

        /// <summary>The copies placed in their grid, settled, counted.</summary>
        private IEnumerator SetUp()
        {
            var anchor = WorldApi.PlanetAnchor(WorldApi.EntityPrefix + Prefix + "anchor");
            _planet = MyGamePruningStructure.GetClosestPlanet(anchor.Translation);
            Check(_planet != null, "no planet");
            _ground = new PlanetGround(_planet);
            var centre = _planet.PositionComp.GetPosition();
            var up = Vector3D.Normalize(anchor.Translation - centre);
            var flat = anchor.Forward - up * Vector3D.Dot(anchor.Forward, up);
            var east = flat.LengthSquared() > 1e-4 ? Vector3D.Normalize(flat) : Vector3D.CalculatePerpendicularVector(up);
            _east = east;
            _planetSite = anchor.Translation + east * 900;
            _spaceSite = centre + up * ((anchor.Translation - centre).Length() + SpaceHeightM);

            // observers first: with selective physics updates nothing settles without someone near
            AddObservers();
            yield return WaitForTicks(60);
            for (var i = 0; i < Copies; i++)
            {
                _nextSpace = Spot(i);
                _placedSpace = true;
                _copies.Add(Place("space " + (i + 1), false, east));
                yield return WaitForTicks(5);
            }
            // the observer moves to the middle of the copies
            FakeClients.RemoveAll();
            AddObservers();
            foreach (var copy in _copies) copy.Heads = Attached(copy);
            // the stators take their heads in the frames after the spawn
            var attached = Until(() => _copies.All(c => Attached(c) == 16), "every copy's 16 heads attached after the spawn");
            while (attached.MoveNext()) yield return attached.Current;
            // nothing shoots or steers from here on (switched off before the heads held, some never took them)
            foreach (var copy in _copies) Quiet(copy);
            foreach (var copy in _copies) copy.Heads = Attached(copy);
            _headsWatched = true;
            Note(_copies.Count + " copies of " + _copies[0].Grids.Count + " grids, heads " + string.Join(",", _copies.Select(c => c.Heads)));
            Check(_copies.All(c => c.Heads == 16), "a copy came without all 16 heads: " + string.Join(", ", _copies.Where(c => c.Heads != 16).Select(c => c.Name + " " + c.Heads)));
            if (_docked)
            {
                // the plates lock onto the slab the way a player locks them
                yield return WaitForTicks(30);
                var gears = _copies.SelectMany(c => RigParts.Gears(c.Grids)).ToList();
                foreach (var gear in gears) ((SpaceEngineers.Game.ModAPI.IMyLandingGear)gear).AutoLock = true;
                var tries = 0;
                while (!_copies.All(c => RigParts.Locked(c.Grids) == 4) && tries++ < 20)
                {
                    foreach (var gear in gears) ((SpaceEngineers.Game.ModAPI.IMyLandingGear)gear).Lock();
                    yield return WaitForTicks(60);
                }
                if (!_copies.All(c => RigParts.Locked(c.Grids) == 4))
                {
                    var first = _copies[0];
                    Check(false, "the plates did not lock to the slabs: locked " + string.Join(",", _copies.Select(c => RigParts.Locked(c.Grids))) + "; " + first.Name + "'s plates: " +
                                 string.Join(", ", RigParts.Gears(first.Grids).Select(g => g.Min + " " + g.LockMode + ", working " + g.IsWorking + ", enabled " + g.Enabled +
                                     ", " + (first.Station == null ? "no slab" : Vector3D.Distance(g.PositionComp.GetPosition(), first.Station.PositionComp.WorldAABB.Center).ToString("F2") + " m from the slab's middle") +
                                     ", up " + Vector3D.Dot(g.WorldMatrix.Up, first.Grids[0].WorldMatrix.Up).ToString("F1") + ", forward·hull up " + Vector3D.Dot(g.WorldMatrix.Forward, first.Grids[0].WorldMatrix.Up).ToString("F1"))));
                }
                Note("docked: plates locked " + string.Join(",", _copies.Select(c => RigParts.Locked(c.Grids))) + ", hulls held fixed by the game " +
                     _copies.Count(c => c.Grids[0].Physics?.RigidBody != null && c.Grids[0].Physics.RigidBody.IsFixed));
            }

            _phase = "settling";
            var settle = Watch(SettleSeconds);
            while (settle.MoveNext()) yield return settle.Current;
            foreach (var copy in _copies) copy.Heads = Attached(copy);
            Note("settled: heads " + string.Join(",", _copies.Select(c => c.Heads)) + ", blocks " + string.Join(",", _copies.Select(c => RigParts.Blocks(c.Grids))) +
                 ", fastest grid while settling " + _maxKick.ToString("F1") + " m/s (" + _maxKickWhat + ")" +
                 (_losses.Count > 0 ? "; lost before the freezer: " + string.Join(" | ", _losses) : ""));
            _lostBefore = _losses.Count;
            // what happened while the copies settled is the spawn's, not the freezer's: kicks and blocks are counted from here
            _maxKick = 0;
            _maxKickWhat = "";
            foreach (var copy in _copies)
            {
                copy.Blocks = RigParts.Blocks(copy.Grids);
                foreach (var g in copy.Grids) copy.Known[g] = new HashSet<MySlimBlock>(g.CubeBlocks);
            }
        }

        /// <summary>
        /// Grids of earlier runs of these scenarios, closed: new copies spawned into the wrecks of a failed release (still
        /// there under its name) and flailed before the freezer had done anything.
        /// </summary>
        private static int Purge(bool keepSeed)
        {
            var old = MyEntities.GetEntities().OfType<MyCubeGrid>().Where(g => !g.MarkedForClose &&
                ((g.Name ?? "").StartsWith(WorldApi.EntityPrefix + Prefix) || !keepSeed && (g.Name ?? "").StartsWith(KeepPrefix))).ToList();
            foreach (var g in old)
            {
                Sandbox.ModAPI.MyAPIGateway.Entities.RemoveEntity(g);
                g.Close();
            }
            return old.Count;
        }

        private IEnumerator CyclesRun()
        {
            WorldApi.EnsureUnpaused(Name);
            var purged = Purge(keepSeed: false);
            if (purged > 0) Note("closed " + purged + " grids left by earlier runs");
            yield return WaitForTicks(30);
            var setUp = SetUp();
            while (setUp.MoveNext()) yield return setUp.Current;
            _config.Set("FreezeDistanceDynamic", FreezeDistance);
            _config.Set("FreezeDistanceStatic", FreezeDistance);
            _config.Set("FreezePhysics", true);
            _config.Set("FreezerEnabled", true);
            WatchDamage();
            Note("freezer on: physics freeze, " + FreezeDistance + " m");

            for (var cycle = 1; cycle <= Cycles; cycle++)
            {
                var run = RunCycle(cycle, cycle % 2 == 1, _invulnerable);
                while (run.MoveNext()) yield return run.Current;
            }
            FakeClients.RemoveAll();
            Finish();
        }

        /// <summary>The run's verdict: heads lost after the setup, grids closed, blocks lost, kicks.</summary>
        private void Finish()
        {
            foreach (var copy in _copies)
            {
                foreach (var g in copy.Grids.Where(g => g.Closed)) _problems.Add(copy.Name + ": grid '" + g.DisplayName + "' closed");
                var blocks = RigParts.Blocks(copy.Grids);
                if (blocks < copy.Blocks) _problems.Add(copy.Name + ": blocks " + blocks + "/" + copy.Blocks);
                if (_docked && RigParts.Locked(copy.Grids) < 4) _problems.Add(copy.Name + ": plates locked " + RigParts.Locked(copy.Grids) + "/4");
                if (copy.OnPlanet)
                    foreach (var g in copy.Grids.Where(g => !g.Closed && _ground.UnderGround(g))) _problems.Add(copy.Name + ": grid '" + g.DisplayName + "' under the ground");
            }
            if (_maxKick > MaxThawSpeed) _problems.Add("a grid at " + _maxKick.ToString("F1") + " m/s: " + _maxKickWhat);
            Note("DAMAGE to the copies' blocks by source (type, what hit, hits, amount): " + DamageReport());
            Note("FREEZER REX RESULT | " + Name + " | heads lost " + _losses.Count + " (" + _lostBefore + " before the freezer) | fastest grid " +
                 _maxKick.ToString("F1") + " m/s | " + (_losses.Count == 0 ? "no head lost" : string.Join(" | ", _losses.Take(10))) +
                 (_problems.Count > 0 ? " | problems: " + string.Join("; ", _problems.Distinct().Take(8)) : ""));
            Check(_losses.Count == _lostBefore, "heads lost: " + string.Join(" | ", _losses.Skip(_lostBefore).Take(6)));
            Check(_problems.Count == 0, string.Join("; ", _problems.Distinct().Take(6)));
        }

        /// <summary>Everything frozen with nobody near for <see cref="FrozenSeconds"/>, then thawed by a player or the freezer's wake-up.</summary>
        private IEnumerator RunCycle(int cycle, bool byPlayer, bool invulnerable)
        {
            _phase = "cycle " + cycle + ": freezing";
            // the turrets turning when the freeze comes, as the controllers turn them after a target
            foreach (var copy in _copies) RigParts.StartMotors(copy.Grids);
            FakeClients.RemoveAll();
            var frozen = Until(AllFrozen, "cycle " + cycle + ": every copy frozen");
            while (frozen.MoveNext()) yield return frozen.Current;
            // the physics freeze takes a moment after the logic one (a docked copy is held by the game: logic only)
            if (!_docked)
            {
                var physicsFrozen = Until(() => _copies.All(c => c.Grids.All(g => g.Closed || FreezerState.IsPhysicsFrozen(g))), "cycle " + cycle + ": every grid physics-frozen");
                while (physicsFrozen.MoveNext()) yield return physicsFrozen.Current;
            }
            else
            {
                var settleFreeze = Watch(3);
                while (settleFreeze.MoveNext()) yield return settleFreeze.Current;
            }
            var all = _copies.SelectMany(c => c.Grids).Where(g => !g.Closed).ToList();
            var overlapping = _copies.Select(c => (c, Overlaps(c))).Where(x => x.Item2.Count > 0).ToList();
            Note("cycle " + cycle + " frozen: " + all.Count(FreezerState.IsPhysicsFrozen) + "/" + all.Count + " grids physics-frozen, " + all.Count(FreezerState.HasFixedBody) + " with a fixed body; " +
                 "grids of a copy in each other (colliding pairs whose shapes overlap): " +
                 (overlapping.Count == 0 ? "none" : string.Join(" | ", overlapping.Select(x => x.c.Name + ": " + string.Join(", ", x.Item2)))));
            _phase = "cycle " + cycle + ": frozen";
            // nobody near: the copies' Havok worlds must not be stepped for the whole hold
            _steppedFrames = 0;
            _holdFrames = 0;
            _holding = true;
            var hold = Watch(FrozenSeconds);
            while (hold.MoveNext()) yield return hold.Current;
            _holding = false;
            Note("cycle " + cycle + " held " + FrozenSeconds + " s with nobody near: the copies' physics stepped in " + _steppedFrames + " of " + _holdFrames + " frames" +
                 (_steppedFrames > 0 ? " (" + _steppedWhy + ")" : ""));
            if (_steppedFrames > 0) _problems.Add("cycle " + cycle + ": the copies' physics was stepped in " + _steppedFrames + " of " + _holdFrames + " frames with nobody near: " + _steppedWhy);

            _phase = "cycle " + cycle + ": thaw by " + (byPlayer ? "a player" : "the freezer's wake-up");
            if (invulnerable)
            {
                // the same switch as the server's first minutes: every damage zeroed, deformation off, until this second of the session
                var until = (int)(Sandbox.MySandboxGame.Static.SimulationFrameCounter / 60) + InvulnerableSeconds;
                _gameplay.Set("DisableAnyDamageAfterStartTime", until);
                _phase += " (invulnerable " + InvulnerableSeconds + " s)";
            }
            if (byPlayer) AddObservers();
            else WakeSoon(1);
            var thawed = Until(AllThawed, "cycle " + cycle + ": every copy thawed");
            while (thawed.MoveNext()) yield return thawed.Current;
            _phase = "cycle " + cycle + ": thawed by " + (byPlayer ? "a player" : "the wake-up");
            var awake = Watch(AwakeSeconds);
            while (awake.MoveNext()) yield return awake.Current;
            Note("cycle " + cycle + " (" + (byPlayer ? "player" : "wake-up") + "): heads " + string.Join(",", _copies.Select(Attached)) +
                 ", fastest grid " + _maxKick.ToString("F1") + " m/s" + (_losses.Count > 0 ? ", heads lost so far " + _losses.Count : ""));
        }

        // ------------------------------------------------------------------ across a restart

        /// <summary>
        /// The copies placed and settled, the stand's freezer switched on with the physics freeze as on the server, kept so
        /// across the restart (<see cref="KeptConfig"/>, put back by the release), nobody near: everything frozen, and the
        /// world saved.
        /// </summary>
        private IEnumerator Seed()
        {
            WorldApi.EnsureUnpaused(Name);
            FakeClients.RemoveAll();
            var purged = Purge(keepSeed: false);
            if (purged > 0) Note("closed " + purged + " grids left by earlier runs");
            yield return WaitForTicks(30);
            var setUp = SetUp();
            while (setUp.MoveNext()) yield return setUp.Current;
            KeptConfig.Set(ConfigOverride.Optimisations, "FreezeDistanceDynamic", FreezeDistance);
            KeptConfig.Set(ConfigOverride.Optimisations, "FreezeDistanceStatic", FreezeDistance);
            KeptConfig.Set(ConfigOverride.Optimisations, "FreezePhysics", true);
            KeptConfig.Set(ConfigOverride.Optimisations, "FreezerEnabled", true);
            // the server's first minute without damage, from the session's start
            KeptConfig.Set(ConfigOverride.Gameplay, "DisableAnyDamageAfterStartTime", InvulnerableSeconds);
            FakeClients.RemoveAll();
            _phase = "seed: freezing";
            var frozen = Until(() => _copies.All(c => c.Grids.All(g => g.Closed || FreezerState.IsPhysicsFrozen(g))), "seed: every grid physics-frozen");
            while (frozen.MoveNext()) yield return frozen.Current;
            var all = _copies.SelectMany(c => c.Grids).Where(g => !g.Closed).ToList();
            Note("seed frozen: " + all.Count(FreezerState.IsPhysicsFrozen) + "/" + all.Count + " grids physics-frozen, heads " + string.Join(",", _copies.Select(Attached)));
            Poses.Write(_copies.SelectMany(c => c.Grids));
            var task = SentisTestsPlugin.TorchInstance.Save();
            Check(task != null, "Torch refused to start a save");
            while (!task.IsCompleted) yield return null;
            Note("FREEZER REX SEED RESULT | " + _copies.Count + " copies frozen and saved: " + task.Result + "; restart the server and run " + ReleaseName);
        }

        /// <summary>
        /// After the restart: the seed's copies as the load left them (heads, frozen), then a player comes while no damage
        /// is taken (the server's first minute), then <see cref="ReleaseCycles"/> freezes and thaws more. The stand's
        /// settings the seed kept are put back at the end.
        /// </summary>
        private IEnumerator Release()
        {
            WorldApi.EnsureUnpaused(Name);
            var kept = MyEntities.GetEntities().OfType<MyCubeGrid>().Where(g => !g.MarkedForClose && (g.Name ?? "").StartsWith(KeepPrefix)).ToList();
            Check(kept.Count > 0, "no " + KeepPrefix + "* grids: run " + SeedName + " and restart first");
            foreach (var g in kept) Track(g);
            foreach (var group in kept.GroupBy(g => Regex.Replace(g.Name, "-part[0-9]+$", "")).OrderBy(x => x.Key.Length).ThenBy(x => x.Key))
            {
                var copy = new Copy { Name = group.Key.Substring(KeepPrefix.Length) };
                var hull = group.FirstOrDefault(g => g.Name == group.Key);
                if (hull != null) copy.Grids.Add(hull);
                copy.Grids.AddRange(group.Where(g => g != hull));
                _copies.Add(copy);
            }
            // (150 km up the game's own lookup finds no planet: the nearest one by distance)
            var at = _copies[0].Grids[0].PositionComp.GetPosition();
            _planet = MyEntities.GetEntities().OfType<MyPlanet>().OrderBy(pl => Vector3D.Distance(pl.PositionComp.GetPosition(), at)).FirstOrDefault();
            Check(_planet != null, "no planet");
            _ground = new PlanetGround(_planet);
            _middle = _copies.Aggregate(Vector3D.Zero, (sum, c) => sum + c.Grids[0].PositionComp.GetPosition()) / _copies.Count;
            var loadedHeads = _copies.Select(Attached).ToList();
            var all = _copies.SelectMany(c => c.Grids).ToList();
            Note("after the restart: " + _copies.Count + " copies of " + string.Join("/", _copies.Select(c => c.Grids.Count).Distinct()) + " grids, heads " + string.Join(",", loadedHeads) +
                 ", frozen " + all.Count(FreezerState.IsFrozen) + "/" + all.Count + ", physics-frozen " + all.Count(FreezerState.IsPhysicsFrozen) +
                 ", session " + (Sandbox.MySandboxGame.Static.SimulationFrameCounter / 60) + " s old");
            foreach (var copy in _copies.Where(c => Attached(c) < 16))
                foreach (var stator in RigParts.Tops(copy.Grids).Where(t => t.TopGrid == null))
                {
                    copy.Lost.Add(stator.EntityId);
                    _losses.Add("as loaded: " + Describe(copy, stator));
                }
            foreach (var copy in _copies)
            {
                copy.Heads = Attached(copy);
                copy.Blocks = RigParts.Blocks(copy.Grids);
                foreach (var g in copy.Grids) copy.Known[g] = new HashSet<MySlimBlock>(g.CubeBlocks);
            }
            WatchDamage();
            _headsWatched = true;

            Note("after the restart, against the poses before the save: " + Poses.Compare(_copies.SelectMany(c => c.Grids)));
            _phase = "after the restart, nobody near";
            var still = Watch(10);
            while (still.MoveNext()) yield return still.Current;
            if (_unarmed)
            {
                // switched off, the guns were still fired by their turret controllers: their ammunition goes too
                var guns = _copies.SelectMany(c => c.Grids).SelectMany(g => g.GetFatBlocks()).OfType<Sandbox.ModAPI.IMyUserControllableGun>().ToList();
                foreach (var gun in guns) gun.Enabled = false;
                var removed = 0.0;
                foreach (var block in _copies.SelectMany(c => c.Grids).SelectMany(g => g.GetFatBlocks()).Where(b => b.HasInventory))
                    for (var i = 0; i < block.InventoryCount; i++)
                    {
                        var inventory = (Sandbox.Game.MyInventory)block.GetInventory(i);
                        foreach (var item in inventory.GetItems().Where(it => it.Content is MyObjectBuilder_AmmoMagazine).ToList())
                        {
                            removed += (double)item.Amount;
                            inventory.RemoveItems(item.ItemId);
                        }
                    }
                Note("every weapon of the copies switched off (" + guns.Count + ") and their ammunition taken (" + removed.ToString("F0") + " magazines)");
            }
            // the server's first minute: no damage, and a player comes to them
            if (_vulnerable) _gameplay.Set("DisableAnyDamageAfterStartTime", 0);
            else _gameplay.Set("DisableAnyDamageAfterStartTime", (int)(Sandbox.MySandboxGame.Static.SimulationFrameCounter / 60) + InvulnerableSeconds);
            _phase = "after the restart: thaw by a player" + (_vulnerable ? "" : " (invulnerable " + InvulnerableSeconds + " s)") + (_unarmed ? ", weapons off" : "");
            AddObservers();
            var thawed = Until(AllThawed, "after the restart: every copy thawed");
            while (thawed.MoveNext()) yield return thawed.Current;
            _phase = "after the restart: thawed by a player" + (_vulnerable ? "" : " (invulnerable)") + (_unarmed ? ", weapons off" : "");
            var awake = Watch(30);
            while (awake.MoveNext()) yield return awake.Current;
            Note("after the restart thawed: heads " + string.Join(",", _copies.Select(Attached)) + ", fastest grid " + _maxKick.ToString("F1") + " m/s (" + _maxKickWhat + ")");
            _config.Set("FreezeDistanceDynamic", FreezeDistance);
            _config.Set("FreezeDistanceStatic", FreezeDistance);
            _config.Set("FreezePhysics", true);
            _config.Set("FreezerEnabled", true);
            for (var cycle = 1; cycle <= ReleaseCycles; cycle++)
            {
                var run = RunCycle(cycle, cycle % 2 == 1, false);
                while (run.MoveNext()) yield return run.Current;
            }
            FakeClients.RemoveAll();
            Finish();
        }

        // ------------------------------------------------------------------ watching

        /// <summary>Watches every frame for <paramref name="seconds"/>.</summary>
        private IEnumerator Watch(double seconds)
        {
            var end = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < end)
            {
                Look();
                yield return null;
            }
        }

        /// <summary>Watches every frame until <paramref name="done"/>, at most <see cref="WaitStateSeconds"/>.</summary>
        private IEnumerator Until(Func<bool> done, string what)
        {
            var end = DateTime.UtcNow.AddSeconds(WaitStateSeconds);
            while (!done())
            {
                if (DateTime.UtcNow > end)
                    throw new ScenarioFailedException(what + ": not in " + WaitStateSeconds + " s; " + string.Join(", ", _copies.Select(c =>
                        c.Name + " frozen " + c.Grids.Count(g => !g.Closed && FreezerState.IsFrozen(g)) + "/" + c.Grids.Count(g => !g.Closed))) +
                        (_losses.Count > 0 ? "; heads lost " + string.Join(" | ", _losses.Take(4)) : ""));
                Look();
                yield return null;
            }
        }

        /// <summary>One frame's look: heads that came off, and the fastest grid.</summary>
        // ------------------------------------------------------------------ damage

        private readonly object _damageGate = new object();
        private readonly Dictionary<string, (int Hits, double Amount)> _damage = new Dictionary<string, (int, double)>();
        private readonly Dictionary<MySlimBlock, string> _lastHit = new Dictionary<MySlimBlock, string>();
        private Dictionary<long, Copy> _copyOf;
        private List<Tuple<int, VRage.Game.ModAPI.BeforeDamageApplied>> _handlers;
        private VRage.Game.ModAPI.BeforeDamageApplied _handler;

        /// <summary>Every hit on a block of a copy, before any plugin's handler changes it (the lowest priority runs first).</summary>
        private void WatchDamage()
        {
            _copyOf = new Dictionary<long, Copy>();
            foreach (var copy in _copies) foreach (var g in copy.Grids) _copyOf[g.EntityId] = copy;
            var system = Sandbox.Game.GameSystems.MyDamageSystem.Static;
            _handlers = (List<Tuple<int, VRage.Game.ModAPI.BeforeDamageApplied>>)typeof(Sandbox.Game.GameSystems.MyDamageSystem)
                .GetField("m_beforeDamageHandlers", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(system);
            _handler = OnDamage;
            system.RegisterBeforeDamageHandler(-1000000, _handler);
        }

        private void OnDamage(object target, ref VRage.Game.ModAPI.MyDamageInformation info)
        {
            try
            {
                if (!(target is MySlimBlock block) || block.CubeGrid == null || _copyOf == null || !_copyOf.TryGetValue(block.CubeGrid.EntityId, out var copy)) return;
                var attacker = MyEntities.GetEntityById(info.AttackerId);
                var attackerGrid = (attacker as MyCubeGrid) ?? (attacker as Sandbox.Game.Entities.MyCubeBlock)?.CubeGrid;
                string by;
                if (attacker == null) by = info.AttackerId == 0 ? "nothing named" : "gone entity";
                else if (attackerGrid != null && _copyOf.TryGetValue(attackerGrid.EntityId, out var other))
                    by = other != copy ? "another copy" : attackerGrid == block.CubeGrid ? "its own grid" :
                         "own " + (attackerGrid.GridSizeEnum == MyCubeSize.Small ? "small turret grid" : attackerGrid == copy.Grids[0] ? "hull" : "turret base grid");
                else by = attacker.GetType().Name + " '" + attacker.DisplayName + "'";
                var where = block.CubeGrid == copy.Grids[0] ? "hull" : block.CubeGrid.GridSizeEnum == MyCubeSize.Small ? "small turret grid" : "turret base grid";
                var key = info.Type.String + (info.IsDeformation ? " (deformation)" : "") + " to " + where + " by " + by;
                lock (_damageGate)
                {
                    _damage.TryGetValue(key, out var sum);
                    _damage[key] = (sum.Hits + 1, sum.Amount + info.Amount);
                    _lastHit[block] = key + ", " + info.Amount.ToString("F0") + " in " + _phase;
                }
            }
            catch { /* a diagnostic only */ }
        }

        private string DamageReport()
        {
            lock (_damageGate)
                return _damage.Count == 0 ? "none" : string.Join("; ", _damage.OrderByDescending(p => p.Value.Amount).Take(12)
                    .Select(p => p.Key + ": " + p.Value.Hits + " hits, " + p.Value.Amount.ToString("F0")));
        }

        private const double KickTraceSpeed = 10;
        private readonly HashSet<string> _traced = new HashSet<string>();

        /// <summary>A copy at the frame one of its grids went fast: every grid's speed, every joint's head against its stator.</summary>
        private string Trace(Copy copy, MyCubeGrid fast)
        {
            var text = new System.Text.StringBuilder();
            text.Append(copy.Name).Append(", ").Append(_phase).Append(", frame ").Append(Sandbox.MySandboxGame.Static.SimulationFrameCounter)
                .Append(": '").Append(fast.DisplayName).Append("' at ").Append(fast.Physics.LinearVelocity.Length().ToString("F1")).Append(" m/s");
            text.Append(" | grids:");
            foreach (var g in copy.Grids.Where(g => !g.Closed && g.Physics != null))
                text.Append(' ').Append(g.GridSizeEnum == MyCubeSize.Small ? "S" : g == copy.Grids[0] ? "H" : "B").Append(copy.Grids.IndexOf(g)).Append('=')
                    .Append(g.Physics.LinearVelocity.Length().ToString("F1")).Append('/').Append(g.Physics.AngularVelocity.Length().ToString("F1"))
                    .Append(g.Physics.RigidBody == null ? "" : g.Physics.RigidBody.IsFixedOrKeyframed ? "F" : g.Physics.RigidBody.IsActive ? "" : "z");
            var overlaps = Overlaps(copy);
            text.Append(" | grids in each other: ").Append(overlaps.Count == 0 ? "none" : string.Join(", ", overlaps));
            text.Append(" | joints (head off its saved place in the stator's frame, m / deg):");
            foreach (var stator in RigParts.Tops(copy.Grids).Where(t => t.TopBlock != null))
            {
                var d = Poses.JointDrift(stator);
                text.Append(' ').Append(stator.BlockDefinition.Id.SubtypeName.Replace("Large", "")).Append(copy.Grids.IndexOf(stator.CubeGrid)).Append("->")
                    .Append(copy.Grids.IndexOf(stator.TopGrid)).Append('=').Append(d);
            }
            return text.ToString();
        }

        /// <summary>
        /// The pairs of the copy's grids that collide with each other (the collision filter lets them) and whose shapes
        /// overlap now: grids frozen in each other are pushed apart by the solver the moment they move again.
        /// </summary>
        private static List<string> Overlaps(Copy copy)
        {
            var found = new List<string>();
            try
            {
                var grids = copy.Grids.Where(g => !g.Closed && g.Physics?.RigidBody != null).ToList();
                for (var i = 0; i < grids.Count; i++)
                for (var j = i + 1; j < grids.Count; j++)
                {
                    var a = grids[i].Physics.RigidBody;
                    var b = grids[j].Physics.RigidBody;
                    var world = grids[i].Physics.HavokWorld;
                    if (world == null || world != grids[j].Physics.HavokWorld || !world.IsCollisionEnabled(a.GetCollisionFilterInfo(), b.GetCollisionFilterInfo())) continue;
                    if (!grids[i].PositionComp.WorldAABB.Intersects(grids[j].PositionComp.WorldAABB)) continue;
                    var pa = grids[i].Physics.ClusterToWorld(a.Position);
                    var ra = Quaternion.CreateFromRotationMatrix(a.GetRigidBodyMatrix());
                    var pb = grids[j].Physics.ClusterToWorld(b.Position);
                    var rb = Quaternion.CreateFromRotationMatrix(b.GetRigidBodyMatrix());
                    if (Sandbox.Engine.Physics.MyPhysics.IsPenetratingShapeShape(a.GetShape(), ref pa, ref ra, b.GetShape(), ref pb, ref rb))
                        found.Add(Tag(copy, grids[i]) + "x" + Tag(copy, grids[j]));
                }
            }
            catch (Exception e)
            {
                found.Add("(" + e.GetType().Name + ")");
            }
            return found;
        }

        private static string Tag(Copy copy, MyCubeGrid g) =>
            (g.GridSizeEnum == MyCubeSize.Small ? "S" : g == copy.Grids[0] ? "H" : "B") + copy.Grids.IndexOf(g);

        private bool _holding;
        private int _steppedFrames, _holdFrames;
        private string _steppedWhy = "";
        private static MethodInfo _inUnstepped;

        /// <summary>Whether SentisOptimisations' selective physics left the grid's Havok world out of this frame's step.</summary>
        private static bool Unstepped(MyCubeGrid g)
        {
            if (_inUnstepped == null)
                _inUnstepped = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("Optimizer.Optimizations.SelectivePhysicsBodies", false)).FirstOrDefault(t => t != null)
                                   ?.GetMethod("InUnstepped", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                               ?? throw new ScenarioFailedException("SentisOptimisations' SelectivePhysicsBodies.InUnstepped is not there");
            return (bool)_inUnstepped.Invoke(null, new object[] { g });
        }

        private void Look()
        {
            if (_holding)
            {
                _holdFrames++;
                var stepped = _copies.SelectMany(c => c.Grids).FirstOrDefault(g => !g.Closed && g.Physics != null && !Unstepped(g));
                if (stepped != null)
                {
                    _steppedFrames++;
                    if (_steppedFrames == 1)
                    {
                        // who keeps it stepped: characters and anything replicated near
                        var at = stepped.PositionComp.GetPosition();
                        var characters = MyEntities.GetEntities().OfType<Sandbox.Game.Entities.Character.MyCharacter>()
                            .Where(c => !c.IsDead).OrderBy(c => Vector3D.Distance(c.PositionComp.GetPosition(), at)).Take(3)
                            .Select(c => c.DisplayName + " " + Vector3D.Distance(c.PositionComp.GetPosition(), at).ToString("F0") + " m" + (c.ControllerInfo?.Controller != null ? " controlled" : ""));
                        _steppedWhy = "first '" + stepped.DisplayName + "', nearest characters: " + string.Join(", ", characters) + ", online players " +
                                      Sandbox.Game.World.MySession.Static.Players.GetOnlinePlayers().Count;
                    }
                }
            }
            foreach (var copy in _copies)
            {
                foreach (var g in copy.Grids)
                {
                    if (g.Closed || g.Physics == null) continue;
                    var speed = g.Physics.LinearVelocity.Length();
                    if (speed > KickTraceSpeed && _traced.Add(copy.Name + "|" + _phase)) Note("KICK " + Trace(copy, g));
                    if (speed <= _maxKick) continue;
                    _maxKick = speed;
                    _maxKickWhat = copy.Name + " '" + g.DisplayName + "' (" + g.BlocksCount + " blocks) in " + _phase + ", angular " + g.Physics.AngularVelocity.Length().ToString("F1") + " rad/s";
                }
                foreach (var pair in copy.Known)
                {
                    if (pair.Key.Closed || pair.Key.BlocksCount >= pair.Value.Count) continue;
                    var gone = pair.Value.Where(b => pair.Key.GetCubeBlock(b.Position) != b).ToList();
                    foreach (var b in gone)
                    {
                        pair.Value.Remove(b);
                        string last;
                        lock (_damageGate) _lastHit.TryGetValue(b, out last);
                        var text = copy.Name + ", " + _phase + ": block " + b.BlockDefinition.Id.SubtypeName + " " + b.Position + " gone from '" + pair.Key.DisplayName + "' (" +
                                   pair.Key.BlocksCount + " blocks left, " + Speed(pair.Key) + ", " + Frozen(pair.Key) + "), integrity it had " + b.Integrity.ToString("F0") + "/" + b.MaxIntegrity.ToString("F0") +
                                   ", last hit: " + (last ?? "none seen");
                        _blocksLost.Add(text);
                        Note("BLOCK LOST: " + text);
                    }
                }
                foreach (var stator in RigParts.Tops(copy.Grids))
                {
                    if (!_headsWatched || stator.TopGrid != null || copy.Lost.Contains(stator.EntityId)) continue;
                    copy.Lost.Add(stator.EntityId);
                    _losses.Add(Describe(copy, stator));
                    Note("HEAD LOST: " + _losses[_losses.Count - 1]);
                }
            }
        }

        private string Describe(Copy copy, MyMechanicalConnectionBlockBase stator)
        {
            // the head it had: the grid of the copy nearest to the stator that is not on a stator of its own grid
            var at = stator.PositionComp.GetPosition();
            var head = copy.Grids.Where(g => !g.Closed && g != stator.CubeGrid && g.GetFatBlocks().OfType<MyAttachableTopBlockBase>().Any(t => t.Stator == null))
                .OrderBy(g => Vector3D.Distance(g.PositionComp.GetPosition(), at)).FirstOrDefault();
            var headTop = head?.GetFatBlocks().OfType<MyAttachableTopBlockBase>().FirstOrDefault(t => t.Stator == null);
            return copy.Name + ", " + _phase + ": " + stator.BlockDefinition.Id.SubtypeName + " on '" + stator.CubeGrid.DisplayName + "' (" +
                   stator.CubeGrid.BlocksCount + " blocks, " + Speed(stator.CubeGrid) + ", " + Frozen(stator.CubeGrid) + ")" +
                   (head == null ? ", its head not found" :
                       ", head on '" + head.DisplayName + "' (" + head.BlocksCount + " blocks, " + Speed(head) + ", " + Frozen(head) + ") " +
                       (headTop == null ? "" : Vector3D.Distance(headTop.PositionComp.GetPosition(), at).ToString("F2") + " m from the stator")) +
                   ", stator working " + stator.IsWorking + (stator is Sandbox.ModAPI.IMyMotorStator m ? ", angle " + m.Angle.ToString("F2") + ", velocity " + m.TargetVelocityRPM.ToString("F1") : "");
        }

        private static string Speed(MyCubeGrid g) =>
            g.Physics == null ? "no physics" : g.Physics.LinearVelocity.Length().ToString("F1") + " m/s, " + g.Physics.AngularVelocity.Length().ToString("F1") + " rad/s" +
                                              (g.Physics.RigidBody == null ? "" : g.Physics.RigidBody.IsFixedOrKeyframed ? ", body fixed" : g.Physics.RigidBody.IsActive ? "" : ", body asleep");

        private static string Frozen(MyCubeGrid g) =>
            FreezerState.IsPhysicsFrozen(g) ? "physics frozen" : FreezerState.IsFrozen(g) ? "frozen" : "awake";

        private static int Attached(Copy copy) => RigParts.Attached(copy.Grids);

        private bool AllFrozen() => _copies.All(c => c.Grids.All(g => g.Closed || FreezerState.IsFrozen(g)));

        private bool AllThawed() => _copies.All(c => c.Grids.All(g => g.Closed || !FreezerState.IsFrozen(g)));

        // a player at the middle of the copies (between them): every copy within FreezeDistance of them
        private Vector3D? _middle;

        private void AddObservers()
        {
            var rows = (Copies + Columns - 1) / Columns;
            var middle = _middle ?? _spaceSite + _east * (SpacingM * (Columns - 1) / 2) + Side * (SpacingM * (rows - 1) / 2);
            FakeClients.Add(1, Network, i => (middle, 0, 0), withCharacters: true);
        }

        private Vector3D Side => Vector3D.Normalize(Vector3D.Cross(_ground.Up(_spaceSite), _east));

        /// <summary>Where copy <paramref name="i"/> stands: <see cref="Columns"/> along east, the rows to the side.</summary>
        private Vector3D Spot(int i) => _spaceSite + _east * (SpacingM * (i % Columns)) + Side * (SpacingM * (i / Columns));

        private Vector3D _east;

        /// <summary>The freezer's own wake-up of every copy, <paramref name="seconds"/> from now (as DebugBridge's wake-soon).</summary>
        private void WakeSoon(double seconds)
        {
            var logic = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("SentisOptimisationsPlugin.Freezer.FreezeLogic", false)).FirstOrDefault(t => t != null);
            var wakeUps = logic?.GetField("WakeUpDatas", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as IDictionary<long, DateTime>;
            var gate = logic?.GetField("_wakeUpLock", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
            Check(wakeUps != null && gate != null, "the freezer's wake-up schedule is not there");
            lock (gate)
                foreach (var g in _copies.SelectMany(c => c.Grids).Where(g => !g.Closed))
                    wakeUps[g.EntityId] = DateTime.Now.AddSeconds(seconds);
        }

        // ------------------------------------------------------------------ placing

        private Vector3D _nextPlanet, _nextSpace;
        private bool _placedPlanet, _placedSpace;

        /// <summary>
        /// A copy of the blueprint next to the previous one along <paramref name="east"/>: on the planet standing up and set
        /// down a metre over the ground at its lowest block, in space as built, turned the same way.
        /// </summary>
        private Copy Place(string name, bool onPlanet, Vector3D east)
        {
            var group = WorldApi.LoadAuthoredGroup(ResourceName, (_mode == Mode.Seed ? KeepPrefix : WorldApi.EntityPrefix + Prefix) + name.Replace(' ', '-'));
            var built = group[0].PositionAndOrientation.Value.GetMatrix();
            var site = onPlanet ? (_placedPlanet ? _nextPlanet : _planetSite) : (_placedSpace ? _nextSpace : _spaceSite);
            var up = _ground.Up(site);
            // stand up as the ship stood when it was blueprinted (its grid's up), facing along the row
            var turn = MatrixD.CreateFromQuaternion(QuaternionD.CreateFromTwoVectors(Vector3D.Normalize(built.Up), up));
            var poses = group.Select(ob =>
            {
                var m = ob.PositionAndOrientation.Value.GetMatrix();
                var offset = Vector3D.TransformNormal(m.Translation - built.Translation, turn);
                return MatrixD.CreateWorld(offset, Vector3D.TransformNormal(m.Forward, turn), Vector3D.TransformNormal(m.Up, turn));
            }).ToList();
            // the corners of every block, relative to the first grid's place: how low it reaches, how wide along the row
            double low = double.MaxValue, west = double.MaxValue, eastMost = double.MinValue;
            for (var i = 0; i < group.Count; i++)
            {
                var size = group[i].GridSizeEnum == MyCubeSize.Large ? 2.5 : 0.5;
                foreach (var block in group[i].CubeBlocks)
                {
                    var p = Vector3D.Transform(new Vector3D(block.Min.X, block.Min.Y, block.Min.Z) * size, poses[i]);
                    low = Math.Min(low, Vector3D.Dot(p, up));
                    west = Math.Min(west, Vector3D.Dot(p, east));
                    eastMost = Math.Max(eastMost, Vector3D.Dot(p, east));
                }
            }
            var width = eastMost - west + 2.5;
            var place = onPlanet ? _ground.Ground(site) + up * (1 - low) : site;
            place -= east * west;
            var copy = new Copy { Name = name, OnPlanet = onPlanet };
            for (var i = 0; i < group.Count; i++)
            {
                var m = poses[i];
                m.Translation += place;
                group[i].PositionAndOrientation = new MyPositionAndOrientation(m);
                var grid = WorldApi.SpawnGrid(group[i]);
                if (_mode != Mode.Seed) Track(grid);
                copy.Grids.Add(grid);
            }
            foreach (var grid in copy.Grids) WorldApi.ChargeBatteries(grid);
            if (_docked) copy.Station = Station(copy);
            var next = site + east * width;
            if (onPlanet) { _nextPlanet = next; _placedPlanet = true; }
            else { _nextSpace = next; _placedSpace = true; }
            return copy;
        }

        /// <summary>
        /// Nothing of the copy shoots or steers: its turret controllers, turrets, guns, combat and flight AI, event
        /// controllers and scripts switched off, as by its owners around it (on the server the turrets came off with no
        /// shot fired; the fake players here are strangers, and the copies' own missiles hit their own turrets).
        /// </summary>
        private static void Quiet(Copy copy)
        {
            foreach (var block in copy.Grids.SelectMany(g => g.GetFatBlocks()).OfType<Sandbox.ModAPI.IMyFunctionalBlock>())
            {
                var type = block.GetType().Name;
                if (block is Sandbox.ModAPI.IMyUserControllableGun || block is Sandbox.ModAPI.Ingame.IMyLargeTurretBase || block is Sandbox.ModAPI.IMyProgrammableBlock ||
                    type.Contains("TurretControl") || type.Contains("CombatBlock") || type.Contains("FlightMovement") || type.Contains("EventController"))
                    block.Enabled = false;
            }
        }

        /// <summary>
        /// A static slab of a station over the copy's magnetic plates: two strips of 4 armour blocks one layer above the hull's top
        /// (the plates stand at y 9, x -2 and 2, z 18 and 21 of the hull, facing up), turned as the hull.
        /// </summary>
        private MyCubeGrid Station(Copy copy)
        {
            var hull = copy.Grids[0];
            var owner = WorldApi.PlayerIdentityId();
            var blocks = new List<MyObjectBuilder_CubeBlock>();
            // two strips, one over each pair of plates: the turrets at z 22 stand right by the plates, a whole slab struck them
            foreach (var x in new[] { -2, 2 })
            for (var z = 18; z <= 21; z++)
            {
                var block = WorldApi.MakeBlockOb("LargeBlockArmorBlock");
                block.Min = new SerializableVector3I(x + 2, 0, z - 18);
                block.Owner = owner;
                block.BuiltBy = owner;
                blocks.Add(block);
            }
            var at = hull.GridIntegerToWorld(new Vector3I(-2, 10, 18));
            var name = WorldApi.EntityPrefix + Prefix + copy.Name.Replace(' ', '-') + "-station";
            var station = WorldApi.SpawnGrid(new MyObjectBuilder_CubeGrid
            {
                Name = name,
                DisplayName = name,
                GridSizeEnum = MyCubeSize.Large,
                IsStatic = true,
                PositionAndOrientation = new MyPositionAndOrientation(MatrixD.CreateWorld(at, hull.WorldMatrix.Forward, hull.WorldMatrix.Up)),
                PersistentFlags = VRage.ObjectBuilders.MyPersistentEntityFlags2.InScene,
                CubeBlocks = blocks,
            });
            Track(station);
            return station;
        }

        public override void Cleanup()
        {
            try
            {
                FakeClients.RemoveAll();
                _config.Restore();
                _gameplay.Restore();
                if (_handlers != null && _handler != null) _handlers.RemoveAll(h => h.Item2 == _handler);
                if (_mode == Mode.Release)
                {
                    KeptConfig.RestoreAll();
                    // the seed's copies by name, pieces split off them too
                    Purge(keepSeed: false);
                }
            }
            finally { base.Cleanup(); }
        }

        public override void CleanupLeftovers()
        {
            try
            {
                _config.Restore();
                _gameplay.Restore();
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

    /// <summary>
    /// Settings a seed sets for the session after a restart: not noted for ConfigOverride's restore (which puts back
    /// everything after every run and on the next load), but in a file of their own, put back by the release.
    /// </summary>
    internal static class KeptConfig
    {
        private static string File => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(ConfigOverride.RestoreFile ?? ".") ?? ".", "freezer_rex_keep.txt");

        public static void Set(string plugin, string property, object value)
        {
            var values = Read();
            var key = plugin + ":" + property;
            if (!values.ContainsKey(key))
                values[key] = Convert.ToString(ConfigOverride.Get(plugin, property), System.Globalization.CultureInfo.InvariantCulture);
            Write(values);
            ConfigOverride.SetUnnoted(plugin, property, value);
        }

        public static void RestoreAll()
        {
            var values = Read();
            // the freezer off first, so everything thaws
            foreach (var pair in values.OrderBy(p => p.Key.EndsWith(":FreezerEnabled") ? 0 : 1))
            {
                var colon = pair.Key.IndexOf(':');
                ConfigOverride.SetUnnoted(pair.Key.Substring(0, colon), pair.Key.Substring(colon + 1), pair.Value);
            }
            if (System.IO.File.Exists(File)) System.IO.File.Delete(File);
        }

        private static Dictionary<string, string> Read()
        {
            var values = new Dictionary<string, string>();
            if (!System.IO.File.Exists(File)) return values;
            foreach (var line in System.IO.File.ReadAllLines(File))
            {
                var eq = line.IndexOf('=');
                if (eq > 0) values[line.Substring(0, eq)] = line.Substring(eq + 1);
            }
            return values;
        }

        private static void Write(Dictionary<string, string> values) =>
            System.IO.File.WriteAllLines(File, values.Select(p => p.Key + "=" + p.Value));
    }

    /// <summary>
    /// Where every grid of the seed stood and where every head stood against its stator, written before the save and
    /// compared after the load: nothing moves in between, so any difference is the load's.
    /// </summary>
    internal static class Poses
    {
        private static string File => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(ConfigOverride.RestoreFile ?? ".") ?? ".", "freezer_rex_poses.txt");
        private static readonly Dictionary<long, MatrixD> Grids = new Dictionary<long, MatrixD>();
        private static readonly Dictionary<long, MatrixD> Joints = new Dictionary<long, MatrixD>();

        /// <summary>The head block's matrix in the stator block's frame.</summary>
        private static MatrixD Relative(MyMechanicalConnectionBlockBase stator) =>
            stator.TopBlock.WorldMatrix * MatrixD.Invert(stator.WorldMatrix);

        public static void Write(IEnumerable<MyCubeGrid> grids)
        {
            var lines = new List<string>();
            foreach (var g in grids.Where(g => !g.Closed))
            {
                lines.Add("G " + g.EntityId + " " + Encode(g.WorldMatrix));
                foreach (var stator in g.GetFatBlocks().OfType<MyMechanicalConnectionBlockBase>().Where(t => t.TopBlock != null))
                    lines.Add("J " + stator.EntityId + " " + Encode(Relative(stator)));
            }
            System.IO.File.WriteAllLines(File, lines);
        }

        private static void Read()
        {
            if (Grids.Count > 0 || !System.IO.File.Exists(File)) return;
            foreach (var line in System.IO.File.ReadAllLines(File))
            {
                var parts = line.Split(' ');
                if (parts.Length < 3) continue;
                var m = Decode(parts.Skip(2).ToArray());
                (parts[0] == "G" ? Grids : Joints)[long.Parse(parts[1])] = m;
            }
        }

        /// <summary>How far each grid moved and each head drifted against its stator since the save: the largest ones.</summary>
        public static string Compare(IEnumerable<MyCubeGrid> grids)
        {
            Read();
            if (Grids.Count == 0) return "no poses saved";
            var moved = new List<(double M, string What)>();
            var drift = new List<(double M, double Deg, string What)>();
            foreach (var g in grids.Where(g => !g.Closed))
            {
                if (Grids.TryGetValue(g.EntityId, out var was))
                    moved.Add((Vector3D.Distance(was.Translation, g.WorldMatrix.Translation), "'" + g.DisplayName + "'"));
                foreach (var stator in g.GetFatBlocks().OfType<MyMechanicalConnectionBlockBase>().Where(t => t.TopBlock != null))
                    if (Joints.TryGetValue(stator.EntityId, out var rel))
                    {
                        var now = Relative(stator);
                        drift.Add(((now.Translation - rel.Translation).Length(), Angle(rel, now), stator.BlockDefinition.Id.SubtypeName + " on '" + g.DisplayName + "'"));
                    }
            }
            return "grids moved: max " + (moved.Count == 0 ? "-" : moved.Max(x => x.M).ToString("F3")) + " m (" + moved.Count(x => x.M > 0.01) + " over 1 cm); " +
                   "heads against their stators: max " + (drift.Count == 0 ? "-" : drift.Max(x => x.M).ToString("F3")) + " m, " +
                   (drift.Count == 0 ? "-" : drift.Max(x => x.Deg).ToString("F2")) + " deg (" + drift.Count(x => x.M > 0.01 || x.Deg > 0.5) + " of " + drift.Count + " off by over 1 cm or 0.5 deg)" +
                   (drift.Any(x => x.M > 0.01 || x.Deg > 0.5) ? "; worst: " + string.Join(", ", drift.OrderByDescending(x => x.M + x.Deg / 10).Take(6)
                       .Select(x => x.What + " " + x.M.ToString("F3") + " m " + x.Deg.ToString("F1") + " deg")) : "");
        }

        /// <summary>One head against its stator now, against the saved pose: "metres/degrees".</summary>
        public static string JointDrift(MyMechanicalConnectionBlockBase stator)
        {
            Read();
            if (stator.TopBlock == null || !Joints.TryGetValue(stator.EntityId, out var rel)) return "?";
            var now = Relative(stator);
            return (now.Translation - rel.Translation).Length().ToString("F2") + "/" + Angle(rel, now).ToString("F0");
        }

        private static double Angle(MatrixD a, MatrixD b)
        {
            var q = QuaternionD.CreateFromRotationMatrix(MatrixD.Invert(a.GetOrientation()) * b.GetOrientation());
            return MathHelper.ToDegrees(2 * Math.Acos(Math.Min(1, Math.Abs(q.W))));
        }

        private static string Encode(MatrixD m) => string.Join(" ", new[] { m.Translation.X, m.Translation.Y, m.Translation.Z, m.Forward.X, m.Forward.Y, m.Forward.Z, m.Up.X, m.Up.Y, m.Up.Z }
            .Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));

        private static MatrixD Decode(string[] v)
        {
            var d = v.Select(x => double.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            return MatrixD.CreateWorld(new Vector3D(d[0], d[1], d[2]), new Vector3D(d[3], d[4], d[5]), new Vector3D(d[6], d[7], d[8]));
        }
    }
}
