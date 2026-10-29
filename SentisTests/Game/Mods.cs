using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using VRage.Game;
using VRage.Game.Entity;

namespace SentisTests.Game
{
    /// <summary>
    /// What the world's mods change of what the checks expect (the modded V5: WeaponCore, its weapons, the speed limits of
    /// its script pack, components and definitions of its own). Read by reflection: the mods' scripts are compiled when
    /// the world loads, in assemblies of their own.
    /// </summary>
    public static class Mods
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>A type of a mod's script by its full name, or null (looked up in every loaded assembly).</summary>
        public static Type Script(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var type = assembly.GetType(fullName, false);
                    if (type != null) return type;
                }
                catch (Exception) { }
            }
            return null;
        }

        /// <summary>Whether the world has any mods.</summary>
        public static bool InWorld => Sandbox.Game.World.MySession.Static?.Mods?.Count > 0;

        // ---------------------------------------------------------------------------------------------- WeaponCore

        private static object WcSession() => Script("CoreSystems.Session")?.GetField("I", Any)?.GetValue(null);

        /// <summary>Whether WeaponCore runs in this world.</summary>
        public static bool WeaponCore => WcSession() != null;

        /// <summary>
        /// Whether WeaponCore takes the block over (its parts definitions name the subtype: vanilla turrets, searchlights and
        /// missile launchers among them): the game's own targeting and firing of it do not run.
        /// </summary>
        public static bool WeaponCoreManages(MyDefinitionId block)
        {
            var session = WcSession();
            if (session == null) return false;
            return session.GetType().GetField("CoreSystemsDefs", Any)?.GetValue(session) is IDictionary defs && defs.Contains(block.SubtypeName);
        }

        /// <summary>WeaponCore's AI of a top grid, or null.</summary>
        public static object WeaponCoreAi(MyCubeGrid grid)
        {
            var session = WcSession();
            return session?.GetType().GetField("EntityAIs", Any)?.GetValue(session) is IDictionary ais && ais.Contains(grid) ? ais[grid] : null;
        }

        /// <summary>
        /// The magazines of the ammunition WeaponCore has active for a block's weapons (physical items only): its own, not
        /// the game's definition's - those its weapons take no shot from.
        /// </summary>
        public static List<MyDefinitionId> WeaponCoreMagazines(MyCubeBlock block)
        {
            var result = new List<MyDefinitionId>();
            var ai = WeaponCoreAi(block.CubeGrid);
            if (ai == null) return result;
            foreach (var comp in (IList)ai.GetType().GetField("WeaponComps", Any).GetValue(ai))
            {
                if (Peek(comp, "Cube") != block) continue;
                foreach (var weapon in (IList)Peek(comp, "Platform.Weapons"))
                    if (Peek(weapon, "ActiveAmmoDef.AmmoDefinitionId") is MyDefinitionId id && !id.TypeId.IsNull &&
                        MyDefinitionManager.Static.TryGetPhysicalItemDefinition(id, out _) && !result.Contains(id))
                        result.Add(id);
            }
            return result;
        }

        /// <summary>A field or property path of an object of a mod's (the most derived class's member first), or null.</summary>
        public static object Peek(object of, string path)
        {
            foreach (var name in path.Split('.'))
            {
                if (of == null) return null;
                object next = null;
                var found = false;
                for (var type = of.GetType(); type != null && !found; type = type.BaseType)
                {
                    var field = type.GetField(name, Any | BindingFlags.DeclaredOnly);
                    if (field != null) { next = field.GetValue(of); found = true; continue; }
                    var property = type.GetProperties(Any | BindingFlags.DeclaredOnly).FirstOrDefault(p => p.Name == name && p.GetIndexParameters().Length == 0);
                    if (property != null) { next = property.GetValue(of); found = true; }
                }
                if (!found) return null;
                of = next;
            }
            return of;
        }

        // ---------------------------------------------------------------------------------------------- speed limits

        /// <summary>
        /// The speed a grid is held to by a script of the world's (the V5 script pack's SpeedRestrictionGrid: a limit of each
        /// grid's, past the game's), or null when no script holds it.
        /// </summary>
        public static double? ScriptSpeedLimit(MyCubeGrid grid)
        {
            foreach (var name in new[] { "Sentis_v5.SpeedRestrictionsSession" })
            {
                var session = Script(name);
                if (session?.GetField("MaxSpeeds", Any)?.GetValue(null) is IDictionary speeds && speeds.Contains(grid))
                    return Convert.ToDouble(speeds[grid]);
            }
            return null;
        }

        // ---------------------------------------------------------------------------------------------- components

        private static HashSet<MyDefinitionId> _made;

        /// <summary>Whether some blueprint makes the item (an admin's component, a plushie of a mod's: no blueprint does).</summary>
        public static bool Craftable(MyDefinitionId item)
        {
            if (_made == null)
            {
                var made = new HashSet<MyDefinitionId>();
                foreach (var blueprint in MyDefinitionManager.Static.GetBlueprintDefinitions())
                    if (blueprint?.Results != null)
                        foreach (var result in blueprint.Results) made.Add(result.Id);
                _made = made;
            }
            return _made.Contains(item);
        }

        /// <summary>
        /// A block of a mod's no player builds: one of its components is a mod's that no blueprint makes (an admin's
        /// component), or it takes hours to weld (a prize box, a zone's block, a mod's fake block: build times of 99999 s
        /// and more). The game's own components with no blueprint (a safe zone's chip) do not count.
        /// </summary>
        public static bool AdminBlock(MyCubeBlockDefinition definition) =>
            definition.Components != null && definition.Components.Any(c => !Craftable(c.Definition.Id) && c.Definition.Context?.IsBaseGame == false ||
                                                                             c.Definition.Id.SubtypeName.IndexOf("admin", StringComparison.OrdinalIgnoreCase) >= 0) ||
            definition.Context?.IsBaseGame == false && (definition.Id.SubtypeName.IndexOf("admin", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                        definition.Id.SubtypeName.IndexOf("_ADM", StringComparison.Ordinal) >= 0) ||
            definition.IntegrityPointsPerSec > 0 && definition.MaxIntegrity / definition.IntegrityPointsPerSec > 10000;
    }
}
