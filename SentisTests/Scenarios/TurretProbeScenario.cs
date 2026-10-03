using System.Collections;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using SentisTests.Core;
using SentisTests.Game;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// What the turrets of the bots' bases are doing, to the log, for 60 seconds: working, loaded, what they are set to
    /// shoot at, whether they have a target - and the animals within their range with what the turret's owner is to
    /// them. A probe, not a test: it passes when there is a turret to look at.
    /// </summary>
    public sealed class TurretProbeScenario : TestScenario
    {
        public const string ScenarioName = "turret_probe";

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 180;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            var turrets = MyEntities.GetEntities().OfType<MyCubeGrid>().Where(g => g.DisplayName != null && g.DisplayName.StartsWith("[BOT] "))
                .SelectMany(g => g.GetFatBlocks<Sandbox.Game.Weapons.MyLargeTurretBase>()).ToList();
            Check(turrets.Count > 0, "no turret on a bot's base");
            // an empty turret with an animal within 100 m gets magazines for the watch: whether a loaded turret shoots
            // the animal is what the probe is for (the bot loads its turrets itself, when it has the magnesium)
            foreach (var turret in turrets)
            {
                var here = turret.PositionComp.GetPosition();
                var near = MyEntities.GetEntities().OfType<MyCharacter>().Any(c => !c.IsDead && c.Definition?.Id.SubtypeName is string n &&
                    (n.Contains("Wolf") || n.Contains("Spider")) && Vector3D.Distance(c.PositionComp.GetPosition(), here) < 100);
                var inventory = turret.GetInventory(0) as Sandbox.Game.MyInventory;
                if (near && inventory != null && inventory.GetItemsCount() == 0)
                {
                    inventory.AddItems(5, new VRage.Game.MyObjectBuilder_AmmoMagazine { SubtypeName = "AutomaticRifleGun_Mag_20rd" });
                    Note($"{turret.CubeGrid.DisplayName} {turret.DisplayNameText}: empty with an animal near - 5 magazines put in for the watch");
                }
            }
            // a wolf of its own for the watch: 30 m from the first loaded turret, on the ground, where the turret sees it
            // (loaded with the magazine it is set to fire: any other it does not use)
            // the highest of them first: the one on a mast over its base
            double Height(Sandbox.Game.Weapons.MyLargeTurretBase t)
            {
                var p = t.PositionComp.GetPosition();
                var planetOf = MyGamePruningStructure.GetClosestPlanet(p);
                return planetOf == null ? 0 : Vector3D.Distance(p, planetOf.GetClosestSurfacePointGlobal(ref p));
            }
            turrets = turrets.OrderByDescending(Height).ToList();
            var armed = turrets.FirstOrDefault(t => t.GunBase is Sandbox.Game.Weapons.MyGunBase g && (double)(t.GetInventory(0)?.GetItemAmount(g.CurrentAmmoMagazineId) ?? 0) > 0)
                        ?? turrets.FirstOrDefault(t => (t.GetInventory(0)?.GetItemsCount() ?? 0) > 0);
            MyCharacter wolf = null;
            if (armed != null)
            {
                var from = armed.PositionComp.GetPosition();
                var planet = MyGamePruningStructure.GetClosestPlanet(from);
                var up = planet != null ? Vector3D.Normalize(from - planet.PositionComp.GetPosition()) : Vector3D.Up;
                var side = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(up));
                var spot = from + side * 30;
                if (planet != null) spot = planet.GetClosestSurfacePointGlobal(ref spot) + up * 1.5;
                var definition = Sandbox.Definitions.MyDefinitionManager.Static.GetBotDefinition(VRage.Game.MyDefinitionId.Parse("MyObjectBuilder_AnimalBot/Wolf")) as Sandbox.Definitions.MyAgentDefinition;
                Check(definition != null, "no wolf bot definition");
                var before = new System.Collections.Generic.HashSet<long>(MyEntities.GetEntities().OfType<MyCharacter>().Select(c => c.EntityId));
                Sandbox.Game.AI.MyAIComponent.Static.SpawnNewBot(definition, spot);
                var born = Wait(() => (wolf = MyEntities.GetEntities().OfType<MyCharacter>().FirstOrDefault(c => !before.Contains(c.EntityId) && c.Definition?.Id.SubtypeName?.Contains("Wolf") == true)) != null, "the wolf appears", 20);
                while (born.MoveNext()) yield return born.Current;
                Note($"the loaded turret stands {Height(armed):0.0} m over the ground");
                Note($"a wolf put {Vector3D.Distance(wolf.PositionComp.GetPosition(), from):0} m from {armed.CubeGrid.DisplayName} {armed.DisplayNameText} (ammo {armed.GetInventory(0).GetItems().Sum(i => (double)i.Amount):0}), its health {wolf.StatComp?.Health?.Value:0}");
            }
            // every frame for 30 s: each change of what the loaded turret aims at and does
            if (armed != null && wolf != null)
            {
                var last = "";
                var changes = 0;
                var api0 = (Sandbox.ModAPI.Ingame.IMyLargeTurretBase)armed;
                var rec = (Sandbox.Game.Entities.Interfaces.IMyTargetingReceiver)armed;
                for (var tick = 0; tick < 30 * 60; tick++)
                {
                    var now = $"target {armed.Target?.DisplayName ?? "none"}, status {armed.GetStatus()}, shooting {api0.IsShooting}, aimed {(armed.Target == null ? "-" : Vector3D.Dot(rec.ShootDirection, Vector3D.Normalize(armed.Target.PositionComp.WorldAABB.Center - rec.ShootOrigin)).ToString("0.00"))}";
                    if (now != last && changes < 40)
                    {
                        changes++;
                        Note($"tick {tick}: {now}; wolf {(wolf.IsDead ? "dead" : $"health {wolf.StatComp?.Health?.Value:0}, {Vector3D.Distance(wolf.PositionComp.GetPosition(), armed.PositionComp.GetPosition()):0} m")}, magazines {armed.GetInventory(0).GetItems().Sum(i => (double)i.Amount):0}");
                        last = now;
                    }
                    yield return null;
                }
                Note($"30 s watched: {changes} changes");
                Note($"after 30 s the wolf is {(wolf.IsDead || wolf.MarkedForClose ? "dead" : $"alive, health {wolf.StatComp?.Health?.Value:0}")}");
            }
            for (var second = 0; second < 60; second += 10)
            {
                if (armed != null)
                {
                    var gun = armed.GunBase as Sandbox.Game.Weapons.MyGunBase;
                    Note($"{second} s: the loaded turret: render visible {armed.Render?.IsVisible()}, in scene {armed.InScene}, needs update {armed.NeedsUpdate}, " +
                         $"has enough ammunition {gun?.HasEnoughAmmunition()}, its magazine now {gun?.CurrentAmmoMagazineId.SubtypeName}, ammo in it {gun?.CurrentAmmo}, " +
                         $"grid physics {(armed.CubeGrid.Physics == null ? "none" : armed.CubeGrid.Physics.Enabled ? "on" : "off")}, grid needs update {armed.CubeGrid.NeedsUpdate}, target {armed.Target?.DisplayName ?? "none"}");
                }
                if (armed != null && wolf != null && !wolf.IsDead)
                {
                    // the turret's own tests on the wolf, and whether SentisOptimisations lets its search run
                    var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
                    var system = typeof(Sandbox.Game.Weapons.MyLargeTurretBase).GetField("m_targetingSystem", flags)?.GetValue(armed) as Sandbox.Game.Weapons.MyLargeTurretTargetingSystem;
                    var idle = System.AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(x => x.GetName().Name == "SentisOptimisations")
                        ?.GetType("Optimizer.Optimizations.TurretIdleSearch")?.GetMethod("NothingToFind", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                    var roots = new System.Collections.Generic.List<VRage.Game.Entity.MyEntity>();
                    armed.CubeGrid.Components.Get<Sandbox.Game.EntityComponents.MyGridTargeting>()?.GetTargetRoots(true, false, false, roots);
                    Note($"{second} s: the turret on the wolf: is target {system?.IsTarget(wolf)}, valid {system?.IsValidTarget(wolf)}, visible {system?.IsTargetVisibleMainThread(wolf, null, false)}, " +
                         $"search in process {system?.ParallelTargetSelectionInProcess}, SO says nothing to find {idle?.Invoke(null, new object[] { system })}, " +
                         $"roots {roots.Count} (the wolf among them {roots.Contains(wolf)}), wolf physics {(wolf.Physics == null ? "none" : wolf.Physics.Enabled ? "on" : "off")}");
                    // what stands between the muzzle and the wolf
                    var muzzle = ((Sandbox.Game.Entities.Interfaces.IMyTargetingReceiver)armed).ShootOrigin;
                    var aim = wolf.PositionComp.WorldAABB.Center;
                    var hits = new System.Collections.Generic.List<Sandbox.Engine.Physics.MyPhysics.HitInfo>();
                    Sandbox.Engine.Physics.MyPhysics.CastRay(muzzle, aim, hits);
                    var inWay = string.Join(", ", hits.Take(4).Select(h =>
                    {
                        var e = Sandbox.Engine.Physics.MyPhysicsExtensions.GetEntity(h.HkHitInfo.Body, 0u);
                        var block = e is MyCubeGrid g ? g.GetCubeBlock(g.WorldToGridInteger(h.Position + Vector3D.Normalize(aim - muzzle) * 0.2))?.BlockDefinition.Id.SubtypeName : null;
                        return $"{(e as VRage.Game.Entity.MyEntity)?.DisplayName ?? e?.GetType().Name}{(block != null ? "/" + block : "")} at {Vector3D.Distance(muzzle, h.Position):0.0} m";
                    }));
                    var receiver = (Sandbox.Game.Entities.Interfaces.IMyTargetingReceiver)armed;
                    Note($"{second} s: in the turret's view {receiver.IsTargetInView(aim)}, shoot direction {receiver.ShootDirection}, to the wolf {Vector3D.Normalize(aim - muzzle)}, " +
                         $"grid up {armed.WorldMatrix.Up}, enabled {armed.Enabled}, working {armed.IsWorking}, status {armed.GetStatus()}");
                    var timer = armed.Components.Get<Sandbox.Game.Components.MyTimerComponent>();
                    Note($"{second} s: the turret's timer: {(timer == null ? "none" : $"enabled {timer.TimerEnabled}, every {timer.TimerTickInFrames} frames, {timer.FramesFromLastTrigger} since the last, session update {timer.IsSessionUpdateEnabled}, type {timer.TimerType}")}; " +
                         $"grid tiers: player {armed.CubeGrid.PlayerPresenceTier}, grid {armed.CubeGrid.GridPresenceTier}");
                    Note($"{second} s: muzzle to wolf {Vector3D.Distance(muzzle, aim):0.0} m; in the way: {(hits.Count == 0 ? "nothing" : inWay)}; prediction in range {system?.TargetPrediction?.IsLastPredictedCoordinatesInRange}; " +
                         $"turret at cell {armed.Position}, orientation up {armed.Orientation.Up} forward {armed.Orientation.Forward}");
                }
                if (wolf != null) Note($"{second} s: the wolf {(wolf.IsDead ? "is dead" : $"lives, health {wolf.StatComp?.Health?.Value:0}, {Vector3D.Distance(wolf.PositionComp.GetPosition(), armed.PositionComp.GetPosition()):0} m off")}; " +
                                       $"the turret holds {armed.GetInventory(0).GetItems().Sum(i => (double)i.Amount):0} magazines");
                foreach (var turret in turrets.Where(t => !t.MarkedForClose))
                {
                    var api = (Sandbox.ModAPI.Ingame.IMyLargeTurretBase)turret;
                    var at = turret.PositionComp.GetPosition();
                    var ammo = turret.GetInventory(0)?.GetItems().Sum(i => (double)i.Amount) ?? 0;
                    var animals = MyEntities.GetEntities().OfType<MyCharacter>()
                        .Where(c => !c.IsDead && c.Definition?.Id.SubtypeName is string n && (n.Contains("Wolf") || n.Contains("Spider")) && Vector3D.Distance(c.PositionComp.GetPosition(), at) < 800)
                        .OrderBy(c => Vector3D.Distance(c.PositionComp.GetPosition(), at)).Take(3)
                        .Select(c => $"{c.Definition.Id.SubtypeName} {Vector3D.Distance(c.PositionComp.GetPosition(), at):0} m (identity {c.GetPlayerIdentityId()}, " +
                                     $"faction {MySession.Static.Factions.TryGetPlayerFaction(c.GetPlayerIdentityId())?.Tag ?? "-"}, to the owner {turret.GetUserRelationToOwner(c.GetPlayerIdentityId())})");
                    var field = System.AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(x => x.GetName().Name == "SentisAi")?.GetType("SentisAi.Bots.Bot")
                        ?.GetMethod("FireField", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic, null, new[] { typeof(MyCubeBlock) }, null);
                    var planetHere = MyGamePruningStructure.GetClosestPlanet(at);
                    var over = planetHere == null ? 0 : Vector3D.Distance(at, planetHere.GetClosestSurfacePointGlobal(ref at));
                    Note($"{second} s: {turret.CubeGrid.DisplayName} {turret.DisplayNameText}: functional {turret.IsFunctional}, built {turret.SlimBlock.BuildLevelRatio * 100:0}%, enabled {turret.Enabled}, " +
                         $"powered {turret.ResourceSink?.IsPoweredByType(Sandbox.Game.EntityComponents.MyResourceDistributorComponent.ElectricityId)}, {over:0.0} m over the ground, cell {turret.Position}, " +
                         $"open directions {field?.Invoke(null, new object[] { turret })}");
                    Note($"{second} s: {turret.CubeGrid.DisplayName} {turret.DisplayNameText}: working {turret.IsWorking}, ammo {ammo:0}, AI {api.AIEnabled}, range {api.Range:0}, " +
                         $"characters {api.TargetCharacters}, enemies {api.TargetEnemies}, neutrals {api.TargetNeutrals}, has target {api.HasTarget}, shooting {api.IsShooting}; " +
                         $"owner {turret.OwnerId} (faction {MySession.Static.Factions.TryGetPlayerFaction(turret.OwnerId)?.Tag ?? "-"}); animals: {string.Join("; ", animals)}");
                }
                var wait = WaitForSeconds(10, "the turrets watched");
                while (wait.MoveNext()) yield return wait.Current;
            }
        }
    }
}
