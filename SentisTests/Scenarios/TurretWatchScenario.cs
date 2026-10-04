using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using SentisTests.Core;
using SentisTests.Game;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// What the turrets of the bots' bases shoot at in the world as it goes, for fifteen minutes: every frame a turret
    /// shoots, its target, how far, what stands first on the line from the muzzle to it, and whether the target loses
    /// health. On the stand the turrets emptied thirty magazines in a day and the watcher saw not one bullet hit. A
    /// probe, not a test: it passes when there is a turret to look at. (No magazines are put in: only what the bots
    /// loaded is watched - but an empty one with an animal within its range gets five of the magazines it is set
    /// to fire, once, as the bot would load it.)
    /// </summary>
    public sealed class TurretWatchScenario : TestScenario
    {
        public const string ScenarioName = "turret_watch";

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 1020;

        private sealed class Watch
        {
            public int ShootingFrames;
            public readonly Dictionary<string, int> Targets = new Dictionary<string, int>();
            public readonly Dictionary<string, int> InWay = new Dictionary<string, int>();
            public double AmmoAtStart;
            public float HealthTaken;
        }

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            var turrets = MyEntities.GetEntities().OfType<MyCubeGrid>().Where(g => g.DisplayName != null && g.DisplayName.StartsWith("[BOT] "))
                .SelectMany(g => g.GetFatBlocks<Sandbox.Game.Weapons.MyLargeTurretBase>()).ToList();
            Check(turrets.Count > 0, "no turret on a bot's base");
            double Ammo(Sandbox.Game.Weapons.MyLargeTurretBase t) => t.GetInventory(0)?.GetItems().Sum(i => (double)i.Amount) ?? 0;
            var watches = turrets.ToDictionary(t => t, t => new Watch { AmmoAtStart = Ammo(t) });
            Note($"{turrets.Count} turrets, {turrets.Count(t => Ammo(t) > 0)} loaded: " +
                 string.Join("; ", turrets.Where(t => Ammo(t) > 0).Select(t => $"{t.CubeGrid.DisplayName} {t.DisplayNameText} {Ammo(t):0}")));
            var lastHealth = new Dictionary<long, float>();
            var loaded = new HashSet<Sandbox.Game.Weapons.MyLargeTurretBase>();
            var lastNote = new Dictionary<Sandbox.Game.Weapons.MyLargeTurretBase, string>();
            for (var tick = 0; tick < 900 * 60; tick++)
            {
                foreach (var turret in turrets)
                {
                    if (turret.MarkedForClose) continue;
                    var api = (Sandbox.ModAPI.Ingame.IMyLargeTurretBase)turret;
                    if (tick % 60 == 0 && Ammo(turret) == 0 && !loaded.Contains(turret) && turret.GunBase is Sandbox.Game.Weapons.MyGunBase gun &&
                        MyEntities.GetEntities().OfType<MyCharacter>().Any(c => !c.IsDead && c.Definition?.Id.SubtypeName is string sn && (sn.Contains("Wolf") || sn.Contains("Spider")) &&
                                                                               Vector3D.Distance(c.PositionComp.GetPosition(), turret.PositionComp.GetPosition()) < api.Range))
                    {
                        loaded.Add(turret);
                        var magazine = gun.CurrentAmmoMagazineId;
                        (turret.GetInventory(0) as Sandbox.Game.MyInventory)?.AddItems(5, new VRage.Game.MyObjectBuilder_AmmoMagazine { SubtypeName = magazine.SubtypeName });
                        watches[turret].AmmoAtStart = Ammo(turret);
                        Note($"tick {tick}: {turret.CubeGrid.DisplayName} {turret.DisplayNameText}: empty with an animal within its range - 5 {magazine.SubtypeName} put in");
                    }
                    if (!api.IsShooting) continue;
                    var w = watches[turret];
                    w.ShootingFrames++;
                    var target = turret.Target;
                    var name = target == null ? "none" : target is MyCharacter c ? c.Definition?.Id.SubtypeName ?? "character" : target.GetType().Name;
                    w.Targets[name] = (w.Targets.TryGetValue(name, out var n) ? n : 0) + 1;
                    if (target is MyCharacter victim && victim.StatComp?.Health != null)
                    {
                        var health = victim.StatComp.Health.Value;
                        if (lastHealth.TryGetValue(victim.EntityId, out var before) && health < before) w.HealthTaken += before - health;
                        lastHealth[victim.EntityId] = health;
                    }
                    if (target == null || tick % 10 != 0) continue;
                    var receiver = (Sandbox.Game.Entities.Interfaces.IMyTargetingReceiver)turret;
                    var muzzle = receiver.ShootOrigin;
                    var aim = target.PositionComp.WorldAABB.Center;
                    var hits = new List<Sandbox.Engine.Physics.MyPhysics.HitInfo>();
                    Sandbox.Engine.Physics.MyPhysics.CastRay(muzzle, aim, hits);
                    var first = hits.Select(h => Sandbox.Engine.Physics.MyPhysicsExtensions.GetEntity(h.HkHitInfo.Body, 0u) as VRage.Game.Entity.MyEntity)
                        .FirstOrDefault(e => e != null && e != turret.CubeGrid);
                    var way = first == null ? "nothing" : first == target ? "the target" : first is MyCubeGrid ? "a grid" : first.GetType().Name;
                    w.InWay[way] = (w.InWay.TryGetValue(way, out var m) ? m : 0) + 1;
                    var aimed = Vector3D.Dot(receiver.ShootDirection, Vector3D.Normalize(aim - muzzle));
                    var note = $"{name}/{way}";
                    if (!lastNote.TryGetValue(turret, out var was) || was != note)
                    {
                        lastNote[turret] = note;
                        Note($"tick {tick}: {turret.CubeGrid.DisplayName} {turret.DisplayNameText} shoots at {name} {Vector3D.Distance(muzzle, aim):0} m off " +
                             $"({Vector3D.Dot(aim - muzzle, Vector3D.Normalize(muzzle - (MyGamePruningStructure.GetClosestPlanet(muzzle)?.PositionComp.GetPosition() ?? Vector3D.Zero))):0} m up), " +
                             $"aimed {aimed:0.00}, first on the line: {way}{(first != null && first != target ? $" at {Vector3D.Distance(muzzle, hits.First(h => Sandbox.Engine.Physics.MyPhysicsExtensions.GetEntity(h.HkHitInfo.Body, 0u) == first).Position):0.0} m" : "")}, " +
                             $"target health {(target as MyCharacter)?.StatComp?.Health?.Value:0}, ammo {Ammo(turret):0}");
                    }
                }
                if (tick % (60 * 60) == 0 && tick > 0) Note($"{tick / 3600} min watched");
                yield return null;
            }
            foreach (var pair in watches.Where(p => p.Value.ShootingFrames > 0 || Ammo(p.Key) != p.Value.AmmoAtStart))
                Note($"{pair.Key.CubeGrid.DisplayName} {pair.Key.DisplayNameText}: shot {pair.Value.ShootingFrames / 60.0:0} s, magazines {pair.Value.AmmoAtStart:0} -> {Ammo(pair.Key):0}, " +
                     $"at {string.Join(", ", pair.Value.Targets.Select(t => $"{t.Key} {t.Value / 60.0:0} s"))}; on the line {string.Join(", ", pair.Value.InWay.Select(t => $"{t.Key} {t.Value}"))}; " +
                     $"health its targets lost {pair.Value.HealthTaken:0}");
        }
    }
}
