using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using SentisTests.Core;
using SentisTests.Game;
using SentisTests.Scenarios;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// FixShip on a player's ship with its subgrids (only when asked; the blueprint is a player's, not in the repository:
    /// <c>pb_enigma.sbc</c> in the instance folder, else skipped). "[STARFIRE] - Enigma_BG" (vanilla production, 18.11.2026:
    /// players said it blew up when fixed) stands in space, the test player in its main cockpit, and is fixed as the
    /// cockpit's action does it (SentisGameplayImprovements' FixShipLogic.DoFixShip). Before and 10 s after: its grids, the
    /// heads of its rotors, pistons and hinges attached, its blocks' integrity, the grids' speeds. Nothing of it may be
    /// broken, detached or flying apart.
    /// </summary>
    public sealed class FixShipEnigmaCheck : RegressionCheck
    {
        public override string Name => Prefix + "fixship_enigma";
        public override bool OnlyWhenAsked => true;
        public override int TimeoutSeconds => 240;
        protected override bool NeedsDamage => true;

        private const string Seat = "[Starfire] Main Cockpit/Seat Locked";

        private sealed class Snapshot
        {
            public int Grids, Blocks, Heads, HeadsAttached;
            public double Integrity, MaxIntegrity, MaxSpeed, MaxSpin;
            public List<string> Detached = new List<string>();

            public override string ToString() =>
                Grids + " grids, " + Blocks + " blocks, integrity " + (100 * Integrity / Math.Max(1, MaxIntegrity)).ToString("F3") + "%, heads attached " + HeadsAttached + " of " + Heads +
                ", fastest grid " + MaxSpeed.ToString("F2") + " m/s, " + MaxSpin.ToString("F2") + " rad/s" + (Detached.Count > 0 ? "; detached: " + string.Join(", ", Detached.Take(12)) : "");
        }

        private static Snapshot Take(IEnumerable<MyCubeGrid> grids)
        {
            var s = new Snapshot();
            foreach (var grid in grids.Where(g => !g.Closed && !g.MarkedForClose))
            {
                s.Grids++;
                s.Blocks += grid.BlocksCount;
                foreach (var block in grid.GetBlocks())
                {
                    s.Integrity += block.Integrity;
                    s.MaxIntegrity += block.MaxIntegrity;
                }
                if (grid.Physics != null)
                {
                    s.MaxSpeed = Math.Max(s.MaxSpeed, grid.Physics.LinearVelocity.Length());
                    s.MaxSpin = Math.Max(s.MaxSpin, grid.Physics.AngularVelocity.Length());
                }
                foreach (var joint in grid.GetFatBlocks().OfType<MyMechanicalConnectionBlockBase>())
                {
                    s.Heads++;
                    if (joint.TopGrid != null) s.HeadsAttached++;
                    else s.Detached.Add(joint.CustomName + " (" + joint.BlockDefinition.Id.SubtypeName + ")");
                }
            }
            return s;
        }

        protected override IEnumerator Check()
        {
            var file = Path.Combine(Path.GetDirectoryName(ConfigOverride.RestoreFile) ?? "", "pb_enigma.sbc");
            SkipUnless(File.Exists(file), "no " + file);
            var fix = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("SentisGameplayImprovements.FixShipLogic", false)).FirstOrDefault(t => t != null)
                ?.GetMethod("DoFixShip");
            SkipUnless(fix != null, "no SentisGameplayImprovements' FixShipLogic.DoFixShip");
            yield return Arrive(Site.At(0, 60, -120));

            var obs = WorldApi.LoadBlueprintFile(file, WorldApi.EntityPrefix + "fixship-enigma");
            foreach (var block in obs.SelectMany(g => g.CubeBlocks))
            {
                block.Owner = Identity;
                block.BuiltBy = Identity;
            }
            var shift = Site.Origin - (Vector3D)obs[0].PositionAndOrientation.Value.Position;
            foreach (var ob in obs)
            {
                var po = ob.PositionAndOrientation.Value;
                po.Position = (Vector3D)po.Position + shift;
                ob.PositionAndOrientation = po;
                ob.LinearVelocity = default(VRage.SerializableVector3);
                ob.AngularVelocity = default(VRage.SerializableVector3);
            }
            var grids = obs.Select(WorldApi.SpawnGrid).ToList();
            foreach (var grid in grids) Track(grid);
            yield return Seconds(8); // the heads attach, the physics settles
            var seat = grids.SelectMany(g => g.GetFatBlocks().OfType<MyShipController>()).FirstOrDefault(c => c.CustomName.ToString() == Seat);
            SkipUnless(seat != null, "no '" + Seat + "' on the ship");
            var sit = Sit(seat);
            while (sit.MoveNext()) yield return sit.Current;
            yield return Seconds(2);

            var results = new List<string>();
            var fails = new List<string>();
            var current = MyCubeGridGroups.Static.Physical.GetGroup(seat.CubeGrid)?.Nodes.Select(n => n.NodeData).ToList() ?? new List<MyCubeGrid> { seat.CubeGrid };
            // the phases: (name, the ship's speed, its turn, the joints moving)
            var phases = new[]
            {
                ("at rest", 0.0, 0.0, false),
                ("moving 40 m/s, turning 0.1 rad/s", 40.0, 0.1, false),
                ("the joints moving (rotors and hinges turning, pistons going)", 0.0, 0.0, true),
                ("the joints moving, the ship at 80 m/s turning 0.2 rad/s", 80.0, 0.2, true),
            };
            var jointsOn = false;
            foreach (var (phase, speed, turn, joints) in phases)
            {
                seat = current.SelectMany(g => g.GetFatBlocks().OfType<MyShipController>()).FirstOrDefault(c => c.CustomName.ToString() == Seat) ?? seat;
                // the dampeners off: the speed is kept through the fix
                ((Sandbox.ModAPI.Ingame.IMyShipController)seat).DampenersOverride = speed <= 0;
                if (joints && !jointsOn)
                {
                    jointsOn = true;
                    // the turret controllers hold their rotors still without a target: off, the rotors are the test's
                    foreach (var controller in current.SelectMany(g => g.GetFatBlocks()).OfType<Sandbox.ModAPI.IMyFunctionalBlock>().Where(b => b.BlockDefinition.TypeIdString.EndsWith("TurretControlBlock"))) controller.Enabled = false;
                    foreach (var joint in current.SelectMany(g => g.GetFatBlocks()))
                    {
                        if (joint is Sandbox.ModAPI.IMyMotorStator stator)
                        {
                            stator.Enabled = true;
                            stator.TargetVelocityRPM = stator.BlockDefinition.SubtypeName.Contains("Hinge") ? 3f : 10f;
                        }
                        else if (joint is Sandbox.ModAPI.IMyPistonBase piston)
                        {
                            piston.Enabled = true;
                            piston.Velocity = piston.CurrentPosition > (piston.MaxLimit + piston.MinLimit) / 2 ? -1f : 1f;
                        }
                    }
                }
                // the turn as the pilot makes it: the gyros overridden to yaw (unturned, they stop any turn the test gives)
                foreach (var gyro in current.SelectMany(g => g.GetFatBlocks()).OfType<Sandbox.ModAPI.IMyGyro>())
                {
                    gyro.GyroOverride = turn > 0;
                    gyro.Yaw = (float)turn;
                }
                if (speed > 0 || turn > 0)
                {
                    // the whole ship as one: the same speed for every grid, the turn about the main grid's centre
                    var mainGrid = current.OrderByDescending(g => g.BlocksCount).First();
                    var pivot = mainGrid.Physics.CenterOfMassWorld;
                    var spin = (Vector3)(mainGrid.WorldMatrix.Up * turn);
                    foreach (var grid in current.Where(g => g.Physics != null))
                    {
                        grid.Physics.AngularVelocity = spin;
                        grid.Physics.LinearVelocity = (Vector3)(Site.Forward * speed) + Vector3.Cross(spin, (Vector3)(grid.Physics.CenterOfMassWorld - pivot));
                    }
                }
                yield return Seconds(3);
                seat = current.SelectMany(g => g.GetFatBlocks().OfType<MyShipController>()).FirstOrDefault(c => c.CustomName.ToString() == Seat) ?? seat;
                if (joints)
                    Note(phase + ": the rotors " + string.Join(", ", current.SelectMany(g => g.GetFatBlocks()).OfType<Sandbox.ModAPI.IMyMotorStator>()
                        .Select(r => r.TargetVelocityRPM.ToString("F0") + " rpm at " + MathHelper.ToDegrees(r.Angle).ToString("F0") + "°")));
                var group = current;
                var before = Take(group);
                Note(phase + ", before FixShip: " + before);
                var centre = seat.CubeGrid.PositionComp.WorldAABB.Center;
                fix.Invoke(null, new object[] { seat.CubeGrid.EntityId, Identity });
                var samples = new List<string>();
                var mainName = seat.CubeGrid.DisplayName;
                // the ship made again: its main grid (by its name), and every grid joined to it by heads
                List<MyCubeGrid> Fixed()
                {
                    var newMain = MyEntities.GetEntities().OfType<MyCubeGrid>().FirstOrDefault(g => !g.Closed && !g.MarkedForClose && !g.IsPreview && !group.Contains(g) &&
                                                                                                     g.DisplayName == mainName && Vector3D.Distance(g.PositionComp.WorldAABB.Center, centre) < 3000);
                    if (newMain == null) return new List<MyCubeGrid>();
                    return MyCubeGridGroups.Static.Physical.GetGroup(newMain)?.Nodes.Select(n => n.NodeData).ToList() ?? new List<MyCubeGrid> { newMain };
                }
                for (var s = 1; s <= 10; s++)
                {
                    yield return Seconds(1);
                    var now = Take(Fixed());
                    samples.Add(s + "s: " + now.Grids + " grids, heads " + now.HeadsAttached + "/" + now.Heads + ", integrity " +
                                (100 * now.Integrity / Math.Max(1, now.MaxIntegrity)).ToString("F3") + "%, fastest " + now.MaxSpeed.ToString("F1") + " m/s " + now.MaxSpin.ToString("F2") + " rad/s");
                }
                var fixedGrids = Fixed();
                foreach (var grid in fixedGrids) Track(grid);
                var after = Take(fixedGrids);
                Note(phase + ", FixShip a second at a time: " + string.Join("; ", samples));
                Note(phase + ", after FixShip: " + after);
                var broken = fixedGrids.SelectMany(g => g.GetBlocks()).Where(b => b.Integrity < b.MaxIntegrity - 0.01f).ToList();
                Note(phase + ", damaged blocks after FixShip: " + broken.Count + (broken.Count > 0 ? " - " + string.Join(", ", broken.GroupBy(b => b.BlockDefinition.Id.SubtypeName)
                    .OrderByDescending(g => g.Count()).Take(10).Select(g => g.Key + " x" + g.Count())) : ""));
                if (after.Blocks < before.Blocks - 2) fails.Add(phase + ": blocks " + before.Blocks + " -> " + after.Blocks);
                if (after.HeadsAttached < before.HeadsAttached) fails.Add(phase + ": heads " + before.HeadsAttached + " -> " + after.HeadsAttached + " of " + after.Heads);
                if (after.Integrity < before.Integrity * 0.999) fails.Add(phase + ": integrity " + (100 * before.Integrity / Math.Max(1, before.MaxIntegrity)).ToString("F3") + "% -> " +
                                                                    (100 * after.Integrity / Math.Max(1, after.MaxIntegrity)).ToString("F3") + "%");
                if (after.MaxSpin > before.MaxSpin + 1 || after.MaxSpeed > before.MaxSpeed + 15) fails.Add(phase + ": thrown about, " + after.MaxSpeed.ToString("F1") + " m/s, " + after.MaxSpin.ToString("F2") + " rad/s");
                results.Add(phase + " " + after.Grids + " grids, heads " + after.HeadsAttached + "/" + after.Heads);
                current = fixedGrids;
            }
            Check(fails.Count == 0, string.Join("; ", fails));
            Result("FixShip remade the ship whole, " + string.Join("; ", results) + ", nothing damaged or thrown");
        }
    }
}
