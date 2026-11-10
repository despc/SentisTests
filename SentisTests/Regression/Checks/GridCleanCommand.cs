using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Sandbox.Game.Entities;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// The admins' <c>!gridclean</c> (SO GridCleanCommands), from the console round a point: four grids of a battery near it - bare,
    /// with a cockpit, with a beacon, with both - and a bare one beyond the radius. Without <c>delete</c> nothing goes, and
    /// <c>noblock LargeBlockArmorBlock</c> (an armor block, not a functional one) keeps the bare ones; with
    /// <c>noblock Cockpit, LargeBlockBeacon delete</c> (a type id and a subtype, a space after the comma) only the bare one near
    /// goes; with <c>noblock cockpit delete</c> then the beacon's one too; the far one and those with a cockpit stay. A radius of
    /// 0, an unknown condition and a block type the game has not are refused. A bare ship (dynamic) near: <c>static</c> leaves it
    /// out, <c>dynamic</c> lists it alone, <c>dynamic delete</c> at the end takes it and nothing else; static with dynamic refused.
    /// Away from them a grid of a battery switched off and an armor block: <c>nopower</c> lists it and none of the charged ones;
    /// <c>lessblocks 2</c> leaves it out, <c>lessblocks 3</c> lists it; <c>nopower lessblocks 3 delete</c> takes it.
    /// A fake player then stands 10 m off the far grid: <c>noplayers 100</c> leaves that grid out and lists the bare one 400 m
    /// from the player, <c>noplayers 500</c> leaves both out.
    /// </summary>
    public sealed class GridCleanCommandCheck : RegressionCheck
    {
        public override string Name => Prefix + "grid_clean_command";

        private readonly SessionSettingsOverride _settings = new SessionSettingsOverride();

        protected override IEnumerator Check()
        {
            // the game's trash removal takes a small grid with no power away at once: off for the check
            _settings.Set("TrashRemovalEnabled", false);
            MyCubeGrid Make(string name, double off, params string[] top) => Made(name, off, true, top);
            MyCubeGrid Made(string name, double off, bool isStatic, params string[] top) => Powered(name, off, isStatic, true, top);
            MyCubeGrid Powered(string name, double off, bool isStatic, bool on, params string[] top)
            {
                // a battery: grids of armor alone are taken away by the stand's clean-up at once
                var parts = new List<Part> { new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, 0)
                    .With(b =>
                    {
                        // a battery switched off for a grid with no power
                        ((Sandbox.Common.ObjectBuilders.MyObjectBuilder_BatteryBlock)b).CurrentStoredPower = 1f;
                        ((Sandbox.Common.ObjectBuilders.MyObjectBuilder_BatteryBlock)b).Enabled = on;
                    }) };
                for (var i = 0; i < top.Length; i++) parts.Add(new Part(top[i], 0, i + 1, 0));
                return Build(name, Site.Origin + Site.Forward * off, isStatic, parts.ToArray());
            }
            var bare = Make("clean-bare", 0, "CubeBlock/LargeBlockArmorBlock");
            var cockpit = Make("clean-cockpit", 20, "Cockpit/LargeBlockCockpitSeat");
            var beacon = Make("clean-beacon", 40, "Beacon/LargeBlockBeacon");
            var both = Make("clean-both", 60, "Cockpit/LargeBlockCockpitSeat", "Beacon/LargeBlockBeacon");
            var far = Make("clean-far", 400, "CubeBlock/LargeBlockArmorBlock");
            var ship = Made("clean-ship", 80, false, "CubeBlock/LargeBlockArmorBlock");
            var dead = Powered("clean-dead", 250, true, false, "CubeBlock/LargeBlockArmorBlock");
            yield return Seconds(1);
            var all = new[] { bare, cockpit, beacon, both, far, ship, dead };
            Check(all.All(g => !g.Closed), "the grids did not stay: " + string.Join(", ", all.Where(g => g.Closed).Select(g => g.DisplayName)));
            var at = string.Format(CultureInfo.InvariantCulture, "at {0:0.0} {1:0.0} {2:0.0}", Site.Origin.X, Site.Origin.Y, Site.Origin.Z);
            string Gone() => string.Join(", ", all.Where(g => g.Closed || g.MarkedForClose).Select(g => g.DisplayName));

            var preview = PcuCommandCheck.Command("!gridclean 150 noblock Cockpit " + at);
            Note("preview: " + preview);
            Check(preview.Contains(bare.DisplayName) && preview.Contains(beacon.DisplayName) && !preview.Contains(cockpit.DisplayName) && !preview.Contains(far.DisplayName),
                "the preview does not list the bare and the beacon's grids alone: " + preview);
            var armor = PcuCommandCheck.Command("!gridclean 150 noblock LargeBlockArmorBlock " + at);
            Note("noblock LargeBlockArmorBlock: " + armor);
            Check(!armor.Contains(bare.DisplayName) && armor.Contains(beacon.DisplayName), "an armor block did not keep its grid: " + armor);
            var statics = PcuCommandCheck.Command("!gridclean 150 static " + at);
            Note("static: " + statics);
            Check(statics.Contains(bare.DisplayName) && !statics.Contains(ship.DisplayName), "static: " + statics);
            var dynamics = PcuCommandCheck.Command("!gridclean 150 dynamic " + at);
            Note("dynamic: " + dynamics);
            Check(dynamics.Contains(ship.DisplayName) && !dynamics.Contains(bare.DisplayName) && !dynamics.Contains(cockpit.DisplayName), "dynamic: " + dynamics);
            var unpowered = PcuCommandCheck.Command("!gridclean 500 nopower " + at);
            Note("nopower: " + unpowered);
            Check(unpowered.Contains(dead.DisplayName) && !unpowered.Contains(bare.DisplayName) && !unpowered.Contains(ship.DisplayName) && !unpowered.Contains(far.DisplayName),
                "nopower does not list the switched-off battery's grid alone: " + unpowered);
            var two = PcuCommandCheck.Command("!gridclean 500 lessblocks 2 " + at);
            var three = PcuCommandCheck.Command("!gridclean 500 lessblocks 3 " + at);
            Note("lessblocks 2: " + two + " | lessblocks 3: " + three);
            Check(!two.Contains(dead.DisplayName) && three.Contains(dead.DisplayName) && three.Contains(bare.DisplayName) && !three.Contains(both.DisplayName),
                "lessblocks does not count the blocks: " + two + " | " + three);
            Check(PcuCommandCheck.Command("!gridclean 150 static dynamic " + at).Contains("static и dynamic"), "static with dynamic was not refused");
            yield return Seconds(1);
            Check(Gone() == "", "the preview removed: " + Gone());

            // a player by the far grid
            yield return Arrive(Site.Origin + Site.Forward * 400 + Site.Up * 10);
            yield return Seconds(1);
            var near100 = PcuCommandCheck.Command("!gridclean 500 noplayers 100 " + at);
            var near500 = PcuCommandCheck.Command("!gridclean 500 noplayers 500 " + at);
            Note("player by the far grid; noplayers 100: " + near100 + " | noplayers 500: " + near500);
            Check(!near100.Contains(far.DisplayName) && near100.Contains(bare.DisplayName), "noplayers 100 does not keep the grid by the player alone: " + near100);
            Check(!near500.Contains(far.DisplayName) && !near500.Contains(bare.DisplayName), "noplayers 500 does not keep the grids 400 m from the player: " + near500);
            Check(Gone() == "", "noplayers without delete removed: " + Gone());

            var first = PcuCommandCheck.Command("!gridclean 150 noblock Cockpit, LargeBlockBeacon static delete " + at);
            Note("noblock Cockpit, LargeBlockBeacon delete: " + first);
            yield return Seconds(1);
            Check(Gone() == bare.DisplayName, "noblock Cockpit, LargeBlockBeacon removed " + Gone() + " (only " + bare.DisplayName + " should go)");

            var second = PcuCommandCheck.Command("!gridclean 150 static noblock cockpit delete " + at);
            Note("noblock cockpit delete: " + second);
            yield return Seconds(1);
            Check(bare.Closed && beacon.Closed && !cockpit.Closed && !both.Closed && !far.Closed, "noblock cockpit then removed " + Gone());

            Check(PcuCommandCheck.Command("!gridclean 0 delete " + at).Contains("больше нуля"), "a radius of 0 was not refused");
            Check(PcuCommandCheck.Command("!gridclean 100 whatever " + at).Contains("Не понял"), "an unknown condition was not refused");
            var typo = PcuCommandCheck.Command("!gridclean 100 noblock Cokpit delete " + at);
            Check(typo.Contains("Нет таких типов"), "an unknown block type was not refused: " + typo);
            yield return Seconds(1);
            Check(!cockpit.Closed && !both.Closed && !far.Closed && !ship.Closed, "a refused command removed " + Gone());
            PcuCommandCheck.Command("!gridclean 150 dynamic delete " + at);
            yield return Seconds(1);
            Check(ship.Closed && !cockpit.Closed && !both.Closed && !far.Closed && !dead.Closed, "dynamic delete removed " + Gone());
            PcuCommandCheck.Command("!gridclean 500 nopower lessblocks 3 delete " + at);
            yield return Seconds(1);
            Check(dead.Closed && !cockpit.Closed && !both.Closed && !far.Closed, "nopower lessblocks 3 delete removed " + Gone());
            Result("preview removes nothing, an armor block keeps its grid; noblock Cockpit, LargeBlockBeacon delete took the bare grid only, " +
                   "noblock cockpit delete the beacon's one too; the cockpits' and the one beyond the radius stayed; radius 0, an unknown condition and an unknown block type refused; static/dynamic pick stations/ships, dynamic delete took the ship alone; nopower picked the switched-off battery's grid only, lessblocks counted 2 blocks right; noplayers kept the grids within its distance of a player");
        }

        public override void Cleanup()
        {
            try { _settings.Restore(); }
            catch (System.Exception) { }
            base.Cleanup();
        }
    }
}
