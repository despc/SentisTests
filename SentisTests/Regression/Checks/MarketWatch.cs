using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.GameSystems.BankingAndCurrency;
using SentisTests.Core;
using SentisTests.Game;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// SentisWatcher's MarketWatch and GarageWatch on VirtualGarage's trade console (<c>ShipMarket</c>; a vanilla projector
    /// on a station stands in for the mod's console, the market's handlers take any projector): a ship put into the
    /// seller's garage and put up for sale is told (<c>market_listed</c>), taken off by the seller (<c>market_unlisted</c>,
    /// with who), put up again and bought by another player (<c>market_bought</c>: buyer, seller, price, console); the
    /// seller's garage entry goes with the ship to the buyer's (what it was put in with); the buyer takes it out and no
    /// grid is told twice in the world.
    /// </summary>
    public sealed class MarketWatchCheck : RegressionCheck
    {
        public override string Name => Prefix + "market_watch";
        public override int TimeoutSeconds => 120;

        private const BindingFlags Any = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const long Price = 100;
        private readonly List<string> _files = new List<string>();
        private readonly List<MyCubeGrid> _grids = new List<MyCubeGrid>();
        private Type _market;
        private object _listing;

        protected override IEnumerator Check()
        {
            var save = Integrations.TypeOf(Integrations.Garage, "VirtualGarage.VirtualGarageSave");
            var load = Integrations.TypeOf(Integrations.Garage, "VirtualGarage.VirtualGarageLoad");
            var files = Integrations.TypeOf(Integrations.Garage, "VirtualGarage.GarageFiles");
            _market = Integrations.TypeOf(Integrations.Garage, "VirtualGarage.Market.ShipMarket");
            SkipUnless(save != null && load != null && files != null && _market != null, "no VirtualGarage with its trade console");
            var watch = Integrations.TypeOf(Integrations.Watcher, "SentisWatcher.Recording.MarketWatch");
            var garage = Integrations.TypeOf(Integrations.Watcher, "SentisWatcher.Recording.GarageWatch");
            SkipUnless(watch != null && garage != null, "no SentisWatcher with MarketWatch");
            long Count(string name) => (long)watch.GetField(name, Any).GetValue(null);
            string Last() => (string)watch.GetField("LastDetail", Any).GetValue(null);
            string Key(string file) => (string)garage.GetMethod("Key", Any).Invoke(null, new object[] { file });
            var log = TorchLog();
            var start = new FileInfo(log).Length;

            yield return Arrive(Site.At(0, 3, -12));
            var buyer = -1;
            yield return Join(Site.At(6, 3, -12), i => buyer = i);
            var buyerIdentity = FakeClients.PlayerOf(buyer).Identity.IdentityId;
            var buyerSteam = FakeClients.PlayerOf(buyer).Id.SteamId;
            var sellerSteam = FakeClients.PlayerOf(Me).Id.SteamId;

            // the console: a projector of the seller's on a station
            var station = Build("market-station", Site.At(0, 0, 20), true,
                new Part("Projector/LargeProjector", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 2, 0, 0).With(b => Motion.Charged(b)));
            var console = station.GetFatBlocks().OfType<MyProjectorBase>().First();

            // the ship into the seller's garage, the world's save of it as the garage takes it (the file renamed)
            var ship = Build("market-ship", Site.Origin, false,
                new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 1, 0, 0).With(b => Motion.Charged(b)));
            var shipId = ship.EntityId;
            var unsaved = (string)save.GetMethod("Store", Any).Invoke(null, new object[] { Identity, new List<MyCubeGrid> { ship } });
            Check(unsaved != null, "VirtualGarage did not put the ship into the garage");
            _files.Add(unsaved);
            yield return Until(() => File.Exists(unsaved) ? 1 : 0, v => v > 0, "the garage file written", 10);
            var file = unsaved.Replace("_unsaved.sbc", ".sbc");
            File.Move(unsaved, file);
            _files.Add(file);
            var title = (string)files.GetMethod("Title", Any).Invoke(null, new object[] { file });
            var sellerKey = Key(file);

            // put up, taken off, put up again
            var listed = Count("Listings");
            Sell(console, sellerSteam, title);
            yield return Until(() => ListingOf(console) != null ? 1 : 0, v => v > 0, "the ship on sale on the console", 15);
            Check(Count("Listings") == listed + 1, "the ship put up for sale is not told (market_listed)");
            Note("listed: " + Last());
            Check(Last().Contains(Price.ToString("N0")) && Last().Contains("console " + console.EntityId), "market_listed does not tell the price and the console");
            var unlisted = Count("Unlistings");
            _market.GetMethod("Cancel", Any).Invoke(null, new object[] { console, sellerSteam, Identity });
            yield return null;
            Check(ListingOf(console) == null, "the seller's cancel left the ship on sale");
            Check(Count("Unlistings") == unlisted + 1 && Last().Contains("taken off by"), "the ship taken off sale is not told with who took it off (market_unlisted): " + Last());
            Note("unlisted: " + Last());
            Sell(console, sellerSteam, title);
            yield return Until(() => ListingOf(console) != null ? 1 : 0, v => v > 0, "the ship on sale again", 15);

            // bought
            MyBankingSystem.ChangeBalance(buyerIdentity, Price * 2);
            var bought = Count("Purchases");
            _listing = ListingOf(console);
            _market.GetMethod("Buy", Any).Invoke(null, new object[] { console, buyerSteam, buyerIdentity });
            var buyerFile = (string)_listing.GetType().GetField("BuyerFile").GetValue(_listing);
            Check(buyerFile != null && File.Exists(buyerFile), "the purchase did not put the ship into the buyer's garage");
            _files.Add(buyerFile);
            Check(Count("Purchases") == bought + 1, "the purchase is not told (market_bought)");
            Note("bought: " + Last());
            Check(Last().Contains(buyerIdentity.ToString()) && Last().Contains(Identity.ToString()) && Last().Contains(Price.ToString("N0")),
                "market_bought does not tell the buyer, the seller and the price: " + Last());

            // the seller's entry went with the ship
            var entry = garage.GetMethod("EntryOf", Any).Invoke(null, new object[] { Key(buyerFile) });
            Check(entry != null, "the buyer's garage entry is not known to the watcher");
            var from = (string)entry.GetType().GetField("From").GetValue(entry);
            var originals = (long[])entry.GetType().GetField("Originals").GetValue(entry);
            Note("the buyer's entry " + Key(buyerFile) + ": from " + from + ", put in with " + string.Join(",", originals ?? new long[0]));
            Check(from == sellerKey, "the buyer's entry does not come from the seller's (" + from + " for " + sellerKey + ")");
            Check(originals != null && originals.Contains(shipId), "the buyer's entry does not know what the ship was put in with");

            // taken out by the buyer: nothing twice in the world
            List<MyCubeGrid> taken = null;
            string failed = null;
            Load(load, buyerFile, buyerIdentity, Site.At(40, 0, 0), g => taken = g, why => failed = why);
            yield return Until(() => taken != null || failed != null ? 1 : 0, v => v > 0, "the bought ship taken out", 20);
            Check(failed == null, "the buyer could not take the ship out: " + failed);
            _grids.AddRange(taken);
            _files.Add(buyerFile + "_spawned_unsaved");
            yield return Seconds(3);
            var dupes = Tail(log, start).Split('\n').Where(l => l.Contains("a grid twice in the world") &&
                                                                (l.Contains(Key(buyerFile)) || l.Contains(sellerKey))).ToList();
            foreach (var line in dupes) Note(line.Trim());
            Check(dupes.Count == 0, "a clean sale and take-out told as a grid twice in the world");
            Result("put up, taken off (by the seller), put up again, bought for " + Price + " SC: each told with who, what, where; the seller's entry " +
                   "followed the ship to the buyer's; taken out with nothing told twice");
        }

        private void Sell(MyProjectorBase console, ulong sender, string title) =>
            _market.GetMethod("Sell", Any).Invoke(null, new object[] { console, sender, Identity, title, Price });

        private object ListingOf(MyProjectorBase console)
        {
            var listings = (IList)_market.GetField("_listings", Any).GetValue(null);
            lock (_market.GetField("Lock", Any).GetValue(null))
                foreach (var listing in listings)
                    if ((long)listing.GetType().GetField("ShipyardId").GetValue(listing) == console.EntityId &&
                        !(bool)listing.GetType().GetField("Pending").GetValue(listing)) return listing;
            return null;
        }

        private static void Load(Type load, string file, long owner, Vector3D around, Action<List<MyCubeGrid>> spawned, Action<string> failed)
        {
            var placementType = load.GetNestedType("Placement", Any);
            var placement = Activator.CreateInstance(placementType);
            placementType.GetField("Around").SetValue(placement, around);
            load.GetMethods(Any).First(m => m.Name == "Load" && m.GetParameters().Length == 5)
                .Invoke(null, new object[] { file, owner, placement, spawned, failed });
        }

        public override void Cleanup()
        {
            // the purchase is not left for the next start to undo, nor the listings with a ship of the check's
            try
            {
                if (_market != null)
                {
                    var listings = (IList)_market.GetField("_listings", Any).GetValue(null);
                    lock (_market.GetField("Lock", Any).GetValue(null))
                    {
                        foreach (var listing in listings.Cast<object>().ToList())
                            if (ReferenceEquals(listing, _listing) || ((string)listing.GetType().GetField("ShipName").GetValue(listing) ?? "").Contains("market-ship"))
                                listings.Remove(listing);
                        _market.GetMethod("Save", Any).Invoke(null, null);
                    }
                }
            }
            catch (Exception e) { Log.Warn("market cleanup: {0}", e.Message); }
            foreach (var grid in _grids)
                try { if (grid != null && !grid.MarkedForClose) grid.Close(); }
                catch { }
            foreach (var f in _files.Distinct())
                try
                {
                    foreach (var variant in new[] { f, f + "_market", f + "_spawned_unsaved", f + "_spawned" })
                        if (File.Exists(variant)) File.Delete(variant);
                }
                catch { }
            base.Cleanup();
        }

        private static string TorchLog() =>
            Directory.GetFiles(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs"), "Torch-*.log").OrderByDescending(File.GetLastWriteTimeUtc).First();

        private static string Tail(string path, long from)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                stream.Seek(Math.Min(from, stream.Length), SeekOrigin.Begin);
                using (var reader = new StreamReader(stream)) return reader.ReadToEnd();
            }
        }
    }
}
