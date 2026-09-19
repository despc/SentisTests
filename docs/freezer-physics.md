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

## Stress: `freezer_stress`

128 copies of FREEZER_TEST_WITH_SUBGRIDS, 1696 grids:
- 64 copies on flat spots all over the planet. A spot is flat when the ground within 1.5 m of level at 12, 24 and
  36 m out in 8 directions. The copy reaches ~35 m from its chassis, and at a 12 m check the piston chains dug
  into the terrain.
- 64 copies at random points 150-300 km above the planet.

Each copy gets a random yaw (full random rotation in space) and its chassis stands on the ground as it did where it
was built. A copy is lifted in 0.5 m steps until no wheel or subgrid is in rock. Every fourth copy keeps its gear
locked to its static wall. If that gear fails to lock at spawn, the copy is counted as free. Cockpit handbrakes are
on: the rover as built has none and rolled 474 m downhill in an early run. The seed is fixed (20260919), so the
sites are the same every run.

There are 64 fake players with characters, one at each of 64 copies. Every 1-3 s, 1-4 of them teleport to copies
nobody is at (`FakeClients.MoveTo`). The freezer runs with a 500 m distance and FreezePhysics on for 600 s. Twice a
second, every copy is checked for:
- a held copy with frozen physics;
- a free copy with only part of it physics-frozen;
- a physics-frozen grid moving more than 5 cm;
- an awake grid left with a fixed body;
- lost tops or gears;
- lost blocks or closed grids;
- grids under the ground.

The first time a kind of problem shows up on a copy, the test logs it with the copy's freezer state. At the end the
freezer is switched off, everything thaws, and each copy is compared with its state before the run: damage, fixed
bodies, and a chassis still moving.

Result:

```
600 s, 754 player moves, freezes 778 (per copy min 3 median 6 max 10), thaws 718
frozen share avg 45% (after 30 s min 39% max 49%)
free planet copies moved median 1.7 m, max 3.1 m
tops attached 1536/1536, gears locked 32/32, blocks 10272/10272
copies with problems 0/128
```

The earlier runs failed, but only because of the test setup; the freezer was not involved:
- piston chains dug into slopes, and SentisGameplayImprovements `Assholes.Voxels` re-created those copies as stuck
  grids;
- wheels spawned in rock; one of them later sank, and its grid was destroyed;
- rovers rolled downhill without a handbrake;
- a held copy whose gear did not lock was expected to keep its physics dynamic.

Under this load (1696 grids, 64 clients) the server ran at about 30 fps, 33 ms per frame. The next thing to look at is
where that time goes.

## Profile: where the stress frame goes (`freezer_stress_profile`)

The same 128 copies, run in phases of 30 s. Each phase changes one thing: whether players move, whether motors run,
which copies are awake, and the freezer settings. For each phase the test logs the frame, the physics step and the
Havok worlds. dotTrace samples one phase of the run. Runs differ from each other by 2-4 ms on this machine, so
compare phases only within one run. One run, with the active-set fix below:

| phase | frame ms | physics ms | Havok worlds (with active bodies) | active bodies |
|---|---|---|---|---|
| players jump, half frozen | 21.5 | 12.9 | 91 (65) | 1127 |
| players still, half frozen | 19.0 | 11.5 | 91 (63) | 968 |
| motors off | 18.9 | 11.4 | 91 (63) | 951 |
| planet copies awake, space frozen | 20.9 | 13.3 | 91 (45) | 957 |
| space copies awake, planet frozen | 16.6 | 9.3 | 91 (65) | 972 |
| all frozen, 64 characters at one spot | 20.4 | 11.9 | 92 (19) | 25 |
| all frozen, 64 characters 3 km apart | 9.4 | 5.1 | 92 (20) | 43 |
| all awake, freezer off | 28.1 | 17.8 | 92 (90) | 1910 |
| half frozen, FreezePhysics off | 22.0 | 14.0 | 91 (80) | 1383 |
| all frozen, no players | 5.9 | 4.2 | 91 (17) | 23 |

What the time is:
- **The copies themselves.** With all of them awake the frame takes 28 ms; with all of them frozen and the players
  spread out, 9.4 ms. Freezing half of them saves about 9 ms. FreezePhysics on top of the logic freeze saves about
  2.5 ms (half frozen: 19.0 ms against 22.0 ms).
- **The 64 fake characters.** Spread out, they cost about 3.5 ms: `UpdateCharacters`, ground ray casts and contacts.
  Stacked at one spot, they cost about 11 ms, which is only an artefact of the test. They hover with their velocity
  forced every frame, so a real idle player probably costs less.
- **The Havok worlds.** Each group of copies far from the others is a world of its own, 85-92 worlds here.
  `StepWorldsParallel` steps every world every frame, even one where nothing is active. With everything frozen and
  no players this still takes 4.2 ms, roughly 45 us per world. Vanilla `EnableSelectivePhysicsUpdates`, which is off
  on this server, skips clusters that have no character and no entity replicated to a client.
