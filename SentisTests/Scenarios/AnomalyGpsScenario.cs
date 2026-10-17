using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.ModAPI;
using SentisTests.Core;
using SentisTests.Game;
using VRage.Game.ModAPI;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// SentisAnomalyZone's GPS of the zones for every player (a fake client with a character):
    /// - a player without the zone's GPS gets it, shown on the HUD, in the color of the settings;
    /// - hidden by the player, it stays hidden after a reconnect (no second one either);
    /// - the zone moved: the GPS goes with it, still hidden;
    /// - removed by the player, it comes back, shown, after a reconnect.
    /// A reconnect is AnomalyZoneApi.GpsRecheck: the player is looked at again, as the plugin does at a connect.
    /// </summary>
    public class AnomalyGpsScenario : TestScenario
    {
        public const string ScenarioName = "anomaly_gps";
        private const string ZoneId = "scenario-gps";
        private const string ZoneName = "Сценарий GPS";
        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };

        private readonly Dictionary<string, object> _settingsBefore = new Dictionary<string, object>();
        private Type _api;
        private long _player;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 120;
        public override string[] Requires => new[] { Integrations.AnomalyZone };

        private object Call(string method, params object[] args)
        {
            var m = _api.GetMethod(method, BindingFlags.Public | BindingFlags.Static) ?? throw new MissingMethodException("AnomalyZoneApi", method);
            return m.Invoke(null, args);
        }

        private void Setting(string name, object value)
        {
            var old = Call("Set", name, value);
            if (!_settingsBefore.ContainsKey(name)) _settingsBefore[name] = old;
        }

        private List<IMyGps> Ours(Vector3D? at = null) =>
            MyAPIGateway.Session.GPS.GetGpsList(_player).Where(g => g.Name == ZoneName || at.HasValue && Vector3D.Distance(g.Coords, at.Value) < 1).ToList();

        public override IEnumerator Run()
        {
            _api = RequireType(Integrations.TypeOf(Integrations.AnomalyZone, "SentisAnomalyZone.AnomalyZoneApi"), "SentisAnomalyZone.AnomalyZoneApi");
            Check((bool)_api.GetProperty("Running").GetValue(null), "SentisAnomalyZone is not running");
            WorldApi.EnsureUnpaused(Name);

            var anchor = WorldApi.LoadAuthoredGroup(WheelPerfScenario.ResourceName, WorldApi.EntityPrefix + "ag-anchor")[0]
                .PositionAndOrientation.Value.GetMatrix().Translation;
            var site = anchor + Vector3D.Normalize(new Vector3D(-0.4, -0.7, 0.6)) * 400000;
            var moved = site + new Vector3D(3000, 0, 0);

            Setting("GpsEnabled", true);
            Setting("GpsName", "{zone}");
            Setting("GpsColor", "#FFFF0000");
            Setting("CenterCube", false);
            Call("Forget", ZoneId);
            Call("PutZone", ZoneId, ZoneName, site.X, site.Y, site.Z, 60.0, 600.0);

            FakeClients.Add(1, Network, i => (site + new Vector3D(0, 2000, 0), 0, 0), withCharacters: true);
            var body = Wait(() => FakeClients.HasLiveCharacter(0), "a player with a body", 30);
            while (body.MoveNext()) yield return body.Current;
            _player = FakeClients.PlayerOf(0).Identity.IdentityId;

            // a new player gets the zone's GPS, shown, red
            var given = Wait(() => Ours(site).Count == 1, "the zone's GPS given", 15);
            while (given.MoveNext()) yield return given.Current;
            var gps = Ours(site).Single();
            Note($"given: «{gps.Name}» at {Vector3D.Distance(gps.Coords, site):0.0} m off the center, shown {gps.ShowOnHud}, color {gps.GPSColor}");
            Check(gps.Name == ZoneName && Vector3D.Distance(gps.Coords, site) < 1, "the GPS is not the zone's name and center");
            Check(gps.ShowOnHud, "the new GPS is not shown on the HUD");
            Check(gps.GPSColor.R == 255 && gps.GPSColor.G == 0 && gps.GPSColor.B == 0, "the new GPS is not red: " + gps.GPSColor);

            // hidden by the player: stays hidden after a reconnect
            MyAPIGateway.Session.GPS.SetShowOnHud(_player, gps, false);
            yield return WaitForTicks(5);
            Call("GpsRecheck", _player);
            var hiddenLook = WaitForSeconds(12, "a reconnect with the GPS hidden");
            while (hiddenLook.MoveNext()) yield return hiddenLook.Current;
            var hidden = Ours(site);
            Check(hidden.Count == 1, hidden.Count + " zone GPS after a reconnect with it hidden, not 1");
            Check(!hidden[0].ShowOnHud, "the hidden GPS was shown again");

            // the zone moved: the GPS goes with it, still hidden
            Call("PutZone", ZoneId, ZoneName, moved.X, moved.Y, moved.Z, 60.0, 600.0);
            var follows = Wait(() => Ours(moved).Count == 1 && Vector3D.Distance(Ours(moved)[0].Coords, moved) < 1, "the GPS moved with the zone", 15);
            while (follows.MoveNext()) yield return follows.Current;
            var after = Ours(moved);
            Check(after.Count == 1, after.Count + " zone GPS after the zone moved, not 1");
            Check(!after[0].ShowOnHud, "the moved GPS was shown though the player hid it");

            // removed by the player: back at the next visit, shown
            MyAPIGateway.Session.GPS.RemoveGps(_player, after[0]);
            yield return WaitForTicks(5);
            Check(Ours(moved).Count == 0, "the GPS was not removed");
            Call("GpsRecheck", _player);
            var back = Wait(() => Ours(moved).Count == 1, "the removed GPS back after a reconnect", 15);
            while (back.MoveNext()) yield return back.Current;
            Check(Ours(moved).Single().ShowOnHud, "the GPS given again is not shown");

            Note("PASS: given shown and red; hidden stays hidden after a reconnect and when the zone moves; removed comes back");
        }

        public override void Cleanup()
        {
            try
            {
                if (_player != 0)
                    foreach (var gps in MyAPIGateway.Session.GPS.GetGpsList(_player).Where(g => g.Name == ZoneName).ToList())
                        MyAPIGateway.Session.GPS.RemoveGps(_player, gps);
                if (_api != null)
                {
                    Call("Forget", ZoneId);
                    Call("RemoveZone", ZoneId);
                    foreach (var setting in _settingsBefore) Call("Set", setting.Key, setting.Value);
                }
            }
            catch (Exception e)
            {
                Log.Warn(e, "anomaly_gps: the zone's settings were not restored");
            }
            FakeClients.RemoveAll();
            base.Cleanup();
        }
    }
}
