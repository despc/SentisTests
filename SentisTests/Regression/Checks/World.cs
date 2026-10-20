using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// Weather on a planet: heavy rain set at a place (as an admin's /weather does) is there, dims the sun on solar panels
    /// and strengthens the wind there as its definition says, and changes nothing 2 km away; removed, it is gone.
    /// </summary>
    public sealed class WeatherCheck : RegressionCheck
    {
        public override string Name => Prefix + "weather";
        public override SiteKind Kind => SiteKind.Planet;

        private const string Rain = "RainHeavy";
        private bool _set;

        protected override IEnumerator Check()
        {
            SkipUnless(MySession.Static.Settings.WeatherSystem, "the weather is off in this world");
            Check(Site.Planet.HasAtmosphere, "the planet of the planet sites has no atmosphere");
            var weathers = MyAPIGateway.Session.WeatherEffects;
            var here = Site.Ground(Site.Origin) + Site.Up * 5;
            var far = Site.Ground(Site.Origin + Site.Forward * 2000) + Site.Up * 5;
            yield return Arrive(here + Site.Up * 3);
            var definition = Sandbox.Definitions.MyDefinitionManager.Static.GetWeatherEffect(Rain);
            SkipUnless(definition != null, "no weather " + Rain + " in this game");
            var farWeather = weathers.GetWeather(far);
            Check(weathers.SetWeather(Rain, 150, here, false, Vector3D.Zero), "the weather " + Rain + " was not set");
            _set = true;
            yield return Until(() => weathers.GetWeather(here) == Rain ? 1 : 0, v => v > 0, "the rain where it was set", 10, () => "the weather there: '" + weathers.GetWeather(here) + "'");
            var solar = weathers.GetSolarMultiplier(here);
            var wind = weathers.GetWindMultiplier(here);
            Check(Math.Abs(solar - definition.SolarOutputModifier) < 0.05, "the sun in the rain is x" + F(solar) + ", the rain's definition says x" + F(definition.SolarOutputModifier));
            Check(Math.Abs(wind - definition.WindOutputModifier) < 0.05, "the wind in the rain is x" + F(wind) + ", the rain's definition says x" + F(definition.WindOutputModifier));
            // 2 km off the planet's own weather goes on (it may change by itself), not the rain set here
            Check(weathers.GetWeather(far) != Rain || farWeather == Rain, "2 km off the weather became the rain set here ('" + weathers.GetWeather(far) + "', was '" + farWeather + "')");
            weathers.RemoveWeather(here);
            _set = false;
            yield return Until(() => weathers.GetWeather(here) == Rain ? 0 : 1, v => v > 0, "the rain gone", 10);
            Result(Rain + ": the sun x" + F(solar) + ", the wind x" + F(wind) + " in it, nothing 2 km off; removed");
        }

        public override void CleanupLeftovers()
        {
            if (_set && Site != null) MyAPIGateway.Session.WeatherEffects.RemoveWeather(Site.Ground(Site.Origin) + Site.Up * 5);
        }
    }

    /// <summary>A meteor flung at a station's armor plate in open space hits it and damages it.</summary>
    public sealed class MeteorCheck : RegressionCheck
    {
        public override string Name => Prefix + "meteor";
        protected override bool NeedsDamage => true;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(-30, 10, 0));
            var parts = new List<Part>();
            for (var x = -1; x <= 1; x++)
            for (var y = -1; y <= 1; y++)
                parts.Add(new Part("CubeBlock/LargeBlockArmorBlock", x, y, 0));
            var plate = Build("meteor-target", Site.Origin, true, parts);
            var whole = plate.GetBlocks().Sum(b => (double)b.Integrity);
            var from = Site.Origin + Site.Forward * 80;
            var meteor = MyMeteor.SpawnRandom(from, (Vector3)(-Site.Forward));
            Track(meteor);
            Func<double> damage = () => whole - (plate.MarkedForClose ? 0 : plate.GetBlocks().Sum(b => (double)b.Integrity));
            yield return Until(damage, v => v > 0, "damage from the meteor", 15,
                () => "the meteor " + (meteor.MarkedForClose ? "gone" : F(Vector3D.Distance(meteor.PositionComp.GetPosition(), Site.Origin)) + " m off at " + F(Motion.Speed(meteor)) + " m/s"));
            Result("the meteor hit the plate: " + F(damage()) + " of " + F(whole) + " integrity, " + (whole - damage() <= 0 ? "all" : plate.BlocksCount + " blocks") + " left");
        }
    }
}
