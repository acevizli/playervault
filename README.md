# PlayerVault

A reusable Unity SDK for player resources and idempotent reward claims, plus `CoinRush` — a mobile game that integrates it.

| Project | What it is |
| --- | --- |
| `PlayerVault/` | The SDK. Exports as `PlayerVault.unitypackage`. |
| `CoinRush/` | Mobile Roll-a-Ball that imports the package and demonstrates it. |

Built and tested against Unity `6000.6.2f1` with URP. The runtime assembly references nothing but `UnityEngine`, so it drops into any render pipeline. Targets iOS and Android; the editor test suite runs on macOS and Windows.

Work is tracked on the [wayfinder map](https://github.com/acevizli/playervault/issues/1), where every decision below is argued out in full on its own ticket.

---

## Install

1. In the consuming project, **Assets → Import Package → Custom Package…** and pick `PlayerVault.unitypackage`.
2. Import everything. You get `Assets/PlayerVault/Runtime` (the SDK), `Assets/PlayerVault/Samples` (one file) and a short readme.
3. That is the whole installation. There is no manifest entry, no service to register, no scene object you are required to add.

The package deliberately does not ship the tests or the editor exporter — they are development artifacts of this repo, not part of what a game consumes.

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

vault.GetBalance("coins");          // 100
vault.Spend("coins", 30);           // SpendResult { Success = true, Balance = 70 }
vault.Spend("coins", 1000);         // SpendResult { Success = false, Failure = InsufficientBalance }

var result = await vault.ClaimAsync("level-10-first-clear", "coins", 250);
// Granted once, ever. Claim it again and you get AlreadyGranted with no request sent.
```

`Assets/PlayerVault/Samples/MinimalExample.cs` is the same thing as a runnable component, with the error paths filled in. `CoinRush` is the full integration.

---

## Concepts

### Resources and identity

A resource is a key, a starting balance and an optional ceiling. Declaring them up front is the default because it catches the typo that would otherwise create a `coin` balance next to your `coins` one; set `AllowUndeclaredResources = true` if you would rather they spring into existence.

`PlayerId` identifies the player **and** keys the save, so changing it switches ledger. It is snapshotted when the vault is constructed: editing the config afterwards does not move an open vault to a different player. To switch player, dispose the vault and open another.

A resource capped at `1` is how this SDK spells a boolean — CoinRush stores "owns level 3" that way, and the vault refuses the second grant itself.

### Reading and spending

| Call | Returns when | Use it for |
| --- | --- | --- |
| `Spend` / `GrantLocal` | immediately; the write is scheduled | incidental income and costs — a coin picked up, a life lost |
| `SpendAsync` / `GrantAsync` | once the change is on disk | anything the game acts on afterwards |
| `TransactAsync` | once every step is on disk, together | purchases |

Spending never throws. A refusal is a `SpendResult` carrying a `SpendFailure` — `InsufficientBalance`, `UnknownResource`, `InvalidAmount`, `AtMaximum`, `StorageUnavailable`. Bad *configuration* throws, at construction; bad *runtime input* returns a result. A player running out of coins is not an exceptional condition.

Pending claims never reserve or freeze a balance, so spending is always available regardless of what is in flight.

### Transactions

Charging and delivering as two calls is two commits, and a process killed in between leaves a player who paid and owns nothing. One call, one commit:

```csharp
var result = await vault.TransactAsync(VaultTransaction.Purchase("coins", 250, "level-3-unlocked"));
if (result.Success) ShowLevel(3);
else                ShowNotice(result.Failure);   // nothing was charged
```

`Purchase` is shorthand for `.Spend(price).GrantExact(item)`. `GrantExact` refuses the transaction with `AtMaximum` when the ceiling would clamp any of the item away — which is exactly what happens when a player already owns it — so an undeliverable purchase never takes the money. Plain `.Grant()` clamps instead and reports how much landed.

Steps are evaluated in order against the running balance. If any step is refused, nothing moves.

### Claiming rewards

`ClaimAsync(rewardId, resource, amount)` POSTs

```json
{ "player_id": "...", "reward_id": "...", "resource": "coins", "amount": 250 }
```

and applies the resource once the backend accepts.

| `ClaimResult.Status` | Meaning |
| --- | --- |
| `Granted` | Accepted and applied. |
| `AlreadyGranted` | This reward id was granted before. **No request was sent.** |
| `Pending` | Not finished: offline, timed out, or the retry budget ran out. Replayed on the next launch. |
| `Failed` | Terminally refused. Only deterministic outcomes land here. |

`Status` describes *this attempt*; `Record.Status` describes the stored claim. They differ for `AlreadyGranted`, which wraps the original — still `Granted` — record. A listener that reads only the record announces a fresh reward on every replay, which is the double-grant the SDK exists to prevent, faked on top of an SDK that refused it.

`ClaimFailure` says why: `Rejected` (4xx), `Network`, `Parse`, `Cancelled`, `Invalid`, `Storage`, `Conflict`. `Record.LastStatusCode`, `Record.LastError` and `Record.Describe()` carry the diagnostic detail — `"pending after 3 attempt(s) (Network) — HTTP 429: Too Many Requests"` rather than a bare `Network`.

**Granted once, ever.** The guard survives restarts because it is derived from the stored claim records rather than kept beside them, so it cannot drift. Concurrent callers for one reward id join a single request; the join is published before any work starts, so even a subscriber that re-enters `ClaimAsync` from a state-change event joins rather than races. The guard is checked again at grant time, under the same lock that applies the balance.

**Capped rewards are consumed, not deferred.** A reward of 10 lives against a balance of 3 with a maximum of 5 grants 2 and marks the claim `Granted`. `Record.AmountApplied` is 2, `AmountRequested` is 10 and `WasClamped` is true. The overflow is discarded — the backend already accepted the reward, and refusing it locally would strand it forever. Design your ceilings knowing that a player at cap loses the difference.

**A pending reward cannot be retried with a different payload.** Claiming `level-10` for 2 lives while it is pending for 250 coins returns `Conflict` and leaves the stored claim untouched. Retry it with its original resource and amount.

### Retries and resume

Per session: `MaxAttempts` (default 3) with exponential backoff from `BaseDelay`, capped at `MaxDelay`, spread by `Jitter` so a crowd of clients does not retry in lockstep after an outage.

Only a deterministic refusal is terminal. 429 and 5xx retry; connection errors and timeouts retry. Exhausting the budget leaves the claim **`Pending`, not `Failed`**, so an entirely offline session replays on the next launch. `ResumePendingAsync()` retries everything unsettled right now — wire it to a connectivity change, a resume-from-background, or a button, as CoinRush does.

A client-side timeout does not cancel server-side work, so a timed-out claim's true outcome is unknown. The SDK treats unknown as unfinished and replays it; the local guard is what keeps the replay from paying twice.

### Persistence

One JSON document per player holding balances, claim records and — implicitly — the granted-reward guard, so a single atomic write commits all three together. `JsonFileStorage` writes to a temporary file, fsyncs, then renames over the target. The fsync is not optional: without it the rename can reach disk while the bytes it points at have not.

Location: `Application.persistentDataPath/playervault/<readable-prefix>-<digest>.json`. The digest is the first 8 bytes of SHA-256 of the player id, because sanitizing punctuation into underscores maps `a/b` and `a_b` — and every pair of email addresses differing only in punctuation — onto one file. The document also records the player id, and loading one that belongs to somebody else is refused.

**Guarantees.** The `Async` forms complete only once the change is on disk. If the write fails they roll the change back in memory and report `StorageUnavailable`, so what the game sees never gets ahead of what a restart would see. A claim whose grant cannot be written rolls back to `Pending` and replays later rather than reporting a reward no restart remembers. A claim whose write-ahead record cannot be written never sends its request at all.

The synchronous `Spend`/`GrantLocal` schedule their write and report failures through the logger — that is the trade for not awaiting, and why anything the game acts on should use the durable form.

**Reading.** "There is no save" and "the save could not be read" are different things. The first starts a new player; the second throws `VaultStorageException` and leaves the open failed, because treating a temporarily unreadable file as a new player invites the first write of the session to overwrite it.

**Corruption.** A save that parses as nothing recognisable is moved aside with a `.corrupt-<timestamp>` suffix and the player starts fresh (`CorruptDataPolicy.Quarantine`, the default) — a player who cannot launch is worse off than one who lost progress, and the quarantined file keeps support recovery possible. `CorruptDataPolicy.Throw` hands the decision to the game instead. A save written by a newer SDK is always refused rather than truncated.

### Threading and lifecycle

Every public member of `Vault` is safe to call from any thread. One lock guards the ledger; each durable operation mutates state and takes the snapshot it is about to write inside that same lock, so a snapshot can never catch a half-applied change. Waiting — the network, the disk — happens outside it, which is what keeps a pending claim from blocking a spend.

**Events are raised on whichever thread finished the work.** For anything downstream of a claim that is a thread-pool thread, and a handler touching a `GameObject` or a UI graphic will throw. Use `VaultBehaviour`, which re-raises them on the main thread. A handler that throws is logged and swallowed: a broken HUD must not be able to prevent a save.

The game owns the vault. `Dispose()` cancels in-flight work but does not flush; `CloseAsync()` flushes and then disposes, which is what you want on logout or scene teardown. Two vaults over one storage file would race each other's writes, so keep one per player id.

### VaultBehaviour

The optional component. It configures a vault from the Inspector, pumps events onto the main thread, offers coroutine forms for teams not using async, and flushes when the app is backgrounded.

It is not a dead end for a game with real requirements:

```csharp
// Authenticated player, custom backend, Inspector settings kept.
behaviour.PlayerId = await SignIn();          // before opening
await behaviour.OpenAsync();

var config = behaviour.CreateConfig();        // …or take the config and adjust it
config.Transport = new MyBackendTransport();
config.Storage   = new MyCloudSaveStorage();
await behaviour.OpenAsync(config);

behaviour.Attach(myOwnVault);                 // …or hand over a vault you built yourself
```

Opening can fail. `OpenFailed` is raised on the main thread, `OpenError` holds the exception, the failure is retryable by calling `OpenAsync()` again, and the coroutines refuse to run rather than dereferencing a vault that never opened. A component destroyed mid-open disposes the vault that finishes opening afterwards instead of orphaning it.

---

## Design decisions

**A game-owned object, not a singleton.** `Vault` is a plain C# class. A game can hold one per player; a test can create one per test with no scene. `VaultBehaviour` is a wrapper for teams who want a component, and nothing in the SDK needs it.

**Small interfaces at every seam.** `IVaultTransport` is one method and no Unity types — a fake is about ten lines, which is what makes the whole suite runnable without a network. `IVaultStorage`, `IVaultClock` and `IVaultLogger` are the same shape. Substituting a real transport is how a game points the SDK at its own backend.

**Sealed state machine.** Every seam worth replacing is an interface on the config. The claim state machine is not one of them, because its ordering is the only thing standing between the SDK and a reward granted twice.

**Write-ahead, then one atomic commit.** The pending record is durable *before* the request goes out — if the process dies mid-request the claim is replayable; before that write there is no evidence it happened. Balance, record and guard then reach disk in a single write. Any other ordering either loses a reward or grants it twice.

**Results for expected failures, exceptions for programming errors and durability.** An insufficient balance returns. A malformed config throws at construction, on the first frame. A write that failed throws or returns `StorageUnavailable`, because silently logging it lets a game hand over goods that were never saved.

**One document, not key-by-key storage.** This is why the SDK does not use `PlayerPrefs`: it writes key by key and offers no way to commit several values together, which is the whole guarantee.

**`JsonUtility`, not Newtonsoft.** A package whose selling point is dropping into any project should not make every consumer inherit a serializer dependency. The cost is public fields and key/value lists instead of dictionaries, which is a fair trade.

**Requested versus applied amounts.** Surfacing both, plus `WasClamped`, means a designer can see that a capped reward silently discarded the overflow instead of discovering it from a player complaint.

---

## Tests

**Window → General → Test Runner** in the editor, or from the command line:

```bash
Unity -batchmode -nographics -projectPath PlayerVault \
      -executeMethod PlayerVault.Tests.BatchTestRunner.RunEditMode
```

That entry point drives NUnit synchronously and exits with the suite's status. Unity's own
`-runTests` switch needs the batchmode editor to keep ticking after the command returns, and on
some machines — including the one this was developed on — it registers the run and then idles
indefinitely without executing a single test. Running NUnit inside the one `-executeMethod` call
sidesteps that; it is possible only because these are plain `[Test]` methods with no coroutines
and no scene. `BatchTestRunner` is development scaffolding and is not part of the exported package.

**Last run: 72 passed, 0 failed, 0 skipped** (Unity 6000.6.2f1, macOS).

**EditMode** (no scene, no network — every dependency is a double):

- `ResourceTests` — balances, spending, refusals, clamping, undeclared resources.
- `ClaimTests` — grant-once, request shape, joined concurrent claims, clamped claims, rejection versus retry versus offline, non-JSON bodies, malformed input.
- `PersistenceTests` — restart survival, the guard outliving a session, write-ahead ordering, unreadable and newer-schema saves, storage failure at every boundary, player identity isolation.
- `ReliabilityTests` — reentrant claims from an event, conflicting retry payloads, a crash at the grant commit, throwing subscribers, disposal mid-write, config mutated after opening, spending while a claim completes on another thread, asynchronously completing dependencies.
- `TransactionTests` — atomic purchases, refusals that move nothing, already-owned items, storage failure, step ordering, clamping.

**PlayMode** (`LiveEndpointTests`) — smoke tests against the real endpoint over `UnityWebRequest`, plus an unreachable-host case. Network-dependent by design; they are the only tests that can fail because of somebody else's outage.

### Known limitations

- **No authentication or server-side validation.** The sample endpoint echoes whatever it is sent, so "the backend accepted it" means "the request completed". A production economy needs signed requests, server-side reward validation and cross-device reconciliation, all of which sit behind `IVaultTransport`.
- **Claim history grows without bound.** Every persistence operation serializes the whole document, and granted records accumulate. Fine at CoinRush's scale; a game with frequent claims and a long history should profile before shipping, and any compaction must preserve the duplicate-prevention information.
- **One vault per storage key.** Nothing stops a game from constructing two over the same file; they will race each other's writes.
- **Backoff is per session.** There is no persistent retry schedule across launches — unsettled claims replay on the next open, or whenever the game calls `ResumePendingAsync()`.
