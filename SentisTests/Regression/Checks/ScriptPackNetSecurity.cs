using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.GameSystems.BankingAndCurrency;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using SentisTests.Core;
using SentisTests.Game;
using VRage.Game;
using VRage.Game.Entity;
using VRage.ObjectBuilders;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// What the modded V5's ScriptPack (and the plugins behind it) do with the commands clients send, each sent the way a
    /// client's SendMessageToServer arrives (the game's ModMessageServerReliable, in the fake client's event context: a
    /// secure handler gets the client's real steam id):
    /// - the admin console (Admin_Console, the admins' shop): a non-admin's settings and sale are refused, the console's
    ///   owner's too while he is not an admin; the admin's price is taken, the stored credits and the deposit a client
    ///   sends are not; the sale of another's ship is refused, of the admin's own it is done by the server (the ship
    ///   gone, its prefab saved); a buyer short of credits is refused, one with them pays the server's price and gets
    ///   the ship, and so does the next buyer: the ship stays on sale, nobody is paid (the old claim pays nothing);
    ///   an offer the admin accepts is what the buyer pays; the admin takes the ship back; no handler takes the old
    ///   string commands;
    /// - the blink drive: a blink asked by another than the pilot, or in a compressed packet, is refused; the pilot's
    ///   goes 500 m; the pilot's again at once (the cooldown) is refused;
    /// - the nanobot: another's settings of the block are refused; the owner's are taken, the area held to its limit;
    /// - FixShip (SentisGameplayImprovements): another's ship is not fixed; the owner's is; again within the minute it
    ///   is not; a ship docked to another's (a connector's logical link) is not;
    /// - the trade console (Shipyard_block, VirtualGarage): a non-owner's sale is refused; the owner's puts the garage
    ///   file on sale; a buyer short of credits and the seller himself are refused; the buyer pays the seller and the
    ///   ship is in his garage at once, not to be sold on before a world save; !g load takes it out next to the buyer
    ///   though the garage takes grids out only where they were saved (OnlyLoadBase, in memory); a crash before the
    ///   world save gives it (taken out) back to the seller's garage, after the save it stays the buyer's; he sells it
    ///   on, another cannot take it off sale, he can: back in his garage.
    /// Skipped where the mod is not loaded; a part whose block or plugin is missing is skipped with a note.
    /// </summary>
    public sealed class ScriptPackNetSecurityCheck : RegressionCheck
    {
        public override string Name => Prefix + "scriptpack_netsec";
        public override int TimeoutSeconds => 300;

        private const BindingFlags Any = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private Assembly _mod;
        private int _buyer = -1, _thief = -1;
        private readonly List<string> _failures = new List<string>();
        private readonly List<string> _files = new List<string>();

        protected override IEnumerator Check()
        {
            _mod = MyScriptManager.Static?.Scripts?.Values.FirstOrDefault(a => a.GetType("Admin_Shipyard.ShipyardServer") != null);
            SkipUnless(_mod != null, "no Sentis_v5_ScriptPack_Fix mod with the shipyard's server in this world");

            yield return Arrive(Site.At(0, 0, -30));
            yield return Join(Site.At(6, 0, -30), i => _buyer = i);
            yield return Join(Site.At(-6, 0, -30), i => _thief = i);

            try
            {
                yield return AdminConsole();
                yield return Blink();
                yield return Nanobot();
                yield return FixShip();
                yield return Market();
            }
            finally
            {
                foreach (var file in _files)
                    try { if (File.Exists(file)) File.Delete(file); }
                    catch (Exception e) { Note("could not delete " + file + ": " + e.Message); }
                // the fake players' garage folders, when nothing else is in them
                foreach (var folder in _files.Select(Path.GetDirectoryName).Distinct())
                    try { if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder, false); }
                    catch (Exception e) { Note("could not delete " + folder + ": " + e.Message); }
            }

            foreach (var failure in _failures) Note("FAILED: " + failure);
            Check(_failures.Count == 0, string.Join("; ", _failures));
            Result("every command checked its sender: the admin console, the blink, the nanobot, FixShip and the trade console refused " +
                   "the others' and the impossible ones and did the owners'");
        }

        private void Fail(string what) => _failures.Add(what);

        // ------------------------------------------------------------------ the network, as a client's message arrives

        private ulong Steam(int client) => FakeClients.PlayerOf(client).Id.SteamId;
        private long IdentityOf(int client) => FakeClients.PlayerOf(client).Identity.IdentityId;

        private static readonly Type ModMultiplayer = typeof(MyModAPIHelper).GetNestedType("MyMultiplayer", Any);

        /// <summary>A client's message to the server, through the game's own handling (its plain handlers and secure ones).</summary>
        private void ToServer(int client, ushort channel, byte[] data) =>
            RequestAs(client, ModMultiplayer, "ModMessageServerReliable", channel, data, MyAPIGateway.Multiplayer.ServerId);

        private static bool PlainHandler(ushort channel)
        {
            var listeners = (System.Collections.IDictionary)ModMultiplayer.GetField("m_registeredListeners", Any).GetValue(null);
            return listeners.Contains(channel) && ((System.Collections.ICollection)listeners[channel]).Count > 0;
        }

        private static byte[] Bytes(object value) =>
            (byte[])typeof(VRage.Game.ModAPI.IMyUtilities).GetMethod("SerializeToBinary").MakeGenericMethod(value.GetType()).Invoke(MyAPIGateway.Utilities, new[] { value });

        private static object FromBytes(Type type, byte[] data) =>
            typeof(VRage.Game.ModAPI.IMyUtilities).GetMethod("SerializeFromBinary").MakeGenericMethod(type).Invoke(MyAPIGateway.Utilities, new object[] { data });

        private Type ModType(string name) => _mod.GetType(name) ?? throw new ScenarioFailedException("no " + name + " in the mod");

        private static object New(Type type) => Activator.CreateInstance(type, true);

        private static void Set(object target, string member, object value)
        {
            var type = target.GetType();
            var field = type.GetField(member, Any);
            if (field != null)
            {
                field.SetValue(target, field.FieldType.IsEnum ? Enum.ToObject(field.FieldType, value) : value);
                return;
            }

            var property = type.GetProperty(member, Any) ?? throw new ScenarioFailedException(type.Name + " has no " + member);
            property.SetValue(target, property.PropertyType.IsEnum ? Enum.ToObject(property.PropertyType, value) : value);
        }

        private static object Get(object target, string member)
        {
            var type = target as Type ?? target.GetType();
            var instance = target is Type ? null : target;
            var field = type.GetField(member, Any);
            if (field != null) return field.GetValue(instance);
            var property = type.GetProperty(member, Any) ?? throw new ScenarioFailedException(type.Name + " has no " + member);
            return property.GetValue(instance);
        }

        private static object Logic(MyEntity entity, Type type) =>
            entity.GameLogic?.GetType().GetMethod("GetAs")?.MakeGenericMethod(type).Invoke(entity.GameLogic, null);

        private static long Balance(long identity) => MyBankingSystem.GetBalance(identity);

        private static void GiveCredits(long identity, long credits) => MyBankingSystem.ChangeBalance(identity, credits - Balance(identity));

        private static bool Has(string id)
        {
            try { Definition(id); return true; }
            catch (ScenarioSkippedException) { return false; }
        }

        private Part Battery(int x, int y, int z) => new Part("BatteryBlock/LargeBlockBatteryBlock", x, y, z).With(b => Motion.Charged(b));

        private static bool Gone(MyEntity entity) => entity.Closed || entity.MarkedForClose;

        // ------------------------------------------------------------------ the admin console

        private IEnumerator AdminConsole()
        {
            if (!Has("Projector/Admin_Console")) { Note("admin console: no Admin_Console in this game, skipped"); yield break; }
            const ushort commands = 4622;
            var commandType = ModType("Admin_Shipyard.ShipyardCommand");
            var settingsType = ModType("Admin_Shipyard.ClientSettings");
            var coreType = ModType("Admin_Shipyard.Shipyard_Core");

            var station = Build("sy-console", Site.At(0, 0, 40), true, new Part("Projector/Admin_Console", 0, 0, 0), Battery(2, 0, 0));
            var console = station.GetFatBlocks().OfType<MyProjectorBase>().First();
            var ship = Build("sy-ship", Site.At(0, 60, 40), false, new Part("Cockpit/LargeBlockCockpit", 0, 0, 0), Battery(0, -1, 0));
            var foreign = Build("sy-foreign", Site.At(0, -60, 40), false, new[] { new Part("Cockpit/LargeBlockCockpit", 0, 0, 0), Battery(0, -1, 0) }, ownedBy: IdentityOf(_thief));
            yield return Seconds(3); // the console's logic sets itself up on its first frames; BigOwners on the next update
            var core = Logic(console, coreType);
            if (core == null) { Fail("admin console: no Shipyard_core on the console"); yield break; }
            object S() => Get(core, "consoleBlockClientSettings");

            void Command(int client, int type, long value = 0, object settings = null)
            {
                var command = New(commandType);
                Set(command, "Type", (byte)type);
                Set(command, "BlockId", console.EntityId);
                Set(command, "Value", value);
                if (settings != null) Set(command, "Settings", settings);
                ToServer(client, commands, Bytes(command));
            }

            object Wanted(long price, long credits, long seller)
            {
                var s = New(settingsType);
                Set(s, "EntityId", console.EntityId);
                Set(s, "Price", price);
                Set(s, "isPriceSet", true);
                Set(s, "StoredCredits", credits);
                Set(s, "SellerId", seller);
                Set(s, "AllowNegotiation", true);
                Set(s, "AutoDeposit", true);
                return s;
            }

            List<MyCubeGrid> Copies(MyCubeGrid of) => MyEntities.GetEntities().OfType<MyCubeGrid>().Where(g => !Gone(g) && g.DisplayName == of.DisplayName && g != of).ToList();

            // the old string commands: no handler on the server
            foreach (var channel in new ushort[] { 4620, 4621 })
                if (PlainHandler(channel)) Fail("admin console: a plain handler still takes channel " + channel + " on the server");

            // 1. another's settings; the owner's while he is not an admin: the console is the admins' shop
            Command(_thief, 1, 0, Wanted(-5, 1000000000, IdentityOf(_thief)));
            Command(Me, 1, 0, Wanted(700, 0, Identity));
            Command(Me, 2, ship.EntityId);
            yield return Seconds(1);
            if ((long)Get(S(), "Price") != 0 || (long)Get(S(), "StoredCredits") != 0)
                Fail("admin console: a non-admin's settings were taken (price " + Get(S(), "Price") + ", credits " + Get(S(), "StoredCredits") + ")");
            if (Gone(ship) || (bool)Get(S(), "IsProjected")) Fail("admin console: a non-admin owner put a ship on sale");

            var level = MySession.Static.GetUserPromoteLevel(Steam(Me));
            MySession.Static.SetUserPromoteLevel(Steam(Me), VRage.Game.ModAPI.MyPromoteLevel.Admin);
            try
            {
                // 2. the admin's price; the credits and the deposit he sends are not taken (nobody is paid)
                Command(Me, 1, 0, Wanted(1000, 999, Identity));
                yield return Seconds(0.5);
                Note("admin console: the admin's price " + Get(S(), "Price") + ", the stored credits " + Get(S(), "StoredCredits") + ", auto deposit " + Get(S(), "AutoDeposit"));
                if ((long)Get(S(), "Price") != 1000) Fail("admin console: the admin's price was not taken");
                if ((long)Get(S(), "StoredCredits") != 0 || (bool)Get(S(), "AutoDeposit")) Fail("admin console: the stored credits or the deposit a client sent were taken");

                // 3. another's sale; the admin's sale of another's ship
                Command(_thief, 2, ship.EntityId);
                Command(Me, 2, foreign.EntityId);
                yield return Seconds(1);
                if (Gone(ship)) Fail("admin console: another sold the admin's ship");
                if (Gone(foreign)) Fail("admin console: the admin sold another's ship");
                if ((bool)Get(S(), "IsProjected")) Fail("admin console: a refused sale put a ship on sale");

                // 4. the admin's own ship
                Command(Me, 2, ship.EntityId);
                yield return Seconds(1);
                var prefab = (string)Get(S(), "ProjectionName");
                Note("admin console: the sale: ship " + (Gone(ship) ? "gone" : "still there") + ", on sale " + Get(S(), "IsProjected") + ", prefab " + prefab);
                if (!Gone(ship) || !(bool)Get(S(), "IsProjected") || string.IsNullOrEmpty(prefab)) { Fail("admin console: the admin's sale of his ship was not done"); yield break; }

                // 5. a buyer short of credits; one with them; another: the ship stays on sale, nobody is paid
                var seller = Balance(Identity);
                GiveCredits(IdentityOf(_buyer), 500);
                Command(_buyer, 4);
                yield return Seconds(1);
                if (Balance(IdentityOf(_buyer)) != 500 || Copies(ship).Any()) Fail("admin console: a buyer with 500 bought a ship for 1000");
                GiveCredits(IdentityOf(_buyer), 5000);
                Command(_buyer, 4);
                yield return Seconds(4);
                var bought = Copies(ship).ToList();
                foreach (var grid in bought) Track(grid);
                Note("admin console: bought: buyer's balance " + Balance(IdentityOf(_buyer)) + ", stored credits " + Get(S(), "StoredCredits") +
                     ", the admin's balance +" + (Balance(Identity) - seller) + ", the ship " + (bought.Count == 0 ? "not in the world" : "in the world, owned by " + string.Join(",", bought[0].BigOwners)));
                if (Balance(IdentityOf(_buyer)) != 4000) Fail("admin console: the buyer paid " + (5000 - Balance(IdentityOf(_buyer))) + ", not 1000");
                if (bought.Count == 0 || !bought[0].BigOwners.Contains(IdentityOf(_buyer))) Fail("admin console: the bought ship is not the buyer's in the world");
                if (!(bool)Get(S(), "IsProjected")) Fail("admin console: the shop does not sell the ship again");

                GiveCredits(IdentityOf(_thief), 1000);
                Command(_thief, 4);
                yield return Seconds(4);
                var again = Copies(ship).Where(g => !bought.Contains(g)).ToList();
                foreach (var grid in again) Track(grid);
                Note("admin console: bought again by another: his balance " + Balance(IdentityOf(_thief)) + ", copies " + again.Count);
                if (Balance(IdentityOf(_thief)) != 0 || again.Count == 0 || !again[0].BigOwners.Contains(IdentityOf(_thief))) Fail("admin console: the second buyer did not get the ship for 1000");

                // 6. the old claim of the credits pays nobody
                Command(Me, 5);
                yield return Seconds(0.5);
                if (Balance(Identity) != seller || (long)Get(S(), "StoredCredits") != 0) Fail("admin console: the admin was paid " + (Balance(Identity) - seller) + " for the sales");

                // 7. an offer the admin accepts
                Command(_buyer, 8, 600);
                yield return Seconds(0.5);
                Command(Me, 9, IdentityOf(_buyer));
                yield return Seconds(0.5);
                GiveCredits(IdentityOf(_buyer), 600);
                Command(_buyer, 4);
                yield return Seconds(4);
                foreach (var grid in Copies(ship)) Track(grid);
                Note("admin console: the accepted offer: buyer's balance " + Balance(IdentityOf(_buyer)) + ", the admin +" + (Balance(Identity) - seller));
                if (Balance(IdentityOf(_buyer)) != 0) Fail("admin console: the accepted offer of 600 was not what the buyer paid");
                if (Balance(Identity) != seller) Fail("admin console: the admin was paid for the offer");

                // 8. the admin takes the ship back: the prefab is gone
                Command(Me, 3);
                yield return Seconds(4);
                foreach (var grid in Copies(ship)) Track(grid);
                if ((bool)Get(S(), "IsProjected")) Fail("admin console: the admin's take back left the ship on sale");

                // the buyers and the admin sit in the cockpits of the spawned ships: out, for the next parts
                foreach (var grid in Copies(ship))
                foreach (var cockpit in grid.GetFatBlocks().OfType<MyCockpit>())
                    if (cockpit.Pilot != null) cockpit.RemovePilot();
                yield return Seconds(1);
            }
            finally
            {
                MySession.Static.SetUserPromoteLevel(Steam(Me), level);
            }
        }

        // ------------------------------------------------------------------ the blink drive

        private IEnumerator Blink()
        {
            const string drive = "JumpDrive/LargeJumpDrive";
            if (!Has(drive) || ModType("Sentis_v5.BlinkDrive") == null) { Note("blink: no blink drive, skipped"); yield break; }
            const ushort channel = 30014;
            var size = Definition(drive).Size;
            var stored = ((Sandbox.Definitions.MyJumpDriveDefinition)Definition(drive)).PowerNeededForJump;
            var ship = Build("netsec-blink", Site.At(400, 0, 0), false, new[]
            {
                new Part("Cockpit/LargeBlockCockpit", 0, 0, 0),
                new Part(drive, 0, -size.Y, 0).With(b => ((Sandbox.Common.ObjectBuilders.MyObjectBuilder_JumpDrive)b).StoredPower = stored),
                new Part("BatteryBlock/LargeBlockBatteryBlock", size.X, -1, 0).With(b => Motion.Charged(b)),
            });
            yield return Seconds(2);
            var seat = ship.GetFatBlocks().OfType<MyCockpit>().First();
            var jump = ship.GetFatBlocks().OfType<IMyJumpDrive>().First();
            yield return Sit(seat);
            yield return Seconds(4); // the game ties the seated player to the grid

            var netSync = _mod.GetType("SENetworkAPI.NetSync");
            var properties = (System.Collections.IDictionary)netSync.GetField("PropertiesByEntity", Any).GetValue(null);
            var list = properties.Contains(jump) ? (System.Collections.IList)properties[jump] : null;
            if (list == null || list.Count == 0) { Fail("blink: the drive has no network property"); yield break; }
            var blinking = list[0];

            byte[] Packet(bool compressed)
            {
                var sync = New(_mod.GetType("SENetworkAPI.SyncData"));
                Set(sync, "Id", 0L);
                Set(sync, "EntityId", jump.EntityId);
                Set(sync, "Data", Bytes(true));
                Set(sync, "SyncType", 2); // Broadcast
                var command = New(_mod.GetType("SENetworkAPI.Command"));
                Set(command, "IsProperty", true);
                Set(command, "IsCompressed", compressed);
                Set(command, "Data", Bytes(sync));
                return Bytes(command);
            }

            IEnumerator Try(int client, bool compressed, string what, bool goes)
            {
                jump.CurrentStoredPower = jump.MaxStoredPower;
                var from = ship.PositionComp.GetPosition();
                ToServer(client, channel, Packet(compressed));
                yield return Seconds(2);
                var moved = Vector3D.Distance(ship.PositionComp.GetPosition(), from);
                Note("blink: " + what + ": moved " + moved.ToString("F0") + " m, blinking " + Get(blinking, "Value"));
                if (goes && Math.Abs(moved - 500) > 30) Fail("blink: " + what + " went " + moved.ToString("F0") + " m, not 500");
                if (!goes && moved > 5) Fail("blink: " + what + " went " + moved.ToString("F0") + " m");
            }

            yield return Try(_thief, false, "another than the pilot", false);
            yield return Try(Me, true, "the pilot's in a compressed packet", false);
            yield return Try(Me, false, "the pilot's", true);
            yield return Try(Me, false, "the pilot's again at once (the cooldown)", false);
            seat.RemovePilot();
        }

        // ------------------------------------------------------------------ the nanobot

        private IEnumerator Nanobot()
        {
            const string welder = "ShipWelder/SELtdLargeNanobotBuildAndRepairSystem";
            var modType = _mod.GetType("SpaceEquipmentLtd.NanobotBuildAndRepairSystem.NanobotBuildAndRepairSystemMod");
            if (!Has(welder) || modType == null) { Note("nanobot: no nanobot, skipped"); yield break; }
            const ushort channel = 40103;
            var rig = Build("netsec-nanobot", Site.At(-400, 0, 0), true, new Part(welder, 0, 0, 0), Battery(0, 3, 0));
            yield return Seconds(3);
            var block = rig.GetFatBlocks().First(b => b.BlockDefinition.Id.SubtypeName == "SELtdLargeNanobotBuildAndRepairSystem");
            var systems = (System.Collections.IDictionary)Get(modType, "BuildAndRepairSystems");
            if (!systems.Contains(block.EntityId)) { Fail("nanobot: the block is not among the mod's systems"); yield break; }
            var settings = Get(systems[block.EntityId], "Settings");
            Vector3 Area() => (Vector3)Get(settings, "AreaSize");

            void Send(int client, Vector3 area)
            {
                var copy = FromBytes(settings.GetType(), Bytes(settings));
                Set(copy, "AreaSize", area);
                var message = New(_mod.GetType("SpaceEquipmentLtd.NanobotBuildAndRepairSystem.MsgBlockSettings"));
                Set(message, "EntityId", block.EntityId);
                Set(message, "Settings", copy);
                ToServer(client, channel, Bytes(message));
            }

            var original = Area();
            Send(_thief, new Vector3(20, 20, 20));
            yield return Seconds(0.5);
            Note("nanobot: area " + original + ", after another's settings " + Area());
            if (Area() != original) Fail("nanobot: another's settings were taken (area " + Area() + ")");
            Send(Me, new Vector3(20, 20, 20));
            yield return Seconds(0.5);
            if (Area() != new Vector3(20, 20, 20)) Fail("nanobot: the owner's settings were not taken (area " + Area() + ")");
            Send(Me, new Vector3(100000, 100000, 100000));
            yield return Seconds(0.5);
            var max = (int)Get(settings, "MaximumRange");
            Note("nanobot: the owner's area of 100 km became " + Area() + " (the limit " + max + ")");
            if (Area().X > max || Area().Y > max || Area().Z > max) Fail("nanobot: the area was not held to " + max + ": " + Area());
        }

        // ------------------------------------------------------------------ FixShip

        private IEnumerator FixShip()
        {
            var requestType = AppDomain.CurrentDomain.GetAssemblies().Select(a => SafeType(a, "SentisGameplayImprovements.FixShipRequest")).FirstOrDefault(t => t != null);
            if (requestType == null) { Note("fixship: no SentisGameplayImprovements, skipped"); yield break; }
            const ushort channel = 10778;

            void Fix(int client, MyCubeGrid grid)
            {
                var request = New(requestType);
                Set(request, "gridId", grid.EntityId);
                var data = Bytes(request);
                var packet = new byte[data.Length + 1];
                packet[0] = 0; // MessageType.FixShip
                data.CopyTo(packet, 1);
                ToServer(client, channel, packet);
            }

            MyCubeGrid Again(MyCubeGrid old) =>
                MyEntities.GetEntities().OfType<MyCubeGrid>().FirstOrDefault(g => !Gone(g) && g != old && g.DisplayName == old.DisplayName);

            var ship = Build("netsec-fix", Site.At(0, 400, 0), false, new Part("Cockpit/LargeBlockCockpit", 0, 0, 0), Battery(0, -1, 0));
            yield return Seconds(2);
            Fix(_thief, ship);
            yield return Seconds(4);
            if (Gone(ship)) Fail("fixship: another's request fixed the ship");
            Fix(Me, ship);
            yield return Seconds(5);
            var fixedShip = Again(ship);
            Note("fixship: the owner's: the ship " + (Gone(ship) ? "made anew" : "as it was"));
            if (!Gone(ship) || fixedShip == null) { Fail("fixship: the owner's request did not fix the ship"); yield break; }
            Track(fixedShip);
            Fix(Me, fixedShip);
            yield return Seconds(4);
            if (Gone(fixedShip)) Fail("fixship: fixed again within the minute");

            // a ship docked to another's (the buyer's ship, the thief's): the logical link a locked connector makes (a
            // grid on a rotor takes its stator's owner on this server, so that one is the player's anyway)
            var docked = Build("netsec-fix-docked", Site.At(0, 400, 60), false, new[] { new Part("Cockpit/LargeBlockCockpit", 0, 0, 0), Battery(0, -1, 0) }, ownedBy: IdentityOf(_buyer));
            var other = Build("netsec-fix-other", Site.At(0, 400, 75), false, new[] { new Part("Cockpit/LargeBlockCockpit", 0, 0, 0), Battery(0, -1, 0) }, ownedBy: IdentityOf(_thief));
            yield return Seconds(2);
            const long link = 0x7E57F1;
            MyCubeGridGroups.Static.CreateLink(VRage.Game.ModAPI.GridLinkTypeEnum.Logical, link, docked, other);
            Fix(_buyer, docked);
            yield return Seconds(4);
            Note("fixship: a ship docked to another's: " + (Gone(docked) ? "made anew" : "as it was") + ", the other's " + (Gone(other) ? "made anew" : "as it was"));
            if (Gone(docked) || Gone(other)) Fail("fixship: a ship docked to another's was fixed (the other's with it)");
            if (!Gone(docked) && !Gone(other)) MyCubeGridGroups.Static.BreakLink(VRage.Game.ModAPI.GridLinkTypeEnum.Logical, link, docked, other);
        }

        private static Type SafeType(Assembly a, string name)
        {
            try { return a.GetType(name); }
            catch { return null; }
        }

        // ------------------------------------------------------------------ the trade console

        private IEnumerator Market()
        {
            var market = AppDomain.CurrentDomain.GetAssemblies().Select(a => SafeType(a, "VirtualGarage.Market.ShipMarket")).FirstOrDefault(t => t != null);
            if (market == null || !Has("Projector/Shipyard_block")) { Note("trade console: no VirtualGarage market or no Shipyard_block, skipped"); yield break; }
            var messageType = market.Assembly.GetType("VirtualGarage.Market.MarketMessage");
            var files = market.Assembly.GetType("VirtualGarage.GarageFiles");
            const ushort channel = 10777;
            string Folder(int client) => (string)files.GetMethod("FolderOf").Invoke(null, new object[] { Steam(client) });
            List<string> Garage(int client) => (List<string>)files.GetMethod("List").Invoke(null, new object[] { Steam(client) });
            string Title(string path) => (string)files.GetMethod("Title").Invoke(null, new object[] { path });

            var station = Build("netsec-trade", Site.At(0, -400, 0), true, new Part("Projector/Shipyard_block", 0, 0, 0), Battery(2, 0, 0));
            var console = station.GetFatBlocks().OfType<MyProjectorBase>().First();

            // a ship in the seller's garage, as the garage keeps it (a blueprint, the world saved since), saved 400 m away
            var name = "netsec-garage-" + DateTime.Now.Ticks % 100000;
            Directory.CreateDirectory(Folder(Me));
            var file = Path.Combine(Folder(Me), name + ".sbc");
            var definitions = MyObjectBuilderSerializer.CreateNewObject<MyObjectBuilder_Definitions>();
            var blueprint = MyObjectBuilderSerializer.CreateNewObject<MyObjectBuilder_ShipBlueprintDefinition>();
            blueprint.Id = new VRage.ObjectBuilders.SerializableDefinitionId(new MyObjectBuilderType(typeof(MyObjectBuilder_ShipBlueprintDefinition)), name);
            var model = Build("netsec-garage-model", Site.At(0, -400, 60), false, new Part("Cockpit/LargeBlockCockpit", 0, 0, 0));
            var savedAt = model.PositionComp.GetPosition();
            var modelName = model.DisplayName;
            blueprint.CubeGrids = new[] { (MyObjectBuilder_CubeGrid)model.GetObjectBuilder(true) };
            model.Close();
            definitions.ShipBlueprints = new[] { blueprint };
            // written as the garage writes (the game's own path check is for mods)
            VRage.ObjectBuilders.Private.MyObjectBuilderSerializerKeen.SerializeXML(file, false, definitions);
            var listed = file + "_market";
            var bought = Path.Combine(Folder(_buyer), name + "_bought.sbc");
            var takenOut = bought + "_spawned_unsaved";
            _files.AddRange(new[] { file, listed, bought, takenOut, bought + "_spawned", bought + "_market", listed + "B5", file + "B5", bought + "B5", bought + "_marketB5" });
            yield return Seconds(2);

            void Ask(int client, byte type, string ship = null, long price = 0)
            {
                var message = New(messageType);
                Set(message, "Type", type);
                Set(message, "ShipyardId", console.EntityId);
                Set(message, "Name", ship);
                Set(message, "Price", price);
                ToServer(client, channel, Bytes(message));
            }

            // 1. a non-owner's sale, the owner's
            Ask(_thief, 2, name, 1);
            yield return Seconds(0.5);
            if (!File.Exists(file)) Fail("trade console: a non-owner put the owner's ship on sale");
            Ask(Me, 2, name, 1000);
            yield return Seconds(1);
            Note("trade console: the sale: " + (File.Exists(listed) ? "the file on sale" : "the file not moved"));
            if (!File.Exists(listed)) { Fail("trade console: the owner's sale did not put the file on sale"); yield break; }

            // 2. short of credits, the seller himself, the buyer: the ship is in his garage at once
            GiveCredits(IdentityOf(_buyer), 500);
            Ask(_buyer, 3);
            yield return Seconds(0.5);
            if (!File.Exists(listed) || Balance(IdentityOf(_buyer)) != 500) Fail("trade console: a buyer with 500 bought a ship for 1000");
            GiveCredits(Identity, 10000);
            Ask(Me, 3);
            yield return Seconds(0.5);
            if (!File.Exists(listed) || Balance(Identity) != 10000) Fail("trade console: the seller bought his own ship");
            GiveCredits(IdentityOf(_buyer), 5000);
            Ask(_buyer, 3);
            yield return Seconds(1);
            var garage = Garage(_buyer);
            Note("trade console: bought: buyer " + Balance(IdentityOf(_buyer)) + ", seller " + Balance(Identity) + ", the buyer's !g list: " + string.Join(", ", garage.Select(Title)));
            if (Balance(IdentityOf(_buyer)) != 4000 || Balance(Identity) != 11000) Fail("trade console: the money did not move by 1000");
            if (!garage.Contains(bought)) { Fail("trade console: the bought ship is not in the buyer's garage at once"); yield break; }

            // 3. not sold on before the world save (a crash gives it back to its seller)
            console.ChangeOwner(IdentityOf(_buyer), MyOwnershipShareModeEnum.None);
            yield return Seconds(0.5);
            Ask(_buyer, 2, Title(bought), 300);
            yield return Seconds(1);
            if (!File.Exists(bought)) Fail("trade console: a purchase the world has not saved was put on sale again");
            console.ChangeOwner(Identity, MyOwnershipShareModeEnum.None);
            yield return Seconds(0.5);

            // 4. taken out at once, next to the buyer, whatever the settings: the garage here takes grids out only where
            // they were saved (OnlyLoadBase, set in memory), and the ship was saved 400 m away
            var config = market.Assembly.GetType("VirtualGarage.Plugin").GetProperty("Instance").GetValue(null);
            config = config.GetType().GetProperty("Config").GetValue(config);
            var onlyBase = config.GetType().GetField("_OnlyLoadBase", Any);
            var wasOnlyBase = onlyBase.GetValue(config);
            var enemyRange = config.GetType().GetField("_EnemyPlayerInRange", Any);
            var wasEnemyRange = enemyRange.GetValue(config);
            onlyBase.SetValue(config, true); // the fields: the settings are not written to the config file
            enemyRange.SetValue(config, 0.0); // the thief stands next to the buyer: no enemy check here
            MyCubeGrid outside = null;
            try
            {
                var number = Garage(_buyer).IndexOf(bought) + 1;
                var consumed = false;
                var commands = (Torch.Commands.CommandManager)Torch.TorchBase.Instance.CurrentSession.Managers.GetManager(typeof(Torch.Commands.CommandManager));
                commands.HandleCommand("!g load " + number, Steam(_buyer), ref consumed);
                yield return Seconds(5);
                outside = MyEntities.GetEntities().OfType<MyCubeGrid>().FirstOrDefault(g => !Gone(g) && g.DisplayName == modelName);
                if (outside != null) Track(outside);
            }
            finally
            {
                onlyBase.SetValue(config, wasOnlyBase);
                enemyRange.SetValue(config, wasEnemyRange);
            }

            var buyerAt = FakeClients.PlayerOf(_buyer).Character.PositionComp.GetPosition();
            Note("trade console: !g load of the bought ship: " + (outside == null ? "not in the world" :
                "out " + (int)Vector3D.Distance(outside.PositionComp.GetPosition(), buyerAt) + " m from the buyer, " +
                (int)Vector3D.Distance(outside.PositionComp.GetPosition(), savedAt) + " m from where it was saved") + "; the file " +
                (File.Exists(takenOut) ? "taken out" : File.Exists(bought) ? "still in the garage" : "elsewhere"));
            if (outside == null) Fail("trade console: the bought ship did not come out (OnlyLoadBase)");
            else if (Vector3D.Distance(outside.PositionComp.GetPosition(), savedAt) < 50) Fail("trade console: the bought ship came out where it was saved, not next to the buyer");
            if (!File.Exists(takenOut)) Fail("trade console: the taken out bought file is not marked taken out");

            // 5. a crash before the world save: the purchase did not happen, the file (taken out) is back in the seller's garage
            market.GetMethod("OnWorldLoaded").Invoke(null, null);
            Note("trade console: after a crash: the seller's garage " + (File.Exists(file) ? "has the ship" : "has not the ship") + ", the buyer's " + (File.Exists(takenOut) || File.Exists(bought) ? "has it" : "has not"));
            if (!File.Exists(file) || File.Exists(takenOut) || File.Exists(bought)) Fail("trade console: after a crash the bought ship is not back in the seller's garage");

            // 6. sold and bought again, then the world save: it stays the buyer's; a crash after it changes nothing
            Ask(Me, 2, name, 1000);
            yield return Seconds(1);
            GiveCredits(IdentityOf(_buyer), 5000);
            Ask(_buyer, 3);
            yield return Seconds(1);
            market.GetMethod("OnWorldSaved").Invoke(null, new object[] { long.MaxValue });
            market.GetMethod("OnWorldLoaded").Invoke(null, null);
            Note("trade console: after the world save and a crash the ship is " + (File.Exists(bought) ? "in the buyer's garage" : "not in the buyer's garage"));
            if (!File.Exists(bought) || File.Exists(file)) Fail("trade console: after the world save the ship is not the buyer's");

            // 7. the buyer sells it on: on sale, another cannot take it off, the buyer can: back in his garage
            console.ChangeOwner(IdentityOf(_buyer), MyOwnershipShareModeEnum.None);
            yield return Seconds(0.5);
            Ask(_buyer, 2, Title(bought), 300);
            yield return Seconds(1);
            if (!File.Exists(bought + "_market")) Fail("trade console: the new owner could not put the ship on sale");
            Ask(_thief, 4);
            yield return Seconds(0.5);
            if (!File.Exists(bought + "_market")) Fail("trade console: another took the ship off sale");
            Ask(_buyer, 4);
            yield return Seconds(0.5);
            if (!File.Exists(bought)) Fail("trade console: taken off sale, the ship is not back in the garage");
        }
    }
}
