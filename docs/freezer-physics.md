# Freezer: physics freeze with subgrids, landing gears, planets

Scenario `freezer_physics`, SentisOptimisations freezer with **Freeze Physics** on, distance 500 m, a fake
client with a character coming and going to freeze and thaw.

## Rigs

`Resources/FreezerTest.xml` is FREEZER_TEST_WITH_SUBGRIDS from the world save (`tools/extract_group.py`, which
follows landing gears via `<AttachedEntityId>` too): a chassis on six 5x5 wheels, two piston chains and a hinge
(12 tops, 14 grids), one landing gear locked sideways onto a static wall ("Static Grid 9155").
`WorldApi.LoadAuthoredGroup` remaps the gear's `AttachedEntityId` together with the entity ids.

| rig | what holds it | expected freeze |
|---|---|---|
| planet, gear on static | gear on the static wall | logic only |
| planet, free | wheels on the ground (wall left out, gear released) | physics |
| space, gear on static | gear on the static wall, 150 km up | logic only |
| space, free | nothing | physics |
| planet, gear on ground | 5x5 plate on a large gear (1x2x3) locked to voxels | logic only |

Pistons run at 0.3 m/s, the hinge at 2 RPM, so the tops are moving when the freeze comes.

## Checks per cycle (5 cycles, then a toggle)

- when frozen: a group with a static grid or a voxel lock has no physics-frozen grid; a free group is physics-frozen
  whole or not at all; a physics-frozen grid has a fixed body and does not move (> 5 cm) while frozen;
- after thaw: no grid closed, bases do not move > 1 m, nothing under the ground, tops still attached, gears still
  locked, no damage, no lost blocks, the chassis moves < 1 m/s, and no non-static grid keeps a fixed body;
- toggle: with everything frozen, FreezePhysics off must return every body to dynamic and keep the logic freeze;
  on again must physics-freeze the free groups whole and leave the held ones alone.

Vanilla keeps the body of a grid whose gear is locked to a static grid or voxels fixed (the chassis of both
"gear on static" rigs and the plate). The check takes the fixed bodies before the freezer as the baseline.

## Result

Before the fixes, 5 cycles: freeze, thaw, subgrids, gears, damage and the ground were all fine. The code review
found the problems below. The toggle then caught a regression in the first version of the fix (FreezePhysics on did
not physics-freeze groups that were logic-frozen), which was fixed.

After the fixes (`FreezeLogic`), everything passes:

```
planet, gear on static: physics frozen 0, logic only 6, max speed after thaw 0.8 m/s (chassis 0.00), problems none
planet, free:           physics frozen 6, logic only 0, max speed after thaw 2.9 m/s (chassis 0.43), problems none
space, gear on static:  physics frozen 0, logic only 6, max speed after thaw 2.6 m/s (chassis 0.00), problems none
space, free:            physics frozen 6, logic only 0, max speed after thaw 2.6 m/s (chassis 0.19), problems none
planet, gear on ground: physics frozen 0, logic only 6, max speed after thaw 0.0 m/s (chassis 0.00), problems none
toggle: free rigs 13/13 physics-frozen again, held rigs 0
```

"max speed" is the fastest grid of the rig: moving piston tops and the hinge arm. The chassis of the free planet rig
settles on its suspension at 0.43 m/s right after the thaw.

## Problems found in the code and fixed

1. **A body could stay fixed forever.** Unfreeze converted bodies back only if FreezePhysics was still on and the
   group had no static grid or voxel lock *at thaw time*. If either changed while frozen, the grid stayed in
   `FrozenPhysicsGrids` with a fixed body while `IsStatic` was false. Now every grid the freezer made fixed is converted
   back. `DoUnfreezePhysics` removes the grid from the set itself, catches its own errors, and skips bodies that are
   not fixed.
2. **The physics decision was stale and made off the game thread.** `GroupContainsFixedGrid` ran in the freezer loop
   thread (reading Havok constraints) 5 s before the freeze. Now the decision is made on the game thread when the
   freeze happens (`CanFreezePhysics`), and it re-reads the physical group. If a grid joined during the delay, or part
   of the group is already frozen without physics, the group stays dynamic, so there is no dynamic grid chained to
   fixed bodies.
3. **A freeze could happen after the player came back.** The queue entry was dropped before the game-thread action
   ran, so the "still queued" check never saw a cancel. The entry is now dropped inside the game-thread action.
4. **`UpdateFreezePhysics`** physics-froze every grid of a group, awake ones too, and threw on a grid that was already
   removed. Now it only freezes groups whose grids are all frozen, skips missing grids, and only converts back what it
   froze.
5. Freeze and unfreeze errors were swallowed silently. They are now logged.

## Not covered

- A group that is held and changes while frozen (someone welds, grinds or locks a gear next to it) needs a player,
  who would thaw it first. Fix 1 covers the case where it happens anyway.
- With FreezePhysics off (logic-only freeze everywhere), a ship hovering on thrusters in gravity or a rover on a slope
  keeps its dynamic body without thruster or brake logic. This is outside physics freeze and was not tested here.
