using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.GameSystems;
using SentisTests.Core;
using SentisTests.Game;
using VRage.Game;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// Thrusters under load: <see cref="Ships"/> ships, each an armour cube of <see cref="CoreSize"/>
    /// with a thruster lattice on all six faces (<see cref="ThrusterStep"/> apart) and batteries inside.
    /// Two modes:
    ///  - ion, in space: the dampeners are on and every <see cref="KickSeconds"/> each ship is given
    ///    <see cref="KickMps"/> in a random direction, so the thrusters fight it back down;
    ///  - atmospheric, over the planet: the ships hover on their dampeners in gravity, and the same
    ///    kicks push them around.
    /// Measured: <see cref="IdleSeconds"/> with the thrusters off, then <see cref="RunSeconds"/> of
    /// work; dotTrace attaches on PROFILE WINDOW START.
    /// </summary>
    public sealed class ThrustPerfScenario : TestScenario
    {
        public const string IonScenarioName = "thrust_ion";
        public const string AtmoScenarioName = "thrust_atmo";
        private const string Prefix = "thr-";
        private const int Ships = 6;
        private const int CoreSize = 11;
        private const int ThrusterStep = 1;
        private const double ShipSpacingM = 600;
        private const double SpaceHeightM = 150000;
        private const double PlanetHeightM = 300;
        private const double SettleSeconds = 10;
        private const double IdleSeconds = 10;
        private const double RunSeconds = 90;
        private const double ProfileAfterSeconds = 20;
        private const double LogEverySeconds = 20;
        private const double KickSeconds = 4;
        private const double KickMps = 25;
        private const int Players = 2;

        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };

        private readonly bool _atmospheric;
        private readonly Random _rng = new Random(20260920);
        private readonly List<MyCubeGrid> _ships = new List<MyCubeGrid>();

        public ThrustPerfScenario(bool atmospheric) => _atmospheric = atmospheric;

        public override string Name => _atmospheric ? AtmoScenarioName : IonScenarioName;
        public override int TimeoutSeconds => (int)(RunSeconds + 300);

        private string ThrusterSubtype => _atmospheric ? "LargeBlockSmallAtmosphericThrust" : "LargeBlockSmallThrust";
        private int Batteries => 60;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            var anchorM = WorldApi.LoadAuthoredGroup(WheelPerfScenario.ResourceName, WorldApi.EntityPrefix + Prefix + "anchor")[0]
                .PositionAndOrientation.Value.GetMatrix();
            var planet = MyGamePruningStructure.GetClosestPlanet(anchorM.Translation);
            Check(planet != null, "no planet");
            var centre = planet.PositionComp.GetPosition();
            var up = Vector3D.Normalize(anchorM.Translation - centre);
            var east = Vector3D.Normalize(anchorM.Forward - up * Vector3D.Dot(anchorM.Forward, up));
            var first = _atmospheric
                ? anchorM.Translation + up * PlanetHeightM
                : centre + up * ((anchorM.Translation - centre).Length() + SpaceHeightM);

            for (var i = 0; i < Ships; i++)
            {
                _ships.Add(BuildShip(i, first + east * (ShipSpacingM * i), up));
                yield return null;
            }

            var watch = _ships[0].PositionComp.WorldAABB.Center + east * (ShipSpacingM * Ships);
            FakeClients.Add(Players, Network, p => (watch + east * (10 * p), 0, 0), withCharacters: true);
            var settle = WaitForSeconds(SettleSeconds, "ships settle");
            while (settle.MoveNext()) yield return settle.Current;

            var thrusters = _ships.Sum(s => WorldApi.FindFunctionals<MyThrust>(s).Count);
            var gravity = Sandbox.Game.GameSystems.MyGravityProviderSystem.CalculateNaturalGravityInPoint(_ships[0].PositionComp.GetPosition()).Length();
            Note(Ships + " ships, " + _ships[0].CubeBlocks.Count + " blocks each, " + thrusters + " " + ThrusterSubtype +
                 " thrusters in all, gravity " + gravity.ToString("F2") + " m/s2, " + Batteries + " batteries per ship: " +
                 WorldApi.DescribePower(_ships[0]));

            // Thrusters off: the cost of the ships alone.
            foreach (var thrust in _ships.SelectMany(WorldApi.FindFunctionals<MyThrust>)) thrust.Enabled = false;
            var off = WaitForSeconds(IdleSeconds, "thrusters off");
            while (off.MoveNext()) yield return off.Current;
            TickMetrics.Take();
            FrameProbe.Take();
            var idle = DateTime.UtcNow;
            while ((DateTime.UtcNow - idle).TotalSeconds < IdleSeconds) yield return null;
            var idleProbe = FrameProbe.Take();
            TickMetrics.Take();

            foreach (var thrust in _ships.SelectMany(WorldApi.FindFunctionals<MyThrust>)) thrust.Enabled = true;
            foreach (var ship in _ships) Dampeners(ship, true);
            var started = DateTime.UtcNow;
            var lastLog = started;
            var lastKick = DateTime.MinValue;
            var windowStarted = DateTime.MinValue;
            while ((DateTime.UtcNow - started).TotalSeconds < RunSeconds)
            {
                var now = DateTime.UtcNow;
                if ((now - lastKick).TotalSeconds >= KickSeconds)
                {
                    lastKick = now;
                    foreach (var ship in _ships)
                        if (ship.Physics != null)
                            ship.Physics.LinearVelocity = RandomDirection() * KickMps;
                }
                var elapsed = (now - started).TotalSeconds;
                if (windowStarted == DateTime.MinValue && elapsed >= ProfileAfterSeconds)
                {
                    windowStarted = now;
                    TickMetrics.Take();
                    FrameProbe.Take();
                    Note("PROFILE WINDOW START: " + thrusters + " thrusters working");
                }
                if ((now - lastLog).TotalSeconds >= LogEverySeconds)
                {
                    lastLog = now;
                    Note("thrusting " + elapsed.ToString("F0") + "s: speeds " +
                         string.Join("/", _ships.Select(s => (s.Physics?.LinearVelocity.Length() ?? 0).ToString("F0"))) + " m/s, thrusters working " +
                         _ships.Sum(s => WorldApi.FindFunctionals<MyThrust>(s).Count(t => t.IsWorking)) + ", power " + WorldApi.DescribePower(_ships[0]));
                }
                yield return null;
            }
            var metrics = TickMetrics.Take();
            var probe = FrameProbe.Take();
            Note((_atmospheric ? "ATMO" : "ION") + " THRUST RESULT | " + Ships + " ships, " + thrusters + " thrusters | idle, thrusters off: " +
                 Summary(idleProbe) + " | thrusting: " + Summary(probe) + " | " + metrics.Format() + " | " + probe);
            Check(_ships.All(s => !s.Closed), "a ship was destroyed");
        }

        private static void Dampeners(MyCubeGrid ship, bool on)
        {
            var thrust = ship.Components.Get<MyEntityThrustComponent>();
            if (thrust != null) thrust.DampenersEnabled = on;
        }

        /// <summary>An armour cube with batteries inside and thrusters over all six faces.</summary>
        private MyCubeGrid BuildShip(int index, Vector3D position, Vector3D up)
        {
            var blocks = new List<BlockSpec>();
            var battery = 0;
            for (var x = 0; x < CoreSize; x++)
            for (var y = 0; y < CoreSize; y++)
            for (var z = 0; z < CoreSize; z++)
            {
                // Batteries fill the middle layer, the rest is armour.
                var middle = x == CoreSize / 2 && y % 2 == 0 && z % 2 == 0;
                if (middle && battery < Batteries)
                {
                    blocks.Add(new BlockSpec("LargeBlockBatteryBlock", new Vector3I(x, y, z)));
                    battery++;
                }
                else blocks.Add(new BlockSpec("LargeBlockArmorBlock", new Vector3I(x, y, z)));
            }

            // Thrusters on every face: their Min sits on the face, so the body grows outwards.
            foreach (var axis in new[] { 0, 1, 2 })
            foreach (var side in new[] { -1, 1 })
            {
                var forward = Base6Directions.GetDirection(Axis(axis) * side);
                for (var a = 0; a < CoreSize; a += ThrusterStep)
                for (var b = 0; b < CoreSize; b += ThrusterStep)
                {
                    var position3 = new int[3];
                    position3[axis] = side > 0 ? CoreSize : -1;
                    position3[(axis + 1) % 3] = a;
                    position3[(axis + 2) % 3] = b;
                    blocks.Add(new BlockSpec(ThrusterSubtype, new Vector3I(position3[0], position3[1], position3[2]), (int)forward));
                }
            }
            blocks.Add(new BlockSpec("LargeBlockCockpitSeat", new Vector3I(CoreSize / 2, CoreSize, CoreSize / 2)));
            blocks.Add(new BlockSpec("LargeBlockGyro", new Vector3I(0, CoreSize, 0)));

            var name = WorldApi.EntityPrefix + Prefix + (_atmospheric ? "atmo-" : "ion-") + index;
            var ob = WorldApi.GridOb(name, MyCubeSize.Large, false, position, blocks, Vector3.Forward, (Vector3)up);
            var ship = WorldApi.SpawnGrid(ob);
            Track(ship);
            WorldApi.ChargeBatteries(ship);
            return ship;
        }

        private static Vector3I Axis(int axis) => axis == 0 ? Vector3I.UnitX : axis == 1 ? Vector3I.UnitY : Vector3I.UnitZ;

        private Vector3D RandomDirection()
        {
            while (true)
            {
                var v = new Vector3D(_rng.NextDouble() * 2 - 1, _rng.NextDouble() * 2 - 1, _rng.NextDouble() * 2 - 1);
                var length = v.Length();
                if (length > 0.1 && length <= 1) return v / length;
            }
        }

        private static string Summary(string probe)
        {
            string Pick(string name)
            {
                var m = System.Text.RegularExpressions.Regex.Match(probe, System.Text.RegularExpressions.Regex.Escape(name) + @"=\d+ms/\d+calls\([\d.]+ms each, ([\d.]+)ms/frame");
                return m.Success ? m.Groups[1].Value : "?";
            }
            var sim = System.Text.RegularExpressions.Regex.Match(probe, @"sim-work frames=\d+ avg=([\d.]+)ms p50=[\d.]+ p95=[\d.]+ p99=([\d.]+)");
            return "frame avg " + (sim.Success ? sim.Groups[1].Value : "?") + " ms, p99 " + (sim.Success ? sim.Groups[2].Value : "?") +
                   ", physics " + Pick("physics") + ", thrust.update " + Pick("thrust.update") + ", thrust.recompute " + Pick("thrust.recompute") +
                   ", power.distributor " + Pick("power.distributor") + ", sink.setRequired " + Pick("sink.setRequired");
        }

        public override void Cleanup()
        {
            try { FakeClients.RemoveAll(); }
            finally { base.Cleanup(); }
        }

        public override void CleanupLeftovers()
        {
            try
            {
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
