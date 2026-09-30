using System;
using System.Collections;
using System.Diagnostics;
using System.Threading;
using Sandbox.Game;
using SentisTests.Core;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// A patched instance method of a structure survives collections while it runs (see <see cref="PatchRetBufScenario"/>).
    ///
    /// Torch re-emits a patched method as a static one that takes the instance as an object. For a structure's method the
    /// instance is a pointer to the structure - into the object that holds it, when it is a field: the explosion's own
    /// <c>MyExplosionInfo</c>. The collector, stopping the thread inside the re-emitted copy, takes that pointer for an
    /// object and reads its first field (a reference) for the object's type. This calls the patched
    /// <c>MyExplosionInfo.AffectVoxels</c> (SentisGameplayImprovements) on a structure held by an object for
    /// <see cref="Seconds"/> while another thread collects all the time, and checks the object and the heap after.
    /// </summary>
    public sealed class StructThisGcScenario : TestScenario
    {
        public const string ScenarioName = "struct_this_gc";
        private const int Seconds = 60;
        private const int CallsPerFrame = 200000;

        private sealed class Holder
        {
            public MyExplosionInfo Info;
        }

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => Seconds + 60;

        public override IEnumerator Run()
        {
            var holders = new Holder[64];
            for (var i = 0; i < holders.Length; i++)
                holders[i] = new Holder { Info = new MyExplosionInfo { Damage = 1234.5f, PlayerDamage = 42f, LifespanMiliseconds = 777, OriginEntity = 0x1122334455667788 } };

            var stop = false;
            var collections = 0;
            var collector = new Thread(() =>
            {
                var junk = new object[4096];
                var n = 0;
                while (!Volatile.Read(ref stop))
                {
                    for (var i = 0; i < junk.Length; i++) junk[i] = new byte[64 + (i & 255)];
                    GC.Collect((n++ & 7) == 0 ? 2 : 0, GCCollectionMode.Forced, true);
                    Interlocked.Increment(ref collections);
                }
            }) { IsBackground = true, Name = "struct_this_gc collector" };
            collector.Start();

            long calls = 0, trues = 0;
            var watch = Stopwatch.StartNew();
            try
            {
                while (watch.Elapsed.TotalSeconds < Seconds)
                {
                    for (var i = 0; i < CallsPerFrame; i++)
                    {
                        // a field of an object: the call gets a pointer into the holder
                        if (holders[i & 63].Info.AffectVoxels) trues++;
                        calls++;
                    }
                    yield return null;
                }
            }
            finally
            {
                Volatile.Write(ref stop, true);
            }
            collector.Join(10000);

            GC.Collect(2, GCCollectionMode.Forced, true);
            var damaged = 0;
            foreach (var holder in holders)
            {
                var info = holder.Info;
                if (info.Damage != 1234.5f || info.PlayerDamage != 42f || info.LifespanMiliseconds != 777 || info.OriginEntity != 0x1122334455667788 ||
                    info.ExcludedEntity != null || info.OwnerEntity != null || info.HitEntity != null || info.CustomEffect != null || info.CustomSound != null)
                    damaged++;
            }
            Note("STRUCT THIS GC RESULT | " + calls + " calls (" + trues + " true), " + collections + " collections in " + watch.Elapsed.TotalSeconds.ToString("F0") +
                 " s | holders damaged " + damaged + " of " + holders.Length);
            Check(damaged == 0, damaged + " structures changed under the collector");
        }
    }
}
