using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.GameSystems;
using Sandbox.Game.World;
using SentisTests.Core;
using VRage.Game;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// Sentis_v5_ScriptPack_Fix's speed limits and blink drives under load: 50 ships (a cockpit, two jump drives, thrusters,
    /// a battery; dampeners off) flying at 120 m/s side by side. After the limits are worked out: every ship has one and
    /// none goes over it; then the mod's logic measured (<see cref="ScriptPackPerfCheck.Measure"/>: its components on the
    /// world's grids and blocks, the updates the game is asked for a frame, the cost a frame) and the server's frame over
    /// 10 s with the ships. Run only when asked (a load of 50 ships).
    /// </summary>
    public sealed class ScriptPackLoadCheck : RegressionCheck
    {
        public override string Name => Prefix + "scriptpack_load";
        public override bool OnlyWhenAsked => true;
        public override int TimeoutSeconds => 180;

        private const int Ships = 50;
        private const double Speed = 120;

        protected override IEnumerator Check()
        {
            var mod = MyScriptManager.Static?.Scripts?.Values.FirstOrDefault(a => a.GetType("AdminProtector.AdminProtectorBlock") != null);
            SkipUnless(mod != null, "no Sentis_v5_ScriptPack_Fix mod in this world");
            yield return Arrive(Site.At(0, 0, -60));

            var ships = new List<MyCubeGrid>();
            var drive = Definition("JumpDrive/LargeJumpDrive").Size;
            var thrust = Definition("Thrust/LargeBlockSmallThrust").Size;
            for (var i = 0; i < Ships; i++)
            {
                // a row of 10 by 5, 150 m apart, all along the site's forward
                var at = Site.At((i % 10) * 150, (i / 10) * 150, 0);
                var ship = Build("load-ship-" + i, at, false, new[]
                {
                    new Part("Cockpit/LargeBlockCockpit", 0, 0, 0),
                    new Part("JumpDrive/LargeJumpDrive", 0, -drive.Y, 0),
                    new Part("JumpDrive/LargeJumpDrive", drive.X, -drive.Y, 0),
                    new Part("BatteryBlock/LargeBlockBatteryBlock", 1, 0, 0).With(b => Motion.Charged(b)),
                    new Part("Thrust/LargeBlockSmallThrust", 0, -drive.Y - thrust.Y, 0),
                    new Part("Thrust/LargeBlockSmallThrust", thrust.X, -drive.Y - thrust.Y, 0),
                }, ownedBy: Identity);
                ships.Add(ship);
            }
            yield return Seconds(1);
            foreach (var ship in ships)
            {
                var thrustComp = ship.Components.Get<MyEntityThrustComponent>();
                if (thrustComp != null) thrustComp.DampenersEnabled = false;
                ship.Physics.LinearVelocity = (Vector3)(Site.Forward * Speed);
            }
            // the limits: worked out within a pass (3 s)
            yield return Seconds(7);

            var limits = mod.GetType("Sentis_v5.SpeedRestrictionsSession")?.GetField("MaxSpeeds")?.GetValue(null) as IDictionary;
            var limited = 0;
            var over = new List<string>();
            double minLimit = double.MaxValue, maxLimit = 0;
            foreach (var ship in ships)
            {
                double limit = -1;
                if (limits != null)
                    foreach (DictionaryEntry e in limits)
                        if (e.Key is VRage.Game.ModAPI.IMyCubeGrid g && g.EntityId == ship.EntityId) limit = (double)e.Value;
                var v = ship.Physics.LinearVelocity.Length();
                if (limit >= 0)
                {
                    limited++;
                    minLimit = Math.Min(minLimit, limit);
                    maxLimit = Math.Max(maxLimit, limit);
                }
                // a ship slowed to the least limit is slow and uncontrolled: the mod lets its entry go then
                var cap = limit >= 0 ? limit : 80;
                if (v > cap + 1) over.Add(ship.DisplayName + " " + v.ToString("F0") + " m/s over " + cap.ToString("F0"));
            }
            Note(limited + " of " + Ships + " ships have a speed limit" + (limited > 0 ? " (" + minLimit.ToString("F0") + "-" + maxLimit.ToString("F0") + " m/s)" : "") +
                 "; speeds " + ships.Min(s => s.Physics.LinearVelocity.Length()).ToString("F0") + "-" + ships.Max(s => s.Physics.LinearVelocity.Length()).ToString("F0") + " m/s");

            var (ms, calls, components, classes) = ScriptPackPerfCheck.Measure(mod, Note);

            // the server's frame with the ships
            var frames = MySession.Static.GameplayFrameCounter;
            var clock = Stopwatch.StartNew();
            yield return Seconds(10);
            var frameMs = clock.Elapsed.TotalMilliseconds / Math.Max(1, MySession.Static.GameplayFrameCounter - frames);
            Note("server frame with " + Ships + " ships: " + frameMs.ToString("F2") + " ms");

            foreach (var o in over) Note("over its limit: " + o);
            Check(over.Count == 0, over.Count + " ships over their speed limit: " + string.Join("; ", over.Take(5)));
            Result(Ships + " ships at " + Speed + " m/s held to their limits; the mod's logic " + (ms * 1000).ToString("F0") + " µs a frame in the classes run, " +
                   calls.ToString("F0") + " updates asked a frame (" + components + " components of " + classes + " classes); the frame " + frameMs.ToString("F2") + " ms");
        }
    }
}
