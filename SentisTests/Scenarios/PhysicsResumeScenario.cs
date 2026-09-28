using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Havok;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using Sandbox.Game.World;
using SentisTests.Core;
using SentisTests.Game;
using VRage.Game;
using VRage.Game.Components;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// A physics cluster nobody is in, and its return when a player comes (SentisOptimisations SelectivePhysicsBodies).
    ///
    /// With selective physics updates the game steps Havok only in a cluster with a character or an entity replicated
    /// to a client; SelectivePhysicsBodies also leaves the bodies of a cluster that is not stepped out of
    /// MyPhysics.UpdateActiveRigidBodies (the walk after the step) and keeps forces off them. The planet rigs stand on a
    /// planet made for the run, far from the stand's bots. The rigs, all in motion when the players leave:
    /// FREEZER_TEST_WITH_SUBGRIDS (a chassis on six wheels, piston chains, a hinge, a landing gear) on the planet as
    /// built (gear on its static grid), free and parked, free and driving on its wheels, and free in open space with its
    /// motors running; the piston stack (a static base and fourteen pistons) with the pistons moving; a Spitfire hanging
    /// on its atmospheric thrusters in the planet's gravity (thrust and gravity are applied every frame); a Spitfire flying
    /// and turning in open space.
    ///
    /// The freezer is off for the run (it would take the rigs as soon as the players leave, and this is about the
    /// clusters, not the freezer). Each of <see cref="Cycles"/> cycles: the fake players leave; every rig's cluster must
    /// stop being stepped, and while it is not, its grids must not move and must not gather speed (a force there only
    /// piles up velocity, with nothing to turn it into movement); then the players come back next to the rigs, the clusters are stepped again,
    /// and each rig is watched from its own first stepped frame for <see cref="WatchSeconds"/>: the jump of that first
    /// frame, speed kicks from one frame to the next in its first second, tops still attached, gears still locked, no damage, no grid under
    /// the ground.
    /// </summary>
    public sealed class PhysicsResumeScenario : TestScenario
    {
        public const string ScenarioName = "physics_resume";
        private const string Prefix = "prs-";
        private const int Cycles = 3;
        private const double SettleSeconds = 15;
        private const double AwakeSeconds = 10;
        private const double AloneSeconds = 20;
        private const double WaitStateSeconds = 40;
        private const double WatchSeconds = 5;
        private const double SpaceHeightM = 150000;
        private const double HoverHeightM = 80;
        private const float SpaceShipSpeed = 30;
        private const float PlanetSizeM = 30000;
        private const double PlanetDistanceM = 600000;
        /// <summary>A speed change of more than this from one frame to the next (180 m/s², 18 g) is a kick, not driving.</summary>
        private const double MaxFrameKick = 3;
        /// <summary>How long after the return a speed kick counts as the return's.</summary>
        private const double KickWindowSeconds = 1;
        /// <summary>The first stepped frame may move a grid by its own speed and this much more.</summary>
        private const double MaxResumeJump = 0.5;
        /// <summary>
        /// Speed a body may gather while its cluster is not stepped. Forces piling up there came to 50-90 m/s on the
        /// driving rover; what is left is a single change of up to 1.5 m/s on one of its wheels in the first second
        /// alone, which does not grow and does not kick on the return.
        /// </summary>
        private const double MaxGatheredSpeed = 2;
        /// <summary>Spin a body may gather while its cluster is not stepped (a driven wheel spun up to its speed limit).</summary>
        private const double MaxGatheredSpin = 1;

        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private sealed class Rig
        {
            public string Name;
            public List<MyCubeGrid> Grids = new List<MyCubeGrid>();
            public bool OnPlanet;
            public List<string> Problems = new List<string>();
            // cycle state
            public DateTime? AloneAt, ResumedAt;
            public List<Vector3D> AlonePoses, LastPoses;
            public List<Vector3> AloneVel, LastVel, AloneSpin;
            public int AloneActiveBodies, ClosedAlone, ClosedBack, ClosedAtStart;
            public double Gathered, Spun, Drift;
            public string SpunWhere = "";
            public double ResumeJump, ResumeKick, MaxKick, AwakeKick;
            public string MaxKickWhere = "", AwakeKickWhere = "", GatheredWhere = "";
            public int Attached, Locked, Blocks;
            public double Integrity;
        }

        private readonly List<Rig> _rigs = new List<Rig>();
        private readonly ConfigOverride _config = new ConfigOverride();
        private MyPlanet _planet;
        private PlanetGround _ground, _home;
        private bool _done;
        private bool? _trashWas;
        private List<Vector3D> _watch = new List<Vector3D>();

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => (int)(SettleSeconds + Cycles * (AwakeSeconds + AloneSeconds + 2 * WaitStateSeconds + WatchSeconds) + 300);

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            var physics = MySession.Static.GetComponent<MyPhysics>();
            Check(typeof(MyPhysics).GetField("m_worldObserver", Any)?.GetValue(physics) != null,
                "selective physics updates are off in this world: every cluster is stepped, nothing to test");

            var anchorM = WorldApi.LoadAuthoredGroup(WheelPerfScenario.ResourceName, WorldApi.EntityPrefix + Prefix + "anchor")[0]
                .PositionAndOrientation.Value.GetMatrix();
            var home = MyGamePruningStructure.GetClosestPlanet(anchorM.Translation);
            Check(home != null, "no planet");
            _home = new PlanetGround(home);
            var homeCentre = home.PositionComp.GetPosition();
            var homeUp = Vector3D.Normalize(anchorM.Translation - homeCentre);
            var spaceSite = homeCentre + homeUp * ((anchorM.Translation - homeCentre).Length() + SpaceHeightM);

            // The stand's bots (SentisAi) share themselves out over the planets when they (re)spawn: one that respawned
            // while the test planet is there would land on it and keep its clusters stepped. Right after a server start
            // they are still coming (they join 10 s after the world loads, one a second), so first every bot is in the world.
            var bots = Wait(() =>
            {
                if (Sandbox.MySandboxGame.Static.SimulationFrameCounter < 40 * 60) return false;
                var identities = MySession.Static.Players.GetAllIdentities().Count(i => i.DisplayName != null && i.DisplayName.StartsWith("[BOT]"));
                var alive = MyEntities.GetEntities().OfType<Sandbox.Game.Entities.Character.MyCharacter>()
                    .Count(c => !c.MarkedForClose && !c.IsDead && (c.DisplayName ?? "").StartsWith("[BOT]"));
                return alive >= identities;
            }, "every bot of the stand in the world", 120);
            while (bots.MoveNext()) yield return bots.Current;

            // A character near the rigs steps their cluster, and the stand's bots live on the home planet: the planet
            // rigs go on a planet of their own, far from everything, made for the run and removed after it.
            var planetAt = homeCentre + Vector3D.Normalize(Vector3D.Cross(homeUp, Vector3D.Up) + homeUp * 0.3) * PlanetDistanceM;
            _planet = Sandbox.ModAPI.MyAPIGateway.Session.VoxelMaps.SpawnPlanet("EarthLike", PlanetSizeM, 4711, planetAt) as MyPlanet;
            Check(_planet != null, "the test planet was not made");
            Track(_planet);
            yield return null;
            _ground = new PlanetGround(_planet);
            var centre = _planet.PositionComp.GetPosition();
            var up = FlattestUp(centre);
            var east = Vector3D.Normalize(Vector3D.Cross(up, Math.Abs(up.Y) < 0.9 ? Vector3D.Up : Vector3D.Right));
            var north = Vector3D.Cross(up, east);
            var planetSite = _ground.Ground(centre + up * PlanetSizeM);
            Note("test planet " + _planet.StorageName + " at " + (centre - homeCentre).Length().ToString("F0") + " m from " + home.StorageName +
                 ", gravity " + MyGravityProviderSystem.CalculateNaturalGravityInPoint(planetSite + up * 5).Length().ToString("F1") + " m/s2, atmosphere " + _planet.HasAtmosphere);

            // the freezer would take every rig the moment the players leave, and the game's trash removal the stack's
            // unpowered two-block subgrids
            _config.Set("FreezerEnabled", false);
            _trashWas = MySession.Static.Settings.TrashRemovalEnabled;
            MySession.Static.Settings.TrashRemovalEnabled = false;

            var authored = WorldApi.LoadAuthoredGroup(FreezerPhysicsScenario.ResourceName, WorldApi.EntityPrefix + Prefix + "authored")[0]
                .PositionAndOrientation.Value.GetMatrix().Translation;
            _rigs.Add(Copy("planet, gear on static", authored, planetSite, withStatic: true, onPlanet: true));
            yield return null;
            _rigs.Add(Copy("planet, free, parked", authored, planetSite + north * 120, withStatic: false, onPlanet: true));
            yield return null;
            _rigs.Add(Copy("planet, free, driving", authored, planetSite - north * 120, withStatic: false, onPlanet: true));
            yield return null;
            _rigs.Add(Copy("space, free, motors", authored, spaceSite, withStatic: false, onPlanet: false));
            yield return null;
            _rigs.Add(PistonStack("planet, piston stack", planetSite + north * 260, up));
            yield return null;
            _rigs.Add(Spitfire("planet, ship hovering", _ground.Ground(planetSite - east * 300) + up * HoverHeightM, east, up, onPlanet: true));
            yield return null;
            _rigs.Add(Spitfire("space, ship flying", spaceSite + east * 400, east, up, onPlanet: false));
            yield return null;

            // With selective physics updates nothing is stepped without somebody there: observers for the settling.
            PlayersCome();
            var settle = WaitForSeconds(SettleSeconds, "rigs settle, gears lock, the ship takes its hover");
            while (settle.MoveNext()) yield return settle.Current;

            // In motion from now on.
            foreach (var rig in _rigs) RigParts.StartMotors(rig.Grids);
            foreach (var s in WorldApi.FindFunctionals<MyMotorSuspension>(Named("planet, free, driving").Grids[0]))
            {
                s.PropulsionOverride = 0.5f;
                s.SteeringOverride = 0.3f;
            }
            var flyer = Named("space, ship flying").Grids[0];
            flyer.Physics.SetSpeeds(east * SpaceShipSpeed, up * 0.2);
            foreach (var rig in _rigs)
                Note("rig " + rig.Name + ": " + rig.Grids.Count + " grids, " + RigParts.Blocks(rig.Grids) + " blocks, tops attached " +
                     RigParts.Attached(rig.Grids) + ", gears locked " + RigParts.Locked(rig.Grids) + ", chassis speed " + Speed(rig.Grids[0]).ToString("F1"));
            Note("clusters: " + Clusters());
            var unlocked = _rigs.Where(r => r.Grids.Any(g => g.IsStatic) && r.Name.Contains("gear") && RigParts.Locked(r.Grids) != RigParts.Gears(r.Grids).Count).ToList();
            Check(unlocked.Count == 0, "landing gear not locked to the static grid: " + string.Join(", ", unlocked.Select(r => r.Name)));

            // who closes a grid of a rig: the call that did it, into the log
            foreach (var rig in _rigs)
                foreach (var g in rig.Grids)
                {
                    var name = rig.Name + " grid " + rig.Grids.IndexOf(g);
                    g.OnMarkForClose += _ => { if (!_done) Note(name + " marked for close (stepped " + Stepped(g) + ") from: " +
                        string.Join(" < ", new System.Diagnostics.StackTrace().GetFrames()?.Skip(1).Take(20)
                            .Select(f => f.GetMethod()?.DeclaringType?.Name + "." + f.GetMethod()?.Name) ?? new string[0])); };
                }
            TickMetrics.Take();
            FrameProbe.Take();
            for (var cycle = 1; cycle <= Cycles; cycle++)
            {
                // the stack on its way (out on odd cycles, in on even ones) when the players leave: it has to carry on
                // from where it was when they come back
                foreach (var p in PistonsOf(Named("planet, piston stack"))) p.Velocity = cycle % 2 == 1 ? 0.5f : -0.5f;
                // with the players there: how hard the rigs shake on their own, the baseline for the return
                var awakeAt = DateTime.UtcNow;
                var awake = WaitForSeconds(AwakeSeconds, "cycle " + cycle + ": players there");
                foreach (var rig in _rigs)
                {
                    rig.AwakeKick = 0;
                    rig.LastVel = Velocities(rig);
                }
                while (awake.MoveNext())
                {
                    foreach (var rig in _rigs) Shake(rig, ref rig.AwakeKick, ref rig.AwakeKickWhere, awakeAt);
                    yield return awake.Current;
                }
                foreach (var rig in _rigs) Reset(rig);

                // The players leave: every rig's cluster must drop out of the step.
                FakeClients.RemoveAll();
                var left = DateTime.UtcNow;
                while (_rigs.Any(r => r.AloneAt == null))
                {
                    if ((DateTime.UtcNow - left).TotalSeconds > WaitStateSeconds)
                        throw new ScenarioFailedException("cycle " + cycle + ": still stepped with nobody there: " +
                            string.Join(", ", _rigs.Where(r => r.AloneAt == null).Select(r => r.Name + " (" + WhyStepped(r.Grids[0]) + ")")));
                    foreach (var rig in _rigs.Where(r => r.AloneAt == null && r.Grids.All(g => g.Closed || Stepped(g) == false)))
                        Alone(rig);
                    yield return null;
                }
                var alone = DateTime.UtcNow;
                while ((DateTime.UtcNow - alone).TotalSeconds < AloneSeconds)
                {
                    foreach (var rig in _rigs) WhileAlone(rig);
                    yield return null;
                }

                // The players come back: each rig from its first stepped frame.
                PlayersCome();
                var back = DateTime.UtcNow;
                while (_rigs.Any(r => r.ResumedAt == null || (DateTime.UtcNow - r.ResumedAt.Value).TotalSeconds < WatchSeconds))
                {
                    if ((DateTime.UtcNow - back).TotalSeconds > WaitStateSeconds + WatchSeconds)
                        throw new ScenarioFailedException("cycle " + cycle + ": not stepped again with the players back: " +
                            string.Join(", ", _rigs.Where(r => r.ResumedAt == null).Select(r => r.Name + " (" + WhyStepped(r.Grids[0]) + ")")) + "; clusters: " + Clusters());
                    foreach (var rig in _rigs) AfterReturn(rig, cycle);
                    yield return null;
                }
                foreach (var rig in _rigs) Compare(rig, cycle);
                Note("cycle " + cycle + ": " + string.Join(" | ", _rigs.Select(r => r.Name + ": active bodies " + r.AloneActiveBodies + "/" + r.Grids.Count +
                     ", gathered " + r.Gathered.ToString("F2") + " m/s " + r.GatheredWhere + ", spun " + r.Spun.ToString("F2") + " rad/s " + r.SpunWhere + ", drift " + r.Drift.ToString("F3") + " m, stepped again after " +
                     (r.ResumedAt.Value - back).TotalSeconds.ToString("F1") + " s, first frame jump " + r.ResumeJump.ToString("F2") + " m kick " +
                     r.ResumeKick.ToString("F2") + " m/s, max kick " + r.MaxKick.ToString("F2") + " m/s")));
            }

            _done = true;
            Note("PHYSICS RESUME RESULT | " + string.Join(" | ", _rigs.Select(r => r.Name + ": problems " +
                 (r.Problems.Count == 0 ? "none" : string.Join("; ", r.Problems.Distinct().Take(6))))) + " | " + TickMetrics.Take().Format() + " | " + FrameProbe.Take());
            Check(_rigs.All(r => r.Problems.Count == 0), "problems: " + string.Join(" | ", _rigs.Where(r => r.Problems.Count > 0)
                .Select(r => r.Name + ": " + string.Join("; ", r.Problems.Distinct().Take(4)))));
        }

        // ------------------------------------------------------------------ the cycle

        private static void Reset(Rig rig)
        {
            rig.AloneAt = rig.ResumedAt = null;
            rig.ClosedAlone = rig.ClosedBack = 0;
            rig.Spun = 0;
            rig.SpunWhere = "";
            rig.Gathered = rig.Drift = rig.ResumeJump = rig.ResumeKick = rig.MaxKick = 0;
            rig.MaxKickWhere = rig.GatheredWhere = "";
        }

        private void Alone(Rig rig)
        {
            rig.AloneAt = DateTime.UtcNow;
            rig.AlonePoses = Poses(rig);
            rig.AloneVel = Velocities(rig);
            rig.AloneSpin = Spins(rig);
            rig.LastPoses = rig.AlonePoses;
            rig.LastVel = rig.AloneVel;
            rig.ClosedAtStart = rig.Grids.Count(g => g.Closed);
            rig.AloneActiveBodies = rig.Grids.Count(g => !g.Closed && g.Physics?.RigidBody != null && g.Physics.RigidBody.IsActive);
            rig.Attached = RigParts.Attached(rig.Grids);
            rig.Locked = RigParts.Locked(rig.Grids);
            rig.Blocks = RigParts.Blocks(rig.Grids);
            rig.Integrity = RigParts.Integrity(rig.Grids);
        }

        /// <summary>Not stepped: nothing moves and nothing gathers speed.</summary>
        private static void WhileAlone(Rig rig)
        {
            var poses = Poses(rig);
            var vel = Velocities(rig);
            rig.ClosedAlone = Math.Max(rig.ClosedAlone, rig.Grids.Count(g => g.Closed) - rig.ClosedAtStart);
            for (var i = 0; i < rig.Grids.Count; i++)
            {
                if (rig.Grids[i].Closed) continue;
                rig.Drift = Math.Max(rig.Drift, Vector3D.Distance(poses[i], rig.AlonePoses[i]));
                var spun = (Spins(rig)[i] - rig.AloneSpin[i]).Length();
                if (spun > rig.Spun)
                {
                    rig.Spun = spun;
                    rig.SpunWhere = "grid " + i + " (" + rig.Grids[i].BlocksCount + " blocks)";
                }
                var gathered = (vel[i] - rig.AloneVel[i]).Length();
                if (gathered > rig.Gathered)
                {
                    rig.Gathered = gathered;
                    rig.GatheredWhere = "grid " + i + " (" + rig.Grids[i].BlocksCount + " blocks) at +" + (DateTime.UtcNow - rig.AloneAt.Value).TotalSeconds.ToString("F1") + " s";
                }
            }
            rig.LastPoses = poses;
            rig.LastVel = vel;
        }

        private void AfterReturn(Rig rig, int cycle)
        {
            var poses = Poses(rig);
            var vel = Velocities(rig);
            if (rig.ResumedAt == null)
            {
                if (rig.Grids.Any(g => !g.Closed && Stepped(g) == true))
                {
                    // Stepped from this frame: the step runs before the scenario sees the frame, so what is read now is
                    // the first stepped frame against the last one alone.
                    rig.ResumedAt = DateTime.UtcNow;
                    for (var i = 0; i < rig.Grids.Count; i++)
                    {
                        if (rig.Grids[i].Closed) continue;
                        var expected = rig.LastVel[i].Length() / 60.0 * 3;   // the scenario may see the frame late by a couple of steps
                        rig.ResumeJump = Math.Max(rig.ResumeJump, Vector3D.Distance(poses[i], rig.LastPoses[i]) - expected);
                        rig.ResumeKick = Math.Max(rig.ResumeKick, (vel[i] - rig.LastVel[i]).Length());
                    }
                }
                else
                {
                    WhileAlone(rig);
                    return;
                }
            }
            else
            {
                rig.ClosedBack = Math.Max(rig.ClosedBack, rig.Grids.Count(g => g.Closed) - rig.ClosedAtStart - rig.ClosedAlone);
                // a kick of the return comes in its first frames; later on a driving rig just shakes on the ground
                if ((DateTime.UtcNow - rig.ResumedAt.Value).TotalSeconds <= KickWindowSeconds)
                    Shake(rig, ref rig.MaxKick, ref rig.MaxKickWhere, rig.ResumedAt.Value);
                else
                {
                    rig.LastPoses = poses;
                    rig.LastVel = vel;
                }
                return;
            }
            rig.LastPoses = poses;
            rig.LastVel = vel;
            foreach (var g in rig.Grids)
                if (rig.OnPlanet && !g.Closed && !g.IsStatic && _ground.UnderGround(g))
                    rig.Problems.Add("cycle " + cycle + ": grid " + rig.Grids.IndexOf(g) + " under the ground after the return");
        }

        /// <summary>The largest speed change of a grid from one frame to the next, which grid and when.</summary>
        private static void Shake(Rig rig, ref double max, ref string where, DateTime since)
        {
            var vel = Velocities(rig);
            for (var i = 0; i < rig.Grids.Count; i++)
            {
                if (rig.Grids[i].Closed || i >= rig.LastVel.Count) continue;
                var kick = (vel[i] - rig.LastVel[i]).Length();
                if (kick <= max) continue;
                max = kick;
                where = "grid " + i + " (" + rig.Grids[i].BlocksCount + " blocks) at +" + (DateTime.UtcNow - since).TotalSeconds.ToString("F1") + " s";
            }
            rig.LastPoses = Poses(rig);
            rig.LastVel = vel;
        }

        private void Compare(Rig rig, int cycle)
        {
            var c = "cycle " + cycle + ": ";
            if (rig.Drift > 0.05) rig.Problems.Add(c + "moved " + rig.Drift.ToString("F2") + " m while not stepped");
            if (rig.Spun > MaxGatheredSpin) rig.Problems.Add(c + "spun up by " + rig.Spun.ToString("F1") + " rad/s while not stepped (" + rig.SpunWhere + ")");
            if (rig.Gathered > MaxGatheredSpeed) rig.Problems.Add(c + "gathered " + rig.Gathered.ToString("F1") + " m/s while not stepped (" + rig.GatheredWhere + ")");
            if (rig.ClosedAlone > 0) rig.Problems.Add(c + rig.ClosedAlone + " grids closed while not stepped");
            if (rig.ClosedBack > 0) rig.Problems.Add(c + rig.ClosedBack + " grids closed after the return");
            if (rig.ResumeJump > MaxResumeJump) rig.Problems.Add(c + "jumped " + rig.ResumeJump.ToString("F2") + " m on the first stepped frame");
            if (rig.ResumeKick > MaxFrameKick) rig.Problems.Add(c + "kicked " + rig.ResumeKick.ToString("F1") + " m/s on the first stepped frame");
            // a rig that shakes that hard with the players there anyway (wheels on rough ground) is judged against itself
            if (rig.MaxKick > Math.Max(MaxFrameKick, rig.AwakeKick * 1.5))
                rig.Problems.Add(c + "kicked " + rig.MaxKick.ToString("F1") + " m/s in one frame after the return (" + rig.MaxKickWhere + "), with the players there at most " +
                                 rig.AwakeKick.ToString("F1") + " m/s (" + rig.AwakeKickWhere + ")");
            var attached = RigParts.Attached(rig.Grids);
            if (attached < rig.Attached) rig.Problems.Add(c + "tops attached " + attached + "/" + rig.Attached);
            var locked = RigParts.Locked(rig.Grids);
            if (locked < rig.Locked) rig.Problems.Add(c + "gears locked " + locked + "/" + rig.Locked);
            var blocks = RigParts.Blocks(rig.Grids);
            if (blocks < rig.Blocks) rig.Problems.Add(c + "blocks " + blocks + "/" + rig.Blocks);
            var integrity = RigParts.Integrity(rig.Grids);
            if (integrity < rig.Integrity - 1) rig.Problems.Add(c + "damage " + (rig.Integrity - integrity).ToString("F0"));
        }

        // ------------------------------------------------------------------ clusters

        /// <summary>Whether the game steps the Havok world the grid is in; null without a body in a world.</summary>
        private static bool? Stepped(MyCubeGrid grid)
        {
            var world = grid.Physics?.HavokWorld;
            if (world == null) return null;
            var physics = MySession.Static.GetComponent<MyPhysics>();
            foreach (var cluster in MyPhysics.Clusters.GetClusters())
                if (cluster.UserData == world)
                    return (bool)IsClusterActive.Invoke(physics, new object[] { cluster.ClusterId, world.CharacterRigidBodies.Count });
            return null;
        }

        private static readonly MethodInfo IsClusterActive =
            typeof(MyPhysics).GetMethod("IsClusterActive", Any, null, new[] { typeof(int), typeof(int) }, null)
            ?? throw new MissingMethodException("MyPhysics", "IsClusterActive");

        /// <summary>
        /// A fake player next to every rig but the hovering ship (in the air; the stack's player is under it). A fake
        /// client wakes up only the cluster its character is in, and the game splits clusters along the gaps between
        /// objects: rigs a hundred metres apart can be in two.
        /// </summary>
        private void PlayersCome()
        {
            _watch = _rigs.Where(r => r.Name != "planet, ship hovering").Select(r =>
            {
                // just clear of the rig's own box
                var box = r.Grids[0].PositionComp.WorldAABB;
                var clear = box.HalfExtents.Length() + 10;
                if (!r.OnPlanet)
                {
                    var v = (Vector3D)(r.Grids[0].Physics?.LinearVelocity ?? Vector3.Zero);
                    return box.Center + Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(v.LengthSquared() > 1 ? v : Vector3D.Up)) * clear;
                }
                var up = _ground.Up(box.Center);
                var side = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(up));
                return _ground.Ground(box.Center + side * clear) + up * 2;
            }).ToList();
            FakeClients.Add(_watch.Count, Network, i => (_watch[i], 0, 0), withCharacters: true);
        }

        private static int ClusterOf(MyPhysicsComponentBase physics)
        {
            var world = (physics as MyPhysicsBody)?.HavokWorld;
            return world == null ? -1 : MyPhysics.Clusters.GetClusters().FirstOrDefault(c => c.UserData == world)?.ClusterId ?? -1;
        }

        /// <summary>Which cluster each rig's grids and each fake player are in, and whether it is stepped.</summary>
        private string Clusters()
        {
            var rigs = _rigs.Select(r => r.Name + " " + string.Join("/", r.Grids.Select(g => g.Physics).Select(ClusterOf).Distinct()) +
                                         (r.Grids.Any(g => Stepped(g) == true) ? " stepped" : " not stepped"));
            var players = Enumerable.Range(0, _watch.Count).Select(i =>
            {
                Sandbox.Game.Entities.Character.MyCharacter c = null;
                try { c = FakeClients.Character(i); } catch (Exception) { }
                return "player " + i + " " + (c == null ? "none" : ClusterOf(c.Physics) + " at " + c.PositionComp.GetPosition().ToString("F0"));
            });
            return string.Join(", ", rigs.Concat(players));
        }

        private static string WhyStepped(MyCubeGrid grid)
        {
            var world = grid.Physics?.HavokWorld;
            if (world == null) return "no world";
            var chars = world.CharacterRigidBodies.Count;
            var observer = typeof(MyPhysics).GetField("m_worldObserver", Any)?.GetValue(MySession.Static.GetComponent<MyPhysics>());
            var byCluster = observer?.GetType().GetField("m_clusterReplicablesCount", Any)?.GetValue(observer) as IDictionary;
            var id = MyPhysics.Clusters.GetClusters().FirstOrDefault(c => c.UserData == world)?.ClusterId ?? -1;
            var replicated = (byCluster?[id] as ICollection)?.Count ?? 0;
            var who = string.Join("/", world.CharacterRigidBodies.Select(b => (b.GetHitRigidBody()?.UserObject as MyPhysicsBody)?.Entity as Sandbox.Game.Entities.Character.MyCharacter)
                .Select(c => c == null ? "?" : c.Definition.Id.SubtypeName + " " + c.DisplayName + " " + Vector3D.Distance(c.PositionComp.GetPosition(), grid.PositionComp.GetPosition()).ToString("F0") + " m"));
            return chars + " characters (" + who + "), " + replicated + " replicated entities";
        }

        /// <summary>Of a few places on the test planet, the one with the flattest ground within 300 m.</summary>
        private Vector3D FlattestUp(Vector3D centre)
        {
            var best = Vector3D.Up;
            var bestSpread = double.MaxValue;
            for (var i = 0; i < 24; i++)
            {
                // points spread over the upper half of the sphere
                var a = i * 2.39996;
                var y = 1 - (i + 0.5) / 24.0;
                var r = Math.Sqrt(1 - y * y);
                var up = Vector3D.Normalize(new Vector3D(Math.Cos(a) * r, y, Math.Sin(a) * r));
                var east = Vector3D.Normalize(Vector3D.Cross(up, Math.Abs(up.Y) < 0.9 ? Vector3D.Up : Vector3D.Right));
                var north = Vector3D.Cross(up, east);
                var site = centre + up * PlanetSizeM;
                var heights = new List<double>();
                for (var dx = -1; dx <= 1; dx++)
                for (var dz = -1; dz <= 1; dz++)
                    heights.Add((_ground.Ground(site + east * (300 * dx) + north * (300 * dz)) - centre).Length());
                var spread = heights.Max() - heights.Min();
                if (spread < bestSpread)
                {
                    bestSpread = spread;
                    best = up;
                }
            }
            Note("test planet site: ground spread " + bestSpread.ToString("F1") + " m over 600 m");
            return best;
        }

        // ------------------------------------------------------------------ rigs

        private Rig Named(string name) => _rigs.First(r => r.Name == name);

        private static List<Vector3D> Poses(Rig rig) => rig.Grids.Select(g => g.PositionComp.GetPosition()).ToList();

        private static List<Vector3> Velocities(Rig rig) => rig.Grids.Select(g => g.Physics?.LinearVelocity ?? Vector3.Zero).ToList();

        private static List<Vector3> Spins(Rig rig) => rig.Grids.Select(g => g.Physics?.AngularVelocity ?? Vector3.Zero).ToList();

        private static double Speed(MyCubeGrid g) => g.Physics?.LinearVelocity.Length() ?? 0;

        private static IEnumerable<Sandbox.ModAPI.IMyPistonBase> PistonsOf(Rig rig) =>
            RigParts.Tops(rig.Grids).OfType<Sandbox.ModAPI.IMyPistonBase>();

        /// <summary>A copy of FREEZER_TEST_WITH_SUBGRIDS moved so its chassis is at the site (on the planet, on the ground there).</summary>
        private Rig Copy(string name, Vector3D authored, Vector3D site, bool withStatic, bool onPlanet)
        {
            var group = WorldApi.LoadAuthoredGroup(FreezerPhysicsScenario.ResourceName, WorldApi.EntityPrefix + Prefix + name.Replace(' ', '-').Replace(",", ""));
            // Built on the home planet: in space moved as it is; on the test planet turned from the home planet's up to
            // the test planet's and stood on the ground there as high as it stood where it was built.
            var move = MatrixD.CreateTranslation(site - authored);
            if (onPlanet)
            {
                var homeUp = _home.Up(authored);
                var up = _ground.Up(site);
                var height = Vector3D.Dot(authored - _home.Ground(authored), homeUp);
                move = MatrixD.CreateTranslation(-authored) * MatrixD.CreateFromQuaternion(QuaternionD.CreateFromTwoVectors(homeUp, up)) *
                       MatrixD.CreateTranslation(_ground.Ground(site) + up * (height + 0.3));
            }
            var rig = new Rig { Name = name, OnPlanet = onPlanet };
            foreach (var ob in group)
            {
                if (ob.IsStatic && !withStatic) continue;
                ob.PositionAndOrientation = new VRage.MyPositionAndOrientation(ob.PositionAndOrientation.Value.GetMatrix() * move);
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

        /// <summary>The piston stack standing up from the ground at the site, its base static.</summary>
        private Rig PistonStack(string name, Vector3D site, Vector3D up)
        {
            var group = WorldApi.LoadAuthoredGroup(PistonStackScenario.ResourceName, WorldApi.EntityPrefix + Prefix + "stack");
            var base0 = group[0].PositionAndOrientation.Value.GetMatrix();
            var tip0 = group.Select(g => (Vector3D)g.PositionAndOrientation.Value.Position)
                .OrderByDescending(p => Vector3D.DistanceSquared(p, base0.Translation)).First();
            var turn = MatrixD.CreateFromQuaternion(QuaternionD.CreateFromTwoVectors(Vector3D.Normalize(tip0 - base0.Translation), up));
            var move = MatrixD.CreateTranslation(-base0.Translation) * turn * MatrixD.CreateTranslation(_ground.Ground(site) + up * 2);
            foreach (var ob in group)
                ob.PositionAndOrientation = new VRage.MyPositionAndOrientation(ob.PositionAndOrientation.Value.GetMatrix() * move);
            group[0].IsStatic = true;
            var rig = new Rig { Name = name, OnPlanet = true };
            foreach (var ob in group)
            {
                var grid = WorldApi.SpawnGrid(ob);
                Track(grid);
                rig.Grids.Add(grid);
            }
            return rig;
        }

        /// <summary>A Spitfire (atmospheric thrusters, batteries) with its dampeners on.</summary>
        private Rig Spitfire(string name, Vector3D at, Vector3D forward, Vector3D up, bool onPlanet)
        {
            var ob = WorldApi.LoadGridTemplate("SentisTests.Resources.Spitfire.xml", WorldApi.EntityPrefix + Prefix + name.Replace(' ', '-').Replace(",", ""),
                at, forward, up);
            Sandbox.ModAPI.MyAPIGateway.Entities.RemapObjectBuilder(ob);
            var ship = WorldApi.SpawnGrid(ob);
            Track(ship);
            WorldApi.ChargeBatteries(ship);
            var thrust = ship.Components.Get<MyEntityThrustComponent>();
            if (thrust != null) thrust.DampenersEnabled = onPlanet;
            return new Rig { Name = name, OnPlanet = onPlanet, Grids = { ship } };
        }

        public override void Cleanup()
        {
            try
            {
                FakeClients.RemoveAll();
                _config.Restore();
                if (_trashWas.HasValue) MySession.Static.Settings.TrashRemovalEnabled = _trashWas.Value;
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
                // the test planet (its storage is named after the generator, the seed and the size)
                foreach (var planet in MyEntities.GetEntities().OfType<MyPlanet>().ToList())
                    if (!planet.MarkedForClose && planet.StorageName == "EarthLike-4711d" + PlanetSizeM)
                        planet.Close();
            }
            finally { base.CleanupLeftovers(); }
        }
    }
}
