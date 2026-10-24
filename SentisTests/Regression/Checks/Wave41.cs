using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using SentisTests.Game;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Utils;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>What the admin screen's tools take: the game's admin menu (<c>MyGuiScreenAdminMenu</c>) and its type.</summary>
    internal static class AdminMenu
    {
        public static readonly Type Type = typeof(MySession).Assembly.GetTypes().First(t => t.Name == "MyGuiScreenAdminMenu");

        public static object Action(string name) =>
            Enum.Parse(typeof(MyEntityList).GetNestedTypes(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                .First(t => t.Name == "EntityListAction"), name);

        public static void Promote(ulong steamId, MyPromoteLevel level)
        {
            MySession.Static.SetUserPromoteLevel(steamId, level);
            if (level == MyPromoteLevel.None) MySession.Static.RemoteAdminSettings.Remove(steamId);
        }
    }

    /// <summary>
    /// The admin screen's entity list (<c>MyGuiScreenAdminMenu.ProceedEntitiesAction_Implementation</c>, an admin's
    /// request): on a moving ship of the list, "Stop" stops it, "Depower" switches its power producers off and "Power" on
    /// again, "Remove" takes it out of the world.
    /// </summary>
    public sealed class AdminEntityActionsCheck : RegressionCheck
    {
        public override string Name => Prefix + "admin_entity_actions";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 0, -20));
            AdminMenu.Promote(Player.Id.SteamId, MyPromoteLevel.Admin);
            var ship = Build("admin-ship", Site.Origin, false,
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, 0).With(b => Motion.Charged(b)),
                new Part("InteriorLight/SmallLight", 1, 0, 0));
            var battery = Block<MyBatteryBlock>(ship);
            var light = ship.GetFatBlocks().OfType<MyFunctionalBlock>().First(b => !(b is MyBatteryBlock));
            yield return Until(() => light.IsWorking ? 1 : 0, v => v > 0, "the ship's light working", 10, () => State(light));
            ship.Physics.LinearVelocity = Site.Forward * 10;
            yield return Seconds(1);
            void Act(string action) => Request(AdminMenu.Type, "ProceedEntitiesAction_Implementation", new List<long> { ship.EntityId }, AdminMenu.Action(action));

            Act("Stop");
            yield return WaitForTicks(2);
            var speed = Motion.Speed(ship);
            Check(speed < 0.5, "the ship goes on at " + F(speed) + " m/s after the admin stopped it");

            Act("Depower");
            yield return Until(() => !battery.Enabled && !light.IsWorking ? 1 : 0, v => v > 0, "the ship depowered", 10, () => State(battery) + "; " + State(light));

            Act("Power");
            yield return Until(() => battery.Enabled && light.IsWorking ? 1 : 0, v => v > 0, "the ship powered again", 10, () => State(battery) + "; " + State(light));

            Act("Remove");
            yield return Until(() => ship.MarkedForClose || ship.Closed ? 1 : 0, v => v > 0, "the ship removed", 10);
            Result("admin's entity list on a ship: stopped (" + F(speed) + " m/s), depowered, powered again, removed");
        }

        public override void Cleanup()
        {
            try { if (Me >= 0) AdminMenu.Promote(Player.Id.SteamId, MyPromoteLevel.None); } catch { }
            base.Cleanup();
        }
    }

    /// <summary>
    /// An admin's own settings (<c>MyGuiScreenAdminMenu.AdminSettingsChanged</c>, the admin screen's checkboxes): set by
    /// an admin, "Invulnerable" keeps their character from harm and "Use terminals" opens another player's block to them;
    /// cleared, both are gone. A player who is not an admin may set none of them (the game allows a promote level its
    /// own share: <c>GetUserAllowedAdminSettings</c>).
    /// </summary>
    public sealed class AdminSettingsCheck : RegressionCheck
    {
        public override string Name => Prefix + "admin_settings";
        protected override bool NeedsDamage => true;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 0, -6));
            var other = -1;
            yield return Join(Site.At(4, 0, -6), i => other = i);
            var steam = Player.Id.SteamId;
            Check(MySession.Static.GetUserAllowedAdminSettings(steam) == AdminSettingsEnum.None,
                "a player who is no admin may set " + MySession.Static.GetUserAllowedAdminSettings(steam));
            var theirs = Block<MyTerminalBlock>(BuildFor(FakeClients.PlayerOf(other).Identity.IdentityId, "admin-theirs", Site.At(0, 0, 6),
                new Part("CargoContainer/LargeBlockSmallContainer", 0, 0, 0).With(b => b.ShareMode = MyOwnershipShareModeEnum.None)));
            Check(!theirs.HasPlayerAccess(Identity), "the other player's container is open to the player before any admin setting");

            AdminMenu.Promote(steam, MyPromoteLevel.Admin);
            yield return WaitForTicks(2);
            Request(AdminMenu.Type, "AdminSettingsChanged", AdminSettingsEnum.Invulnerable | AdminSettingsEnum.UseTerminals);
            Check(MySession.Static.RemoteAdminSettings.TryGetValue(steam, out var set) && set.HasFlag(AdminSettingsEnum.Invulnerable) && set.HasFlag(AdminSettingsEnum.UseTerminals),
                "the admin's settings did not take: " + set);
            var health = Body.StatComp.Health.Value;
            ((VRage.Game.ModAPI.Interfaces.IMyDestroyableObject)Body).DoDamage(30f, MyDamageType.Bullet, true);
            yield return WaitForTicks(2);
            var after = Body.StatComp.Health.Value;
            Check(Math.Abs(after - health) < 0.01, "the invulnerable admin took damage: health " + F(health) + " -> " + F(after));
            Check(theirs.HasPlayerAccess(Identity), "the other player's container is not open to the admin with \"Use terminals\"");

            Request(AdminMenu.Type, "AdminSettingsChanged", AdminSettingsEnum.None);
            ((VRage.Game.ModAPI.Interfaces.IMyDestroyableObject)Body).DoDamage(30f, MyDamageType.Bullet, true);
            yield return WaitForTicks(2);
            var hurt = Body.StatComp.Health.Value;
            Check(hurt < after - 1, "with the settings cleared the admin took no damage: health " + F(after) + " -> " + F(hurt));
            Check(!theirs.HasPlayerAccess(Identity), "with the settings cleared the other player's container is still open to the admin");
            Result("a player may set none; an admin's \"Invulnerable\" kept health " + F(after) + " of " + F(health) + " and \"Use terminals\" opened another's container; cleared: hurt to " + F(hurt) + ", the container closed");
        }

        public override void Cleanup()
        {
            try { if (Me >= 0) AdminMenu.Promote(Player.Id.SteamId, MyPromoteLevel.None); } catch { }
            base.Cleanup();
        }
    }

    /// <summary>
    /// An admin's tools for a player (the admin screen): teleport another player's character to a point
    /// (<c>MyMultiplayer.OnTeleport</c>), change their account's balance (<c>MyGuiScreenAdminMenu.RequestChange</c>) and
    /// their reputation with an NPC faction (<c>RequestChangeReputation</c>).
    /// </summary>
    public sealed class AdminPlayerToolsCheck : RegressionCheck
    {
        public override string Name => Prefix + "admin_player_tools";

        protected override IEnumerator Check()
        {
            var npc = MySession.Static.Factions.TryGetPlayerFaction(Npcs.Trader())?.FactionId ?? 0;
            SkipUnless(npc != 0, "no NPC trader faction in this world");
            yield return Arrive(Site.At(0, 0, -6));
            var other = -1;
            yield return Join(Site.At(4, 0, -6), i => other = i);
            var them = FakeClients.PlayerOf(other);
            var theirId = them.Identity.IdentityId;
            AdminMenu.Promote(Player.Id.SteamId, MyPromoteLevel.Admin);
            yield return WaitForTicks(2);

            var to = Site.At(60, 0, 0);
            Request(typeof(Sandbox.Engine.Multiplayer.MyMultiplayer), "OnTeleport", them.Id.SteamId, to);
            var off = Vector3D.Distance(FakeClients.Character(other).PositionComp.GetPosition(), to);
            Check(off < 3, "the teleported player is " + F(off) + " m from the point");

            var money = Sandbox.Game.GameSystems.BankingAndCurrency.MyBankingSystem.GetBalance(theirId);
            Request(AdminMenu.Type, "RequestChange", theirId, 5000L);
            var gained = Sandbox.Game.GameSystems.BankingAndCurrency.MyBankingSystem.GetBalance(theirId) - money;
            Check(gained == 5000, "the admin's balance change gave " + gained + " of 5000");

            var factions = MySession.Static.Factions;
            var rep = factions.GetRelationBetweenPlayerAndFaction(theirId, npc).Item2;
            Request(AdminMenu.Type, "RequestChangeReputation", theirId, npc, 100, false);
            var repAfter = factions.GetRelationBetweenPlayerAndFaction(theirId, npc).Item2;
            Check(repAfter > rep, "the admin's reputation change left it at " + repAfter + " (was " + rep + ")");
            Result("teleported " + F(off) + " m from the point; balance +" + gained + "; reputation " + rep + " -> " + repAfter);
        }

        public override void Cleanup()
        {
            try { if (Me >= 0) AdminMenu.Promote(Player.Id.SteamId, MyPromoteLevel.None); } catch { }
            base.Cleanup();
        }
    }
}
