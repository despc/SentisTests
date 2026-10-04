using System.Collections;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using SentisTests.Core;
using SentisTests.Game;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// Whether a bot's turret kills a wolf: a wolf of the world's own (the nearest live one, the game's animal with
    /// its own physics - one spawned for a test came out with none and vanished) moved onto the ground 30 m from the
    /// highest turret of a bot's base, the turret given five of the magazines it is set to fire, then a minute
    /// watched frame by frame: the turret's target, whether it shoots, what stands on the line from its muzzle to
    /// the wolf, the wolf's health. Passes when the wolf dies of the turret's bullets.
    /// </summary>
    public sealed class TurretWolfScenario : TestScenario
    {
        public const string ScenarioName = "turret_wolf";
        /// <summary>The same with the turret's idle rotation switched on first, as a player does in the terminal.</summary>
        public const string IdleName = "turret_wolf_idle";

        private readonly bool _idle;
        public TurretWolfScenario(bool idle = false) { _idle = idle; }

        public override string Name => _idle ? IdleName : ScenarioName;
        public override int TimeoutSeconds => 150;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            double Height(Sandbox.Game.Weapons.MyLargeTurretBase t)
            {
                var p = t.PositionComp.GetPosition();
                var planetOf = MyGamePruningStructure.GetClosestPlanet(p);
                return planetOf == null ? 0 : Vector3D.Distance(p, planetOf.GetClosestSurfacePointGlobal(ref p));
            }
            bool IsWolf(MyCharacter c) => !c.IsDead && !c.MarkedForClose && c.Definition?.Id.SubtypeName?.Contains("Wolf") == true && c.Physics != null;
            var wolves = MyEntities.GetEntities().OfType<MyCharacter>().Where(IsWolf).ToList();
            Check(wolves.Count > 0, "no live wolf in the world");
            var turret = MyEntities.GetEntities().OfType<MyCubeGrid>().Where(g => g.DisplayName != null && g.DisplayName.StartsWith("[BOT] "))
                .SelectMany(g => g.GetFatBlocks<Sandbox.Game.Weapons.MyLargeTurretBase>()).Where(t => t.IsWorking)
                .OrderBy(t => wolves.Min(w => Vector3D.Distance(w.PositionComp.GetPosition(), t.PositionComp.GetPosition())))
                .FirstOrDefault();
            Check(turret != null, "no working turret on a bot's base");
            var from = turret.PositionComp.GetPosition();
            var wolf = wolves.OrderBy(w => Vector3D.Distance(w.PositionComp.GetPosition(), from)).First();
            var planet = MyGamePruningStructure.GetClosestPlanet(from);
            var up = planet != null ? Vector3D.Normalize(from - planet.PositionComp.GetPosition()) : Vector3D.Up;
            var side = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(up));
            var spot = from + side * 30;
            if (planet != null) spot = planet.GetClosestSurfacePointGlobal(ref spot) + up * 1.0;
            Note($"turret {turret.CubeGrid.DisplayName} {turret.DisplayNameText}, {Height(turret):0.0} m over the ground; the wolf {wolf.EntityId} " +
                 $"{Vector3D.Distance(wolf.PositionComp.GetPosition(), from):0} m off, health {wolf.StatComp?.Health?.Value:0}");
            var matrix = wolf.WorldMatrix;
            matrix.Translation = spot;
            wolf.PositionComp.SetWorldMatrix(ref matrix);
            wolf.Physics?.ClearSpeed();
            var gun = turret.GunBase as Sandbox.Game.Weapons.MyGunBase;
            var magazine = gun?.CurrentAmmoMagazineId.SubtypeName ?? "RapidFireAutomaticRifleGun_Mag_50rd";
            var inventory = turret.GetInventory(0) as Sandbox.Game.MyInventory;
            double Ammo() => inventory?.GetItems().Sum(i => (double)i.Amount) ?? 0;
            if (Ammo() < 5) inventory?.AddItems(5, new VRage.Game.MyObjectBuilder_AmmoMagazine { SubtypeName = magazine });
            var api = (Sandbox.ModAPI.Ingame.IMyLargeTurretBase)turret;
            var receiver = (Sandbox.Game.Entities.Interfaces.IMyTargetingReceiver)turret;
            Note($"idle rotation {api.EnableIdleRotation}, azimuth {api.Azimuth:0.00}, elevation {api.Elevation:0.00}");
            if (_idle && !api.EnableIdleRotation)
            {
                ((Sandbox.ModAPI.IMyLargeTurretBase)turret).EnableIdleRotation = true;
                Note("idle rotation switched on, as from the terminal");
            }
            Note($"the wolf put {Vector3D.Distance(wolf.PositionComp.GetPosition(), from):0} m from the turret; magazines {Ammo():0} ({magazine}); " +
                 $"range {api.Range:0}, characters {api.TargetCharacters}, enemies {api.TargetEnemies}, to the owner {turret.GetUserRelationToOwner(wolf.GetPlayerIdentityId())}");
            var startHealth = wolf.StatComp?.Health?.Value ?? 0;
            var startAmmo = Ammo();
            var shootingFrames = 0;
            var last = "";
            for (var tick = 0; tick < 60 * 60 && !wolf.IsDead && !wolf.MarkedForClose; tick++)
            {
                if (api.IsShooting) shootingFrames++;
                if (tick % 30 == 0)
                {
                    var muzzle = receiver.ShootOrigin;
                    var aim = wolf.PositionComp.WorldAABB.Center;
                    var hits = new System.Collections.Generic.List<Sandbox.Engine.Physics.MyPhysics.HitInfo>();
                    Sandbox.Engine.Physics.MyPhysics.CastRay(muzzle, aim, hits);
                    var first = hits.Select(h => (Entity: Sandbox.Engine.Physics.MyPhysicsExtensions.GetEntity(h.HkHitInfo.Body, 0u) as VRage.Game.Entity.MyEntity, h.Position))
                        .FirstOrDefault(h => h.Entity != null && h.Entity != turret.CubeGrid);
                    var way = first.Entity == null ? "nothing" : first.Entity == wolf ? "the wolf" : $"{first.Entity.GetType().Name} at {Vector3D.Distance(muzzle, first.Position):0.0} m";
                    // the turret's own tests: it turns to a target only while it sees it (IsTargetVisibleMainThread: not in a
                    // safe zone, the predicted spot within range, a ray from the muzzle - its own grid not left out)
                    var withOwn = hits.Select(h => (Entity: Sandbox.Engine.Physics.MyPhysicsExtensions.GetEntity(h.HkHitInfo.Body, 0u) as VRage.Game.Entity.MyEntity, h.Position))
                        .FirstOrDefault(h => h.Entity != null);
                    var own = withOwn.Entity == turret.CubeGrid
                        ? $"own grid at {Vector3D.Distance(muzzle, withOwn.Position):0.0} m ({turret.CubeGrid.GetCubeBlock(turret.CubeGrid.WorldToGridInteger(withOwn.Position + Vector3D.Normalize(aim - muzzle) * 0.1))?.BlockDefinition.Id.SubtypeName})"
                        : withOwn.Entity == null ? "nothing" : withOwn.Entity == wolf ? "the wolf" : withOwn.Entity.GetType().Name;
                    var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
                    var system = typeof(Sandbox.Game.Weapons.MyLargeTurretBase).GetField("m_targetingSystem", flags)?.GetValue(turret) as Sandbox.Game.Weapons.MyLargeTurretTargetingSystem;
                    var safe = !Sandbox.Game.Entities.MySessionComponentSafeZones.IsActionAllowed(wolf.PositionComp.GetPosition(), VRage.Game.ObjectBuilders.Components.MySafeZoneAction.Shooting, 0L, 0uL);
                    var visible = system?.IsTargetVisibleMainThread(wolf, null, false);
                    var now = $"target {(turret.Target == wolf ? "the wolf" : turret.Target?.GetType().Name ?? "none")}, shooting {api.IsShooting}, line {way}, with its own grid {own}, " +
                              $"visible {visible}, in a safe zone {safe}, prediction in range {system?.TargetPrediction?.IsLastPredictedCoordinatesInRange}";
                    if (now != last || tick % 120 == 0)
                    {
                        Note($"tick {tick}: {now}; aimed {Vector3D.Dot(receiver.ShootDirection, Vector3D.Normalize(aim - muzzle)):0.00}, wolf {Vector3D.Distance(aim, muzzle):0} m, " +
                             $"health {wolf.StatComp?.Health?.Value:0}, magazines {Ammo():0}, status {turret.GetStatus()}, " +
                             $"azimuth {api.Azimuth:0.00} elevation {api.Elevation:0.00}, muzzle {Vector3D.Distance(muzzle, from):0.00} m from the block, shoot direction {receiver.ShootDirection}");
                        last = now;
                    }
                }
                yield return null;
            }
            var dead = wolf.IsDead;
            Note($"after the watch: the wolf {(dead ? "dead" : wolf.MarkedForClose ? "gone from the world" : "alive")}, health {startHealth:0} -> {wolf.StatComp?.Health?.Value:0}, " +
                 $"the turret shot {shootingFrames / 60.0:0.0} s, magazines {startAmmo:0} -> {Ammo():0}");
            // the turret's kill only: a wolf bitten to death by a bot's grinder, or one that ran off and was taken out of
            // the world, passed this once with the turret silent the whole minute
            Check(dead && shootingFrames > 0, $"the turret did not kill the wolf in a minute 30 m from it (shot {shootingFrames / 60.0:0.0} s, " +
                                              $"wolf {(dead ? "dead" : wolf.MarkedForClose ? "gone" : "alive")}, health {startHealth:0} -> {wolf.StatComp?.Health?.Value:0})");
        }
    }
}
