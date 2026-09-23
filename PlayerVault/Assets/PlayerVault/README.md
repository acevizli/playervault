# PlayerVault

Player resources and idempotent reward claims for Unity. Built against Unity 6000.6.2f1; the
runtime assembly references nothing but `UnityEngine`.

## What you just imported

| Folder | |
| --- | --- |
| `Runtime/` | The SDK. This is all a game needs. |
| `Samples/MinimalExample.cs` | One file: configure, spend, claim, buy, handle every outcome. Drop it on an empty GameObject and press Play. |

## Thirty seconds

```csharp
var vault = await Vault.OpenAsync(new VaultConfig
{
    PlayerId = "player-42",
    ApiUrl   = "https://httpbin.org/anything",
    Resources =
    {
        new ResourceDefinition("coins", initial: 100),
        new ResourceDefinition("lives", initial: 3, max: 5)
    }
});

vault.GetBalance("coins");                                  // 100
vault.Spend("coins", 30);                                   // Success, Balance = 70
vault.Spend("coins", 1000);                                 // Failure = InsufficientBalance

await vault.ClaimAsync("level-10-first-clear", "coins", 250);   // granted once, ever
await vault.TransactAsync(VaultTransaction.Purchase("coins", 250, "level-3-unlocked"));
```

`VaultBehaviour` is an optional component that does the same from the Inspector, re-raises the
vault's events on Unity's main thread, offers coroutine forms, and flushes when the app is
backgrounded.

## The five things worth knowing

1. **A reward id is granted once, ever** — across restarts, concurrent callers and replays. The
   guard is derived from the stored claim records, so it cannot drift out of step with them.
2. **`Pending` is not a failure.** An offline or timed-out claim stays pending on disk and
   replays on the next launch, or whenever you call `ResumePendingAsync()`. Only a deterministic
   refusal is terminal.
3. **A capped reward is consumed, not deferred.** Granting 10 lives into a balance of 3 with a
   maximum of 5 applies 2 and discards the rest. Read `AmountApplied` and `WasClamped`.
4. **The `Async` forms are the durable ones.** They complete once the change is on disk and roll
   back in memory if it could not be written. `Spend` and `GrantLocal` schedule their write and
   return immediately — right for a coin pickup, wrong for a purchase.
5. **Events arrive on whichever thread finished the work.** Touching a `GameObject` from one will
   throw. Subscribe through `VaultBehaviour`, which re-raises them on the main thread.

## Full documentation

Integration guide, design decisions, persistence guarantees, threading and lifecycle contracts,
test notes and known limitations: the README in the repository root.
