using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.ModAPI.Interfaces;
using SentisTests.Core;
using SentisTests.Game;
using VRage.Game;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// Bullets inside a cluster of the physics SentisClusters detached. A fixed gatling gun shoots at a heavy armour wall
    /// 150 m ahead, both in one cluster; beside them (out of the line of fire) a pile of grids knocking about can be added,
    /// so that the cluster's world takes time to step. The game moves bullets on the game thread (MyProjectiles), and a
    /// detached world is hidden from the game thread's ray casts while it steps: there bullets may fly through the wall.
    /// Damage to the wall, phase by phase (<see cref="PhaseSeconds"/> s): attached, detached
    /// (<c>!clusters detach</c>), detached with the pile, attached with the pile; a phase under <see cref="MinShare"/> of
    /// the first fails. Not part of the regression: it switches detaching.
    /// </summary>
    public sealed class ProjectileDetachCheck : RegressionCheck
    {
        public const string ScenarioName = "projectile_detach";
        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 600;
        protected override bool NeedsDamage => true;
        private const int PhaseSeconds = 20;
        private const int PileSize = 300;
        private const double MinShare = 0.9;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 10, -15));
            var definition = MyDefinitionManager.Static.GetAllDefinitions().OfType<MyWeaponBlockDefinition>()
                .Where(d => d.Public && d.CubeSize == MyCubeSize.Small && d.Id.TypeId.ToString().EndsWith("SmallGatlingGun")).OrderBy(d => d.Id.SubtypeName).FirstOrDefault();
            SkipUnless(definition != null, "no small gatling gun in this game");
            var weapon = MyDefinitionManager.Static.GetWeaponDefinition(definition.WeaponDefinitionId);
            var magazine = weapon.AmmoMagazinesId.First();
            var station = Build("gun", Site.Origin, true, new List<Part>
            {
                new Part(definition.Id.TypeId + "/" + definition.Id.SubtypeName, 0, 0, 0),
                new Part("BatteryBlock/SmallBlockBatteryBlock", -3, 0, 0).With(b => ((Sandbox.Common.ObjectBuilders.MyObjectBuilder_BatteryBlock)b).CurrentStoredPower = 1f),
            }, MyCubeSize.Small);
            var gun = station.GetCubeBlock(Vector3I.Zero)?.FatBlock;
            Check(gun != null, "the gun was not built");
            yield return Seconds(1);
            Check(Command("!clusters set Detach on").Any(), "SentisClusters did not answer (!clusters set)");

            var pile = new List<MyCubeGrid>();
            var report = new List<string>();
            var rng = new Random(11);
            double first = 0;
            var failures = new List<string>();
            foreach (var (name, detach, heavy) in new[] { ("attached", false, false), ("detached", true, false), ("detached, heavy", true, true), ("attached, heavy", false, true) })
            {
                Command("!clusters attach all");
                var middle = Site.Origin + Site.Right * 200 + Site.Forward * 75;
                if (heavy && pile.Count == 0)
                {
                    var size = Regex.Match(TestRunner.Focus ?? "", @"pile=(\d+)") is var m && m.Success ? int.Parse(m.Groups[1].Value) : PileSize;
                    for (var i = 0; i < size; i++)
                    {
                        var at = middle + new Vector3D(rng.NextDouble() - 0.5, rng.NextDouble() - 0.5, rng.NextDouble() - 0.5) * (30 + size / 3.0);
                        var cube = Build("gun-pile-" + i, at, false, new Part("CubeBlock/LargeHeavyBlockArmorBlock", 0, 0, 0), new Part("CubeBlock/LargeHeavyBlockArmorBlock", 1, 0, 0),
                            new Part("CubeBlock/LargeHeavyBlockArmorBlock", 0, 1, 0), new Part("CubeBlock/LargeHeavyBlockArmorBlock", 1, 1, 0));
                        cube.DestructibleBlocks = false;
                        pile.Add(cube);
                        if (i % 10 == 9) yield return null;
                    }
                    Note("a pile of " + pile.Count + " grids beside the gun");
                }
                if (detach)
                {
                    var at = station.PositionComp.GetPosition();
                    var answer = string.Join(" ", Command($"!clusters detach {at.X:F0} {at.Y:F0} {at.Z:F0}"));
                    Check(answer.Contains("detached"), name + ": " + answer);
                }
                // a fresh wall for every phase, three blocks thick: a magazine broke a single layer through in the first one
                var wall = BuildFor(WorldApi.TestIdentityId(), "gun-wall-" + report.Count, Site.Origin + Site.Forward * 150, Bodies.Wall("CubeBlock/LargeHeavyBlockArmorBlock", 2, 3).ToArray());
                // a full ammo box for the phase
                var box = (Sandbox.Game.MyInventory)gun.GetInventory(0);
                var fits = Math.Min(10, Math.Floor((double)box.ComputeAmountThatFits(magazine)));
                if (fits > 0) box.AddItems((VRage.MyFixedPoint)fits, (MyObjectBuilder_PhysicalObject)VRage.ObjectBuilders.MyObjectBuilderSerializer.CreateNewObject(magazine));
                yield return Seconds(2);
                var ammo = (double)box.GetItemAmount(magazine);
                var whole = Integrity(wall);
                ((Sandbox.ModAPI.IMyTerminalBlock)gun).GetActionWithName("Shoot_On")?.Apply(gun);
                var lastPull = Stopwatch.StartNew();
                var phase = Stopwatch.StartNew();
                while (phase.Elapsed.TotalSeconds < PhaseSeconds)
                {
                    if (heavy && lastPull.Elapsed.TotalSeconds >= 1)
                    {
                        lastPull.Restart();
                        foreach (var g in pile)
                            if (!g.MarkedForClose && g.Physics != null)
                                g.Physics.LinearVelocity = (Vector3)(Vector3D.Normalize(middle - g.PositionComp.GetPosition()) * 8 +
                                                                     new Vector3D(rng.NextDouble() - 0.5, rng.NextDouble() - 0.5, rng.NextDouble() - 0.5) * 6);
                    }
                    yield return null;
                }
                ((Sandbox.ModAPI.IMyTerminalBlock)gun).GetActionWithName("Shoot_Off")?.Apply(gun);
                yield return Seconds(2);
                var spent = ammo - (double)box.GetItemAmount(magazine);
                var damage = whole - Integrity(wall);
                // every phase lasts as long at 60 ticks a second (a magazine is spent whole as the next one goes in: counting
                // magazines is too coarse): the damage of the phase against the first's
                if (first == 0) first = damage;
                var share = first > 0 ? damage / first : 0;
                if (!wall.MarkedForClose) wall.Close();
                var line = name + ": " + F(spent) + " magazines, the wall lost " + F(damage) + " (" + (share * 100).ToString("F0") + "% of the first phase); " + Detached();
                report.Add(line);
                Note(line);
                if (spent <= 0) failures.Add(name + ": the gun spent no ammunition");
                else if (share < MinShare) failures.Add(name + ": " + (share * 100).ToString("F0") + "% of the first phase's damage");
            }
            Command("!clusters attach all");
            foreach (var g in pile) if (!g.MarkedForClose) g.Close();
            Check(failures.Count == 0, string.Join(" | ", failures) + " || " + string.Join(" | ", report));
            Result(string.Join(" | ", report));
        }

        private static double Integrity(MyCubeGrid grid) => grid.MarkedForClose ? 0 : grid.GetBlocks().Sum(b => (double)b.Integrity);

        private static string Detached()
        {
            var m = Regex.Match(string.Join("\n", Command("!clusters status")), @"detached clusters: [^\n]*");
            return m.Success ? m.Value : "no detached cluster";
        }

        private static List<string> Command(string text)
        {
            var answers = new List<string>();
            var commands = (Torch.Commands.CommandManager)Torch.TorchBase.Instance.CurrentSession.Managers.GetManager(typeof(Torch.Commands.CommandManager));
            commands?.HandleCommandFromServer(text, message => answers.Add(message.Message));
            return answers;
        }

        public override void Cleanup()
        {
            try { Command("!clusters attach all"); }
            catch (Exception) { }
            base.Cleanup();
        }
    }
}
