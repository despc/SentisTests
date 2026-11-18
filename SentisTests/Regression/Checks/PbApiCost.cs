using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities.Blocks;
using SentisTests.Core;
using SentisTests.Game;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// What the game's calls a combat script makes cost the server, in microseconds a call (only when asked: a probe, no
    /// verdict but the numbers). The calls are those of RTAS (a targeting script of the vanilla production, 18.11.2026),
    /// made by a script in a programmable block of a ship of 1200 blocks with cameras, turrets, guns, a cockpit and an LCD,
    /// an enemy grid 1 km ahead: each call many times in one run, the run timed around, the time of an empty loop taken off.
    /// </summary>
    public sealed class PbApiCostCheck : RegressionCheck
    {
        public override string Name => Prefix + "pb_api_cost";
        public override bool OnlyWhenAsked => true;
        public override int TimeoutSeconds => 180;

        private const string Probe = @"
List<IMyTerminalBlock> all = new List<IMyTerminalBlock>();
List<IMyUserControllableGun> guns = new List<IMyUserControllableGun>(), found = new List<IMyUserControllableGun>();
List<IMyCameraBlock> cams = new List<IMyCameraBlock>();
List<IMyLargeTurretBase> turrets = new List<IMyLargeTurretBase>();
List<IMyShipController> seats = new List<IMyShipController>();
List<IMyTextPanel> lcds = new List<IMyTextPanel>();
public Program()
{
    GridTerminalSystem.GetBlocksOfType(guns, g => !(g is IMyLargeTurretBase));
    GridTerminalSystem.GetBlocksOfType(cams);
    GridTerminalSystem.GetBlocksOfType(turrets);
    GridTerminalSystem.GetBlocksOfType(seats);
    GridTerminalSystem.GetBlocksOfType(lcds);
    foreach (var c in cams) c.EnableRaycast = true;
}
public void Main(string a)
{
    var p = a.Split(':');
    var op = p[0];
    int n = int.Parse(p[1]);
    var target = p.Length > 4 ? new Vector3D(double.Parse(p[2]), double.Parse(p[3]), double.Parse(p[4])) : Vector3D.Zero;
    var g = guns[0];
    var me = seats[0];
    var t = turrets[0];
    var lcd = lcds[0];
    double sink = 0;
    for (int i = 0; i < n; i++)
    {
        switch (op)
        {
            case ""noop"": sink++; break;
            case ""types"": GridTerminalSystem.GetBlocksOfType(all); break;
            case ""typesSame"": GridTerminalSystem.GetBlocksOfType(found, b => b.IsSameConstructAs(Me)); break;
            case ""name"": GridTerminalSystem.GetBlockWithName(""(HPR) Main Cockpit""); break;
            case ""group"": GridTerminalSystem.GetBlockGroupWithName(""(HPR) RTAS""); break;
            case ""access"": GridTerminalSystem.CanAccess(g); break;
            case ""same"": sink += g.IsSameConstructAs(Me) ? 1 : 0; break;
            case ""cname"": sink += g.CustomName.Length; break;
            case ""functional"": sink += g.IsFunctional ? 1 : 0; break;
            case ""matrix"": sink += g.WorldMatrix.M11; break;
            case ""detailed"": sink += g.DetailedInfo.Split('\n', ' ').Length; break;
            case ""vel"": sink += me.GetShipVelocities().LinearVelocity.X; break;
            case ""grav"": sink += me.GetNaturalGravity().X; break;
            case ""target"": sink += t.GetTargetedEntity().EntityId; break;
            case ""hastarget"": sink += t.HasTarget ? 1 : 0; break;
            case ""integrity"": sink += g.CubeGrid.GetCubeBlock(g.Position).BuildIntegrity; break;
            case ""setbool"": g.SetValueBool(""OnOff"", true); break;
            case ""prop"": g.GetProperty(""OnOff""); break;
            case ""action"": g.ApplyAction(""OnOff_On""); break;
            case ""canscan"": sink += cams[0].CanScan(target) ? 1 : 0; break;
            case ""raycast"": var c = cams[i % cams.Count]; if (c.CanScan(target)) sink += c.Raycast(target).EntityId; break;
            case ""draw"":
                using (var f = lcd.DrawFrame())
                    for (int s = 0; s < 40; s++)
                        f.Add(new MySprite(SpriteType.TEXTURE, ""SquareSimple"", new Vector2(10 * s, 10), new Vector2(8, 8), Color.Red));
                break;
        }
    }
    Me.CustomData = sink.ToString();
}";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 30, -30));

            // the ship: a 16 x 24 x 3 hull of armour, and on top of it the blocks the script works with, in rows by their sizes
            var parts = new List<Part>();
            for (var x = 0; x < 16; x++)
            for (var hz = 0; hz < 24; hz++)
            for (var y = 0; y < 3; y++)
                parts.Add(new Part("CubeBlock/LargeBlockArmorBlock", x, y, hz));
            var z = 0;
            void Row(string id, int count, Action<VRage.Game.MyObjectBuilder_CubeBlock> setup = null)
            {
                var size = Definition(id).Size;
                for (var i = 0; i < count; i++)
                {
                    var part = new Part(id, i * (size.X + 1), 3, z);
                    parts.Add(setup == null ? part : part.With(setup));
                }
                z += Math.Max(size.Z, Math.Max(size.X, size.Y)) + 1;
            }
            Row("Cockpit/LargeBlockCockpit", 1);
            Row("BatteryBlock/LargeBlockBatteryBlock", 2, b => Motion.Charged(b));
            Row("TextPanel/LargeLCDPanel", 1);
            Row("MyProgrammableBlock/LargeProgrammableBlock", 1, b => ((MyObjectBuilder_MyProgrammableBlock)b).Program = Probe);
            Row("LargeGatlingTurret/", 3);
            Row("SmallMissileLauncher/LargeMissileLauncher", 3);
            Row("CameraBlock/LargeCameraBlock", 4);
            Check(z <= 24, "the blocks do not fit on the hull: " + z + " rows deep");
            var ship = Build("api-ship", Site.Origin, true, parts);
            var enemy = BuildFor(WorldApi.TestIdentityId(), "api-enemy", Site.Origin + Site.Forward * 1000,
                new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0), new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 1, 0).With(b => Motion.Charged(b)));
            var pb = Block<MyProgrammableBlock>(ship);
            yield return Until(() => Scripts.Ready(pb) ? 1 : 0, v => v > 0, "the probe compiled", 30, () => State(pb));
            yield return Seconds(12); // the cameras charge their scan range

            var at = enemy.PositionComp.GetPosition();
            var targetArg = ":" + at.X.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + at.Y.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" +
                            at.Z.ToString(System.Globalization.CultureInfo.InvariantCulture);
            double Run(string op, int count)
            {
                Request(pb, "RunProgramRequest", Scripts.Argument(op + ":" + count + targetArg), Sandbox.ModAPI.Ingame.UpdateType.Terminal); // compiles what it calls
                var best = double.MaxValue;
                for (var attempt = 0; attempt < 5; attempt++)
                {
                    var started = Stopwatch.GetTimestamp();
                    Request(pb, "RunProgramRequest", Scripts.Argument(op + ":" + count + targetArg), Sandbox.ModAPI.Ingame.UpdateType.Terminal);
                    best = Math.Min(best, (Stopwatch.GetTimestamp() - started) * 1e6 / Stopwatch.Frequency);
                }
                return best; // µs of the run, the best of five
            }

            var loop = Run("noop", 1000) / 1000;
            var lines = new List<string>();
            void Cost(string what, string op, int count)
            {
                var us = Run(op, count) / count - loop;
                lines.Add(what + " " + us.ToString("F2") + " µs");
            }
            Cost("GetBlocksOfType<IMyTerminalBlock> (" + ship.BlocksCount + " blocks)", "types", 20);
            Cost("GetBlocksOfType<IMyUserControllableGun>(IsSameConstructAs)", "typesSame", 20);
            Cost("GetBlockWithName", "name", 300);
            Cost("GetBlockGroupWithName", "group", 300);
            Cost("CanAccess", "access", 300);
            Cost("IsSameConstructAs", "same", 1000);
            Cost("CustomName", "cname", 1000);
            Cost("IsFunctional", "functional", 1000);
            Cost("WorldMatrix", "matrix", 1000);
            Cost("DetailedInfo.Split of a gun", "detailed", 300);
            Cost("GetShipVelocities", "vel", 1000);
            Cost("GetNaturalGravity", "grav", 1000);
            Cost("GetTargetedEntity of a turret", "target", 300);
            Cost("HasTarget of a turret", "hastarget", 1000);
            Cost("GetCubeBlock().BuildIntegrity", "integrity", 1000);
            Cost("SetValueBool(OnOff)", "setbool", 300);
            Cost("GetProperty(OnOff)", "prop", 300);
            Cost("ApplyAction(OnOff_On)", "action", 300);
            Cost("camera CanScan(1 km)", "canscan", 300);
            Cost("camera Raycast(1 km)", "raycast", 4);
            Cost("DrawFrame of 40 sprites", "draw", 20);
            Note("an empty loop: " + loop.ToString("F3") + " µs a turn");

            foreach (var line in lines) Note(line);
            Result(string.Join("; ", lines));
        }
    }
}
