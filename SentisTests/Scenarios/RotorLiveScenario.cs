using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Havok;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.GameSystems;
using Sandbox.Game.World;
using SentisTests.Core;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// Watches, for <see cref="WatchSeconds"/>, what the server has of the rotors and hinges near the players on line, while
    /// a player places them by hand: the server's own state at the moment the player sees a head go wrong. Touches nothing.
    /// Every rotor within <see cref="RadiusM"/> of a real player is followed from the moment it appears; once a second a
    /// line for each one whose state changed, and a line at once whenever its head tilts off the axis, the hinge's pivots
    /// part, the head turns or flies against the stator, or a safe zone takes it in or lets it go. At the end a summary
    /// per rotor. The players' own speeds are written too: a player thrown by a head shows there.
    /// </summary>
    public sealed class RotorLiveScenario : TestScenario
    {
        public const string ScenarioName = "rotor_live";
        private const double WatchSeconds = 1200, RadiusM = 300;
        private const double TiltDegrees = 3, GapM = 0.1, TurnRadS = 2, FlyMps = 1;
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => (int)WatchSeconds + 60;

        private sealed class Followed
        {
            public string Label;
            public MyMechanicalConnectionBlockBase Stator;
            public double MaxTilt, MaxGap, MaxTurn, MaxFly;
            public int Bad;
            public string Last = "";
        }

        public override IEnumerator Run()
        {
            var constraint = typeof(MyMechanicalConnectionBlockBase).GetProperty("Constraint", Any);
            var contained = typeof(MySafeZone).GetField("m_containedEntities", Any);
            var followed = new Dictionary<long, Followed>();
            var started = DateTime.UtcNow;
            var nextSecond = 0.0;
            var playerSpeed = new Dictionary<string, double>();
            Note($"watching the rotors within {RadiusM:0} m of the players on line for {WatchSeconds:0} s");
            while ((DateTime.UtcNow - started).TotalSeconds < WatchSeconds)
            {
                var seconds = (DateTime.UtcNow - started).TotalSeconds;
                var players = MySession.Static.Players.GetOnlinePlayers().Where(p => p.IsRealPlayer && p.Character != null && !p.Character.IsDead).ToList();
                var everySecond = seconds >= nextSecond;
                if (everySecond)
                {
                    nextSecond += 1;
                    foreach (var grid in MyEntities.GetEntities().OfType<MyCubeGrid>())
                    {
                        if (grid.MarkedForClose || !players.Any(p => Vector3D.Distance(p.Character.PositionComp.GetPosition(), grid.PositionComp.GetPosition()) < RadiusM)) continue;
                        foreach (var stator in grid.GetFatBlocks().OfType<MyMechanicalConnectionBlockBase>())
                            if (!followed.ContainsKey(stator.EntityId))
                            {
                                var f = new Followed { Label = $"{stator.BlockDefinition.Id.SubtypeName} '{stator.CustomName}' on '{grid.DisplayName}' ({grid.EntityId})", Stator = stator };
                                followed[stator.EntityId] = f;
                                Note($"{seconds:0.0} s: FOLLOW {f.Label}: grid {(grid.IsStatic ? "static" : "dynamic")} of {grid.BlocksCount} blocks, owners {grid.BigOwners.Count}, stator built {stator.SlimBlock.BuildLevelRatio:0.00}");
                            }
                    }
                    foreach (var p in players)
                    {
                        var v = p.Character.Physics?.LinearVelocity.Length() ?? 0;
                        playerSpeed.TryGetValue(p.DisplayName, out var was);
                        if (v > 15 && v > was + 5) Note($"{seconds:0.0} s: PLAYER {p.DisplayName} at {v:0.0} m/s");
                        playerSpeed[p.DisplayName] = v;
                    }
                }

                foreach (var f in followed.Values)
                {
                    var s = f.Stator;
                    if (s.MarkedForClose || s.Closed) continue;
                    var top = s.TopBlock;
                    var hk = constraint.GetValue(s) as HkConstraint;
                    var attached = ((Sandbox.ModAPI.Ingame.IMyMechanicalConnectionBlock)s).IsAttached;
                    double tilt = 0, gap = 0, turn = 0, fly = 0;
                    var zones = "";
                    if (top != null)
                    {
                        tilt = MathHelper.ToDegrees(Math.Acos(MathHelper.Clamp(Vector3D.Dot(top.WorldMatrix.Up, s.WorldMatrix.Up), -1, 1)));
                        turn = ((top.CubeGrid.Physics?.AngularVelocity ?? Vector3.Zero) - (s.CubeGrid.Physics?.AngularVelocity ?? Vector3.Zero)).Length();
                        fly = ((top.CubeGrid.Physics?.LinearVelocity ?? Vector3.Zero) - (s.CubeGrid.Physics?.LinearVelocity ?? Vector3.Zero)).Length();
                        if (hk != null && hk.InWorld)
                        {
                            hk.GetPivotsInWorld(out var a, out var b);
                            gap = (a - b).Length();
                        }
                        foreach (var zone in MySessionComponentSafeZones.SafeZones)
                        {
                            var held = (contained?.GetValue(zone) as IEnumerable)?.Cast<long>().ToList();
                            if (held == null) continue;
                            if (held.Contains(top.CubeGrid.EntityId)) zones += $" head in zone {zone.EntityId}";
                            if (held.Contains(s.CubeGrid.EntityId)) zones += $" stator grid in zone {zone.EntityId}";
                        }
                    }
                    f.MaxTilt = Math.Max(f.MaxTilt, tilt);
                    f.MaxGap = Math.Max(f.MaxGap, gap);
                    f.MaxTurn = Math.Max(f.MaxTurn, turn);
                    f.MaxFly = Math.Max(f.MaxFly, fly);
                    var bad = top != null && (tilt > TiltDegrees || gap > GapM || turn > TurnRadS || fly > FlyMps || !attached || hk == null || !hk.InWorld);
                    var state = top == null ? "no head" :
                        $"{(attached ? "attached" : "DETACHED")}, hinge {(hk == null ? "NONE" : hk.InWorld ? "in world" : "OUT")}, head physics {(top.CubeGrid.Physics?.Enabled == true ? "on" : "OFF")}," +
                        $" head built {top.SlimBlock.BuildLevelRatio:0.00}, head grid owners {top.CubeGrid.BigOwners.Count}{zones}";
                    if (bad) f.Bad++;
                    if (bad || (everySecond && state != f.Last))
                        Note($"{seconds:0.00} s: {(bad ? "BAD " : "")}{f.Label}: {state}; tilted {tilt:0.0}°, pivots {gap:0.000} m apart, turning {turn:0.00} rad/s, flying {fly:0.00} m/s");
                    f.Last = state;
                }
                yield return null;
            }
            foreach (var f in followed.Values)
                Note($"ROTOR LIVE | {f.Label}: frames not held {f.Bad}; tilted up to {f.MaxTilt:0.0}°, pivots up to {f.MaxGap:0.000} m apart, turning up to {f.MaxTurn:0.00} rad/s, " +
                     $"flying up to {f.MaxFly:0.00} m/s against the stator");
            Note($"ROTOR LIVE | {followed.Count} rotors and hinges followed, {followed.Values.Count(f => f.Bad > 0)} with frames not held");
        }
    }
}
