using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Sandbox.Game.Entities;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// Stress test of the SentisOptimisations freezer with FreezePhysics on: 64 copies of
    /// FREEZER_TEST_WITH_SUBGRIDS (a chassis on six wheels, piston chains and a hinge, 12 tops) on
    /// flat spots all over the planet and 64 scattered through space far above it. Every fourth
    /// copy keeps its landing gear locked to its static wall (logic-only freeze), the rest are free
    /// (their physics gets frozen). 64 fake players with characters stand at 64 of the 128 copies,
    /// and every one to three seconds a few of them jump to copies nobody is at: the copies they
    /// leave freeze, the ones they come to thaw, so about half of everything is frozen at any time.
    /// After <see cref="RunSeconds"/> the freezer is switched off and every copy is compared with
    /// how it stood before the run: tops attached, gears locked, blocks, damage, the ground.
    /// While running: a held copy must never get its physics frozen, a free one is physics-frozen
    /// whole or not at all, a physics-frozen grid does not move, and no awake grid keeps a fixed body.
    /// </summary>
    public sealed class FreezerStressScenario : TestScenario
    {
        public const string ScenarioName = "freezer_stress";
        public const string ProfileScenarioName = "freezer_stress_profile";
        private const double PhaseSettleSeconds = 15;
        private const double PhaseMeasureSeconds = 30;
        // The phase dotTrace samples (run_scn attaches on PROFILE WINDOW START).
        private const string ProfiledPhase = "players jump";
        private const string Prefix = "frzs-";
        private const int PlanetRigs = 64;
        private const int SpaceRigs = 64;
        private const int Players = 64;
        private const int HeldEvery = 4;
        private const int FreezeDistance = 500;
        private const double RunSeconds = 600;
        private const double SpacingM = 1500;
        private const double SpaceMinAltitudeM = 150000;
        private const double SpaceMaxAltitudeM = 300000;
        private const double MaxSlopeM = 1.5;
        // The copy reaches ~35 m from its chassis (the wall, the piston chains).
        private static readonly double[] FlatRadiiM = { 12, 24, 36 };
        private const double SettleSeconds = 15;
        private const double EndAwakeSeconds = 15;
        private const double SampleSeconds = 0.5;
        private const double MinMoveSeconds = 1, MaxMoveSeconds = 3;
        private const int MinMoves = 1, MaxMoves = 4;
        private const double PlayerOffsetM = 40;
        private const double FrozenMoveM = 0.05;
        private const int Seed = 20260919;
        internal const string ResourceName = FreezerPhysicsScenario.ResourceName;

        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };

        private sealed class Rig
        {
            public int Index;
            public string Name;
            public bool OnPlanet, Held;
            public List<MyCubeGrid> Grids = new List<MyCubeGrid>();
            public Vector3D PlayerSpot;
            public int Occupant = -1;
            public int Attached, Locked, Blocks;
            public double Integrity;
            public HashSet<long> VanillaFixed = new HashSet<long>();
            public Vector3D StartPosition;
            public bool Frozen;
            public int Freezes, Thaws;
            public double LastChange;
            public Dictionary<long, Vector3D> PhysicsFrozenAt = new Dictionary<long, Vector3D>();
            public List<string> Problems = new List<string>();
            public MyCubeGrid Chassis => Grids[0];

            /// <summary>Records a problem; true the first time this kind shows up on this copy.</summary>
            public bool Problem(double t, string what)
            {
                // One line per kind of problem is enough to find it; the count shows how often.
                var first = !Problems.Any(p => p.EndsWith(" " + what));
                if (first && Problems.Count < 20) Problems.Add(t.ToString("F0") + "s " + what);
                ProblemCount++;
                var kind = new string(what.Where(c => !char.IsDigit(c) && c != '/' && c != '.').ToArray()).Trim();
                Kinds.TryGetValue(kind, out var n);
                Kinds[kind] = n + 1;
                return first;
            }

            public static readonly Dictionary<string, int> Kinds = new Dictionary<string, int>();

            public int ProblemCount;
        }

        private readonly List<Rig> _rigs = new List<Rig>();
        private readonly ConfigOverride _config = new ConfigOverride();
        private readonly Random _rng = new Random(Seed);
        private PlanetGround _ground;
        private int[] _playerRig;

        private readonly bool _profile;

        /// <param name="profile">
        /// Instead of the 10-minute run: phases of <see cref="PhaseMeasureSeconds"/> that change one
        /// thing each (players moving or still, motors, which copies are awake, the freezer) and
        /// measure the frame, the physics step and the Havok worlds. The first phase is the profiling
        /// window for dotTrace.
        /// </param>
        public FreezerStressScenario(bool profile = false) => _profile = profile;

        public override string Name => _profile ? ProfileScenarioName : ScenarioName;
        public override int TimeoutSeconds => (int)(RunSeconds + 900);

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            Rig.Kinds.Clear();
            var anchor = WorldApi.LoadAuthoredGroup(WheelPerfScenario.ResourceName, WorldApi.EntityPrefix + Prefix + "anchor")[0]
                .PositionAndOrientation.Value.GetMatrix().Translation;
            var planet = MyGamePruningStructure.GetClosestPlanet(anchor);
            Check(planet != null, "no planet");
            _ground = new PlanetGround(planet);

            // Where the chassis stood when it was built, and how high above its ground.
            var authored = WorldApi.LoadAuthoredGroup(ResourceName, WorldApi.EntityPrefix + Prefix + "authored")[0]
                .PositionAndOrientation.Value.GetMatrix().Translation;
            var authoredUp = _ground.Up(authored);
            var height = Vector3D.Dot(authored - _ground.Ground(authored), authoredUp);

            // Sites: flat spots anywhere on the planet, points anywhere in the space shell above it.
            var planetSites = new List<Vector3D>();
            var tries = 0;
            while (planetSites.Count < PlanetRigs && tries++ < 50000)
            {
                var ground = FlatGround(_ground.Centre + RandomDirection() * planet.AverageRadius);
                if (ground.HasValue && planetSites.All(p => Vector3D.Distance(p, ground.Value) > SpacingM)) planetSites.Add(ground.Value);
                if (tries % 5 == 0) yield return null;
            }
            Check(planetSites.Count == PlanetRigs, "only " + planetSites.Count + " flat planet sites in " + tries + " tries");
            var spaceSites = new List<Vector3D>();
            while (spaceSites.Count < SpaceRigs)
            {
                var r = planet.AverageRadius + SpaceMinAltitudeM + _rng.NextDouble() * (SpaceMaxAltitudeM - SpaceMinAltitudeM);
                var site = _ground.Centre + RandomDirection() * r;
                if (spaceSites.All(p => Vector3D.Distance(p, site) > SpacingM)) spaceSites.Add(site);
            }
            Note("sites: " + PlanetRigs + " flat on the planet (" + tries + " tries), " + SpaceRigs + " in space " +
                 SpaceMinAltitudeM / 1000 + "-" + SpaceMaxAltitudeM / 1000 + " km up");

            for (var i = 0; i < PlanetRigs + SpaceRigs; i++)
            {
                var onPlanet = i < PlanetRigs;
                var held = i % HeldEvery == HeldEvery - 1;
                MatrixD rotation;
                Vector3D chassisAt, playerSpot;
                if (onPlanet)
                {
                    var ground = planetSites[i];
                    var up = _ground.Up(ground);
                    rotation = MatrixD.CreateFromQuaternion(QuaternionD.CreateFromTwoVectors(authoredUp, up)) *
                               MatrixD.CreateFromAxisAngle(up, _rng.NextDouble() * Math.PI * 2);
                    chassisAt = ground + up * (height + 0.3);
                    var side = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(up));
                    playerSpot = _ground.Ground(ground + side * PlayerOffsetM) + up * 2;
                }
                else
                {
                    rotation = MatrixD.CreateFromQuaternion(QuaternionD.CreateFromYawPitchRoll(
                        _rng.NextDouble() * Math.PI * 2, _rng.NextDouble() * Math.PI * 2, _rng.NextDouble() * Math.PI * 2));
                    chassisAt = spaceSites[i - PlanetRigs];
                    playerSpot = chassisAt + RandomDirection() * PlayerOffsetM;
                }
                _rigs.Add(BuildCopy(i, onPlanet, held, authored, rotation, chassisAt, playerSpot));
                yield return null;
            }

            var settle = WaitForSeconds(SettleSeconds, "copies settle, gears lock");
            while (settle.MoveNext()) yield return settle.Current;
            foreach (var rig in _rigs)
            {
                RigParts.StartMotors(rig.Grids);
                // The rover as built has no handbrake and rolls off downhill once awake.
                foreach (var controller in rig.Grids.SelectMany(g => g.GetFatBlocks().OfType<Sandbox.ModAPI.IMyShipController>()))
                    controller.HandBrake = true;
                rig.Attached = RigParts.Attached(rig.Grids);
                rig.Locked = RigParts.Locked(rig.Grids);
                rig.Blocks = RigParts.Blocks(rig.Grids);
                rig.Integrity = RigParts.Integrity(rig.Grids);
                rig.StartPosition = rig.Chassis.PositionComp.GetPosition();
                foreach (var g in rig.Grids.Where(g => !g.IsStatic && FreezerState.HasFixedBody(g))) rig.VanillaFixed.Add(g.EntityId);
                // A gear that did not lock leaves the copy free: the freezer is right to freeze its physics.
                if (rig.Held && rig.Locked == 0)
                {
                    rig.Held = false;
                    Note(rig.Name + ": the gear did not lock to its wall, counted as a free copy");
                }
            }
            Note("rigs: " + _rigs.Count + " (" + _rigs.Count(r => r.Held) + " held by a gear on a static wall), " +
                 _rigs.Sum(r => r.Grids.Count) + " grids, tops attached " + _rigs.Sum(r => r.Attached) + ", gears locked " + _rigs.Sum(r => r.Locked));

            // Freezer on, for real; the players decide.
            _config.Set("FreezeDistanceDynamic", FreezeDistance);
            _config.Set("FreezeDistanceStatic", FreezeDistance);
            _config.Set("FreezePhysics", true);
            _config.Set("FreezerEnabled", true);
            var start = _rigs.OrderBy(r => _rng.Next()).Take(Players).ToList();
            _playerRig = new int[Players];
            for (var p = 0; p < Players; p++)
            {
                _playerRig[p] = start[p].Index;
                start[p].Occupant = p;
            }
            FakeClients.Add(Players, Network, p => (start[p].PlayerSpot, 0, 0), withCharacters: true);
            Note("freezer on: physics freeze, " + FreezeDistance + " m, " + Players + " players at " + Players + " of " + _rigs.Count + " copies, seed " + Seed);
            TickMetrics.Take();
            FrameProbe.Take();

            if (_profile)
            {
                var phases = ProfilePhases();
                while (phases.MoveNext()) yield return phases.Current;
                yield break;
            }

            var clock = Stopwatch.StartNew();
            double nextMove = 0, nextSample = 0, nextReport = 60;
            long moves = 0, samples = 0;
            double frozenShareSum = 0, frozenShareMin = 1, frozenShareMax = 0;
            while (clock.Elapsed.TotalSeconds < RunSeconds)
            {
                var t = clock.Elapsed.TotalSeconds;
                if (t >= nextMove)
                {
                    var count = _rng.Next(MinMoves, MaxMoves + 1);
                    for (var k = 0; k < count; k++) moves += MovePlayer() ? 1 : 0;
                    nextMove = t + MinMoveSeconds + _rng.NextDouble() * (MaxMoveSeconds - MinMoveSeconds);
                }
                if (t >= nextSample)
                {
                    foreach (var rig in _rigs) Observe(rig, t);
                    var share = _rigs.Count(r => r.Frozen) / (double)_rigs.Count;
                    frozenShareSum += share;
                    samples++;
                    if (t > 30)
                    {
                        frozenShareMin = Math.Min(frozenShareMin, share);
                        frozenShareMax = Math.Max(frozenShareMax, share);
                    }
                    nextSample = t + SampleSeconds;
                }
                if (t >= nextReport)
                {
                    Note("at " + t.ToString("F0") + "s: frozen " + (_rigs.Count(r => r.Frozen) * 100 / _rigs.Count) + "%, " +
                         "physics-frozen grids " + _rigs.Sum(r => r.Grids.Count(FreezerState.IsPhysicsFrozen)) + ", moves " + moves +
                         ", freezes " + _rigs.Sum(r => r.Freezes) + ", thaws " + _rigs.Sum(r => r.Thaws) +
                         ", players alive " + Enumerable.Range(0, Players).Count(FakeClients.HasLiveCharacter) +
                         ", problems " + _rigs.Sum(r => r.ProblemCount) + KindsText());
                    nextReport += 60;
                }
                yield return null;
            }

            // Everything awake: freezer off, then compare with before the run.
            _config.Set("FreezerEnabled", false);
            var thaw = Wait(() => _rigs.All(r => r.Grids.All(g => g.Closed || !FreezerState.IsFrozen(g) && !FreezerState.IsPhysicsFrozen(g))),
                "everything thawed", 60);
            while (thaw.MoveNext()) yield return thaw.Current;
            var awake = WaitForSeconds(EndAwakeSeconds, "awake after the run");
            while (awake.MoveNext()) yield return awake.Current;
            foreach (var rig in _rigs) Final(rig, clock.Elapsed.TotalSeconds);
            FakeClients.RemoveAll();

            var freezes = _rigs.Select(r => r.Freezes).OrderBy(x => x).ToList();
            var physicsRigs = _rigs.Where(r => !r.Held).ToList();
            var moved = _rigs.Where(r => r.OnPlanet && !r.Held)
                .Select(r => Vector3D.Distance(r.Chassis.PositionComp.GetPosition(), r.StartPosition)).OrderBy(x => x).ToList();
            var bad = _rigs.Where(r => r.ProblemCount > 0).ToList();
            Note("FREEZER STRESS RESULT | " + RunSeconds + " s, " + moves + " player moves, freezes " + freezes.Sum() + " (per copy min " + freezes.First() +
                 " median " + freezes[freezes.Count / 2] + " max " + freezes.Last() + "), thaws " + _rigs.Sum(r => r.Thaws) +
                 " | frozen share avg " + (frozenShareSum / Math.Max(1, samples) * 100).ToString("F0") + "% (after 30 s min " +
                 (frozenShareMin * 100).ToString("F0") + "% max " + (frozenShareMax * 100).ToString("F0") + "%)" +
                 " | free planet copies moved median " + moved[moved.Count / 2].ToString("F1") + " m, max " + moved.Last().ToString("F1") + " m" +
                 " | tops attached " + _rigs.Sum(r => RigParts.Attached(r.Grids)) + "/" + _rigs.Sum(r => r.Attached) +
                 ", gears locked " + _rigs.Sum(r => RigParts.Locked(r.Grids)) + "/" + _rigs.Sum(r => r.Locked) +
                 ", blocks " + _rigs.Sum(r => RigParts.Blocks(r.Grids)) + "/" + _rigs.Sum(r => r.Blocks) +
                 " | copies with problems " + bad.Count + "/" + _rigs.Count + KindsText() +
                 (bad.Count == 0 ? "" : ": " + string.Join(" | ", bad.Take(12).Select(r => r.Name + " (" + r.ProblemCount + "): " + string.Join("; ", r.Problems.Take(4))))) +
                 " | " + TickMetrics.Take().Format() + " | " + FrameProbe.Take());
            Check(bad.Count == 0, bad.Count + " copies with problems: " +
                  string.Join(" | ", bad.Take(6).Select(r => r.Name + ": " + string.Join("; ", r.Problems.Take(3)))));
        }

        private string KindsText() => Rig.Kinds.Count == 0 ? "" :
            " (" + string.Join(", ", Rig.Kinds.OrderByDescending(k => k.Value).Select(k => k.Key + " x" + k.Value)) + " in " +
            _rigs.Count(r => r.ProblemCount > 0) + " copies)";

        // ------------------------------------------------------------------ profile

        private IEnumerator ProfilePhases()
        {
            Note("Havok: " + HavokSetup());
            var half = _rigs.Where(r => r.Occupant >= 0).ToList();
            var planet = _rigs.Where(r => r.OnPlanet).ToList();
            var space = _rigs.Where(r => !r.OnPlanet).ToList();
            var nowhere = _rigs.First(r => !r.OnPlanet).PlayerSpot + new Vector3D(0, 0, 20000);
            var phases = new List<(string Name, Action Setup, bool Churn)>
            {
                ("churn: players jump, half frozen", () => { }, true),
                ("steady: players still, half frozen", () => { }, false),
                ("motors off", () => SetMotors(false), false),
                ("planet copies awake, space frozen", () => { SetMotors(true); PlacePlayers(planet); }, false),
                ("space copies awake, planet frozen", () => PlacePlayers(space), false),
                ("all frozen, players away", () => PlacePlayers(null, nowhere), false),
                ("all frozen, players spread 3 km off", SpreadPlayers, false),
                ("all awake, freezer off", () => _config.Set("FreezerEnabled", false), false),
                ("half frozen, FreezePhysics off", () =>
                {
                    _config.Set("FreezePhysics", false);
                    _config.Set("FreezerEnabled", true);
                    PlacePlayers(half);
                }, false),
                ("all frozen, no players", () =>
                {
                    _config.Set("FreezePhysics", true);
                    FakeClients.RemoveAll();
                }, false),
            };
            var results = new List<string>();
            for (var i = 0; i < phases.Count; i++)
            {
                var phase = phases[i];
                phase.Setup();
                var settle = WaitForSeconds(PhaseSettleSeconds, "phase " + (i + 1) + " settles: " + phase.Name);
                while (settle.MoveNext()) yield return settle.Current;
                FrameProbe.Take();
                if (phase.Name.StartsWith(ProfiledPhase)) Note("PROFILE WINDOW START " + phase.Name);
                var clock = Stopwatch.StartNew();
                double nextMove = 0;
                var worlds = "";
                while (clock.Elapsed.TotalSeconds < PhaseMeasureSeconds)
                {
                    var t = clock.Elapsed.TotalSeconds;
                    if (phase.Churn && t >= nextMove)
                    {
                        var count = _rng.Next(MinMoves, MaxMoves + 1);
                        for (var k = 0; k < count; k++) MovePlayer();
                        nextMove = t + MinMoveSeconds + _rng.NextDouble() * (MaxMoveSeconds - MinMoveSeconds);
                    }
                    if (worlds.Length == 0 && t >= PhaseMeasureSeconds / 2) worlds = HavokWorlds();
                    yield return null;
                }
                var line = "phase " + (i + 1) + " " + phase.Name + ": frozen " + _rigs.Count(r => r.Grids.All(g => g.Closed || FreezerState.IsFrozen(g))) +
                           "/" + _rigs.Count + " copies | " + PhaseSummary(FrameProbe.Take()) + " | " + worlds;
                Note(line);
                results.Add(line);
            }
            Note("FREEZER PROFILE RESULT | " + string.Join(" || ", results));
        }

        /// <summary>Every player 3 km off its copy, each in its own direction: nobody near a copy or another player.</summary>
        private void SpreadPlayers()
        {
            foreach (var rig in _rigs) rig.Occupant = -1;
            for (var p = 0; p < Players; p++)
            {
                var from = _rigs[_playerRig[p]].PlayerSpot;
                var off = RandomDirection();
                if (_rigs[_playerRig[p]].OnPlanet)
                {
                    var up = _ground.Up(from);
                    off = Vector3D.Normalize(off - up * Vector3D.Dot(off, up));
                    FakeClients.MoveTo(p, _ground.Ground(from + off * 3000) + up * 2);
                }
                else FakeClients.MoveTo(p, from + off * 3000);
            }
        }

        /// <summary>Puts the players at the given copies (the rest stay free), or all at one far spot.</summary>
        private void PlacePlayers(List<Rig> targets, Vector3D? spot = null)
        {
            foreach (var rig in _rigs) rig.Occupant = -1;
            for (var p = 0; p < Players; p++)
            {
                if (targets != null && p < targets.Count)
                {
                    targets[p].Occupant = p;
                    _playerRig[p] = targets[p].Index;
                    FakeClients.MoveTo(p, targets[p].PlayerSpot);
                }
                else FakeClients.MoveTo(p, spot ?? _rigs[_playerRig[p]].PlayerSpot);
            }
        }

        private void SetMotors(bool on)
        {
            foreach (var rig in _rigs)
            {
                if (on)
                {
                    RigParts.StartMotors(rig.Grids);
                    continue;
                }
                foreach (var mech in RigParts.Tops(rig.Grids))
                {
                    if (mech is Sandbox.ModAPI.IMyPistonBase piston) piston.Velocity = 0;
                    else if (mech is Sandbox.ModAPI.IMyMotorStator stator && !(mech is Sandbox.Game.Entities.Cube.MyMotorSuspension)) stator.TargetVelocityRPM = 0;
                }
            }
        }

        private static string HavokSetup()
        {
            var pool = typeof(Sandbox.Engine.Physics.MyPhysics).GetField("m_threadPool",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)?.GetValue(null) as Havok.HkJobThreadPool;
            return "threads " + (pool?.ThreadCount.ToString() ?? "?") + ", multithreading " + Sandbox.Engine.Utils.MyFakes.ENABLE_HAVOK_MULTITHREADING +
                   ", parallel scheduling " + Sandbox.Engine.Utils.MyFakes.ENABLE_HAVOK_PARALLEL_SCHEDULING +
                   ", selective physics updates " + Sandbox.Game.World.MySession.Static.Settings.EnableSelectivePhysicsUpdates;
        }

        /// <summary>Havok worlds (clusters): how many, how many have anything awake, bodies, islands, constraints.</summary>
        private static string HavokWorlds()
        {
            int worlds = 0, activeWorlds = 0, bodies = 0, active = 0, islands = 0, constraints = 0;
            foreach (var world in Sandbox.Engine.Physics.MyPhysics.Clusters.GetList().OfType<Havok.HkWorld>())
            {
                worlds++;
                bodies += world.RigidBodies.Count;
                var awake = world.ActiveRigidBodies.Count;
                active += awake;
                if (awake > 0) activeWorlds++;
                islands += world.GetActiveSimulationIslandsCount();
                constraints += world.GetConstraintCount();
            }
            return "Havok worlds " + worlds + " (" + activeWorlds + " with active bodies), bodies " + bodies + " (" + active + " active), active islands " +
                   islands + ", constraints " + constraints;
        }

        private static string PhaseSummary(string probe)
        {
            string Pick(string name)
            {
                var m = System.Text.RegularExpressions.Regex.Match(probe, System.Text.RegularExpressions.Regex.Escape(name) + @"=\d+ms/\d+calls\([\d.]+ms each, ([\d.]+)ms/frame");
                return m.Success ? m.Groups[1].Value : "?";
            }
            var sim = System.Text.RegularExpressions.Regex.Match(probe, @"sim-work frames=\d+ avg=([\d.]+)ms p50=[\d.]+ p95=[\d.]+ p99=([\d.]+)");
            return "sim-work avg " + (sim.Success ? sim.Groups[1].Value : "?") + " ms, p99 " + (sim.Success ? sim.Groups[2].Value : "?") +
                   " | physics " + Pick("physics") + ", session.components " + Pick("session.components") + ", entities.before " + Pick("entities.before") +
                   ", entities.after " + Pick("entities.after") + ", replication.sendUpdate " + Pick("replication.sendUpdate") + ", harness " + Pick("harness");
        }

        // ------------------------------------------------------------------ players

        /// <summary>One player jumps from its copy to a random copy nobody is at.</summary>
        private bool MovePlayer()
        {
            var p = _rng.Next(Players);
            var free = _rigs.Where(r => r.Occupant < 0).ToList();
            if (free.Count == 0) return false;
            var to = free[_rng.Next(free.Count)];
            _rigs[_playerRig[p]].Occupant = -1;
            to.Occupant = p;
            _playerRig[p] = to.Index;
            FakeClients.MoveTo(p, to.PlayerSpot);
            return true;
        }

        // ------------------------------------------------------------------ checks

        private void Observe(Rig rig, double t)
        {
            var frozen = rig.Grids.All(g => g.Closed || FreezerState.IsFrozen(g));
            if (frozen != rig.Frozen) rig.LastChange = t;
            if (frozen && !rig.Frozen) rig.Freezes++;
            if (!frozen && rig.Frozen) rig.Thaws++;
            rig.Frozen = frozen;

            var physicsFrozen = 0;
            foreach (var g in rig.Grids)
            {
                if (g.Closed) continue;
                if (FreezerState.IsPhysicsFrozen(g))
                {
                    physicsFrozen++;
                    var at = g.PositionComp.GetPosition();
                    if (!rig.PhysicsFrozenAt.TryGetValue(g.EntityId, out var was)) rig.PhysicsFrozenAt[g.EntityId] = at;
                    else if (Vector3D.Distance(at, was) > FrozenMoveM) Flag(rig, t, "a grid moved while physics-frozen");
                    if (!FreezerState.HasFixedBody(g)) Flag(rig, t, "physics-frozen grid with a dynamic body");
                }
                else
                {
                    rig.PhysicsFrozenAt.Remove(g.EntityId);
                    if (!g.IsStatic && !rig.VanillaFixed.Contains(g.EntityId) && FreezerState.HasFixedBody(g))
                        Flag(rig, t, "grid left with a fixed body");
                }
            }
            if (rig.Held && physicsFrozen > 0) Flag(rig, t, "physics frozen on a held copy");
            if (!rig.Held && physicsFrozen > 0 && physicsFrozen < rig.Grids.Count) Flag(rig, t, "only part of the copy physics-frozen");
            CompareWithStart(rig, t);
        }

        private void CompareWithStart(Rig rig, double t)
        {
            foreach (var g in rig.Grids.Where(g => g.Closed))
                Flag(rig, t, (g.IsStatic ? "static wall" : g == rig.Chassis ? "chassis" : g.DisplayName) + " closed");
            var attached = RigParts.Attached(rig.Grids);
            if (attached < rig.Attached) Flag(rig, t, "tops attached " + attached + "/" + rig.Attached);
            var locked = RigParts.Locked(rig.Grids);
            if (locked < rig.Locked) Flag(rig, t, "gears locked " + locked + "/" + rig.Locked);
            var blocks = RigParts.Blocks(rig.Grids);
            if (blocks < rig.Blocks) Flag(rig, t, "blocks " + blocks + "/" + rig.Blocks);
            if (rig.OnPlanet)
                foreach (var g in rig.Grids.Where(g => !g.Closed && _ground.UnderGround(g)))
                    Flag(rig, t, (g.IsStatic ? "static wall" : g == rig.Chassis ? "chassis" : g.DisplayName) + " under the ground");
        }

        private void Final(Rig rig, double t)
        {
            CompareWithStart(rig, t);
            var damage = rig.Integrity - RigParts.Integrity(rig.Grids);
            if (damage > 1) Flag(rig, t, "damage " + damage.ToString("F0"));
            foreach (var g in rig.Grids.Where(g => !g.Closed && !g.IsStatic && !rig.VanillaFixed.Contains(g.EntityId) && FreezerState.HasFixedBody(g)))
                Flag(rig, t, "fixed body after the freezer was switched off");
            var speed = rig.Chassis.Physics?.LinearVelocity.Length() ?? 0;
            if (speed > 2) Flag(rig, t, "chassis at " + speed.ToString("F1") + " m/s after the run");
        }

        /// <summary>A problem on a copy; the first one of its kind is written out at once with the copy's freezer state.</summary>
        private void Flag(Rig rig, double t, string what)
        {
            if (rig.Problem(t, what))
                Note("PROBLEM " + rig.Name + " at " + t.ToString("F1") + "s: " + what + " (" + (rig.Frozen ? "frozen" : "awake") +
                     " since " + rig.LastChange.ToString("F1") + "s, freezes " + rig.Freezes + ", thaws " + rig.Thaws +
                     ", player " + (rig.Occupant >= 0 ? "here" : "away") + ")");
        }

        // ------------------------------------------------------------------ copies

        private Rig BuildCopy(int index, bool onPlanet, bool held, Vector3D authored, MatrixD rotation, Vector3D chassisAt, Vector3D playerSpot)
        {
            var name = (onPlanet ? "planet-" : "space-") + index.ToString("D3") + (held ? "-held" : "");
            var group = WorldApi.LoadAuthoredGroup(ResourceName, WorldApi.EntityPrefix + Prefix + name);
            var move = MatrixD.CreateTranslation(-authored) * rotation * MatrixD.CreateTranslation(chassisAt);
            if (onPlanet)
            {
                // Uneven ground can put a wheel into the rock; lift the whole copy until none is.
                var up = _ground.Up(chassisAt);
                for (var lift = 0; lift < 20 && group.Any(ob => !ob.IsStatic &&
                         _ground.ContentAt((ob.PositionAndOrientation.Value.GetMatrix() * move).Translation + up * 1.5) >= 128); lift++)
                    move *= MatrixD.CreateTranslation(up * 0.5);
            }
            var rig = new Rig { Index = index, Name = name, OnPlanet = onPlanet, Held = held, PlayerSpot = playerSpot };
            foreach (var ob in group)
            {
                if (ob.IsStatic && !held) continue;
                ob.PositionAndOrientation = new MyPositionAndOrientation(ob.PositionAndOrientation.Value.GetMatrix() * move);
                if (!held)
                    foreach (var gear in ob.CubeBlocks.OfType<Sandbox.Common.ObjectBuilders.MyObjectBuilder_LandingGear>())
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

        /// <summary>The ground under a point if it is flat as far as the copy reaches (within <see cref="MaxSlopeM"/>, 8 directions).</summary>
        private Vector3D? FlatGround(Vector3D point)
        {
            var ground = _ground.Ground(point);
            var up = _ground.Up(ground);
            var a = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(up));
            var b = Vector3D.Cross(up, a);
            for (var k = 0; k < 8; k++)
            {
                var d = a * Math.Cos(k * Math.PI / 4) + b * Math.Sin(k * Math.PI / 4);
                foreach (var r in FlatRadiiM)
                    if (Math.Abs(Vector3D.Dot(_ground.Ground(ground + d * r) - ground, up)) > MaxSlopeM) return null;
            }
            return ground;
        }

        private Vector3D RandomDirection()
        {
            while (true)
            {
                var v = new Vector3D(_rng.NextDouble() * 2 - 1, _rng.NextDouble() * 2 - 1, _rng.NextDouble() * 2 - 1);
                var l = v.Length();
                if (l > 0.1 && l <= 1) return v / l;
            }
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
