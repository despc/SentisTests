using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using Sandbox.Game.World;
using SentisTests.Core;
using SentisTests.Game;
using VRage.Game;
using VRage.Game.ModAPI;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// The virtual reputation of SentisAdventures for breaking a faction's blocks, on a world with the factions set up
    /// (<c>!vr add_f</c>, <c>!vr computer_cost</c>, <c>!vr relate</c>). A static grid of the faction's (<c>faction=ORD</c>,
    /// owned by its founder) out of gravity: a battery (has computers) and a light armor block (has none). A fake
    /// player's character breaks the armor block, then the battery in steps of a tenth of its integrity, then hits the
    /// broken battery once more. Expected: the armor costs nothing; the battery costs the faction's computer block price
    /// once; every related faction moves by that change × its coefficient. The player's reputations are put back after.
    /// </summary>
    public sealed class VirtualRepDamageScenario : TestScenario
    {
        public const string ScenarioName = "vr_damage";
        public override string Name => ScenarioName;
        public override string[] Requires => new[] { "SentisAdventures" };

        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private readonly ConfigOverride _gameplay = new ConfigOverride(ConfigOverride.Gameplay);
        private readonly List<Action> _restore = new List<Action>();
        private MyCubeGrid _grid;

        public override IEnumerator Run()
        {
            var tag = System.Text.RegularExpressions.Regex.Match(TestRunner.Focus ?? "", @"faction=(\w+)") is var m && m.Success ? m.Groups[1].Value : "ORD";
            var faction = MySession.Static.Factions.TryGetFactionByTag(tag);
            SkipUnless(faction != null, "no faction " + tag + " in the world");

            var pluginType = Integrations.TypeOf("SentisAdventures", "SentisAdventures.SentisAdventuresPlugin");
            var core = pluginType.GetProperty("Instance", Any).GetValue(null);
            core = core.GetType().GetField("VirtualRepCore", Any).GetValue(core);
            var repo = core.GetType().GetMethod("GetVirtualReputationRepository").Invoke(core, null);
            var registered = repo.GetType().GetMethod("GetFactionById").Invoke(repo, new object[] { faction.FactionId });
            SkipUnless(registered != null, "faction " + tag + " is not in the virtual reputation (!vr add_f)");
            var price = (int)registered.GetType().GetProperty("ComputerBlockRepCost").GetValue(registered);
            var related = ((IEnumerable)repo.GetType().GetMethod("FindRelatedFactions").Invoke(repo, new object[] { faction.FactionId })).Cast<object>()
                .Select(r =>
                {
                    var id1 = (long)r.GetType().GetProperty("FactionId1").GetValue(r);
                    var id2 = (long)r.GetType().GetProperty("FactionId2").GetValue(r);
                    return (Id: id1 == faction.FactionId ? id2 : id1, Q: (float)r.GetType().GetProperty("FactionsRelationQ").GetValue(r));
                }).ToList();
            Note("faction " + tag + ": a block with a computer costs " + price + "; related: " +
                 string.Join(", ", related.Select(r => (MySession.Static.Factions.TryGetFactionById(r.Id)?.Tag ?? r.Id.ToString()) + " × " + r.Q)));

            _gameplay.Set("DisableAnyDamageAfterStartTime", 0);
            var at = Spot();
            FakeClients.RemoveAll();
            FakeClients.Add(1, new FakeClients.NetworkProfile { RttMs = 50 }, i => (at + Vector3D.Backward * 30, 0, 0), withCharacters: true);
            yield return Wait(() => FakeClients.HasLiveCharacter(0), "the fake player's character", 60);
            var character = FakeClients.Character(0);
            var me = FakeClients.PlayerOf(0).Identity.IdentityId;

            var factions = new[] { faction.FactionId }.Concat(related.Select(r => r.Id)).ToList();
            int Rep(long factionId) => (int)core.GetType().GetMethod("GetRepOfPlayerAndFaction").Invoke(core, new object[] { me, factionId });
            var before = factions.ToDictionary(f => f, Rep);
            _restore.Add(() =>
            {
                foreach (var pair in before)
                    core.GetType().GetMethod("SetPlayerFactionRepAlone").Invoke(core, new object[] { me, pair.Key, pair.Value, "vr_damage check" });
            });
            string Reps() => string.Join(", ", factions.Select(f => (MySession.Static.Factions.TryGetFactionById(f)?.Tag ?? "?") + " " + Rep(f)));

            var ob = WorldApi.GridOb(WorldApi.EntityPrefix + "vr-damage", MyCubeSize.Large, true, at, new List<BlockSpec>
            {
                new BlockSpec("LargeBlockBatteryBlock", Vector3I.Zero),
                new BlockSpec("LargeBlockArmorBlock", new Vector3I(0, 0, 4)),
            });
            _grid = WorldApi.SpawnGrid(ob);
            Track(_grid);
            _grid.ChangeGridOwnership(faction.FounderId, MyOwnershipShareModeEnum.Faction);
            yield return WaitForSeconds(1, "the grid settles");
            var battery = _grid.GetBlocks().First(b => b.BlockDefinition.Id.SubtypeName == "LargeBlockBatteryBlock");
            var armor = _grid.GetBlocks().First(b => b.BlockDefinition.Id.SubtypeName == "LargeBlockArmorBlock");
            Note("owner " + faction.FounderId + ", the grid's big owner " + (_grid.BigOwners.FirstOrDefault()) + "; before: " + Reps());

            // the armor block: no computer, nothing
            var start = before.ToDictionary(p => p.Key, p => p.Value);
            while (!armor.IsDestroyed && armor.Integrity > 0 && !_grid.MarkedForClose)
            {
                Hit(armor, character);
                yield return WaitForTicks(2);
            }
            yield return WaitForSeconds(3, "the reputation settles");
            var afterArmor = factions.ToDictionary(f => f, Rep);
            Note("armor broken: " + Reps());
            Check(factions.All(f => afterArmor[f] == start[f]), "breaking a light armor block changed the reputation: " + Reps());

            // the battery: down past its critical integrity, then one hit more on the broken block
            var hits = 0;
            while (battery.IsFunctional && hits++ < 30)
            {
                Hit(battery, character);
                yield return WaitForTicks(2);
            }
            Check(!battery.IsFunctional, "the battery did not break in 30 hits");
            Hit(battery, character);
            yield return WaitForSeconds(3, "the reputation settles");
            var after = factions.ToDictionary(f => f, Rep);
            Note("battery broken in " + hits + " hits and hit once more: " + Reps());

            var expected = Clamp(registered, start[faction.FactionId] - price);
            Check(after[faction.FactionId] == expected, tag + ": " + start[faction.FactionId] + " -> " + after[faction.FactionId] + ", expected " + expected);
            var delta = expected - start[faction.FactionId];
            foreach (var r in related)
            {
                var wanted = start[r.Id] + (int)Math.Round(delta * (double)r.Q, MidpointRounding.AwayFromZero);
                var other = repo.GetType().GetMethod("GetFactionById").Invoke(repo, new object[] { r.Id });
                wanted = other == null ? wanted : Clamp(other, wanted);
                var otherTag = MySession.Static.Factions.TryGetFactionById(r.Id)?.Tag ?? r.Id.ToString();
                Check(after[r.Id] == wanted, otherTag + ": " + start[r.Id] + " -> " + after[r.Id] + ", expected " + wanted);
            }
            Note("VR DAMAGE OK | " + tag + " " + delta + "; " + string.Join(", ", related.Select(r =>
                (MySession.Static.Factions.TryGetFactionById(r.Id)?.Tag ?? "?") + " " + (after[r.Id] - start[r.Id]))));
        }

        private static int Clamp(object faction, int rep)
        {
            var min = (int)faction.GetType().GetProperty("MinReputation").GetValue(faction);
            var max = (int)faction.GetType().GetProperty("MaxReputation").GetValue(faction);
            if (rep < min) return min;
            if (max > min && rep > max) return max;
            return rep;
        }

        /// <summary>A tenth of the block's integrity, as from the character's gun.</summary>
        private static void Hit(MySlimBlock block, VRage.ModAPI.IMyEntity attacker) =>
            ((IMySlimBlock)block).DoDamage(block.MaxIntegrity * 0.1f, MyDamageType.Bullet, true, null, attacker.EntityId);

        private static Vector3D Spot()
        {
            var anchor = WorldApi.PlanetAnchor(WorldApi.EntityPrefix + "regression-anchor").Translation;
            var planet = MyGamePruningStructure.GetClosestPlanet(anchor);
            var up = planet == null ? Vector3D.Up : Vector3D.Normalize(anchor - planet.PositionComp.GetPosition());
            for (var d = 300000.0; d <= 3000000; d += 50000)
            {
                var at = anchor + up * d + Vector3D.Forward * 250000;
                if (MyGravityProviderSystem.CalculateNaturalGravityInPoint(at).Length() < 0.001f) return at;
            }
            return anchor + up * 3000000;
        }

        public override void Cleanup()
        {
            try
            {
                foreach (var restore in _restore)
                    try { restore(); }
                    catch (Exception e) { Log.Error(e, "vr_damage: putting the reputation back"); }
                _restore.Clear();
                if (_grid != null && !_grid.MarkedForClose) _grid.Close();
                FakeClients.RemoveAll();
                _gameplay.Restore();
            }
            finally { base.Cleanup(); }
        }
    }
}
