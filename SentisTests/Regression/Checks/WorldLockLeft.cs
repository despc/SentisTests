using System;
using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using Havok;
using HarmonyLib;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// A Havok world left locked for its critical operations does not take the server down (the stand, 06-07.11.2026:
    /// eight crashes, an access violation in HkRigidBody_AddGravityAction as a body went into a world locked for good,
    /// the game's handler of a crash killing the process without a word in Torch's log). The check leaves a world locked
    /// the way that happened - Havok's own lock taken, no managed code holding it:
    /// <list type="bullet">
    /// <item>A: nothing put into it: after the frame's physics the world is let go (SentisOptimisations'
    /// HavokWorldLockGuard counts it);</item>
    /// <item>B: a grid built into it in the same frame, before the physics - the crash in the game: the grid's body is in
    /// the world after (the guard let the lock go and the body taken), the world unlocked.</item>
    /// </list>
    /// Skipped without the guard: the second part would crash the server.
    /// </summary>
    public sealed class WorldLockLeftCheck : RegressionCheck
    {
        public override string Name => Prefix + "world_lock_left";
        public override bool NeedsOwnCluster => true;

        private const int LockOffset = 0xE8;
        private const int WorldOfBody = 0x10;

        protected override IEnumerator Check()
        {
            var guard = AccessTools.TypeByName("SentisOptimisationsPlugin.CrashFix.HavokWorldLockGuard");
            long Count(string name) => (long)(guard?.GetField(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(null) ?? -1L);
            var on = guard?.GetField("On", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(null) as bool? == true;
            if (!on) throw new Core.ScenarioSkippedException("SentisOptimisations' HavokWorldLockGuard is not on: a world left locked would crash the server");
            var nativeLock = typeof(HkWorld).GetMethod("HkWorld_LockCriticalOperations", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            Check(nativeLock != null, "no HkWorld_LockCriticalOperations to lock a world with");

            yield return Arrive(Site.At(0, 6, -10));
            var anchor = Build("lock-anchor", Site.Origin, true, new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0));
            yield return WaitForTicks(5);
            var world = (anchor.Physics as MyPhysicsBody)?.HavokWorld;
            Check(world != null && !world.IsDisposed, "the anchor has no world");
            int Locks() => Marshal.ReadInt32(world.NativeObject + LockOffset);
            Check(Locks() == 0, "the site's world is locked before the check: " + Locks());

            // A: locked, nothing put in: let go after the physics
            var unlockedBefore = Count("Unlocked");
            nativeLock.Invoke(null, new object[] { world.NativeObject });
            Check(Locks() == 1, "the world did not lock: " + Locks());
            yield return WaitForTicks(3);
            Note($"A: locked once by Havok's own call; {Locks()} after the physics; the guard let {Count("Unlocked") - unlockedBefore} world(s) go");
            Check(Locks() == 0, "the world left locked stayed locked after the physics: " + Locks());
            Check(Count("Unlocked") > unlockedBefore, "the guard did not count the world it let go");

            // B: locked and a grid built into it in the same frame - the game's crash
            var healedBefore = Count("Healed");
            var leftBefore = Count("LeftOut");
            nativeLock.Invoke(null, new object[] { world.NativeObject });
            var grid = Build("lock-victim", Site.Origin + Site.Up * 8, false, new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0));
            var body = (grid.Physics as MyPhysicsBody)?.RigidBody;
            var inWorld = body != null && !body.IsDisposed && Marshal.ReadIntPtr(body.NativeObject + WorldOfBody) != IntPtr.Zero;
            Note($"B: a grid built into the locked world: its body in a world {inWorld}, the world's lock {Locks()}; the guard took {Count("Healed") - healedBefore}, left out {Count("LeftOut") - leftBefore}");
            yield return WaitForTicks(3);
            Check(Locks() == 0, "the world stayed locked after the grid's add and the physics: " + Locks());
            Check(Count("Healed") > healedBefore || Count("LeftOut") > leftBefore, "the guard saw no body put into the locked world");
            body = (grid.Physics as MyPhysicsBody)?.RigidBody;
            var takenAfter = body != null && !body.IsDisposed && Marshal.ReadIntPtr(body.NativeObject + WorldOfBody) != IntPtr.Zero;
            Check(takenAfter, "the grid's body is in no world after the guard");
            Result($"a world left locked: let go after the physics; a grid built into it the same frame: its body taken ({(Count("Healed") > healedBefore ? "the lock let go at its add" : "left out")}), the server up");
        }
    }
}
