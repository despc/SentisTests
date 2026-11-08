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
    /// Focus: <c>bp=path speed=100 spin=2 kill=1 fp=1 gyro=1 step=gone|stop|keep|late pause=20</c> - step: how the
    /// ship's world is stepped around the freeze (gone: everyone off at the pilot's death; stop: the world not stepped
    /// for `pause` s before the freezer may act; keep: an observer 2 km by the ship, its world stepped while frozen;
    /// late: stepped at the freeze, the observer off while frozen). (kill=0: the pilot just leaves the seat and the
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
            // whether the ship's world is stepped around the freeze: gone (everyone off at the pilot's death - the
            // world stops, the freezer freezes at once), stop (the world stopped `pause` seconds before the freezer
            // may act), keep (an observer by the ship beyond the freeze distance: the world stepped while frozen),
            // late (stepped at the freeze, the observer off while it is frozen)
            var step = Arg("step", "gone");
            var pause = int.Parse(Arg("pause", "20"));
            var observed = step == "keep" || step == "late" || step == "keepoff";
            WorldApi.EnsureUnpaused(Name);
            FakeClients.RemoveAll();
            _config.Set("FreezerEnabled", step != "stop");
            var freezePhysics = Arg("fp", "1") != "0";
            var gyros = Arg("gyro", "1") != "0";
            _config.Set("FreezePhysics", freezePhysics);
            _config.Set("FreezeDistanceDynamic", 500);
            _config.Set("FreezeTraceLogs", true);
            _config.Set("FreezeDistanceStatic", 500);
            HarmonyLib.AccessTools.TypeByName("Optimizer.Optimizations.PhysicsCost")?.GetField("PairedStep", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)?.SetValue(null, Arg("paired", "on") != "off");
            Note("paired step " + Arg("paired", "on"));
            Note($"step={step}{(step == "stop" ? " pause " + pause + " s" : "")}; freezer on, physics {(freezePhysics ? "too" : "not (logic only, as prod)")}, 500 m; gyros {(gyros ? "on" : "off")}; speed {speed} m/s, spin {spin} rad/s, the pilot {(kill ? "killed" : "gone")}");

            // open space, far from everything
            var anchor = WorldApi.PlanetAnchor(WorldApi.EntityPrefix + Prefix + "anchor");
            var planet = MyGamePruningStructure.GetClosestPlanet(anchor.Translation);
            Check(planet != null, "no planet");
            var up = Vector3D.Normalize(anchor.Translation - planet.PositionComp.GetPosition());
            var site = planet.PositionComp.GetPosition() + up * (planet.MaximumRadius * 3 + 30000);
            Note("site " + site.ToString("F0") + ", gravity " + Sandbox.Game.GameSystems.MyGravityProviderSystem.CalculateNaturalGravityInPoint(site).Length().ToString("F2"));

            // the observer first (client 0) where there is one, the pilot after it: the pilot leaves as the tail
            var side = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(up));
            if (observed)
            {
                FakeClients.Add(1, Network, i => (site + side * ObserverM, 0, 0), withCharacters: true);
                var observer = Wait(() => FakeClients.HasLiveCharacter(0), "the observer's character", 30);
                while (observer.MoveNext()) yield return observer.Current;
            }
            var pilotIndex = observed ? 1 : 0;
            FakeClients.Add(1, Network, i => (site + up * 30, 0, 0), withCharacters: true);
            var arrive = Wait(() => FakeClients.HasLiveCharacter(pilotIndex), "the pilot's character", 30);
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
            var pilot = FakeClients.Character(pilotIndex);
            var pilotSteamId = FakeClients.PlayerOf(pilotIndex).Id.SteamId;
            FakeClients.SetFree(pilotIndex, true);
            pilot.PositionComp.SetPosition(seat.PositionComp.GetPosition() + seat.WorldMatrix.Backward * 1.5);
            // seated by the server itself (a client's request is checked against the blueprint's owner, the test identity)
            seat.AttachPilot(pilot, -1);
            var seated = Wait(() => seat.Pilot == pilot, "the pilot seated", 10);
            while (seated.MoveNext()) yield return seated.Current;

            // dampeners off, the whole group moving and turning as one
            ((Sandbox.ModAPI.IMyShipController)seat).DampenersOverride = false;
            if (!gyros) foreach (var gyro in group.SelectMany(g => g.GetFatBlocks().OfType<Sandbox.ModAPI.IMyGyro>())) gyro.Enabled = false;
            if (Arg("thrust", "1") == "0") foreach (var thruster in group.SelectMany(g => g.GetFatBlocks().OfType<Sandbox.ModAPI.IMyThrust>())) thruster.Enabled = false;
            // off=Type1,Type2 (or all): those functional blocks of the group switched off before the flight
            var offTypes = Arg("off", "");
            if (offTypes.Length > 0)
            {
                var offList = offTypes.Split(',');
                var switched = 0;
                foreach (var block in group.SelectMany(g => g.GetFatBlocks().OfType<Sandbox.ModAPI.IMyFunctionalBlock>()))
                    if (offList.Contains("all") || offList.Any(t => block.GetType().Name.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        if (block is Sandbox.ModAPI.IMyShipController || block is Sandbox.ModAPI.IMyCockpit) continue;
                        block.Enabled = false;
                        switched++;
                    }
                Note($"switched off: {switched} blocks ({offTypes})");
            }
            if (Arg("joints", "on") == "off") foreach (var joint in stators) ((Sandbox.ModAPI.IMyMotorStator)joint).Enabled = false;
            if (Arg("joints", "on") == "lock") foreach (var joint in stators) ((Sandbox.ModAPI.IMyMotorStator)joint).RotorLock = true;
            yield return WaitForTicks(2);
            var centre = main.Physics.CenterOfMassWorld;
            var linear = (Vector3)(main.WorldMatrix.Forward * speed);
            var angular = (Vector3)(main.WorldMatrix.Up * spin);
            foreach (var g in group)
            {
                var r = (Vector3)(g.Physics.CenterOfMassWorld - centre);
                g.Physics.SetSpeeds(linear + Vector3.Cross(angular, r), angular);
            }
            // the observer flies beside the ship, beyond the freeze distance: the ship stays replicated to it, its world stepped
            if (observed)
            {
                var beside = Vector3D.Normalize(Vector3D.Cross(main.WorldMatrix.Forward, main.WorldMatrix.Up));
                FakeClients.SetPath(0, seconds => main.PositionComp.GetPosition() + beside * ObserverM);
            }
            yield return WaitForSeconds(3, "the ship flies");
            Note("filters flying: " + Filters(group));
            Note($"flying: {Speeds(group)}; dampeners {((Sandbox.ModAPI.IMyShipController)seat).DampenersOverride}; world stepped {Stepped(main)}");

            // the pilot dies in the seat (or leaves)
            if (kill)
            {
                pilot.DoDamage(10000f, MyDamageType.LowPressure, true, attackerId: 0);
                var dead = Wait(() => pilot.IsDead, "the pilot dead", 10);
                while (dead.MoveNext()) yield return dead.Current;
            }
            // the player gone from the place either way: off the server (the observer stays)
            if (observed) FakeClients.RemoveOwnedTail(pilotIndex, new[] { pilotSteamId });
            else FakeClients.RemoveAll(force: true);
            Note($"the pilot {(kill ? "dead" : "gone")}; seat's pilot {seat.Pilot?.DisplayName ?? "none"}; {Speeds(group)}; world stepped {Stepped(main)}");

            if (Arg("freezer", "1") == "0")
            {
                // no freezer at all: the ship left flying, its pilot dead, watched for 20 s
                _config.Set("FreezerEnabled", false);
                var watch = new List<string>();
                for (var t = 0; t < 1200; t++)
                {
                    yield return null;
                    // readd=1: the group's bodies taken out of the world and put back at 3 s (Havok's contacts made anew)
                    if (t == 180 && Arg("readd", "0") == "1")
                    {
                        foreach (var g in Group()) if (g.Physics != null) g.Physics.Enabled = false;
                        yield return null;
                        foreach (var g in Group()) if (g.Physics != null) g.Physics.Enabled = true;
                        watch.Add("bodies put back");
                    }
                    if (t % 120 == 0) watch.Add($"{t / 60} s: {main.Physics?.LinearVelocity.Length():0.0} m/s {main.Physics?.AngularVelocity.Length():0.00} rad/s");
                }
                Note("no freezer, the ship after its pilot's death: " + string.Join("; ", watch) + $"; joints {Joints(stators, baseline)}");
                Note("FREEZER MOVING PILOT RESULT | no freezer: watched only");
                yield break;
            }
            if (step == "stop")
            {
                // the world stands still, the freezer still off: `pause` seconds of a world not stepped before the freeze
                var stopped = Wait(() => !IsStepped(main), "the ship's world no longer stepped", 30);
                while (stopped.MoveNext()) yield return stopped.Current;
                var stoppedAt = Group().ToDictionary(g => g, g => g.PositionComp.GetPosition());
                yield return WaitForSeconds(pause, "the world not stepped, the freezer off");
                Note($"{pause} s not stepped: {Speeds(Group())}; moved {string.Join(", ", Group().Select(g => g.DisplayName + " " + Vector3D.Distance(stoppedAt.TryGetValue(g, out var a) ? a : g.PositionComp.GetPosition(), g.PositionComp.GetPosition()).ToString("F1") + " m"))}; " +
                     $"world stepped {Stepped(main)}; joints {Joints(stators, baseline)}");
                _config.Set("FreezerEnabled", true);
            }

            // the freezer's turn
            var frozen = Wait(() => Group().All(g => FreezerState.IsFrozen(g)), "the group frozen", 200);
            while (frozen.MoveNext()) yield return frozen.Current;
            group = Group();
            Note($"frozen: physics frozen {group.Count(FreezerState.IsPhysicsFrozen)}/{group.Count}, fixed bodies {group.Count(FreezerState.HasFixedBody)}; {Speeds(group)}; world stepped {Stepped(main)}; joints {Joints(stators, baseline)}; near: {Near(main)}");
            Note("filters frozen: " + Filters(group));
            if (Arg("drift", "0") == "1")
            {
                // drift=1: the connectors' own bodies pushed away after the freeze (as if the freezer had left them moving): the thaw must put them back
                var pushed = 0;
                foreach (var g in group)
                    foreach (var connector in g.GetFatBlocks().OfType<Sandbox.Game.Entities.Cube.MyShipConnector>())
                        if ((connector.Physics as Sandbox.Engine.Physics.MyPhysicsBody)?.RigidBody is Havok.HkRigidBody rigid && !rigid.IsFixed)
                        {
                            rigid.LinearVelocity = (VRageMath.Vector3)g.WorldMatrix.Forward * 100f;
                            pushed++;
                        }
                Note($"drift: {pushed} connector bodies pushed at 100 m/s");
            }
            Note($"active bodies at the freeze: {string.Join(", ", group.Select(g => g.DisplayName.Replace("fiskunt_SG_92_", "") + " " + ((g.Physics as Sandbox.Engine.Physics.MyPhysicsBody)?.RigidBody?.IsActive)))}");
            var frozenAt = group.ToDictionary(g => g, g => g.PositionComp.GetPosition());
            if (step == "late")
            {
                // stepped at the freeze; the observer leaves: no longer stepped while frozen
                FakeClients.RemoveAll(force: true);
                var stopped = Wait(() => !IsStepped(main), "the ship's world no longer stepped", 30);
                while (stopped.MoveNext()) yield return stopped.Current;
                Note($"the observer gone while frozen: world stepped {Stepped(main)}");
            }
            var hold = double.Parse(Arg("hold", FrozenSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)), System.Globalization.CultureInfo.InvariantCulture);
            yield return WaitForSeconds(hold, "frozen");
            Note($"active bodies before the thaw: {string.Join(", ", Group().Select(g => g.DisplayName.Replace("fiskunt_SG_92_", "") + " " + ((g.Physics as Sandbox.Engine.Physics.MyPhysicsBody)?.RigidBody?.IsActive)))}");
            Note($"frozen {FrozenSeconds} s, near: {Near(main)}");
            Note($"frozen {FrozenSeconds} s, world stepped {Stepped(main)}: moved {string.Join(", ", group.Select(g => g.DisplayName + " " + Vector3D.Distance(frozenAt[g], g.PositionComp.GetPosition()).ToString("F1") + " m"))}; joints {Joints(stators, baseline)}");

            if (step == "keepoff")
            {
                // stepped all the time frozen; the observer gone only now, right before a player comes
                FakeClients.RemoveAll(force: true);
                yield return WaitForTicks(2);
                Note($"the observer gone right before the thaw: world stepped {Stepped(main)}");
            }
            if (step == "thawfirst")
            {
                // thawed with nobody near, the world not stepped: the freezer off; a player comes only after it
                _config.Set("FreezerEnabled", false);
                var unfrozen = Wait(() => Group().All(g => !FreezerState.IsFrozen(g) && !FreezerState.IsPhysicsFrozen(g)), "the group thawed with nobody near", 30);
                while (unfrozen.MoveNext()) yield return unfrozen.Current;
                Note($"thawed with nobody near: world stepped {Stepped(main)}; {Speeds(Group())}");
                yield return WaitForSeconds(3, "thawed, the world not stepped");
                Note($"3 s thawed, not stepped: world stepped {Stepped(main)}; {Speeds(Group())}");
            }
            if (Arg("readdfrozen", "0") == "1")
            {
                // the frozen group's bodies taken out of the world and put back right before the thaw
                foreach (var g in Group()) if (g.Physics != null) g.Physics.Enabled = false;
                yield return null;
                foreach (var g in Group()) if (g.Physics != null) g.Physics.Enabled = true;
                yield return null;
                Note($"the frozen bodies put back before the thaw: fixed {string.Join(", ", Group().Select(g => (g.Physics as Sandbox.Engine.Physics.MyPhysicsBody)?.RigidBody?.IsFixed))}");
            }
            // a player comes: the thaw
            var by = main.PositionComp.GetPosition() + main.WorldMatrix.Up * 40;
            FakeClients.Add(1, Network, i => (by, 0, 0), withCharacters: true);
            var thawed = Wait(() => Group().All(g => !FreezerState.IsFrozen(g) && !FreezerState.IsPhysicsFrozen(g)), "the group thawed", 60);
            while (thawed.MoveNext()) yield return thawed.Current;
            var worst = 0.0;
            var curve = new List<string>();
            for (var t = 0; t < 180; t++)
            {
                yield return null;
                if (t < 60) foreach (var g in group.Where(g => g.Physics != null && !g.MarkedForClose))
                    worst = Math.Max(worst, Math.Max(g.Physics.LinearVelocity.Length(), g.Physics.AngularVelocity.Length() * 10));
                if (t == 0) Note("filters thawed: " + Filters(group));
                if (t == 0 || t == 1 || t == 2 || t == 4 || t == 9 || t == 19 || t == 39 || t == 59 || t == 119 || t == 179)
                {
                    var bodies = group.Where(g => g.Physics != null && !g.MarkedForClose).ToList();
                    var mass = bodies.Sum(g => (double)g.Physics.Mass);
                    var momentum = bodies.Aggregate(VRageMath.Vector3D.Zero, (a, g) => a + (VRageMath.Vector3D)g.Physics.LinearVelocity * g.Physics.Mass);
                    curve.Add($"+{t + 1}: {main.Physics?.LinearVelocity.Length():0.0} m/s {main.Physics?.AngularVelocity.Length():0.00} rad/s, the group's mass centre {(mass > 0 ? momentum.Length() / mass : 0):0.0} m/s");
                }
            }
            Note("after the thaw, the ship by ticks: " + string.Join("; ", curve));
            if (Arg("readd", "0") == "1")
            {
                // the bodies taken out of the world and put back after a quiet thaw: Havok's contacts made anew
                foreach (var g in Group()) if (g.Physics != null) g.Physics.Enabled = false;
                yield return null;
                foreach (var g in Group()) if (g.Physics != null) g.Physics.Enabled = true;
                var after = new List<string>();
                for (var t = 0; t < 120; t++)
                {
                    yield return null;
                    if (t == 0 || t == 4 || t == 9 || t == 29 || t == 59 || t == 119) after.Add($"+{t + 1}: {main.Physics?.LinearVelocity.Length():0.0} m/s {main.Physics?.AngularVelocity.Length():0.00} rad/s");
                }
                Note("the bodies put back after the thaw: " + string.Join("; ", after));
            }
            var off = stators.Where(s => s.TopBlock == null || !((Sandbox.ModAPI.IMyMechanicalConnectionBlock)s).IsAttached).ToList();
            var pulled = stators.Where(s => s.TopBlock != null && baseline.ContainsKey(s) && Math.Abs(Gap(s) - baseline[s]) > 0.5).ToList();
            Note($"thawed: world stepped {Stepped(main)}; {Speeds(Group())}; the fastest in the first second {worst:F1} (m/s, or rad/s x10); joints {Joints(stators, baseline)}");
            Check(off.Count == 0, $"{off.Count} of {stators.Count} heads off at the thaw: {string.Join(", ", off.Select(s => s.CustomName + " on " + s.CubeGrid.DisplayName))}");
            Check(pulled.Count == 0, $"{pulled.Count} joints pulled apart: {string.Join(", ", pulled.Select(s => s.CustomName + " " + (Gap(s) - baseline[s]).ToString("F2") + " m"))}");
            Check(worst < 30, $"a grid thrown at the thaw: {worst:F1}");
            Note("FREEZER MOVING PILOT RESULT | the heads on, the joints whole, nothing thrown");
        }

        /// <summary>How far the observer flies beside the ship: beyond the freeze distance (500 m), within the sync distance.</summary>
        private const double ObserverM = 2000;

        private static readonly MethodInfo IsClusterActive = typeof(Sandbox.Engine.Physics.MyPhysics).GetMethod("IsClusterActive",
            BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(int), typeof(int) }, null);

        /// <summary>Whether the game steps the grid's Havok world now (MyPhysics.IsClusterActive of its cluster).</summary>
        private static string Stepped(MyCubeGrid grid)
        {
            var world = (grid?.Physics as Sandbox.Engine.Physics.MyPhysicsBody)?.HavokWorld;
            if (world == null) return "no world";
            var physics = Sandbox.Game.World.MySession.Static.GetComponent<Sandbox.Engine.Physics.MyPhysics>();
            foreach (var cluster in Sandbox.Engine.Physics.MyPhysics.Clusters.GetClusters())
                if (ReferenceEquals(cluster.UserData, world))
                    return ((bool)IsClusterActive.Invoke(physics, new object[] { cluster.ClusterId, world.CharacterRigidBodies.Count })).ToString();
            return "no cluster";
        }

        private static bool IsStepped(MyCubeGrid grid) => Stepped(grid) == "True";

        /// <summary>The distance of a stator's head from the stator.</summary>
        private static double Gap(MyMotorStator s) => s.TopBlock == null ? double.NaN : Vector3D.Distance(s.PositionComp.GetPosition(), s.TopBlock.PositionComp.GetPosition());

        private static string Joints(List<MyMotorStator> stators, Dictionary<MyMotorStator, double> baseline) =>
            string.Join("; ", stators.Select(s => s.CustomName + (s.TopBlock == null ? " OFF" : " " + (Gap(s) - baseline[s]).ToString("+0.00;-0.00") + " m")));

        /// <summary>Characters (a corpse too) and loose bags within 40 m of the grid, with their speeds.</summary>
        private static string Near(MyCubeGrid grid)
        {
            var at = grid.PositionComp.GetPosition();
            var near = MyEntities.GetEntities().Where(e => !(e is MyCubeGrid) && e.Physics != null && !e.MarkedForClose && Vector3D.Distance(e.PositionComp.GetPosition(), at) < 40)
                .Select(e => $"{e.GetType().Name} {e.DisplayName} {(e is Sandbox.Game.Entities.Character.MyCharacter c && c.IsDead ? "dead " : "")}{e.Physics.LinearVelocity.Length():0.0} m/s {Vector3D.Distance(e.PositionComp.GetPosition(), at):0.0} m off").ToList();
            return near.Count == 0 ? "nothing near" : string.Join(", ", near);
        }

        /// <summary>Each grid's body collision filter: layer, system group, subsystem, the subsystem it does not collide with.</summary>
        private static string Filters(List<MyCubeGrid> grids) =>
            string.Join(", ", grids.Where(g => (g.Physics as Sandbox.Engine.Physics.MyPhysicsBody)?.RigidBody != null).Select(g =>
            {
                var info = ((Sandbox.Engine.Physics.MyPhysicsBody)g.Physics).RigidBody.GetCollisionFilterInfo();
                return $"{g.DisplayName.Replace("fiskunt_SG_92_", "")} L{Havok.HkGroupFilter.GetLayerFromFilterInfo(info)} G{Havok.HkGroupFilter.GetSystemGroupFromFilterInfo(info)} " +
                       $"S{Havok.HkGroupFilter.GetSubSystemIdFromFilterInfo(info)}/{Havok.HkGroupFilter.getSubSystemDontCollideWithFromFilterInfo(info)}";
            }));

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
