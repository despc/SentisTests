using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Havok;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.World;
using SentisTests.Core;
using VRage.Game.Entity;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// Reads, without touching anything, the doors near the real players: the door's state, its grid's body (welded
    /// to another by a connector or not), each leaf's body and collision filter against the grid's, the leaves'
    /// constraints, and the safe zones around. Five looks, a second apart. For a door that misbehaves on the server
    /// (14.10.2026: a door on a grid locked by a connector to a static grid in a safe zone shows open and stops the
    /// player, or stays open and does not answer).
    /// </summary>
    public sealed class DoorProbeScenario : TestScenario
    {
        public const string ScenarioName = "door_probe";
        private const double Radius = 300;
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 30;

        public override IEnumerator Run()
        {
            for (var look = 0; look < 5; look++)
            {
                var players = MySession.Static.Players.GetOnlinePlayers().Where(p => p.Character != null && !p.DisplayName.StartsWith("[BOT]")).ToList();
                Check(players.Count > 0, "no real player online");
                foreach (var player in players)
                {
                    var at = player.Character.PositionComp.GetPosition();
                    var sphere = new BoundingSphereD(at, Radius);
                    foreach (var door in MyEntities.GetEntitiesInSphere(ref sphere).OfType<MyDoor>())
                        Note($"DOOR {look} | {Describe(door, at)}");
                    if (look == 0)
                        foreach (var zone in MyEntities.GetEntitiesInSphere(ref sphere).OfType<MySafeZone>())
                            Note($"ZONE | {zone.EntityId} enabled={zone.Enabled} shape={zone.Shape} radius={zone.Radius:0.0} at {(zone.PositionComp.GetPosition() - at).Length():0.0} m, " +
                                 $"actions={zone.AllowedActions}, access={zone.AccessTypeGrids}");
                }
                var wait = WaitForSeconds(1, "the next look");
                while (wait.MoveNext()) yield return wait.Current;
            }
        }

        private static string Describe(MyDoor door, Vector3D at)
        {
            var grid = door.CubeGrid;
            var body = grid.Physics;
            var s = $"{door.EntityId} '{door.CustomName}' on '{grid.DisplayName}' ({(door.PositionComp.GetPosition() - at).Length():0.0} m): open={door.Open} status={((Sandbox.ModAPI.Ingame.IMyDoor)door).Status} " +
                    $"ratio={door.OpenRatio:0.00} speed={Field<float>(door, "m_currSpeed"):0.00} working={door.IsWorking} enabled={door.Enabled} needs={door.NeedsUpdate}; ";
            if (body == null) return s + "grid without physics";
            s += $"grid static={grid.IsStatic} bodyStatic={body.IsStatic} inWorld={body.IsInWorld} welded={body.IsWelded}" +
                 (body.IsWelded ? $" to {(body.WeldInfo.Parent.Entity as MyEntity)?.DisplayName ?? "?"}" : "") +
                 $" weldChildren={body.WeldInfo.Children.Count} system={body.HavokCollisionSystemID} body={Filter(body.RigidBody)} motion={body.RigidBody?.GetMotionType()}; ";
            foreach (var side in new[] { "Left", "Right" })
            {
                var part = Field<MyEntitySubpart>(door, "m_" + side.ToLowerInvariant() + "Subpart");
                var constraint = Field<HkConstraint>(door, "m_" + side.ToLowerInvariant() + "Constraint");
                if (part == null) { s += side + ": none; "; continue; }
                var p = part.Physics as MyPhysicsBody;
                s += $"{side}: ";
                if (p?.RigidBody == null) s += "no body";
                else
                    s += $"enabled={p.Enabled} inWorld={p.IsInWorld} sameWorld={p.HavokWorld == body.HavokWorld} {Filter(p.RigidBody)} motion={p.RigidBody.GetMotionType()} " +
                         $"bodyOff={(p.GetWorldMatrix().Translation - part.PositionComp.WorldMatrixRef.Translation).Length():0.00} m " +
                         $"local={part.PositionComp.LocalMatrixRef.Translation}";
                s += constraint == null ? ", constraint none; "
                    : $", constraint inWorld={constraint.InWorld} enabled={constraint.Enabled} A={Owner(constraint.RigidBodyA)} B={Owner(constraint.RigidBodyB)}; ";
            }
            return s;
        }

        private static string Owner(HkRigidBody b) => b == null ? "null" : (b.UserObject as MyPhysicsBody)?.Entity is MyEntity e ? e.DisplayName ?? e.GetType().Name : "?";

        private static string Filter(HkRigidBody b)
        {
            if (b == null) return "null";
            var info = b.GetCollisionFilterInfo();
            return $"layer={info & 31} system={(info >> 16) & 0xffff} sub={(info >> 5) & 31} noCollide={(info >> 10) & 31}";
        }

        private static T Field<T>(object o, string name)
        {
            for (var t = o.GetType(); t != null; t = t.BaseType)
            {
                var f = t.GetField(name, Any);
                if (f != null) return (T)f.GetValue(o);
            }
            return default(T);
        }
    }
}
