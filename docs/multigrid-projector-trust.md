# multigrid_projector_trust

Optional integrations: `MultigridProjector` and `SentisAnticheat` (reflection only;
SentisTests has no compile-time reference to either).

## Scope

This is a live-server acceptance scenario for the installed patch chain on the actual
`MyProjectorBase.BuildInternal(Vector3I,long,long,bool,long)` handler. It is **event-context
replay, not actual network transport**. `FakeClients.AsClient` installs `MyEventContext.Set`
with the fake player's endpoint, client state and `IsLocallyInvoked=false`; the scenario
checks the sender/non-local flag inside that scope and invokes the actual game handler.
It never invokes the anticheat prefix or a projection-build helper as a substitute.
Packet serialization, client UI, network/replicable dispatch and transport are not covered.

The fixture's piston assembly is **preconstructed**, deliberately, to isolate request
validation. It reuses a cloned embedded `MultigridProjection.xml`, adds an armor target to
preview 1 and removes one armor target from physical grid 1 and 2 before spawn. Only those
two absent cells may count as successful requests. This is not evidence that mechanical
subgrids were welded rather than copied: `multigrid_projector_weld` remains that separate,
unchanged acceptance test.

## Assertions

- Survival and no creative rights; fake player's character is its controlled builder.
- Fake player's ownership, research, PCU and component inventory are prepared before arming.
- MGP maps all three previews to the three distinct connected physical fixture grids.
- For each index 1 and 2, target exists only in its preview, is buildable, has no DLC
  dependency and is researched. Its correct physical cell is initially absent.
- Each index rejects seven requests: negative index; index equal to subgrid count;
  nonexistent cell; foreign owner; foreign character builder; builder 300 m away;
  builder near the root-preview interpretation of that cell but outside Reach of the
  actual subgrid target. The last case distinguishes root/subgrid coordinate spaces.
- Every rejection increments the installed anticheat `BuildTrustCheck.Refused` counter,
  preserves the immediate physical-world grid/cell census, preserves fixture integrity,
  character/block component inventories and construction stockpiles, including a delayed
  recheck three ticks later.
- Legitimate request is issued within 3 m of the actual subgrid cell with `requestInstant=false`.
  It must materialize the expected definition on exactly that physical grid and at the
  expected world location, spend real character components and not increment Refused.
- Cleanup is restricted to tracked fixture grids and the unique `ST-47351-mgp-trust-` prefix.

## Running and evidence

After all three DLLs are built/deployed together by the operator, queue through the normal
DebugBridge `/test` endpoint: `{"name":"multigrid_projector_trust"}`.
A successful scenario must explicitly report both paid scaffolds and all fourteen refusals.
A build or an offline source-contract pass is **not** a live acceptance verdict.

Local compile (Rider SDK .NET 10):

```text
dotnet build SentisTests/SentisTests.csproj -c Debug --nologo -p:LangVersion=12
python tools/test_multigrid_projector_trust_contract.py
python tools/test_multigrid_projector_contract.py
```

`LangVersion=12` avoids the existing unrelated C# 14 `field` keyword collision in
`RuntimePluginControls`; no source change to that helper is required.
The Python checks only enforce harness/source shape and fixture contracts.
No deployment, restart or live execution is performed by this implementation change.
