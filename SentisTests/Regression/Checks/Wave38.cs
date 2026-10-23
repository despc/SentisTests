using System;
using System.Collections;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using SentisTests.Game;
using VRage.Game.ObjectBuilders.Components;
using VRage.Game.ModAPI;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// The hand rifle aimed, reloaded and put away by the player's requests (<c>MyCharacter.EnableIronsightCallback</c>,
    /// <c>ReloadServer</c>, <c>UnequipWeapon</c>): on its iron sight, a rifle with 5 rounds left of its magazine reloads
    /// to a full one, off the sight, a magazine taken from the suit; put away, the character holds nothing.
    /// </summary>
    public sealed class RifleReloadCheck : HandCheck
    {
        public override string Name => Prefix + "rifle_reload";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 0, -3));
            Put(Inventory(Body), "AmmoMagazine/NATO_5p56x45mm", 3);
            yield return Equip("AutomaticRifleItem");
            var gun = (MyAutomaticRifleGun)Body.CurrentWeapon;
            // a magazine's rounds; the one in the gun (it may not be in yet under load) left with 5
            var full = gun.GunBase.WeaponProperties.AmmoMagazineDefinition.Capacity;
            gun.CurrentMagazineAmmunition = 5;
            var magazines = Amount(Inventory(Body), "AmmoMagazine/NATO_5p56x45mm");
            // the gun takes no reload for a while after it is drawn
            yield return Seconds(2);
            // aimed down the sights first: a reload takes the gun off them
            Request(Body, "EnableIronsightCallback", true, true, false, true, false);
            Check(Body.IsIronSighted, "the rifle is not on its iron sight");
            Request(Body, "ReloadServer");
            Check(gun.IsReloading, "the rifle with 5 rounds left did not start reloading");
            Check(!Body.IsIronSighted, "the reload left the rifle on its iron sight");
            yield return Until(() => !gun.IsReloading && gun.GunBase.CurrentAmmo == full ? 1 : 0, v => v > 0, "the magazine full again", 30,
                () => "rounds " + gun.GunBase.CurrentAmmo + " of " + full + ", reloading " + gun.IsReloading);
            var left = Amount(Inventory(Body), "AmmoMagazine/NATO_5p56x45mm");
            Check(Math.Abs(magazines - left - 1) < 0.01, "the reload took " + (magazines - left) + " magazines from the suit, not 1");

            Request(Body, "UnequipWeapon");
            yield return Until(() => Body.CurrentWeapon == null ? 1 : 0, v => v > 0, "the rifle put away", 5);
            Result("on the iron sight, 5 rounds left -> reloaded to " + full + " off the sight, taking one magazine from the suit; the rifle put away");
        }
    }

    /// <summary>
    /// A box of blocks razed at once (<c>MyCubeGrid.RazeBlocksAreaRequest</c>, the creative tools' area delete): a plain
    /// player's request is refused and the blocks stay; an admin with creative tools razes the box, the blocks out of it
    /// stay.
    /// </summary>
    public sealed class RazeAreaCheck : RegressionCheck
    {
        public override string Name => Prefix + "raze_area";

        private ulong _admin;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 0, -6));
            var grid = Build("raze-area", Site.Origin, true, Bodies.Wall("CubeBlock/LargeBlockArmorBlock", 4, 1));
            var blocks = grid.BlocksCount;
            var steamId = Player.Id.SteamId;
            Check(!MySession.Static.HasPlayerCreativeRights(steamId), "a plain player has creative rights in this world");
            var min = grid.Min;
            var size = new Vector3UByte(1, 0, 0);
            Request(grid, "RazeBlocksAreaRequest", min, size, Body.EntityId, steamId);
            yield return Seconds(1);
            Check(grid.BlocksCount == blocks, "a plain player's area delete razed " + (blocks - grid.BlocksCount) + " blocks");

            MySession.Static.SetUserPromoteLevel(steamId, MyPromoteLevel.Admin);
            _admin = steamId;
            MySession.Static.CreativeTools.Add(steamId);
            Request(grid, "RazeBlocksAreaRequest", min, size, Body.EntityId, steamId);
            // the rights for the request alone (the area is razed in it)
            MySession.Static.CreativeTools.Remove(steamId);
            MySession.Static.SetUserPromoteLevel(steamId, MyPromoteLevel.None);
            _admin = 0;
            yield return Until(() => blocks - grid.BlocksCount, v => v >= 2, "the box of 2 blocks razed", 5);
            Check(grid.BlocksCount == blocks - 2, "the admin's area delete razed " + (blocks - grid.BlocksCount) + " blocks, not the box's 2");
            Check(grid.GetCubeBlock(min) == null && grid.GetCubeBlock(min + Vector3I.UnitX) == null, "a block of the box is still there");
            Result("a plain player's area delete refused; an admin's razed the box of 2 blocks of " + blocks);
        }

        public override void Cleanup()
        {
            try
            {
                if (_admin != 0)
                {
                    MySession.Static.CreativeTools.Remove(_admin);
                    MySession.Static.SetUserPromoteLevel(_admin, MyPromoteLevel.None);
                }
            }
            catch { }
            base.Cleanup();
        }
    }

    /// <summary>
    /// The player's paint colour picked (<c>MyPlayerCollection.OnPlayerColorChangedRequest</c>, the colour picker's
    /// slot and colour): the slot becomes the selected one with the colour in it, and new paint uses it.
    /// </summary>
    public sealed class BuildColorCheck : RegressionCheck
    {
        public override string Name => Prefix + "build_color";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 0, -3));
            var color = new Vector3(0.55f, 0.4f, 0.3f);
            var slot = Player.BuildColorSlots.Count - 1;
            Request(typeof(Sandbox.Game.Multiplayer.MyPlayerCollection), "OnPlayerColorChangedRequest", Player.Id.SerialId, slot, color);
            yield return WaitForTicks(2);
            Check(Player.SelectedBuildColorSlot == slot, "the selected slot is " + Player.SelectedBuildColorSlot + ", not " + slot);
            Check(Vector3.DistanceSquared(Player.BuildColorSlots[slot], color) < 1e-6f, "the slot holds " + Player.BuildColorSlots[slot] + ", not " + color);
            Check(Vector3.DistanceSquared(Player.SelectedBuildColor, color) < 1e-6f, "the selected colour is " + Player.SelectedBuildColor);
            Result("slot " + slot + " selected with the colour " + color);
        }
    }

    /// <summary>
    /// A safe zone block's zone set up from its terminal (<c>MySessionComponentSafeZones.UpdateSafeZone_ImplementationPlayer</c>,
    /// <c>UpdateSafeZoneRadius_ImplementationPlayer</c>): with the zone made (not yet switched on), welding forbidden in
    /// it, its radius set, then made a box - each takes; switched off, the zone is gone.
    /// </summary>
    public sealed class SafeZoneSettingsCheck : RegressionCheck
    {
        public override string Name => Prefix + "safe_zone_settings";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 6, -12));
            var grid = Build("safe-zone-settings", Site.Origin, true,
                new Part("SafeZoneBlock/SafeZoneBlock", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 2, 0, 0).With(b => Motion.Charged(b)),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 2, 1, 0).With(b => Motion.Charged(b)));
            var block = Block<SpaceEngineers.Game.Entities.Blocks.SafeZone.MySafeZoneBlock>(grid);
            Put(Inventory(block), "Component/ZoneChip", 3);
            var component = block.Components.Get<SpaceEngineers.Game.EntityComponents.Blocks.MySafeZoneComponent>();
            yield return Until(() => block.IsWorking ? 1 : 0, v => v > 0, "the safe zone block working", 10, () => State(block));
            Request(component, "OnSafezoneCreateRemove", true);
            MySafeZone zone = null;
            yield return Until(() => (zone = MyEntities.GetEntityById(component.SafeZoneEntityId) as MySafeZone) != null ? 1 : 0, v => v > 0, "the block's zone made", 10);
            var definition = (SpaceEngineers.Game.Definitions.SafeZone.MySafeZoneBlockDefinition)block.BlockDefinition;
            var zones = typeof(MySessionComponentSafeZones);

            var ob = (Sandbox.Common.ObjectBuilders.MyObjectBuilder_SafeZone)zone.GetObjectBuilder();
            ob.AllowedActions &= ~MySafeZoneAction.Welding;
            Request(zones, "UpdateSafeZone_ImplementationPlayer", block.EntityId, ob);
            Check((zone.AllowedActions & MySafeZoneAction.Welding) == 0, "welding is still allowed in the zone: " + zone.AllowedActions);

            var radius = (float)Math.Round((definition.MinSafeZoneRadius + definition.MaxSafeZoneRadius) / 2);
            Request(zones, "UpdateSafeZoneRadius_ImplementationPlayer", block.EntityId, zone.EntityId, radius);
            Check(Math.Abs(zone.Radius - radius) < 0.01f, "the zone's radius is " + zone.Radius + ", not " + radius);

            ob = (Sandbox.Common.ObjectBuilders.MyObjectBuilder_SafeZone)zone.GetObjectBuilder();
            ob.Shape = Sandbox.Common.ObjectBuilders.MySafeZoneShape.Box;
            ob.Size = new Vector3(radius);
            Request(zones, "UpdateSafeZone_ImplementationPlayer", block.EntityId, ob);
            Check(zone.Shape == Sandbox.Common.ObjectBuilders.MySafeZoneShape.Box && Math.Abs(zone.Size.X - radius) < 0.01f, "the zone is a " + zone.Shape + " of " + zone.Size);
            Check((zone.AllowedActions & MySafeZoneAction.Welding) == 0, "made a box, the zone allows welding again");

            Request(component, "OnSafezoneCreateRemove", false);
            yield return Until(() => zone.MarkedForClose || zone.Closed ? 1 : 0, v => v > 0, "the zone gone", 10);
            Result("welding forbidden, radius " + radius + " m, then a box of " + radius + " m - all in the block's zone; switched off, gone");
        }
    }

    /// <summary>
    /// A door opened by hand (<c>MyDoorBase.OpenRequest</c>, what pressing F at a door sends): the owner opens and
    /// closes it; another player, not in the owner's faction, cannot; with "anyone can use" on, they can.
    /// </summary>
    public sealed class DoorUseCheck : RegressionCheck
    {
        public override string Name => Prefix + "door_use";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 0, -3));
            int other = -1;
            yield return Join(Site.At(2, 0, -3), i => other = i);
            var stranger = FakeClients.PlayerOf(other).Identity.IdentityId;
            var grid = Build("door-use", Site.Origin, true,
                new Part("Door/", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, 2).With(b => Motion.Charged(b)));
            var door = Block<MyDoorBase>(grid);
            yield return Until(() => door.IsWorking ? 1 : 0, v => v > 0, "the door working", 10, () => State(door));
            Check(!door.Open, "the new door is open");

            Request(door, "OpenRequest", true, Identity);
            yield return WaitForTicks(2);
            Check(door.Open, "the owner's request did not open the door");
            Request(door, "OpenRequest", false, Identity);
            yield return WaitForTicks(2);
            Check(!door.Open, "the owner's request did not close the door");

            RequestAs(other, door, "OpenRequest", true, stranger);
            yield return WaitForTicks(2);
            Check(!door.Open, "a player out of the owner's faction opened the door");
            door.AnyoneCanUse = true;
            RequestAs(other, door, "OpenRequest", true, stranger);
            yield return WaitForTicks(2);
            Check(door.Open, "with anyone can use on, the other player did not open the door");
            Result("the owner opened and closed the door; another player could not, until anyone can use was on");
        }
    }

    /// <summary>
    /// A sound block and a jukebox as the terminal plays them (<c>MySoundBlock.PlaySound</c>, <c>StopSound</c>;
    /// <c>MyJukebox.OnAddSoundsToSelectionRequest</c>, <c>OnRemoveSoundsFromSelectionRequest</c>,
    /// <c>PlayJukeboxSound</c>, <c>StopJukeboxSound</c>): the server takes the sound block, a sound picked, playing and stopped (what the
    /// save keeps), the jukebox's list as songs are added and taken off, and it playing and stopped. The sound itself
    /// only a client plays.
    /// </summary>
    public sealed class SoundBlocksCheck : RegressionCheck
    {
        public override string Name => Prefix + "sound_blocks";

        private const System.Reflection.BindingFlags Any = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 4, -6));
            var grid = Build("sound-blocks", Site.Origin, true,
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, 0).With(b => Motion.Charged(b)),
                new Part("SoundBlock/LargeBlockSoundBlock", 1, 0, 0),
                new Part("Jukebox/Jukebox", -1, 0, 0));
            var sound = Block<SpaceEngineers.Game.Entities.Blocks.MySoundBlock>(grid);
            var jukebox = grid.GetFatBlocks().OfType<SpaceEngineers.Game.Entities.Blocks.MyJukebox>().FirstOrDefault();
            Check(jukebox != null, "the jukebox was not built");
            yield return Until(() => sound.IsWorking && jukebox.IsWorking ? 1 : 0, v => v > 0, "the sound block and the jukebox working", 10, () => State(sound) + "; " + State(jukebox));

            var playing = typeof(SpaceEngineers.Game.Entities.Blocks.MySoundBlock).GetField("m_isPlaying", Any);
            Check(playing != null, "MySoundBlock.m_isPlaying is not in this game");
            // a sound picked from the block's list (a synced setting of the terminal)
            var cue = Sandbox.Definitions.MyDefinitionManager.Static.GetSoundCategoryDefinitions().SelectMany(c => c.Sounds).FirstOrDefault()?.SoundId;
            SkipUnless(cue != null, "no sound block sounds in this game");
            sound.SelectSound(cue, true);
            Request(sound, "PlaySound");
            Check((bool)playing.GetValue(sound), "the sound block is not playing after its play request");
            Request(sound, "StopSound");
            Check(!(bool)playing.GetValue(sound), "the sound block still plays after its stop request");

            var selected = typeof(SpaceEngineers.Game.Entities.Blocks.MyJukebox).GetField("m_selectedSounds", Any)?.GetValue(jukebox) as System.Collections.Generic.List<string>;
            Check(selected != null, "MyJukebox.m_selectedSounds is not in this game");
            var before = selected.Count;
            Request(jukebox, "OnAddSoundsToSelectionRequest", new System.Collections.Generic.List<string> { "reg-song-a", "reg-song-b" });
            Check(selected.Count == before + 2, "the jukebox's list has " + selected.Count + " songs, not " + (before + 2));
            Request(jukebox, "OnRemoveSoundsFromSelectionRequest", new System.Collections.Generic.List<string> { "reg-song-a" });
            Check(selected.Count == before + 1 && selected.Contains("reg-song-b") && !selected.Contains("reg-song-a"), "after one song taken off the list is " + string.Join(", ", selected));
            Request(jukebox, "PlayJukeboxSound", false);
            Check(jukebox.IsJukeboxPlaying, "the jukebox is not playing after its play request");
            Request(jukebox, "StopJukeboxSound");
            Check(!jukebox.IsJukeboxPlaying, "the jukebox still plays after its stop request");
            Result("the sound block played and stopped; the jukebox took 2 songs, gave one back, played and stopped");
        }
    }

    /// <summary>
    /// A turret taken under a player's control and let go (<c>MyTurretController.RequestUseMessageStatic</c>, the
    /// terminal's "Control"; <c>MyLargeTurretBase.RequestRelease</c>): the owner, by the turret, controls it, then is
    /// back in their own character.
    /// </summary>
    public sealed class TurretManualCheck : RegressionCheck
    {
        public override string Name => Prefix + "turret_manual";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 4, -6));
            var grid = Build("turret-manual", Site.Origin, true,
                new Part("LargeGatlingTurret/", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, -1, 0).With(b => Motion.Charged(b)));
            var turret = Block<Sandbox.Game.Weapons.MyLargeTurretBase>(grid);
            yield return Until(() => turret.IsWorking ? 1 : 0, v => v > 0, "the turret working", 10, () => State(turret));
            var body = Body;
            Request(typeof(Sandbox.Game.EntityComponents.MyTurretController), "RequestUseMessageStatic", VRage.Game.Entity.UseObject.UseActionEnum.Manipulate, body.EntityId, turret.EntityId);
            yield return Until(() => Player.Controller.ControlledEntity == turret ? 1 : 0, v => v > 0, "the player controlling the turret", 10,
                () => "controlling " + (Player.Controller.ControlledEntity?.Entity?.DisplayName ?? "nothing"));
            Request(turret, "RequestRelease", false);
            yield return Until(() => Player.Controller.ControlledEntity == body ? 1 : 0, v => v > 0, "the player back in their character", 10,
                () => "controlling " + (Player.Controller.ControlledEntity?.Entity?.DisplayName ?? "nothing"));
            Result("the owner took the turret under control and let it go");
        }
    }

    /// <summary>
    /// The player's own toolbar as the G screen edits it (<c>MyToolBarCollection.OnNewToolbarRequest</c>,
    /// <c>OnChangeSlotItemRequest</c>, <c>OnClearSlotRequest</c>): the server keeps the player's toolbar, a block put in a
    /// slot is there, the slot cleared is empty.
    /// </summary>
    public sealed class PlayerToolbarCheck : RegressionCheck
    {
        public override string Name => Prefix + "player_toolbar";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 0, -3));
            var collection = typeof(Sandbox.Game.Multiplayer.MyToolBarCollection);
            var pid = Player.Id;
            if (!MySession.Static.Toolbars.ContainsToolbar(pid))
                Request(collection, "OnNewToolbarRequest", pid.SerialId);
            var toolbar = MySession.Static.Toolbars.TryGetPlayerToolbar(pid);
            Check(toolbar != null, "the server keeps no toolbar of the player");
            var slot = toolbar.SlotCount - 1;
            VRage.Game.DefinitionIdBlit armor = Definition("CubeBlock/LargeBlockArmorBlock").Id;
            Request(collection, "OnChangeSlotItemRequest", pid.SerialId, slot, armor);
            var item = toolbar.GetItemAtIndex(slot) as Sandbox.Game.Screens.Helpers.MyToolbarItemDefinition;
            Check(item != null && item.Definition.Id.SubtypeName == "LargeBlockArmorBlock", "slot " + slot + " holds " + (toolbar.GetItemAtIndex(slot)?.DisplayName?.ToString() ?? "nothing") + ", not the armor block");
            Request(collection, "OnClearSlotRequest", pid.SerialId, slot);
            Check(toolbar.GetItemAtIndex(slot) == null, "the cleared slot holds " + toolbar.GetItemAtIndex(slot)?.DisplayName);
            Result("the armor block put in slot " + slot + " of the player's toolbar and taken out again");
        }
    }

    /// <summary>
    /// A competitive drop container opened (<c>MyCubeGrid.CompetetiveContainerOpened</c>, what the client sends as the
    /// first player opens one): sent for the player as a competitive drop, the container's grid is there; opened, the
    /// container goes away after the time the drop system gives the others to see it claimed.
    /// </summary>
    public sealed class CompetitiveDropCheck : RegressionCheck
    {
        public override string Name => Prefix + "competitive_drop";

        private static readonly System.Reflection.BindingFlags Any = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
        private Sandbox.Game.SessionComponents.MySessionComponentContainerDropSystem _added;

        public override void Cleanup()
        {
            try { _added?.RemovePlayerDataForIdentity(Identity); }
            finally { base.Cleanup(); }
        }

        protected override IEnumerator Check()
        {
            SkipUnless(MySession.Static.Settings.EnableContainerDrops, "drop containers are off in this world");
            var drops = MySession.Static.GetComponent<Sandbox.Game.SessionComponents.MySessionComponentContainerDropSystem>();
            SkipUnless(drops != null, "no drop container system in this world");
            yield return Arrive(Site.At(0, 0, -3));
            var list = (System.Collections.IList)typeof(Sandbox.Game.SessionComponents.MySessionComponentContainerDropSystem).GetField("m_playerData", Any).GetValue(drops);
            var data = list.Cast<object>().FirstOrDefault(d => (long)d.GetType().GetField("PlayerId").GetValue(d) == Identity);
            if (data == null)
            {
                var type = typeof(Sandbox.Game.SessionComponents.MySessionComponentContainerDropSystem).GetNestedType("MyPlayerContainerData", System.Reflection.BindingFlags.NonPublic);
                data = Activator.CreateInstance(type, Identity, 600, true, false, 0L);
                list.Add(data);
                _added = drops;
            }
            var before = new System.Collections.Generic.HashSet<long>(MyEntities.GetEntities().OfType<MyCubeGrid>().Select(g => g.EntityId));
            var spawn = typeof(Sandbox.Game.SessionComponents.MySessionComponentContainerDropSystem).GetMethod("SpawnContainerDropInternal", Any);
            var args = new object[] { data, true, false, true, null };
            Check((bool)spawn.Invoke(drops, args), "the drop system found no place for a competitive drop");
            SkipUnless((bool)args[2], "the drop system sent a personal drop, not a competitive one");
            MyCubeGrid drop = null;
            yield return Until(() => (drop = MyEntities.GetEntities().OfType<MyCubeGrid>().FirstOrDefault(g => !before.Contains(g.EntityId) && !g.MarkedForClose
                && g.GetFatBlocks().Any(b => b.Components.Get<Sandbox.Game.Components.MyContainerDropComponent>()?.Competetive == true))) != null ? 1 : 0, v => v > 0, "the competitive drop's grid", 20);
            Track(drop);
            // (another check's personal drop may come at the same time: this one is the competitive one)
            var container = drop.GetFatBlocks().First(b => b.Components.Get<Sandbox.Game.Components.MyContainerDropComponent>()?.Competetive == true);
            Check(container.Components.Get<Sandbox.Game.Components.MyContainerDropComponent>().Competetive, "the drop's container is not a competitive one");
            Request(drop, "CompetetiveContainerOpened", container.EntityId, 3, Color.Red);
            yield return Until(() => container.MarkedForClose || container.Closed ? 1 : 0, v => v > 0, "the opened container gone after 3 s", 15);
            foreach (var mark in MySession.Static.Gpss.ExistsForPlayer(Identity) ? MySession.Static.Gpss[Identity].Values.Where(g => Vector3D.Distance(g.Coords, drop.PositionComp.GetPosition()) < 2000).ToList() : new System.Collections.Generic.List<Sandbox.Game.Screens.Helpers.MyGps>())
                MySession.Static.Gpss.SendDeleteGpsRequest(Identity, mark.Hash);
            Result("a competitive drop sent for the player; opened, its container went away after the claimed time");
        }
    }
}
