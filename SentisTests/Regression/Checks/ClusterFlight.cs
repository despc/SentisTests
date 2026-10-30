using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Sandbox.Game.Entities;
using SentisTests.Core;
using SentisTests.Game;
using VRageMath;
using Direction = VRageMath.Base6Directions.Direction;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// Flight on thrusters in many clusters at once (SentisClusters runs each cluster's entities on a thread of its own).
    /// <see cref="Rigs"/> rigs 8 km apart along the world's axis, each a cluster of its own: a ship (cockpit, gyroscope,
    /// small thrusters forward and back) flown by a fake pilot, and an astronaut on the jetpack by it. All rigs fly the same
    /// program in the same frames, the keys sent as a client's update brings them:
    /// <list type="bullet">
    /// <item>the ship forward 4 s, the dampeners stop it; back 4 s, stopped; a turn 2 s on the gyroscope, stopped;</item>
    /// <item>the astronaut forward 3 s, stopped; up 3 s, stopped.</item>
    /// </list>
    /// The program runs twice: the rigs' clusters attached, then each detached by hand (<c>!clusters detach</c>). The
    /// rigs are alike, so they must fly alike: each leg's distance (or angle) of each rig within <see cref="Spread"/> of
    /// the rigs' median (the astronauts of the rigs with the jetpack as strong: the game gives it 0.67 of its force in
    /// space, up to all of it by the air of the closest planet, and the stand's sites differ), everything stopped by the
    /// dampeners, and each rig detached within <see cref="DetachedSpread"/> of itself attached - a detached cluster that
    /// steps less often or loses keys shows as a shorter leg. No exception in the clusters' threads, no error in the log. Focus: <c>rigs=6 detach=0</c> (the
    /// attached run only). Not part of the regression: it detaches clusters.
    /// </summary>
    public sealed class ClusterFlightCheck : RegressionCheck
    {
        public const string ScenarioName = "cluster_flight";
        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 900;
        private const double Apart = 8000;
        private const double Spread = 0.1;
        private const double DetachedSpread = 0.15;
        private const double Stopped = 0.3;
        private int Rigs = 6;

        private sealed class Rig
        {
            public int Index;
            public Vector3D At;
            public MyCubeGrid Ship;
            public MyCockpit Seat;
            public int Pilot, Flyer;
            /// <summary>The jetpack's thrust at the end of the last leg's keys, N: the game's own effectiveness of it at the place.</summary>
            public double JetThrust;
        }

        /// <summary>A leg of the program: keys for so many frames, or none until everything has stopped.</summary>
        private sealed class Leg
        {
            public string Name;
            public int Frames;
            public Vector3 ShipMove;
            public Vector2 ShipTurn;
            public Vector3 FlyerMove;
            public bool Settle;
        }

        private static readonly Leg[] Program =
        {
            new Leg { Name = "forward", Frames = 240, ShipMove = new Vector3(0, 0, -1), FlyerMove = new Vector3(0, 0, -1) },
            new Leg { Name = "stop", Settle = true },
            new Leg { Name = "back/up", Frames = 240, ShipMove = new Vector3(0, 0, 1), FlyerMove = new Vector3(0, 1, 0) },
            new Leg { Name = "stop", Settle = true },
            new Leg { Name = "turn", Frames = 120, ShipTurn = new Vector2(0, 10) },
            new Leg { Name = "stop", Settle = true },
        };

        /// <summary>The jetpack's legs are shorter than the ship's: its keys let go after these frames of a leg.</summary>
        private const int FlyerFrames = 180;

        protected override IEnumerator Check()
        {
            Check(Integrations.IsLoaded("SentisClusters") || ClusterStressCheck.Command("!clusters status").Any(), "SentisClusters is not loaded");
            var focus = TestRunner.Focus ?? "";
            int Arg(string name, int otherwise) => Regex.Match(focus, name + @"=(\d+)") is var m && m.Success ? int.Parse(m.Groups[1].Value) : otherwise;
            Rigs = Math.Max(2, Arg("rigs", 6));
            var detach = Arg("detach", 1) == 1;

            var before = ClusterStressCheck.Exceptions();
            var errorsAt = ClusterStressCheck.LogMark();
            var rigs = new List<Rig>();
            for (var k = 0; k < Rigs; k++)
            {
                var rig = new Rig { Index = k, At = Site.Origin + Site.Up * (Apart * k) };
                rigs.Add(rig);
                if (k == 0)
                {
                    yield return Arrive(rig.At + Site.Forward * 4);
                    rig.Pilot = Me;
                }
                else yield return Join(rig.At + Site.Forward * 4, i => rig.Pilot = i);
                Me = rig.Pilot;
                // the ship of reg_flight_cluster_border: it faces the world's axis the rigs are along (8 km of room)
                rig.Ship = Build("flight-" + k, rig.At, false, new[] {
                    new Part("Cockpit/LargeBlockCockpitSeat", 0, 0, 0),
                    new Part("BatteryBlock/LargeBlockBatteryBlock", 0, -1, 0).With(b => Motion.Charged(b)),
                    new Part("BatteryBlock/LargeBlockBatteryBlock", 1, -1, 0).With(b => Motion.Charged(b)),
                    new Part("BatteryBlock/LargeBlockBatteryBlock", -1, -1, 0).With(b => Motion.Charged(b)),
                    new Part("Gyro/LargeBlockGyro", 0, 1, 0),
                    new Part("Thrust/LargeBlockSmallThrust", 1, 0, 0).Facing(Direction.Backward),
                    new Part("Thrust/LargeBlockSmallThrust", 2, 0, 0).Facing(Direction.Backward),
                    new Part("Thrust/LargeBlockSmallThrust", -1, 0, 0).Facing(Direction.Forward),
                    new Part("Thrust/LargeBlockSmallThrust", -2, 0, 0).Facing(Direction.Forward),
                }, forward: Site.Up, up: -Site.Forward);
                Check(rig.Ship.CubeBlocks.Count == 9, "rig " + k + ": the ship came out with " + rig.Ship.CubeBlocks.Count + " of its 9 blocks");
                rig.Seat = Block<MyCockpit>(rig.Ship);
                yield return Sit(rig.Seat);
                yield return Join(rig.At + Site.Right * 40, i => rig.Flyer = i);
                Me = rig.Flyer;
                FakeClients.SetFree(Me, true);
                Body.PositionComp.SetWorldMatrix(MatrixD.CreateWorld(rig.At + Site.Right * 40, Site.Forward, Site.Up));
            }
            // the astronauts' jetpacks on, held still
            for (var i = 0; i < 60; i++)
            {
                foreach (var rig in rigs)
                {
                    Me = rig.Flyer;
                    Keys(Vector3.Zero, jetpack: true, dampeners: true, face: Site.Forward);
                }
                yield return null;
            }
            foreach (var rig in rigs)
            {
                Me = rig.Flyer;
                Check(Body.JetpackComp?.TurnedOn == true, "rig " + rig.Index + ": the jetpack did not come on");
            }
            var clusters = rigs.Select(r => ClusterId(r.Ship)).ToList();
            Note("rigs " + Rigs + " in clusters " + string.Join(", ", clusters));
            Check(clusters.Distinct().Count() == Rigs, "the rigs are not each in a cluster of their own: " + string.Join(", ", clusters));

            var runs = new List<(string Name, List<double[]> Legs)>();
            var failures = new List<string>();
            var attached = new List<double[]>();
            yield return Fly(rigs, attached, failures, "attached");
            runs.Add(("attached", attached));
            if (detach)
            {
                foreach (var rig in rigs)
                {
                    var at = rig.Ship.PositionComp.GetPosition();
                    var answer = string.Join(" ", ClusterStressCheck.Command($"!clusters detach {at.X:F0} {at.Y:F0} {at.Z:F0}"));
                    Check(answer.Contains("detached"), "rig " + rig.Index + ": its cluster was not detached: " + answer);
                }
                yield return Seconds(1);
                var detached = new List<double[]>();
                yield return Fly(rigs, detached, failures, "detached");
                runs.Add(("detached", detached));
                ClusterStressCheck.Command("!clusters attach all");
                // each rig against itself attached
                for (var l = 0; l < Program.Length; l++)
                {
                    if (Program[l].Settle) continue;
                    for (var part = 0; part < 2; part++)
                        for (var i = 0; i < rigs.Count; i++)
                        {
                            var a = attached[i][l * 2 + part];
                            var d = detached[i][l * 2 + part];
                            if (a < 0.5) continue;
                            if (Math.Abs(d - a) > a * DetachedSpread)
                                failures.Add($"rig {i}, {Program[l].Name}: the {(part == 0 ? "ship" : "astronaut")} {F(d)} detached against {F(a)} attached");
                        }
                }
            }

            var thrown = ClusterStressCheck.Exceptions() - before;
            if (thrown > 0) failures.Add(thrown + " exceptions in the clusters' threads");
            var errors = ClusterStressCheck.LogSince(errorsAt).Where(l => l.Contains("[ERROR]") || l.Contains("[FATAL]"))
                .Where(l => !l.Contains("urlacl") && !l.Contains("SentisAnticheat")).ToList();
            if (errors.Count > 0) failures.Add("errors in the log: " + string.Join(" | ", errors.Take(3).Select(l => l.Length > 300 ? l.Substring(0, 300) : l)));

            var report = string.Join("; ", runs.Select(r => r.Name + ": " + string.Join(", ", Enumerable.Range(0, Program.Length).Where(l => !Program[l].Settle)
                .Select(l => Program[l].Name + " ship " + F(Median(r.Legs.Select(x => x[l * 2]))) + (l == 4 ? "°" : " m") +
                             (l == 4 ? "" : ", astronaut " + F(Median(r.Legs.Select(x => x[l * 2 + 1]))) + " m")))));
            Check(failures.Count == 0, string.Join(" || ", failures.Take(8)) + " ||| " + report);
            Result(Rigs + " rigs in clusters " + string.Join(", ", clusters) + "; medians: " + report + "; exceptions " + thrown);
        }

        /// <summary>
        /// The program flown by all the rigs at once; each rig's legs into <paramref name="legs"/> (two numbers a leg: the
        /// ship's, the astronaut's), what went wrong into <paramref name="failures"/>.
        /// </summary>
        private IEnumerator Fly(List<Rig> rigs, List<double[]> legs, List<string> failures, string run)
        {
            foreach (var rig in rigs) legs.Add(new double[Program.Length * 2]);
            for (var l = 0; l < Program.Length; l++)
            {
                var leg = Program[l];
                var shipFrom = rigs.Select(r => r.Ship.PositionComp.GetPosition()).ToList();
                var shipFacing = rigs.Select(r => r.Ship.WorldMatrix.Forward).ToList();
                var flyerFrom = rigs.Select(r => FakeClients.Character(r.Flyer).PositionComp.GetPosition()).ToList();
                if (leg.Settle)
                {
                    var within = Within(20);
                    while (within() && rigs.Any(r => !Still(r)))
                    {
                        foreach (var rig in rigs) Send(rig, Vector3.Zero, Vector2.Zero, Vector3.Zero);
                        yield return null;
                    }
                    foreach (var rig in rigs.Where(r => !Still(r)))
                        failures.Add($"{run}, rig {rig.Index}, after {Program[l - 1].Name}: not stopped in 20 s: the ship {F(Motion.Speed(rig.Ship))} m/s " +
                                     $"{F(rig.Ship.Physics?.AngularVelocity.Length() ?? 0)} rad/s, the astronaut {F(Motion.Speed(FakeClients.Character(rig.Flyer)))} m/s");
                    continue;
                }
                for (var frame = 0; frame < leg.Frames; frame++)
                {
                    foreach (var rig in rigs) Send(rig, leg.ShipMove, leg.ShipTurn, frame < FlyerFrames ? leg.FlyerMove : Vector3.Zero);
                    if (frame == FlyerFrames - 1 && leg.FlyerMove != Vector3.Zero)
                    {
                        foreach (var rig in rigs) rig.JetThrust = Math.Round(FakeClients.Character(rig.Flyer).JetpackComp?.FinalThrust.Length() ?? 0);
                        Note($"{run} {leg.Name}: the astronauts at the keys' end: " + string.Join("; ", rigs.Select(r =>
                        {
                            var c = FakeClients.Character(r.Flyer);
                            var box = c.PositionComp.WorldAABB;
                            var planet = MyGamePruningStructure.GetClosestPlanet(ref box);
                            return $"rig {r.Index} {F(Motion.Speed(c))} m/s, thrust {F(r.JetThrust)} N, the closest planet {planet?.StorageName ?? "none"} " +
                                   $"{(planet == null ? "" : F(Vector3D.Distance(planet.PositionComp.GetPosition(), box.Center) / 1000) + " km, air " + F(planet.GetAirDensity(box.Center)))}";
                        })));
                    }
                    yield return null;
                }
                for (var i = 0; i < rigs.Count; i++)
                {
                    var ship = rigs[i].Ship;
                    legs[i][l * 2] = leg.ShipTurn != Vector2.Zero
                        ? MathHelper.ToDegrees(Math.Acos(MathHelper.Clamp(Vector3D.Dot(shipFacing[i], ship.WorldMatrix.Forward), -1, 1)))
                        : Vector3D.Distance(ship.PositionComp.GetPosition(), shipFrom[i]);
                    legs[i][l * 2 + 1] = Vector3D.Distance(FakeClients.Character(rigs[i].Flyer).PositionComp.GetPosition(), flyerFrom[i]);
                }
                for (var part = 0; part < 2; part++)
                {
                    if (part == 1 && leg.FlyerMove == Vector3.Zero) continue;
                    var what = part == 0 ? "ship" : "astronaut";
                    var all = legs.Select(x => x[l * 2 + part]).ToList();
                    // the astronauts against those with the jetpack as strong: the game gives it 0.67 to 1 of its force by
                    // the air the closest planet has at the place, worked out once in 100 or 1000 frames
                    foreach (var group in Enumerable.Range(0, rigs.Count).GroupBy(i => part == 0 ? 0 : rigs[i].JetThrust))
                    {
                        var values = group.Select(i => all[i]).ToList();
                        var median = Median(values);
                        if (median < 1) failures.Add($"{run}, {leg.Name}: the {what}s hardly moved: {string.Join(", ", all.Select(v => F(v)))}");
                        else
                            foreach (var i in group)
                                if (Math.Abs(all[i] - median) > median * Spread)
                                    failures.Add($"{run}, {leg.Name}, rig {i}: the {what} {F(all[i])} against the median {F(median)} of the rigs alike ({string.Join(", ", all.Select(v => F(v)))})");
                    }
                }
                Note($"{run} {leg.Name}: ship {string.Join(" ", legs.Select(x => F(x[l * 2])))}; astronaut {string.Join(" ", legs.Select(x => F(x[l * 2 + 1])))}");
            }
        }

        private void Send(Rig rig, Vector3 shipMove, Vector2 shipTurn, Vector3 flyerMove)
        {
            ShipKeys(rig.Seat, shipMove, shipTurn);
            Me = rig.Flyer;
            Keys(flyerMove, jetpack: true, dampeners: true, face: Site.Forward);
        }

        private static bool Still(Rig rig) =>
            Motion.Speed(rig.Ship) < Stopped && (rig.Ship.Physics?.AngularVelocity.Length() ?? 0) < 0.02 && Motion.Speed(FakeClients.Character(rig.Flyer)) < Stopped;

        private static double Median(IEnumerable<double> values)
        {
            var sorted = values.OrderBy(v => v).ToList();
            if (sorted.Count == 0) return 0;
            return sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
        }

        public override void Cleanup()
        {
            try { ClusterStressCheck.Command("!clusters attach all"); }
            catch (Exception) { }
            base.Cleanup();
        }
    }
}
