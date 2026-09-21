using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.ObjectBuilders;
using Sandbox.Common.ObjectBuilders;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// Explosions as SentisGameplayImprovements makes them (ExplosionTweaks): a warhead going off on
    /// a slab of light armour, a pile of explosives going off on the same slab, and the loot the
    /// destroyed blocks leave behind.
    ///
    /// The slab is a static 30 x 30 grid of light armour - it lets a blast reach its full radius - with a few batteries, a kilometre up in the
    /// atmosphere with a player beside it (Havok steps only a cluster somebody is in). Part of it,
    /// well inside the warhead's reach, is covered by a safe zone that forbids damage.
    ///
    ///  0. A warhead on a block of armor ten layers deep: blocks are destroyed, nothing past the
    ///     radius is touched, and the blast goes deeper only through blocks it destroys - every
    ///     block it hits has a neighbour towards the blast that is gone.
    ///  1. The warhead, in the middle of the slab: blocks are destroyed, nothing is touched farther
    ///     from the explosion than its radius, and nothing inside the safe zone.
    ///  2. The explosives (the plugin's replacement for the game's own explosion): damage is done,
    ///     and again nothing past the radius.
    ///  3. Loot: destroyed blocks drop some of their components around the slab.
    ///
    /// The frames of each explosion are measured.
    /// </summary>
    public sealed class ExplosionScenario : TestScenario
    {
        public const string ScenarioName = "explosions";
        private const string Prefix = "boom-";
        private const int Size = 30;
        private const double AltitudeM = 1000;
        private const double SafeZoneRadius = 10;   // the game's smallest zone (MySafeZone.MIN_RADIUS)
        private const double SafeZoneOffsetM = 14;  // the warhead itself stays outside: a warhead in such a zone does not go off
        private const double ExplosivesOffsetM = 26;
        private const double BlockOffsetM = 300;    // far enough from the slab that neither blast reaches the other
        private const int BlockLayers = 10;
        private const int ExplosivesAmount = 40;
        /// <summary>What ExplosionTweaks makes the radius of a pile of explosives, whatever its size.</summary>
        private const double ExplosivesRadius = 15;
        private const double SettleSeconds = 5;
        private const double BlastSeconds = 5;
        private const double LootSeconds = 10;

        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };

        private readonly ConfigOverride _optimisations = new ConfigOverride(ConfigOverride.Optimisations);
        private readonly ConfigOverride _gameplay = new ConfigOverride(ConfigOverride.Gameplay);
        private MyEntity _safeZone;

        // Floating objects that appeared near the test grids. Counted as they are added: the slab
        // hangs a kilometre up, and loot falls out of any sphere around it within seconds.
        private readonly HashSet<long> _spawnedNear = new HashSet<long>();
        private readonly List<Vector3D> _lootSites = new List<Vector3D>();
        private Action<MyEntity> _onEntityAdd;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 240;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            _optimisations.Set("FreezerEnabled", false);
            _optimisations.Set("EnablePhysicsGuard", false);
            _gameplay.Set("ExplosionTweaks", true);
            _gameplay.Set("LootSystemEnabled", true);
            // The plugin cancels every bit of damage for the first seconds after a start, and a bench
            // run right after a restart would measure nothing.
            _gameplay.Set("DisableAnyDamageAfterStartTime", 0);

            // ------------------------------------------------------------- where
            var anchorM = WorldApi.LoadAuthoredGroup(WheelPerfScenario.ResourceName, WorldApi.EntityPrefix + Prefix + "anchor")[0]
                .PositionAndOrientation.Value.GetMatrix();
            var planet = MyGamePruningStructure.GetClosestPlanet(anchorM.Translation);
            Check(planet != null, "no planet");
            var up = Vector3D.Normalize(anchorM.Translation - planet.PositionComp.GetPosition());
            var side = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(up));
            var centre = anchorM.Translation + up * AltitudeM;

            _lootSites.Add(centre);
            _lootSites.Add(centre + side * BlockOffsetM);
            _onEntityAdd = entity =>
            {
                if (!(entity is MyFloatingObject floating)) return;
                var at = floating.PositionComp.GetPosition();
                if (_lootSites.Any(site => Vector3D.Distance(site, at) < 150)) _spawnedNear.Add(floating.EntityId);
            };
            MyEntities.OnEntityAdd += _onEntityAdd;

            FakeClients.RemoveAll();
            FakeClients.Add(1, Network, p => (centre + up * 60 + side * 40, 0, 0), withCharacters: true);
            var arrive = WaitForSeconds(5, "the player arrives");
            while (arrive.MoveNext()) yield return arrive.Current;

            // ------------------------------------------------------------- a thick block
            // In a block of armor every ray crosses many blocks on its way to the centre: this is
            // where what one ray learned has to be kept for the next, and where the blast must go
            // deeper only through blocks it destroys. First, so that a failure further on does not
            // hide how long it took.
            var blockCentre = centre + side * BlockOffsetM;
            var thick = BuildSlab(blockCentre, up, side, BlockLayers, "block");
            var settleBlock = WaitForSeconds(SettleSeconds, "the armor block settles");
            while (settleBlock.MoveNext()) yield return settleBlock.Current;
            var beforeBlock = Snapshot(thick);
            var blockMatrix = thick.WorldMatrix;
            var bomb2 = SpawnWarhead(blockCentre + up * 2.5, up, side, "bomb2");
            var warhead2 = WorldApi.FindFunctional<MyWarhead>(bomb2);
            Check(warhead2 != null, "the second bomb has no warhead");
            var blast2Centre = warhead2.PositionComp.GetPosition();
            TickMetrics.Take();
            FrameProbe.Take();
            ((Sandbox.ModAPI.IMyWarhead)warhead2).IsArmed = true;
            ((Sandbox.ModAPI.IMyWarhead)warhead2).Detonate();
            var blast = WaitForSeconds(BlastSeconds, "the warhead on the armor block goes off");
            while (blast.MoveNext()) yield return blast.Current;
            var blockFrames = TickMetrics.Take();
            var blockWork = FrameProbe.Take();
            var blockHit = Compare(beforeBlock, Snapshot(thick), thick, blockMatrix, blast2Centre, ExplosionRadius(warhead2), blockCentre + side * 1000);
            Note("armor block (" + beforeBlock.Count + " blocks): " + blockHit + " | " + blockFrames.Format() + " | " + blockWork);
            Check(blockHit.Destroyed > 0, "the warhead on the armor block destroyed nothing: " + blockHit);
            var behindStanding = HitBehindStanding(thick, beforeBlock, blast2Centre);
            Note("armor block: " + behindStanding + " blocks hit behind blocks that stand");
            Check(behindStanding == 0, "the blast went on through blocks it did not destroy: " + behindStanding +
                                       " blocks hit with every neighbour towards the blast still standing");
            Check(blockHit.BeyondRadius == 0,
                "the warhead on the armor block damaged " + blockHit.BeyondRadius + " blocks beyond its radius: " + blockHit);

            // ------------------------------------------------------------- the slab
            var slab = BuildSlab(centre, up, side);
            var gridSize = slab.GridSize;
            var zoneCentre = centre + side * SafeZoneOffsetM;
            _safeZone = SpawnSafeZone(zoneCentre, SafeZoneRadius);
            var settle = WaitForSeconds(SettleSeconds, "the slab and the safe zone settle");
            while (settle.MoveNext()) yield return settle.Current;
            var before = Snapshot(slab);
            // Where every block was: a blast that cuts the slab apart moves the pieces, and the
            // grid's own matrix no longer says where the blocks stood.
            var slabMatrix = slab.WorldMatrix;
            Note("slab: " + before.Count + " blocks, safe zone of " + SafeZoneRadius + " m at " + SafeZoneOffsetM + " m from the middle");

            // ------------------------------------------------------------- the warhead
            var bomb = SpawnWarhead(centre + up * gridSize, up, side);
            var warhead = WorldApi.FindFunctional<MyWarhead>(bomb);
            Check(warhead != null, "the bomb has no warhead");
            var radius = ExplosionRadius(warhead);
            var blastCentre = warhead.PositionComp.GetPosition();
            Note("warhead: radius " + radius.ToString("F1") + " m");

            TickMetrics.Take();
            FrameProbe.Take();
            ((Sandbox.ModAPI.IMyWarhead)warhead).IsArmed = true;
            ((Sandbox.ModAPI.IMyWarhead)warhead).Detonate();
            blast = WaitForSeconds(BlastSeconds, "the warhead goes off");
            while (blast.MoveNext()) yield return blast.Current;
            var warheadFrames = TickMetrics.Take();
            var warheadWork = FrameProbe.Take();

            var exploded = typeof(MyWarhead).GetField("m_isExploded", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(warhead);
            Note("warhead after: exploded " + exploded + ", functional " + warhead.IsFunctional + ", block gone " + (bomb.BlocksCount == 0 || bomb.MarkedForClose) +
                 ", radius " + ExplosionRadius(warhead).ToString("F1") + " m");
            if (radius <= 0) radius = ExplosionRadius(warhead);
            var afterWarhead = Snapshot(slab);
            var warheadHit = Compare(before, afterWarhead, slab, slabMatrix, blastCentre, radius, zoneCentre);
            Note("warhead: " + warheadHit + " | " + warheadFrames.Format());
            Check(warheadHit.Destroyed > 0, "the warhead destroyed nothing: " + warheadHit);
            Check(warheadHit.BeyondRadius == 0,
                "the warhead damaged " + warheadHit.BeyondRadius + " blocks farther than its radius, up to " +
                warheadHit.FarthestM.ToString("F1") + " m: " + warheadHit);
            Check(warheadHit.InSafeZone == 0, "the warhead damaged " + warheadHit.InSafeZone + " blocks inside the safe zone");

            // ------------------------------------------------------------- the explosives
            var pilePos = centre - side * ExplosivesOffsetM + up * gridSize;
            MyFloatingObject pile = null;
            MyFloatingObjects.Spawn(new MyPhysicalInventoryItem(ExplosivesAmount,
                    new MyObjectBuilder_Component { SubtypeName = "Explosives" }),
                pilePos, Vector3D.CalculatePerpendicularVector(up), up, null, e => pile = e as MyFloatingObject);
            var landed = Wait(() => pile != null, "the explosives are there", 10);
            while (landed.MoveNext()) yield return landed.Current;
            var beforePile = Snapshot(slab);
            var pileCentre = pile.PositionComp.GetPosition();
            const double pileRadius = ExplosivesRadius;
            TickMetrics.Take();
            FrameProbe.Take();
            pile.DoDamage(999, MyDamageType.Explosion, true, 0, null);
            blast = WaitForSeconds(BlastSeconds, "the explosives go off");
            while (blast.MoveNext()) yield return blast.Current;
            var pileFrames = TickMetrics.Take();
            FrameProbe.Take();
            var afterPile = Snapshot(slab);
            var pileHit = Compare(beforePile, afterPile, slab, slabMatrix, pileCentre, pileRadius, zoneCentre);
            Note("explosives: " + pileHit + " | " + pileFrames.Format());
            Check(pile.MarkedForClose || pile.Closed, "the explosives did not go off");
            Check(pileHit.Damaged + pileHit.Destroyed > 0, "the explosives did nothing to the slab: " + pileHit);
            Check(pileHit.BeyondRadius == 0,
                "the explosives damaged " + pileHit.BeyondRadius + " blocks farther than " + pileRadius + " m, up to " +
                pileHit.FarthestM.ToString("F1") + " m: " + pileHit);

            // ------------------------------------------------------------- the loot
            // the pile of explosives is a floating object too, not loot
            var pileId = pile.EntityId;
            int Dropped() => _spawnedNear.Count(id => id != pileId);
            var loot = Wait(() => Dropped() > 0, "loot lands", (int)LootSeconds);
            while (loot.MoveNext()) yield return loot.Current;
            var lootLater = WaitForSeconds(3, "the rest of the loot lands");
            while (lootLater.MoveNext()) yield return lootLater.Current;
            var dropped = Dropped();
            Check(dropped > 0, "the destroyed blocks dropped nothing");

            Note("EXPLOSIONS RESULT | armor block: " + blockHit + " | " + blockWork +
                 " | warhead r=" + radius.ToString("F1") + " m: " + warheadHit +
                 " | explosives r=" + pileRadius.ToString("F1") + " m: " + pileHit +
                 " | loot " + dropped + " floating objects | warhead frames " + warheadFrames.Format() + " | " + warheadWork +
                 " | explosives frames " + pileFrames.Format());
        }

        // ------------------------------------------------------------------ the rig

        /// <summary>A static block of armor Size x Size wide and <paramref name="layers"/> deep, its top layer at <paramref name="centre"/>.</summary>
        private MyCubeGrid BuildSlab(Vector3D centre, Vector3D up, Vector3D side, int layers = 1, string name = "slab")
        {
            var blocks = new List<MyObjectBuilder_CubeBlock>();
            var owner = WorldApi.PlayerIdentityId();
            for (var y = 0; y < layers; y++)
            for (var x = 0; x < Size; x++)
                for (var z = 0; z < Size; z++)
                {
                    // a battery every so often: blocks with a body of their own take the other path
                    var battery = x % 7 == 3 && z % 7 == 3 && y % 4 == 0;
                    var block = WorldApi.MakeBlockOb(battery ? "LargeBlockBatteryBlock" : "LargeBlockArmorBlock");
                    block.Min = new SerializableVector3I(x - Size / 2, -y, z - Size / 2);
                    block.Owner = owner;
                    block.BuiltBy = owner;
                    blocks.Add(block);
                }
            var ob = new MyObjectBuilder_CubeGrid
            {
                Name = WorldApi.EntityPrefix + Prefix + name,
                DisplayName = WorldApi.EntityPrefix + Prefix + name,
                GridSizeEnum = MyCubeSize.Large,
                IsStatic = true,
                PositionAndOrientation = new MyPositionAndOrientation(MatrixD.CreateWorld(centre, side, up)),
                PersistentFlags = MyPersistentEntityFlags2.InScene,
                CubeBlocks = blocks,
            };
            var grid = WorldApi.SpawnGrid(ob);
            Track(grid);
            return grid;
        }

        private MyCubeGrid SpawnWarhead(Vector3D at, Vector3D up, Vector3D side, string name = "bomb")
        {
            var block = WorldApi.MakeBlockOb("LargeWarhead");
            var owner = WorldApi.PlayerIdentityId();
            block.Owner = owner;
            block.BuiltBy = owner;
            var ob = new MyObjectBuilder_CubeGrid
            {
                Name = WorldApi.EntityPrefix + Prefix + name,
                DisplayName = WorldApi.EntityPrefix + Prefix + name,
                GridSizeEnum = MyCubeSize.Large,
                IsStatic = true,
                PositionAndOrientation = new MyPositionAndOrientation(MatrixD.CreateWorld(at, side, up)),
                PersistentFlags = MyPersistentEntityFlags2.InScene,
                CubeBlocks = new List<MyObjectBuilder_CubeBlock> { block },
            };
            var grid = WorldApi.SpawnGrid(ob);
            Track(grid);
            return grid;
        }

        private static MyEntity SpawnSafeZone(Vector3D at, double radius)
        {
            var ob = new MyObjectBuilder_SafeZone
            {
                PositionAndOrientation = new MyPositionAndOrientation(MatrixD.CreateTranslation(at)),
                PersistentFlags = MyPersistentEntityFlags2.InScene,
                Shape = MySafeZoneShape.Sphere,
                Radius = (float)radius,
                Enabled = true,
                IsVisible = true,
                AllowedActions = 0,     // nothing - in particular no damage
                DisplayName = WorldApi.EntityPrefix + Prefix + "zone",
            };
            return MyEntities.CreateFromObjectBuilderAndAdd(ob, false);
        }

        private static double ExplosionRadius(MyWarhead warhead)
        {
            var field = typeof(MyWarhead).GetField("m_explosionFullSphere",
                BindingFlags.Instance | BindingFlags.NonPublic);
            return field == null ? 0 : ((BoundingSphereD)field.GetValue(warhead)).Radius;
        }

        // ------------------------------------------------------------------ what the blast did

        private static Dictionary<Vector3I, float> Snapshot(MyCubeGrid grid) =>
            grid.CubeBlocks.ToDictionary(b => b.Position, b => b.Integrity);

        /// <summary>
        /// Blocks the blast reached through a block that still stands. The blast only goes deeper
        /// through a block it destroys, so every block it hits must have, on its side facing the
        /// blast, a neighbour that is not there any more - destroyed, or open space from the start.
        /// For a grid the blast did not move.
        /// </summary>
        private static int HitBehindStanding(MyCubeGrid grid, Dictionary<Vector3I, float> before, Vector3D blastCentre)
        {
            var count = 0;
            var blast = grid.WorldToGridScaledLocal(blastCentre);
            foreach (var pair in before)
            {
                var now = grid.GetCubeBlock(pair.Key);
                if (now != null && now.Integrity >= pair.Value - 0.01f) continue;   // not hit
                var distance = Vector3D.DistanceSquared(pair.Key, blast);
                var opening = false;
                foreach (var step in Base6Directions.IntDirections)
                {
                    var cell = pair.Key + step;
                    if (Vector3D.DistanceSquared(cell, blast) >= distance) continue;   // the far side
                    if (grid.GetCubeBlock(cell) == null) { opening = true; break; }
                }
                if (opening) continue;
                if (count++ < 5)
                    Log.Info("[TEST:explosions] hit behind standing blocks: " + pair.Key + " " +
                             (now == null ? "destroyed" : pair.Value.ToString("F0") + "->" + now.Integrity.ToString("F0")));
            }
            return count;
        }

        private sealed class Hit
        {
            public int Destroyed, Damaged, BeyondRadius, InSafeZone, CutOff;
            public double FarthestM;

            public override string ToString() =>
                Destroyed + " destroyed, " + Damaged + " damaged, " + CutOff + " cut off, " + BeyondRadius + " hit beyond the radius, " +
                InSafeZone + " hit in the safe zone, farthest hit " + FarthestM.ToString("F1") + " m";
        }

        private static Hit Compare(Dictionary<Vector3I, float> before, Dictionary<Vector3I, float> after, MyCubeGrid grid,
            MatrixD gridMatrix, Vector3D centre, double radius, Vector3D zoneCentre)
        {
            var hit = new Hit();
            // pieces the blast cut off the slab: their blocks are not gone, just on another grid
            var pieces = MyEntities.GetEntities().OfType<MyCubeGrid>()
                .Where(g => g != grid && !g.MarkedForClose && Vector3D.Distance(g.PositionComp.GetPosition(), centre) < 200).ToList();
            foreach (var pair in before)
            {
                float now;
                var gone = !after.TryGetValue(pair.Key, out now);
                if (!gone && now >= pair.Value - 0.01f) continue;
                var at = Vector3D.Transform((Vector3D)pair.Key * grid.GridSize, gridMatrix);
                if (gone)
                {
                    var moved = pieces.Select(g => g.GetCubeBlock(g.WorldToGridInteger(at))).FirstOrDefault(b => b != null);
                    if (moved != null)
                    {
                        hit.CutOff++;
                        if (moved.Integrity >= pair.Value - 0.01f) continue;
                        gone = false;
                    }
                }
                if (gone) hit.Destroyed++;
                else hit.Damaged++;
                var distance = Vector3D.Distance(at, centre);
                hit.FarthestM = Math.Max(hit.FarthestM, distance);
                if (distance > radius) hit.BeyondRadius++;
                if (Vector3D.Distance(at, zoneCentre) < SafeZoneRadius - grid.GridSize * 0.5) hit.InSafeZone++;
            }
            return hit;
        }

        public override void Cleanup()
        {
            try
            {
                if (_onEntityAdd != null) MyEntities.OnEntityAdd -= _onEntityAdd;
                FakeClients.RemoveAll();
                if (_safeZone != null && !_safeZone.MarkedForClose) _safeZone.Close();
                _gameplay.Restore();
                _optimisations.Restore();
            }
            finally { base.Cleanup(); }
        }
    }
}
