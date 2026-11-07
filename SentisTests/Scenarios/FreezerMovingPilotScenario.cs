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
using VRage.Game;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// Prod 07.11.2026: a ship with subgrids (rotors and hinges) flying with its dampeners off, its pilot dead in the seat
    /// (hunger, cold, no air), was frozen by SentisOptimisations' freezer and lost its subgrids at the thaw: the heads
    /// 5-9 m off their joints, thrown at 870 m/s and 200 rad/s, "Head taken off otherwise"; the grids still at 104 m/s
    /// ten ticks after the thaw. The ship (bp= the blueprint, the operator's "Истреб Автопушки" by default) is spawned in
    /// open space, a fake pilot seated, the dampeners off, the whole group set moving and turning (each grid the same
    /// motion: the linear speed plus the turn about the ship's centre); the pilot killed. The freezer (on, physics too,
    /// 500 m) freezes the group with nobody left by it; after <see cref="FrozenSeconds"/> a player comes to it and it
    /// thaws. Noted at the freeze and after the thaw: every joint (its head on, how far from its stator compared with the
    /// spawn), the grids' speeds; the check fails on a head off or a joint pulled apart, or a grid thrown.
    /// Focus: <c>bp=path speed=100 spin=2 kill=1 fp=1 gyro=1</c> (kill=0: the pilot just leaves the seat and the
    /// server; fp=0: the freezer's FreezePhysics off - logic only, as prod has it; gyro=0: the gyroscopes off, the spin kept).
    /// </summary>
    public sealed class FreezerMovingPilotScenario : TestScenario
    {
        public const string ScenarioName = "freezer_moving_pilot";
        private const string Prefix = "movpilot-";
        private const string DefaultBlueprint = @"C:\Users\user\AppData\Roaming\SpaceEngineers\Blueprints\local\Истреб Автопушки\bp.sbc";
        private const double FrozenSeconds = 20;
        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };

        private readonly ConfigOverride _config = new ConfigOverride();

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 420;

        private static string Arg(string name, string fallback)
        {
            var m = System.Text.RegularExpressions.Regex.Match(TestRunner.Focus ?? "", name + @"=(\S+)");
            return m.Success ? m.Groups[1].Value : fallback;
        }

        public override IEnumerator Run()
        {
            var path = Arg("bp", DefaultBlueprint);
            var speed = double.Parse(Arg("speed", "100"), System.Globalization.CultureInfo.InvariantCulture);
            var spin = double.Parse(Arg("spin", "2"), System.Globalization.CultureInfo.InvariantCulture);
            var kill = Arg("kill", "1") != "0";
            WorldApi.EnsureUnpaused(Name);
            FakeClients.RemoveAll();
            _config.Set("FreezerEnabled", true);
            var freezePhysics = Arg("fp", "1") != "0";
            var gyros = Arg("gyro", "1") != "0";
            _config.Set("FreezePhysics", freezePhysics);
            _config.Set("FreezeDistanceDynamic", 500);
            _config.Set("FreezeTraceLogs", true);
            _config.Set("FreezeDistanceStatic", 500);
            Note($"freezer on, physics {(freezePhysics ? "too" : "not (logic only, as prod)")}, 500 m; gyros {(gyros ? "on" : "off")}; speed {speed} m/s, spin {spin} rad/s, the pilot {(kill ? "killed" : "gone")}");

            // open space, far from everything
            var anchor = WorldApi.PlanetAnchor(WorldApi.EntityPrefix + Prefix + "anchor");
            var planet = MyGamePruningStructure.GetClosestPlanet(anchor.Translation);
            Check(planet != null, "no planet");
            var up = Vector3D.Normalize(anchor.Translation - planet.PositionComp.GetPosition());
            var site = planet.PositionComp.GetPosition() + up * (planet.MaximumRadius * 3 + 30000);
            Note("site " + site.ToString("F0") + ", gravity " + Sandbox.Game.GameSystems.MyGravityProviderSystem.CalculateNaturalGravityInPoint(site).Length().ToString("F2"));

            FakeClients.Add(1, Network, i => (site + up * 30, 0, 0), withCharacters: true);
            var arrive = Wait(() => FakeClients.HasLiveCharacter(0), "the pilot's character", 30);
            while (arrive.MoveNext()) yield return arrive.Current;

            var obs = WorldApi.LoadBlueprintFile(path, WorldApi.EntityPrefix + Prefix + "ship");
            var shift = site - (Vector3D)obs[0].PositionAndOrientation.Value.Position;
            foreach (var ob in obs)
            {
                var po = ob.PositionAndOrientation.Value;
                po.Position = (Vector3D)po.Position + shift;
                ob.PositionAndOrientation = po;
                ob.LinearVelocity = default;
                ob.AngularVelocity = default;
                ob.CreatePhysics = true;
            }
            var grids = obs.Select(WorldApi.SpawnGrid).ToList();
            foreach (var g in grids) Track(g);
            yield return WaitForSeconds(3, "the ship settles");
            List<MyCubeGrid> Group() => MyCubeGridGroups.Static.Physical.GetGroup(grids.First(g => !g.MarkedForClose))?.Nodes.Select(n => n.NodeData).Where(g => !g.MarkedForClose).ToList() ?? new List<MyCubeGrid>();
            var group = Group();
            var main = group.OrderByDescending(g => g.BlocksCount).First();
            var stators = group.SelectMany(g => g.GetFatBlocks().OfType<MyMotorStator>()).ToList();
            Note($"spawned: {string.Join(", ", group.Select(g => g.DisplayName + " " + g.BlocksCount))}; joints {stators.Count}");
            var baseline = stators.ToDictionary(s => s, Gap);

            // the pilot into the seat
            var seat = main.GetFatBlocks().OfType<MyCockpit>().FirstOrDefault() ?? group.SelectMany(g => g.GetFatBlocks().OfType<MyCockpit>()).FirstOrDefault();
            Check(seat != null, "no cockpit on the ship");
            var pilot = FakeClients.Character(0);
            FakeClients.SetFree(0, true);
            pilot.PositionComp.SetPosition(seat.PositionComp.GetPosition() + seat.WorldMatrix.Backward * 1.5);
            // seated by the server itself (a client's request is checked against the blueprint's owner, the test identity)
            seat.AttachPilot(pilot, -1);
            var seated = Wait(() => seat.Pilot == pilot, "the pilot seated", 10);
            while (seated.MoveNext()) yield return seated.Current;

            // dampeners off, the whole group moving and turning as one
            ((Sandbox.ModAPI.IMyShipController)seat).DampenersOverride = false;
            if (!gyros) foreach (var gyro in group.SelectMany(g => g.GetFatBlocks().OfType<Sandbox.ModAPI.IMyGyro>())) gyro.Enabled = false;
            yield return WaitForTicks(2);
            var centre = main.Physics.CenterOfMassWorld;
            var linear = (Vector3)(main.WorldMatrix.Forward * speed);
            var angular = (Vector3)(main.WorldMatrix.Up * spin);
            foreach (var g in group)
            {
                var r = (Vector3)(g.Physics.CenterOfMassWorld - centre);
                g.Physics.SetSpeeds(linear + Vector3.Cross(angular, r), angular);
            }
            yield return WaitForSeconds(3, "the ship flies");
            Note($"flying: {Speeds(group)}; dampeners {((Sandbox.ModAPI.IMyShipController)seat).DampenersOverride}");

            // the pilot dies in the seat (or leaves)
            if (kill)
            {
                pilot.DoDamage(10000f, MyDamageType.LowPressure, true, attackerId: 0);
                var dead = Wait(() => pilot.IsDead, "the pilot dead", 10);
                while (dead.MoveNext()) yield return dead.Current;
            }
            // the player gone from the place either way: off the server
            FakeClients.RemoveAll(force: true);
            Note($"the pilot {(kill ? "dead" : "gone")}; seat's pilot {seat.Pilot?.DisplayName ?? "none"}; {Speeds(group)}");

            // the freezer's turn
            var frozen = Wait(() => Group().All(g => FreezerState.IsFrozen(g)), "the group frozen", 200);
            while (frozen.MoveNext()) yield return frozen.Current;
            group = Group();
            Note($"frozen: physics frozen {group.Count(FreezerState.IsPhysicsFrozen)}/{group.Count}, fixed bodies {group.Count(FreezerState.HasFixedBody)}; {Speeds(group)}; joints {Joints(stators, baseline)}");
            var frozenAt = group.ToDictionary(g => g, g => g.PositionComp.GetPosition());
            yield return WaitForSeconds(FrozenSeconds, "frozen");
            Note($"frozen {FrozenSeconds} s: moved {string.Join(", ", group.Select(g => g.DisplayName + " " + Vector3D.Distance(frozenAt[g], g.PositionComp.GetPosition()).ToString("F1") + " m"))}; joints {Joints(stators, baseline)}");

            // a player comes: the thaw
            var by = main.PositionComp.GetPosition() + main.WorldMatrix.Up * 40;
            FakeClients.Add(1, Network, i => (by, 0, 0), withCharacters: true);
            var thawed = Wait(() => Group().All(g => !FreezerState.IsFrozen(g) && !FreezerState.IsPhysicsFrozen(g)), "the group thawed", 60);
            while (thawed.MoveNext()) yield return thawed.Current;
            var worst = 0.0;
            for (var t = 0; t < 60; t++)
            {
                yield return null;
                foreach (var g in group.Where(g => g.Physics != null && !g.MarkedForClose))
                    worst = Math.Max(worst, Math.Max(g.Physics.LinearVelocity.Length(), g.Physics.AngularVelocity.Length() * 10));
            }
            yield return WaitForSeconds(3, "after the thaw");
            var off = stators.Where(s => s.TopBlock == null || !((Sandbox.ModAPI.IMyMechanicalConnectionBlock)s).IsAttached).ToList();
            var pulled = stators.Where(s => s.TopBlock != null && baseline.ContainsKey(s) && Math.Abs(Gap(s) - baseline[s]) > 0.5).ToList();
            Note($"thawed: {Speeds(Group())}; the fastest in the first second {worst:F1} (m/s, or rad/s x10); joints {Joints(stators, baseline)}");
            Check(off.Count == 0, $"{off.Count} of {stators.Count} heads off at the thaw: {string.Join(", ", off.Select(s => s.CustomName + " on " + s.CubeGrid.DisplayName))}");
            Check(pulled.Count == 0, $"{pulled.Count} joints pulled apart: {string.Join(", ", pulled.Select(s => s.CustomName + " " + (Gap(s) - baseline[s]).ToString("F2") + " m"))}");
            Check(worst < 30, $"a grid thrown at the thaw: {worst:F1}");
            Note("FREEZER MOVING PILOT RESULT | the heads on, the joints whole, nothing thrown");
        }

        /// <summary>The distance of a stator's head from the stator.</summary>
        private static double Gap(MyMotorStator s) => s.TopBlock == null ? double.NaN : Vector3D.Distance(s.PositionComp.GetPosition(), s.TopBlock.PositionComp.GetPosition());

        private static string Joints(List<MyMotorStator> stators, Dictionary<MyMotorStator, double> baseline) =>
            string.Join("; ", stators.Select(s => s.CustomName + (s.TopBlock == null ? " OFF" : " " + (Gap(s) - baseline[s]).ToString("+0.00;-0.00") + " m")));

        private static string Speeds(List<MyCubeGrid> grids) =>
            string.Join(", ", grids.Where(g => g.Physics != null).Select(g => $"{g.DisplayName} {g.Physics.LinearVelocity.Length():F1} m/s {g.Physics.AngularVelocity.Length():F2} rad/s"));

        public override void Cleanup()
        {
            try
            {
                FakeClients.RemoveAll(force: true);
                _config.Restore();
            }
            finally { base.Cleanup(); }
        }
    }
}
