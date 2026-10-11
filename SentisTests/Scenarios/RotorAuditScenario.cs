using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Havok;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using VRage.Game.ObjectBuilders.Components;
using Sandbox.Game.World;
using SentisTests.Core;
using SentisTests.Game;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// Every rotor, hinge and piston of the world as it is now: is its head held? A probe for a world copied from a server -
    /// it touches nothing. For each one whose head is detached, has no constraint in the world, stands tilted off its axis
    /// or moves against it: where, whose, how built, the head's grid and its physics, watched over a few seconds.
    /// </summary>
    public sealed class RotorAuditScenario : TestScenario
    {
        public override void Cleanup()
        {
            try { FakeClients.RemoveAll(); }
            finally { base.Cleanup(); }
        }

        public const string ScenarioName = "rotor_audit";
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const double MaxTiltDegrees = 3, WatchSeconds = 20;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 60;

        public override IEnumerator Run()
        {
            var constraint = typeof(MyMechanicalConnectionBlockBase).GetProperty("Constraint", Any);
            var stators = MyEntities.GetEntities().OfType<MyCubeGrid>().Where(g => !g.MarkedForClose)
                .SelectMany(g => g.GetFatBlocks().OfType<MyMotorStator>()).ToList();
            var players = MySession.Static.Players.GetOnlinePlayers().Where(p => p.Character != null)
                .Select(p => (p.DisplayName, p.Character.PositionComp.GetPosition())).ToList();
            Note($"{stators.Count} rotors and hinges in the world; players on line: {string.Join(", ", players.Select(p => p.DisplayName))}");

            // a player beside the frames of players' rotors: where nobody is, the physics is not stepped and nothing shows
            var frame = stators.Where(x => x.SlimBlock.BuildLevelRatio < 1 && x.TopBlock != null && !MySession.Static.Players.IdentityIsNpc(x.OwnerId))
                .OrderBy(x => x.CubeGrid.DisplayName).FirstOrDefault();
            if (frame != null)
            {
                var at = frame.PositionComp.GetPosition() + frame.WorldMatrix.Up * 6 + frame.WorldMatrix.Backward * 6;
                FakeClients.RemoveAll();
                FakeClients.Add(1, new FakeClients.NetworkProfile { RttMs = 30 }, i => (at, 0, 0), withCharacters: true);
                Note($"a player put beside '{frame.CustomName}' on '{frame.CubeGrid.DisplayName}'");
                var arrive = WaitForSeconds(3, "the player arrives");
                while (arrive.MoveNext()) yield return arrive.Current;
                players.Add(("fake", at));
            }
            // the safe zones the rotors stand in: their access and whether the head is among what the zone holds and
            // pushes out (a dynamic grid that is not safe there is pushed with its mass * 1000 every frame)
            var contained = typeof(MySafeZone).GetField("m_containedEntities", Any);
            var isSafe = typeof(MySafeZone).GetMethod("IsSafe", Any);
            foreach (var s in stators.Where(x => x.TopBlock != null))
                foreach (var zone in MySessionComponentSafeZones.SafeZones)
                {
                    var head = s.TopBlock.CubeGrid;
                    if (Vector3D.Distance(zone.PositionComp.GetPosition(), s.PositionComp.GetPosition()) > zone.Radius + 50) continue;
                    var holds = (contained?.GetValue(zone) as System.Collections.IEnumerable)?.Cast<long>().Contains(head.EntityId) == true;
                    Note($"ZONE | '{s.CustomName}' on '{s.CubeGrid.DisplayName}': zone {zone.EntityId} {zone.Shape} r={zone.Radius:0} enabled {zone.Enabled}, " +
                         $"players {zone.AccessTypePlayers} ({zone.Players.Count}), factions {zone.AccessTypeFactions} ({zone.Factions.Count}), grids {zone.AccessTypeGrids} ({zone.Entities.Count}); " +
                         $"head grid held by the zone {holds}, safe {isSafe?.Invoke(zone, new object[] { head })}; head grid owners {head.BigOwners.Count}, base owners {s.CubeGrid.BigOwners.Count}, " +
                         $"head motors locked {head.ForceLockMotors}");
                }

            var worst = new Dictionary<MyMotorStator, (double Tilt, double Speed, int NoHinge, string Last)>();
            var started = DateTime.UtcNow;
            while ((DateTime.UtcNow - started).TotalSeconds < WatchSeconds)
            {
                foreach (var s in stators)
                {
                    if (s.MarkedForClose) continue;
                    var top = s.TopBlock;
                    var hk = constraint.GetValue(s) as HkConstraint;
                    var attached = ((Sandbox.ModAPI.Ingame.IMyMechanicalConnectionBlock)s).IsAttached;
                    var tilt = top == null ? 0 : MathHelper.ToDegrees(Math.Acos(MathHelper.Clamp(Math.Abs(Vector3D.Dot(top.WorldMatrix.Up, s.WorldMatrix.Up)), 0, 1)));
                    var speed = top == null ? 0 : ((top.CubeGrid.Physics?.AngularVelocity ?? Vector3.Zero) - (s.CubeGrid.Physics?.AngularVelocity ?? Vector3.Zero)).Length();
                    var welded = typeof(MyMechanicalConnectionBlockBase).GetProperty("m_welded", Any)?.GetValue(s) as bool? ?? false;
                    worst.TryGetValue(s, out var w);
                    var last = top == null ? "no head" :
                        $"{(attached ? "attached" : "DETACHED")}, hinge {(hk == null ? (welded ? "welded" : "NONE") : hk.InWorld ? (hk.Enabled ? "in world" : "DISABLED") : "OUT of world")}, " +
                        $"head grid '{top.CubeGrid.DisplayName}' ({top.CubeGrid.BlocksCount} blocks, physics {(top.CubeGrid.Physics?.Enabled == true ? "on" : "OFF")}, " +
                        $"static {top.CubeGrid.IsStatic}, head built {top.SlimBlock.BuildLevelRatio:0.00})";
                    worst[s] = (Math.Max(w.Tilt, tilt), Math.Max(w.Speed, speed), w.NoHinge + (top != null && (hk == null || !hk.InWorld) && !welded ? 1 : 0), last);
                }
                yield return null;
            }

            var bad = 0;
            foreach (var pair in worst.OrderByDescending(p => p.Value.Tilt))
            {
                var s = pair.Key;
                var (tilt, speed, noHinge, last) = pair.Value;
                var where = s.PositionComp.GetPosition();
                var near = players.Count == 0 ? "" : $", {players.Min(p => Vector3D.Distance(p.Item2, where)):0} m from the nearest player";
                var line = $"{s.BlockDefinition.Id.SubtypeName} '{s.CustomName}' on '{s.CubeGrid.DisplayName}' of {MySession.Static.Players.TryGetIdentity(s.OwnerId)?.DisplayName ?? s.OwnerId.ToString()}" +
                           $" (stator built {s.SlimBlock.BuildLevelRatio:0.00}, grid static {s.CubeGrid.IsStatic}{near}): tilted up to {tilt:0.0}°, turning against it at up to {speed:0.00} rad/s, " +
                           $"frames without a hinge {noHinge}; {last}";
                if (tilt > MaxTiltDegrees || noHinge > 0 || last.Contains("DETACHED") || last.Contains("NONE") || last.Contains("OUT") || last.Contains("DISABLED"))
                {
                    bad++;
                    Note("ROTOR AUDIT BAD | " + line);
                }
                else if (s.SlimBlock.BuildLevelRatio < 1) Note("ROTOR AUDIT frame | " + line);
            }
            Note($"ROTOR AUDIT | {stators.Count} rotors and hinges, {bad} with the head not held");
        }
    }
}
