using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces;
using SentisTests.Core;
using VRage.Game.ModAPI;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// Why a bot's drill rig shakes (12.10.2026: Cedar's lift, arm and drops swung at up to 20 m/s, the drills in the air):
    /// the fastest of the rig's grids, a few seconds each, as it is, the rotor stopped, all its pistons and the rotor sharing
    /// their inertia tensor, and the rotor's torque low. Everything set back as it was after. Live, on "База Cedar".
    /// </summary>
    public sealed class RigShakeScenario : TestScenario
    {
        public const string ScenarioName = "rig_shake";
        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 200;

        public override IEnumerator Run()
        {
            var home = MyEntities.GetEntities().OfType<MyCubeGrid>().FirstOrDefault(g => g.DisplayName == "База Cedar");
            Check(home != null, "no grid 'База Cedar'");
            var rig = MyCubeGridGroups.Static.Mechanical.GetGroupNodes(home).Where(g => g != home).ToList();
            Check(rig.Count > 0, "no grids hang off the base");
            var all = MyCubeGridGroups.Static.Mechanical.GetGroupNodes(home);
            var pistons = all.SelectMany(g => g.GetFatBlocks().OfType<IMyPistonBase>()).ToList();
            var rotors = all.SelectMany(g => g.GetFatBlocks().OfType<IMyMotorStator>()).ToList();
            Note($"RIG SHAKE | {rig.Count} grids hang off the base, {rig.Sum(g => g.BlocksCount)} blocks, mass {rig.Sum(g => g.Physics?.Mass ?? 0):0} kg; {pistons.Count} pistons, {rotors.Count} rotors");
            foreach (var r in rotors)
                Note($"RIG SHAKE | rotor {r.CustomName}: {r.TargetVelocityRPM:0.0} rpm, torque {r.Torque:0}, braking {r.BrakingTorque:0}, share {((IMyTerminalBlock)r).GetValueBool("ShareInertiaTensor")}");
            var shares = all.SelectMany(g => g.GetFatBlocks().OfType<IMyMechanicalConnectionBlock>()).ToDictionary(b => b, b => ((IMyTerminalBlock)b).GetValueBool("ShareInertiaTensor"));
            var rpm = rotors.ToDictionary(r => r, r => r.TargetVelocityRPM);
            var torque = rotors.ToDictionary(r => r, r => r.Torque);

            var drills = all.SelectMany(g => g.GetFatBlocks().OfType<IMyShipDrill>()).ToList();
            var lifts = pistons.Where(x => ((IMyCubeBlock)x).CubeGrid == home || x.CustomName.Contains("вверх")).ToList();
            var velocity = pistons.ToDictionary(x => x, x => x.Velocity);
            var impulse = pistons.ToDictionary(x => x, x => (((IMyTerminalBlock)x).GetValueFloat("MaxImpulseAxis"), ((IMyTerminalBlock)x).GetValueFloat("MaxImpulseNonAxis")));
            var drilling = drills.ToDictionary(d => d, d => d.Enabled);
            var arm = rig.OrderByDescending(g => g.BlocksCount).First();
            void Share(bool on) { foreach (var b in shares.Keys) ((IMyTerminalBlock)b).SetValueBool("ShareInertiaTensor", on || shares[b]); }
            void Impulse(bool high)
            {
                foreach (var x in pistons)
                {
                    var t = (IMyTerminalBlock)x;
                    t.SetValueFloat("MaxImpulseAxis", high ? t.GetMaximum<float>("MaxImpulseAxis") : impulse[x].Item1);
                    t.SetValueFloat("MaxImpulseNonAxis", high ? t.GetMaximum<float>("MaxImpulseNonAxis") : impulse[x].Item2);
                }
            }
            Note($"RIG SHAKE | arm grid {arm.BlocksCount} blocks; pistons' impulse limits {impulse.Values.First().Item1:0}/{impulse.Values.First().Item2:0}, the most {((IMyTerminalBlock)pistons[0]).GetMaximum<float>("MaxImpulseAxis"):0}; lifts {string.Join(", ", lifts.Select(x => $"{x.CustomName} {x.CurrentPosition:0.00} m {x.Velocity:0.0000} m/s"))}");

            // (each held every frame: the bot sets its rig again every second)
            var phases = new List<(string Name, Action Hold)>
            {
                ("as it is", () => { }),
                ("the rotor stopped", () => { foreach (var r in rotors) r.TargetVelocityRPM = 0; }),
                ("the lifts stopped", () => { foreach (var x in lifts) x.Velocity = 0; }),
                ("the drills off", () => { foreach (var d in drills) d.Enabled = false; }),
                ("all sharing inertia", () => Share(true)),
                ("the pistons' impulse limits at the most", () => Impulse(true)),
                ("sharing and impulses at the most", () => { Share(true); Impulse(true); }),
            };
            try
            {
                foreach (var phase in phases)
                {
                    // (as it was, then this phase's one change)
                    Share(false); Impulse(false);
                    foreach (var r in rotors) { r.TargetVelocityRPM = rpm[r]; r.Torque = torque[r]; }
                    foreach (var d in drills) d.Enabled = drilling[d];
                    var settle = DateTime.UtcNow.AddSeconds(6);
                    while (DateTime.UtcNow < settle) { phase.Hold(); yield return null; }
                    var top = 0.0;
                    var sum = 0.0;
                    var frames = 0;
                    var fastest = "";
                    // the arm's place against the base, in the base's axes: how far it goes each way, and how often it turns back
                    var seen = new List<VRageMath.Vector3D>();
                    var until = DateTime.UtcNow.AddSeconds(10);
                    while (DateTime.UtcNow < until)
                    {
                        phase.Hold();
                        var grid = rig.Where(g => g.Physics != null).OrderByDescending(g => g.Physics.LinearVelocity.Length()).FirstOrDefault();
                        var speed = grid?.Physics.LinearVelocity.Length() ?? 0;
                        sum += speed;
                        frames++;
                        if (speed > top) { top = speed; fastest = $"{grid.BlocksCount} blocks"; }
                        seen.Add(VRageMath.Vector3D.TransformNormal(arm.PositionComp.WorldAABB.Center - home.PositionComp.GetPosition(), VRageMath.MatrixD.Transpose(home.WorldMatrix)));
                        yield return null;
                    }
                    var mid = seen.Aggregate(VRageMath.Vector3D.Zero, (x, y) => x + y) / seen.Count;
                    string Axis(Func<VRageMath.Vector3D, double> of)
                    {
                        var turns = 0;
                        for (var i = 2; i < seen.Count; i++)
                            if (Math.Sign(of(seen[i]) - of(seen[i - 1])) != Math.Sign(of(seen[i - 1]) - of(seen[i - 2]))) turns++;
                        return $"{seen.Max(of) - seen.Min(of):0.00} m, {turns / 2.0 / 10:0.0}/s";
                    }
                    Note($"RIG SHAKE | {phase.Name}: fastest grid on average {sum / Math.Max(1, frames):0.00} m/s, at the most {top:0.00} m/s ({fastest}); arm swings X {Axis(v => v.X)}, Y {Axis(v => v.Y)}, Z {Axis(v => v.Z)}; " +
                         $"rotor {rotors.Select(r => $"{r.TargetVelocityRPM:0.0} rpm").FirstOrDefault()}, {frames} frames");
                }
            }
            finally
            {
                foreach (var b in shares.Keys) ((IMyTerminalBlock)b).SetValueBool("ShareInertiaTensor", shares[b]);
                foreach (var x in pistons)
                {
                    ((IMyTerminalBlock)x).SetValueFloat("MaxImpulseAxis", impulse[x].Item1);
                    ((IMyTerminalBlock)x).SetValueFloat("MaxImpulseNonAxis", impulse[x].Item2);
                    if (lifts.Contains(x)) x.Velocity = velocity[x];
                }
                foreach (var r in rotors) { r.TargetVelocityRPM = rpm[r]; r.Torque = torque[r]; }
                foreach (var d in drills) d.Enabled = drilling[d];
            }
        }
    }
}