- **The churn itself.** Players jumping from copy to copy costs about 2.5 ms (21.5 ms against 19.0 ms): freezes,
  thaws and streaming.

Found and fixed in `FreezeLogic`: `MyGridPhysics.ConvertToStatic` calls `HkWorld.RigidBodyActivated` on the now
fixed body, and Havok never deactivates a fixed body. So every physics-frozen grid stayed in the world's active set
for as long as it was frozen, and `MyPhysics.UpdateActiveRigidBodies` walked it every frame: `IterateBodies`, then
`OnMotionDynamic`, which the freezer patch skips. With everything frozen the set held 1292 bodies; now it holds 26.
The profile puts the saving at about 1 ms per frame per ~600 frozen grids, which is within the run-to-run noise of
the frame numbers. The body is taken out with the private `HkWorld.RigidBodyDeactivated`, which nothing else listens
to. `ConvertToDynamic` puts the body back when the grid thaws.

## EnableSelectivePhysicsUpdates

On this server the world setting was off. It is now on, in `SpaceEngineers-Dedicated.cfg` and in the save's
`Sandbox.sbc` and `Sandbox_config.sbc`. With it, `StepWorldsParallel` steps a Havok world (cluster) only if it holds a
character or something replicated to a client (`MyWorldObserver`). The same profile run with the setting on:

| phase | frame ms, off | frame ms, on | physics ms, off | physics ms, on |
|---|---|---|---|---|
| players jump, half frozen | 21.5 | 22.2 | 12.9 | 13.2 |
| players still, half frozen | 19.0 | 19.5 | 11.5 | 11.7 |
| all frozen, 64 characters 3 km apart | 9.4 | 9.8 | 5.1 | 5.4 |
| all awake, freezer off | 28.1 | 21.4 | 17.8 | 12.0 |
| all frozen, no players | 5.9 | 2.2 | 4.2 | 0.45 |

With players spread over the copies, the setting changes nothing: the worlds with players are stepped either way.
What it removes is the step of worlds nobody is near. Those worlds cost about 4 ms with everything frozen, and it
also takes the cost of awake grids far from players.

It broke the freezer, and `freezer_physics` caught it: the free copy in space went 15 m at 3.5 m/s on a thaw. A
group could live on its logic in a world that was not stepped:
- the player left and the world stopped at once, but the freeze came only after DelayBeforeFreezeSec;
- the player came back and the freezer thawed the group, but the world was not stepped until the grids had been
  streamed to the client.

Meanwhile the pistons and motors kept moving their constraint targets. When stepping resumed, the constraints
snapped to them. `FreezeLogic` now takes the set of stepped worlds on the game thread once per freezer pass
(`RefreshSteppedWorlds`, through the vanilla `MyPhysics.IsClusterActive`). A group thaws only while its world is
stepped, and it freezes at once, with no delay, when its world is not. With the setting off the set is null and
nothing changes.

The first `freezer_stress` run with the setting on caught two more problems. It tore a top off a copy with a player
next to it (planet-036, 485 s):
- A Havok world that a cluster rebuild had just made was not in the snapshot yet, and was taken as not stepped. The
  group froze at once, next to the player. Now a world the snapshot has not seen counts as stepped.
- The next pass thawed the group, but it picked the grids to thaw on the background thread while the freeze was
  still marking them one by one on the game thread. So only part of the group got its bodies back to dynamic. The
  rest stayed fixed, with constraints to moving grids, and a top broke off. Now the thaw picks its grids on the game
  thread, like the freeze does.

`freezer_physics` now watches each rig from its own thaw, because rigs thaw at different moments. A free copy in
space turns on its own hinge and pistons when nothing holds it. The origin of its grid then swings through about
1.5 m in 3 s, while the centre of mass moves at under 0.3 m/s. So for that copy the test checks the jump at the
moment of the thaw (0.01 m) and the chassis speed, not the distance.

## Config during the tests

Both scenarios change the SentisOptimisations config: FreezerEnabled, FreezePhysics and both freeze distances.
Torch writes the config file on every change, so a run cut short by a restart used to leave the test values in
`SentisOptimisations.cfg`. The next run then started with the freezer already on. `ConfigOverride` now writes the
original values to `SentisTests.config-restore.txt` next to the plugin config and deletes that file once they are
put back. If the file is still there, the originals are restored when the session loads and before the next test
starts. This was checked by killing the server in the middle of a run.

## Not covered

- A group that is held and changes while frozen (someone welds, grinds or locks a gear next to it) needs a player,
  who would thaw it first. Fix 1 covers the case where it happens anyway.
- With FreezePhysics off (logic-only freeze everywhere), a ship hovering on thrusters in gravity or a rover on a slope
  keeps its dynamic body without thruster or brake logic. This is outside physics freeze and was not tested here.
