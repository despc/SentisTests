using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using SentisTests.Core;
using SpaceEngineers.Game.Entities.Blocks;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>The real ground of a planet: its voxel content, not the generated surface.</summary>
    internal sealed class PlanetGround
    {
        public readonly MyPlanet Planet;
        private readonly VRage.Voxels.MyStorageData _probe = new VRage.Voxels.MyStorageData(VRage.Voxels.MyStorageDataTypeFlags.Content);

        public PlanetGround(MyPlanet planet) => Planet = planet;

        public Vector3D Centre => Planet.PositionComp.GetPosition();

        public Vector3D Up(Vector3D point) => Vector3D.Normalize(point - Centre);

        public byte ContentAt(Vector3D point)
        {
            var voxel = Vector3I.Floor(point - Planet.PositionLeftBottomCorner) + Planet.StorageMin;
            _probe.Resize(Vector3I.One);
            Planet.Storage.ReadRange(_probe, VRage.Voxels.MyStorageDataTypeFlags.Content, 0, voxel, voxel);
            return _probe.Content(0);
        }

        /// <summary>The top of the rock under (or over) the point, searched 40 m either side of the generated surface.</summary>
        public Vector3D Ground(Vector3D point)
        {
            var generated = Planet.GetClosestSurfacePointGlobal(ref point);
            var up = Up(generated);
            for (var h = 40.0; h > -40.0; h -= 0.25)
                if (ContentAt(generated + up * h) >= 128)
                    return generated + up * (h + 0.25);
            return generated;
        }

        public bool UnderGround(MyCubeGrid grid)
        {
            var at = grid.PositionComp.WorldAABB.Center;
            return ContentAt(at + Up(at) * 1.5) >= 128;
        }
    }

    /// <summary>What the SentisOptimisations freezer has frozen, read by reflection.</summary>
    internal static class FreezerState
    {
        private static object _frozen, _physics;
        private static MethodInfo _contains;

        private static void Bind()
        {
            if (_contains != null) return;
            var type = AppDomain.CurrentDomain.GetAssemblies()
                           .Select(a => a.GetType("SentisOptimisationsPlugin.Freezer.FreezeLogic")).FirstOrDefault(t => t != null)
                       ?? throw new ScenarioFailedException("SentisOptimisations freezer is not loaded");
            _frozen = type.GetField("FrozenGrids", BindingFlags.Static | BindingFlags.Public).GetValue(null);
            _physics = type.GetField("FrozenPhysicsGrids", BindingFlags.Static | BindingFlags.Public).GetValue(null);
            _contains = _frozen.GetType().GetMethod("Contains");
        }

        public static bool IsFrozen(MyCubeGrid g)
        {
            Bind();
            return (bool)_contains.Invoke(_frozen, new object[] { g.EntityId });
        }

        public static bool IsPhysicsFrozen(MyCubeGrid g)
        {
            Bind();
            return (bool)_contains.Invoke(_physics, new object[] { g.EntityId });
        }

        public static bool HasFixedBody(MyCubeGrid g) => g.Physics?.RigidBody != null && g.Physics.RigidBody.IsFixed;
    }

    /// <summary>
    /// Plugin config changed for a run and put back afterwards. Torch saves a plugin's config file
    /// on every change - "in memory only" does not exist - so the original values also go to
    /// <see cref="RestoreFile"/> the first time a run touches them, and stay there until they are
    /// put back. <see cref="RestoreLeftovers"/> puts back whatever is still in it: before every
    /// run, after every run, and on the next session load if the server was killed halfway.
    ///
    /// Keys are "Plugin:Property" - <see cref="Optimisations"/> or <see cref="Gameplay"/>; a key
    /// without a plugin is a SentisOptimisations one, as the file used to hold only those.
    /// </summary>
    internal sealed class ConfigOverride
    {
        /// <summary>Set by the plugin at init: a file next to its config.</summary>
        public static string RestoreFile;

        public const string Optimisations = "SentisOptimisations";
        public const string Gameplay = "SentisGameplayImprovements";

        private readonly string _plugin;
        private readonly Dictionary<string, object> _saved = new Dictionary<string, object>();

        public ConfigOverride(string plugin = Optimisations) => _plugin = plugin;

        public void Set(string property, object value)
        {
            var config = Config(_plugin);
            var prop = config.GetType().GetProperty(property);
            if (!_saved.ContainsKey(property))
                _saved[property] = Remember(_plugin, property);
            prop.SetValue(config, value);
        }

        public void Restore()
        {
            // Freezer off first, so everything thaws; then the rest.
            var config = Config(_plugin);
            if (_saved.TryGetValue("FreezerEnabled", out var enabled)) config.GetType().GetProperty("FreezerEnabled").SetValue(config, enabled);
            foreach (var pair in _saved)
                if (pair.Key != "FreezerEnabled") config.GetType().GetProperty(pair.Key).SetValue(config, pair.Value);
            var file = ReadFile();
            foreach (var key in _saved.Keys) file.Remove(Key(_plugin, key));
            WriteFile(file);
            _saved.Clear();
        }

        /// <summary>
        /// The original value of the setting, noted in <see cref="RestoreFile"/> before a run
        /// changes it - unless it is noted there already: then that one is the real original.
        /// </summary>
        public static object Remember(string plugin, string property)
        {
            var config = Config(plugin);
            var prop = config.GetType().GetProperty(property)
                       ?? throw new InvalidOperationException(plugin + " has no setting " + property);
            var file = ReadFile();
            if (file.TryGetValue(Key(plugin, property), out var original)) return Parse(prop, original);
            var value = prop.GetValue(config);
            file[Key(plugin, property)] = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
            WriteFile(file);
            return value;
        }

        /// <summary>Puts back every original still in <see cref="RestoreFile"/>.</summary>
        public static void RestoreLeftovers()
        {
            var leftover = ReadFile();
            if (leftover.Count == 0) return;
            foreach (var pair in leftover.OrderBy(p => p.Key.EndsWith("FreezerEnabled") ? 0 : 1))
            {
                var colon = pair.Key.IndexOf(':');
                var plugin = colon > 0 ? pair.Key.Substring(0, colon) : Optimisations;
                var property = colon > 0 ? pair.Key.Substring(colon + 1) : pair.Key;
                try
                {
                    var config = Config(plugin);
                    var prop = config.GetType().GetProperty(property);
                    if (prop != null) prop.SetValue(config, Parse(prop, pair.Value));
                }
                catch (Exception e)
                {
                    TestScenario.Log.Warn("could not put back " + pair.Key + ": " + e.Message);
                }
            }
            DeleteFile();
            TestScenario.Log.Warn("plugin config left changed by a test put back: " + string.Join(", ", leftover.Keys));
        }

        private static string Key(string plugin, string property) => plugin + ":" + property;

        private static object Config(string plugin)
        {
            var typeName = plugin == Gameplay
                ? "SentisGameplayImprovements.SentisGameplayImprovementsPlugin"
                : "SentisOptimisationsPlugin.SentisOptimisationsPlugin";
            var type = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType(typeName)).FirstOrDefault(t => t != null)
                ?? throw new InvalidOperationException(plugin + " is not loaded");
            var property = type.GetProperty("Config", BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            if (property != null) return property.GetValue(null);
            var field = type.GetField("_config", BindingFlags.Static | BindingFlags.NonPublic);
            var persistent = field?.GetValue(null) ?? throw new InvalidOperationException(plugin + " has no config");
            return persistent.GetType().GetProperty("Data")?.GetValue(persistent) ?? persistent;
        }

        private static object Parse(PropertyInfo prop, string value) =>
            Convert.ChangeType(value, prop.PropertyType, System.Globalization.CultureInfo.InvariantCulture);

        private static Dictionary<string, string> ReadFile()
        {
            var values = new Dictionary<string, string>();
            if (RestoreFile == null || !System.IO.File.Exists(RestoreFile)) return values;
            foreach (var line in System.IO.File.ReadAllLines(RestoreFile))
            {
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line.Substring(0, eq);
                if (key.IndexOf(':') < 0) key = Key(Optimisations, key);
                values[key] = line.Substring(eq + 1);
            }
            return values;
        }

        private static void WriteFile(Dictionary<string, string> values)
        {
            if (RestoreFile == null) return;
            if (values.Count == 0)
            {
                DeleteFile();
                return;
            }
            System.IO.File.WriteAllLines(RestoreFile, values.Select(p => p.Key + "=" + p.Value));
        }

        private static void DeleteFile()
        {
            if (RestoreFile != null && System.IO.File.Exists(RestoreFile)) System.IO.File.Delete(RestoreFile);
        }
    }

    /// <summary>Mechanical parts of a group of grids: tops, landing gears, blocks.</summary>
    internal static class RigParts
    {
        public static List<MyMechanicalConnectionBlockBase> Tops(IEnumerable<MyCubeGrid> grids) =>
            grids.Where(g => !g.Closed).SelectMany(g => g.GetFatBlocks().OfType<MyMechanicalConnectionBlockBase>()).ToList();

        public static int Attached(IEnumerable<MyCubeGrid> grids) => Tops(grids).Count(t => t.TopGrid != null);

        public static List<MyLandingGear> Gears(IEnumerable<MyCubeGrid> grids) =>
            grids.Where(g => !g.Closed).SelectMany(g => g.GetFatBlocks().OfType<MyLandingGear>()).ToList();

        public static int Locked(IEnumerable<MyCubeGrid> grids) =>
            Gears(grids).Count(g => g.LockMode == SpaceEngineers.Game.ModAPI.Ingame.LandingGearMode.Locked);

        public static int Blocks(IEnumerable<MyCubeGrid> grids) => grids.Where(g => !g.Closed).Sum(g => g.CubeBlocks.Count);

        public static double Integrity(IEnumerable<MyCubeGrid> grids) =>
            grids.Where(g => !g.Closed).Sum(g => g.CubeBlocks.Sum(b => (double)b.Integrity));

        /// <summary>Pistons out at 0.3 m/s, rotors at 5 RPM, hinges at 2 RPM: the tops move when the freeze comes.</summary>
        public static void StartMotors(IEnumerable<MyCubeGrid> grids)
        {
            foreach (var mech in Tops(grids))
            {
                switch (mech)
                {
                    case Sandbox.ModAPI.IMyPistonBase piston:
                        piston.Velocity = 0.3f;
                        break;
                    case Sandbox.ModAPI.IMyMotorStator stator when !(mech is MyMotorSuspension):
                        stator.TargetVelocityRPM = mech.BlockDefinition.Id.SubtypeName.Contains("Hinge") ? 2f : 5f;
                        break;
                }
            }
        }
    }
}
