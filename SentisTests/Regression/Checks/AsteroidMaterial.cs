using System;
using System.Collections;
using System.Linq;
using System.Threading;
using SentisTests.Game;
using VRage.Voxels;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// The materials of one of the game's predefined asteroids (Asteroid128-001) are read all through it, in small boxes,
    /// on the game thread and on two others at once - what the voxel physics and the mesher do. Such an asteroid takes a
    /// deposit's material from its template, whose own composite provider reads it inside the first reading on the same
    /// thread: in the vanilla game that broke the thread's one list of deposits ("Collection was modified", the server gone
    /// three times on the stand, 22-24.10.2026; SentisOptimisations' CompositeMaterialReentry fixes it). No reading may
    /// throw. The check does not make the vanilla game throw (24.10: none of 8768 readings did without the fix), it
    /// tells how many nested readings SentisOptimisations saw while it ran.
    /// </summary>
    public sealed class AsteroidMaterialCheck : RegressionCheck
    {
        public override string Name => Prefix + "asteroid_material";
        public override int TimeoutSeconds => 180;
        private const int Box = 8;

        protected override IEnumerator Check()
        {
            // a storage the way the game makes a predefined asteroid of an encounter (GE_Voxel_...): the template's data
            // provider over the template, which reads the template's own provider for the deposits' material
            var providerType = typeof(Sandbox.Game.Entities.MyVoxelBase).Assembly.GetType("Sandbox.Game.World.Generator.MyPredefinedDataProvider");
            var provider = providerType?.GetMethod("CreatePredefinedShape", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public)
                ?.Invoke(null, new object[] { "Asteroid128-001", "" });
            var template = provider?.GetType().GetProperty("Storage")?.GetValue(provider) as Sandbox.Engine.Voxels.MyStorageBase;
            Check(template != null, "no predefined provider of Asteroid128-001");
            var storage = new Sandbox.Engine.Voxels.MyOctreeStorage((VRage.Voxels.IMyStorageDataProvider)provider, template.Size);
            Check(storage != null, "no predefined storage of Asteroid128-001");
            yield return null;
            var size = storage.Size;
            // one voxel of the template alone, then one of the asteroid, each on a fresh thread: how many nested readings
            // each makes
            string probe = "";
            foreach (var (what, of) in new[] { ("template", (VRage.Game.Voxels.IMyStorage)template), ("asteroid", storage) })
            {
                long delta = 0;
                var t = new Thread(() =>
                {
                    var one = new MyStorageData();
                    one.Resize(Vector3I.One);
                    var at = of.Size / 2;
                    var before = Nested();
                    try { of.ReadRange(one, MyStorageDataTypeFlags.ContentAndMaterial, 0, at, at); }
                    catch (Exception e) { probe += what + " threw " + e.GetType().Name + "; "; }
                    delta = Nested() - before;
                });
                t.Start();
                t.Join();
                probe += what + " voxel: " + delta + " nested; ";
            }
            Note(probe);
            var nestedBefore = Nested();
            var boxes = 0;
            var errors = 0;
            string first = null;

            // two threads beside the game thread, each through its own half of the boxes
            var others = Enumerable.Range(0, 2).Select(t => new Thread(() =>
            {
                var data = new MyStorageData();
                data.Resize(new Vector3I(Box));
                for (var lod = 0; lod <= 2; lod++)
                for (var z = t; z * Box < size.Z >> lod; z += 2)
                    for (var y = 0; y * Box < size.Y >> lod; y++)
                        for (var x = 0; x * Box < size.X >> lod; x++)
                            try
                            {
                                var min = new Vector3I(x, y, z) * Box;
                                storage.ReadRange(data, (x + y) % 2 == 0 ? MyStorageDataTypeFlags.ContentAndMaterial : MyStorageDataTypeFlags.Material, lod, min, min + Box - 1);
                                Interlocked.Increment(ref boxes);
                            }
                            catch (Exception e)
                            {
                                Interlocked.Increment(ref errors);
                                Interlocked.CompareExchange(ref first, e.GetType().Name + ": " + e.Message, null);
                            }
            }) { IsBackground = true }).ToList();
            foreach (var thread in others) thread.Start();

            var own = new MyStorageData();
            own.Resize(new Vector3I(Box));
            var done = 0;
            for (var z = 0; z * Box < size.Z; z++)
                for (var y = 0; y * Box < size.Y; y++)
                {
                    for (var x = 0; x * Box < size.X; x++)
                        try
                        {
                            var min = new Vector3I(x, y, z) * Box;
                            storage.ReadRange(own, MyStorageDataTypeFlags.ContentAndMaterial, 0, min, min + Box - 1);
                            boxes++;
                        }
                        catch (Exception e)
                        {
                            errors++;
                            first = first ?? e.GetType().Name + ": " + e.Message;
                        }
                    if (++done % 4 == 0) yield return null;
                }
            var waited = 0;
            while (others.Any(t => t.IsAlive) && waited++ < 60 * 60) yield return null;
            Check(others.All(t => !t.IsAlive), "the other threads did not finish in a minute");

            var nested = nestedBefore < 0 ? -1 : Nested() - nestedBefore;
            Check(errors == 0, errors + " of " + (boxes + errors) + " readings threw: " + first);
            storage.Close();
            Result(probe + boxes + " boxes of " + Box + "^3 read on three threads, none threw; nested readings of the template " +
                   (nested < 0 ? "not counted (no SentisOptimisations)" : nested.ToString()));
        }

        /// <summary>SentisOptimisations' count of nested readings, -1 without it.</summary>
        private static long Nested()
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("SentisOptimisationsPlugin.CrashFix.CompositeMaterialReentry")).FirstOrDefault(t => t != null);
            var field = type?.GetField("Nested");
            return field == null ? -1 : Convert.ToInt64(field.GetValue(null));
        }
    }
}
