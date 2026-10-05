using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Weapons;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRage.ObjectBuilders;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// A turret standing low on the ground with several wolves round it: whether it turns from one to the next and
    /// kills them, nothing of its own grid over or under its line but the battery it stands on.
    ///
    /// A static grid - a charged battery on the ground and an interior turret on it, 20 magazines of what it fires -
    /// 150 m off a bot's base; up to four of the world's own wolves (the game's animals with their physics) moved onto
    /// the ground 20-40 m round it, each from another side; a player 300 m off, so the grid updates as on a live server.
    /// For 90 s, every frame: the target, each change of it, how far the barrel is off the target, the shooting, the
    /// wolves' health. Passes when the turret kills every wolf.
    /// </summary>
    public sealed class TurretWolvesScenario : TestScenario
    {
        public const string ScenarioName = "turret_wolves";
        private const string Prefix = "turret-wolves-";
        private const int Wolves = 4;
        private const double WatchSeconds = 90;

        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };
        private readonly ConfigOverride _optimisations = new ConfigOverride(ConfigOverride.Optimisations);

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 150;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            _optimisations.Set("FreezerEnabled", false);
            bool IsWolf(MyCharacter c) => !c.IsDead && !c.MarkedForClose && c.Definition?.Id.SubtypeName?.Contains("Wolf") == true && c.Physics != null;
            var home = MyEntities.GetEntities().OfType<MyCubeGrid>().Where(g => g.DisplayName != null && g.DisplayName.StartsWith("[BOT] ") && g.IsStatic)
                .OrderByDescending(g => g.BlocksCount).FirstOrDefault();
            Check(home != null, "no bot's base");
            var planet = MyGamePruningStructure.GetClosestPlanet(home.PositionComp.GetPosition());
            Check(planet != null, "no planet");
            var basePos = home.PositionComp.GetPosition();
            var up = Vector3D.Normalize(basePos - planet.PositionComp.GetPosition());
            var side = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(up));
            var other = Vector3D.Cross(up, side);
            var spot = basePos + side * 150;
            spot = planet.GetClosestSurfacePointGlobal(ref spot);

            FakeClients.RemoveAll();
            FakeClients.Add(1, Network, p => (spot + other * 300 + up * 30, 0, 0), withCharacters: true);
            var arrive = WaitForSeconds(5, "the player arrives");
            while (arrive.MoveNext()) yield return arrive.Current;

            var grid = SpawnTurret(spot + up * 1.25, up, side);
            var turret = grid.GetFatBlocks().OfType<MyLargeTurretBase>().FirstOrDefault();
            Check(turret != null, "the grid has no turret");
            // characters only (the wolves): left to choose, with no wolf in sight it shot a bot's base 150 m off - 8900
            // damage to its panels, turbines and generator in one run (05.10.2026)
            turret.TargetStations = turret.TargetLargeGrids = turret.TargetSmallGrids = turret.TargetMeteors = turret.TargetMissiles = false;
            var magazine = turret.GunBase.CurrentAmmoMagazineId;
            turret.GetInventory(0).AddItems((MyFixedPoint)20, (MyObjectBuilder_PhysicalObject)MyObjectBuilderSerializer.CreateNewObject(magazine));
            var api = (Sandbox.ModAPI.Ingame.IMyLargeTurretBase)turret;
            var receiver = (Sandbox.Game.Entities.Interfaces.IMyTargetingReceiver)turret;
            var from = turret.PositionComp.GetPosition();
            double Ammo() => turret.GetInventory(0).GetItems().Sum(i => (double)i.Amount);

            // the wolves made for the watch, as the game makes its animals (MyAIComponent.SpawnNewBot with the wolf's bot
            // definition): their characters come a few frames later - taken once they stand in the world with their physics
            var definition = Sandbox.Definitions.MyDefinitionManager.Static.GetBotDefinition(MyDefinitionId.Parse("MyObjectBuilder_AnimalBot/Wolf")) as Sandbox.Definitions.MyAgentDefinition;
            Check(definition != null, "no wolf bot definition");
            var before = new HashSet<long>(MyEntities.GetEntities().OfType<MyCharacter>().Select(c => c.EntityId));
            for (var k = 0; k < Wolves; k++)
            {
                var at = from + side * (30 + 3 * k);
                Sandbox.Game.AI.MyAIComponent.Static.SpawnNewBot(definition, planet.GetClosestSurfacePointGlobal(ref at) + up * 1.0);
            }
            List<MyCharacter> Born() => MyEntities.GetEntities().OfType<MyCharacter>()
                .Where(c => !before.Contains(c.EntityId) && IsWolf(c) && Vector3D.Distance(c.PositionComp.GetPosition(), from) < 200).ToList();
            var born = Wait(() => Born().Count >= Wolves, $"{Wolves} wolves come into the world", 20);
            while (born.MoveNext()) yield return born.Current;
            var wolves = Born().Take(Wolves).ToList();
            Note($"{wolves.Count} wolves made; characters new since the spawn: " +
                 string.Join(", ", MyEntities.GetEntities().OfType<MyCharacter>().Where(c => !before.Contains(c.EntityId))
                     .Select(c => $"{c.Definition?.Id.SubtypeName} physics {c.Physics != null} {Vector3D.Distance(c.PositionComp.GetPosition(), from):0} m")));
            for (var k = 0; k < wolves.Count; k++)
            {
                var angle = MathHelper.TwoPi / wolves.Count * k;
                var at = from + (side * System.Math.Cos(angle) + other * System.Math.Sin(angle)) * (20 + 20.0 * k / System.Math.Max(1, wolves.Count - 1));
                at = planet.GetClosestSurfacePointGlobal(ref at) + up * 1.0;
                var matrix = wolves[k].WorldMatrix;
                matrix.Translation = at;
                wolves[k].PositionComp.SetWorldMatrix(ref matrix);
                wolves[k].Physics?.ClearSpeed();
            }
            Note($"turret on {grid.DisplayName}, {Ammo():0} {magazine.SubtypeName}, range {api.Range:0}, idle rotation {api.EnableIdleRotation}; " +
                 $"{wolves.Count} wolves put round it: " + string.Join(", ", wolves.Select(w => $"{Vector3D.Distance(w.PositionComp.GetPosition(), from):0} m " +
                                                                                            $"(health {w.StatComp?.Health?.Value:0}, to the owner {turret.GetUserRelationToOwner(w.GetPlayerIdentityId())})")));

            var health = wolves.ToDictionary(w => w, w => w.StatComp?.Health?.Value ?? 0);
            var hurtByTurret = wolves.ToDictionary(w => w, w => 0f);
            var killed = new HashSet<MyCharacter>();
            var startAmmo = Ammo();
            var shooting = 0;
            var shootingOff = 0;            // frames shot with the barrel more than ~25 degrees off the target
            var switches = 0;
            VRage.Game.Entity.MyEntity lastTarget = null;
            var aims = new List<double>();
            for (var tick = 0; tick < WatchSeconds * 60 && killed.Count < wolves.Count; tick++)
            {
                var target = turret.Target;
                if (target != lastTarget)
                {
                    switches++;
                    var name = target is MyCharacter c && wolves.Contains(c) ? $"wolf {wolves.IndexOf(c) + 1}" : target?.GetType().Name ?? "none";
                    Note($"tick {tick}: target {name}" + (target == null ? "" :
                         $" {Vector3D.Distance(target.PositionComp.GetPosition(), from):0} m, barrel off it by {Angle(receiver, target):0} degrees"));
                    lastTarget = target;
                }
                if (api.IsShooting)
                {
                    shooting++;
                    if (target != null)
                    {
                        var off = Angle(receiver, target);
                        aims.Add(off);
                        if (off > 25) shootingOff++;
                    }
                }
                foreach (var wolf in wolves)
                {
                    var now = wolf.StatComp?.Health?.Value ?? 0;
                    if (now < health[wolf] && api.IsShooting && target == wolf) hurtByTurret[wolf] += health[wolf] - now;
                    health[wolf] = now;
                    if (wolf.IsDead && killed.Add(wolf))
                        Note($"tick {tick}: wolf {wolves.IndexOf(wolf) + 1} dead ({(hurtByTurret[wolf] > 0 ? $"the turret took {hurtByTurret[wolf]:0} of its health" : "not by the turret")}), " +
                             $"magazines {Ammo():0}");
                }
                if (tick % 600 == 0 && tick > 0)
                    Note($"{tick / 60} s: target {(lastTarget is MyCharacter t && wolves.Contains(t) ? $"wolf {wolves.IndexOf(t) + 1}" : lastTarget?.GetType().Name ?? "none")}, " +
                         $"azimuth {api.Azimuth:0.00} elevation {api.Elevation:0.00}, shot {shooting / 60.0:0.0} s, magazines {Ammo():0}; " +
                         string.Join(", ", wolves.Select(w => $"wolf {wolves.IndexOf(w) + 1} {(w.IsDead ? "dead" : $"{w.StatComp?.Health?.Value:0} hp {Vector3D.Distance(w.PositionComp.GetPosition(), from):0} m")}")));
                yield return null;
            }
            ((Sandbox.ModAPI.IMyFunctionalBlock)turret).Enabled = false;
            var byTurret = wolves.Count(w => w.IsDead && hurtByTurret[w] > 0);
            Note($"after the watch: {byTurret} of {wolves.Count} wolves killed by the turret, {killed.Count} dead in all; the turret shot {shooting / 60.0:0.0} s " +
                 $"({shootingOff / 60.0:0.0} s with the barrel more than 25 degrees off the target; off by {(aims.Count == 0 ? "-" : $"{aims.Average():0} on average")}), " +
                 $"{switches} target changes, magazines {startAmmo:0} -> {Ammo():0}");
            Check(byTurret == wolves.Count, $"the turret killed {byTurret} of {wolves.Count} wolves round it in {WatchSeconds:0} s " +
                                            $"(shot {shooting / 60.0:0.0} s, {shootingOff / 60.0:0.0} s of it off the target)");
        }

        /// <summary>How far the barrel points off the target, degrees.</summary>
        private static double Angle(Sandbox.Game.Entities.Interfaces.IMyTargetingReceiver receiver, VRage.Game.Entity.MyEntity target)
        {
            var to = Vector3D.Normalize(target.PositionComp.WorldAABB.Center - receiver.ShootOrigin);
            return MathHelper.ToDegrees(System.Math.Acos(MathHelper.Clamp(Vector3D.Dot(receiver.ShootDirection, to), -1, 1)));
        }

        private MyCubeGrid SpawnTurret(Vector3D at, Vector3D up, Vector3D side)
        {
            var owner = WorldApi.TestIdentityId();
            var battery = WorldApi.MakeBlockOb("LargeBlockBatteryBlock");
            battery.Min = new SerializableVector3I(0, 0, 0);
            if (battery is MyObjectBuilder_BatteryBlock charged)
            {
                charged.CurrentStoredPower = 3f;
                charged.ProducerEnabled = true;
            }
            var turret = WorldApi.MakeBlockOb("LargeInteriorTurret");
            turret.Min = new SerializableVector3I(0, 1, 0);
            var blocks = new List<MyObjectBuilder_CubeBlock> { battery, turret };
            foreach (var block in blocks)
            {
                block.Owner = owner;
                block.BuiltBy = owner;
            }
            var ob = new MyObjectBuilder_CubeGrid
            {
                Name = WorldApi.EntityPrefix + Prefix + "grid",
                DisplayName = WorldApi.EntityPrefix + Prefix + "grid",
                GridSizeEnum = MyCubeSize.Large,
                IsStatic = true,
                PositionAndOrientation = new MyPositionAndOrientation(MatrixD.CreateWorld(at, side, up)),
                PersistentFlags = MyPersistentEntityFlags2.InScene,
                CubeBlocks = blocks,
            };
            var grid = WorldApi.SpawnGrid(ob);
            Track(grid);
            return grid;
        }

        public override void Cleanup()
        {
            try
            {
                FakeClients.RemoveAll();
                _optimisations.Restore();
            }
            finally { base.Cleanup(); }
        }
    }
}
