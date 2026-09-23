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

Keys are strings everywhere, but a `ResourceDefinition` converts to its key, so a resource declared once in code can be passed to every call instead of repeating the string:

```csharp
static readonly ResourceDefinition Coins = new ResourceDefinition("coins", initial: 100);

config.Resources.Add(Coins);
vault.Spend(Coins, 30);
```

A key that is not declared is refused, and the first refusal for each key is logged with the list of declared keys, so a typo is loud even when the game ignores the result. `IsDeclared(key)` lets a game check at startup that the keys it uses in code match the ones set in the Inspector — CoinRush does exactly that. ScriptableObject definitions and generated key constants were considered and left out: both add an asset or build step to fix a problem the warning already surfaces on first use.

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

**Deleting a save.** `Vault.DeleteSaveAsync(playerId)` removes the save and any quarantined copies, and the next open starts that player over — a "reset progress" option, or a clean test player. It refuses while a vault is open on the save, because the open vault would write its state straight back: close, delete, reopen. In the editor, **Delete Save** on a `VaultBehaviour`'s context menu does the same for its player id (outside play mode), and **Tools → PlayerVault** can show the save folder or delete every save. A custom `IVaultStorage` implements `DeleteAsync` to support this; it has a default that throws `NotSupportedException`, so older storages still compile.

**Corruption.** A save that parses as nothing recognisable is moved aside with a `.corrupt-<timestamp>` suffix and the player starts fresh (`CorruptDataPolicy.Quarantine`, the default) — a player who cannot launch is worse off than one who lost progress, and the quarantined file keeps support recovery possible. `CorruptDataPolicy.Throw` hands the decision to the game instead. A save written by a newer SDK is always refused rather than truncated.

### Tamper detection

The save lives where a player can reach it: `adb pull` on Android, the Files app or a backup editor on iOS. Without a check, changing `"value":60` to `"value":99999` in a text editor is enough. `VaultConfig.DetectTampering` is on by default and has two jobs.

- **Edits.** Every save is written inside an envelope, `{ schemaVersion: 2, playerId, counter, mac, payload }`, where `mac` is an HMAC-SHA256 over the version, the player id, the counter and the document. The key is 32 random bytes per player that never enter the save file. It uses HMAC, not a public-key signature, because the same device writes the save and checks it, so no one needs to be able to check without being able to sign.
- **Putting an old copy back.** A copy of an older save is still correctly signed. Putting it back after a claim would remove the claim and let the reward be granted a second time. `counter` goes up with every write, and the key store keeps the number of the last save written. A save with a lower number than that was put back.

The file is written first and the counter recorded second, so a crash between the two leaves the file one ahead. That is accepted, and the key store catches up on the next open. An honest crash never looks like tampering. The envelope sits in the same file as the data, so the atomic rename still covers everything.

**Where the key lives** (`IVaultKeyStore`, which you can replace; the signing and checks cannot be):

| Platform | Key | Counter |
| --- | --- | --- |
| iOS | Keychain item, `AfterFirstUnlockThisDeviceOnly` (`Plugins/iOS/PlayerVaultKeychain.mm`) | Keychain item |
| Android | Private SharedPreferences, encrypted by a non-exportable AES key in the Android Keystore (`Plugins/Android/PlayerVaultKeyStore.java`) | Private SharedPreferences |
| Editor, Standalone | `FileKeyStore`: a plain file in `playervault/keys/` next to the saves | Same file |

The editor store protects nothing, but it runs the same checks. Edit a save by hand in the editor and the next open detects it, and that is how to test a game's handling. On Android the Java calls run on the main thread, because a thread-pool thread would have to be attached to the Java VM. The vault only calls the key store when opening and after each write, without waiting.

**When the check fails.** `TamperedDataPolicy.Block` (the default) fails the open with `VaultTamperedException` (`Reason`: `Unsigned`, `SignatureMismatch` or `RolledBack`) and leaves the save in place, so every launch fails the same way until `Vault.DeleteSaveAsync` clears the save and its key-store entries. `Quarantine` moves the save aside and starts fresh, like a corrupt save. A tampered save is kept separate from a corrupt one: a file that is not JSON at all still goes through `OnCorruptData`, so a failing disk is not reported as cheating. A save with no signature counts as tampered. This includes a plain v1 save, since nothing shipped before signing did.

**What it does not do.** It stops editing and sharing save files. It does not stop a player on a rooted or jailbroken device, who cannot extract the key but can make the running game sign anything. Nothing on the device can stop that. Only a server that owns the balances can, which this case rules out. The key never leaves the device, so a save restored onto another phone fails its check. Everything here is local and there are no accounts, so that is accepted.

### Threading and lifecycle

Every public member of `Vault` is safe to call from any thread. One lock guards the ledger; each durable operation mutates state and takes the snapshot it is about to write inside that same lock, so a snapshot can never catch a half-applied change. Waiting — the network, the disk — happens outside it, which is what keeps a pending claim from blocking a spend.

**Events are raised on whichever thread finished the work.** For anything downstream of a claim that is a thread-pool thread, and a handler touching a `GameObject` or a UI graphic will throw. Use `VaultBehaviour`, which re-raises them on the main thread. A handler that throws is logged and swallowed: a broken HUD must not be able to prevent a save.

The game owns the vault. `CloseAsync()` flushes, disposes, and completes once the last write is on disk — what you want on logout or scene teardown. `Dispose()` cancels in-flight work and returns at once; a write already running finishes, and anything not yet written is dropped.

**One vault per save.** Two vaults on one save would each write their own snapshot over the other's. Opening a save that already has an open vault throws `InvalidOperationException`. Opening one whose vault is *closing* waits for that vault's last write, then loads — so the next scene opening the save while the previous scene's vault is still writing sees everything it wrote. Two `JsonFileStorage` objects on the same folder count as the same save.

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

**Getting the vault.** `WhenOpen` runs a callback with the vault — immediately if it is already open, otherwise when it opens — so a consumer's `Start` never has to check `IsOpen` and fall back to `Opened`, which never fires for a late subscriber. `await behaviour.WhenOpenAsync()` is the awaitable form. When the open finishes on the main thread, `Vault` is set in the same frame; only background completions wait for the next `Update`.

```csharp
void Start()     => _hook = vaultBehaviour.WhenOpen(OnVaultOpened, OnVaultOpenFailed);
void OnDestroy() => _hook?.Dispose();
```

**Scenes.** Every component that opens a vault for the same player shares one vault, so each scene can carry its own `VaultBehaviour` without opening the save twice; the first one's settings win, and the vault is closed, with a final save, when the last of them is destroyed. Without anything else, a scene change therefore closes the vault and the next scene opens it again — safe, but claims in flight are cancelled (they stay pending and are retried). Tick **Persist Across Scenes** on a root object to keep the component and its vault alive through scene loads instead; returning to its scene does not create a second persistent copy. Code in a scene with no component of its own finds one with `VaultBehaviour.Find()`.

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

That entry point asks the Test Framework to run the suite synchronously and exits with its
status. Unity's own `-runTests` switch needs the batchmode editor to keep ticking after the
command returns, and on some machines — including the one this was developed on — it registers
the run and then idles indefinitely without executing a single test. Running synchronously
inside the one `-executeMethod` call sidesteps that; it is possible only because these are plain
`[Test]` methods with no coroutines. The tests run on the main thread, which the
`VaultBehaviour` tests need to create GameObjects. `BatchTestRunner` is development scaffolding
and is not part of the exported package.

**Last run: 103 passed, 0 failed, 0 skipped** (Unity 6000.6.2f1, macOS).

**EditMode** (no scene, no network — every dependency is a double):

- `ResourceTests` — balances, spending, refusals, clamping, undeclared resources.
- `ClaimTests` — grant-once, request shape, joined concurrent claims, clamped claims, rejection versus retry versus offline, non-JSON bodies, malformed input.
- `PersistenceTests` — restart survival, the guard outliving a session, write-ahead ordering, unreadable and newer-schema saves, storage failure at every boundary, player identity isolation.
- `ReliabilityTests` — reentrant claims from an event, conflicting retry payloads, a crash at the grant commit, throwing subscribers, disposal mid-write, config mutated after opening, spending while a claim completes on another thread, asynchronously completing dependencies.
- `LifecycleTests` — one vault per save, a reopen waiting for the previous vault's last write, deleting a save, components sharing a vault, `WhenOpen` before, after and across a failed open.
- `IntegrityTests` — an edited balance, counter, signature or player id; a save put back after a claim; an unsigned save; a deleted save; a crash between writing the save and recording its counter; the quarantine policy; an unavailable key store; detection turned off; an edited file on disk.
- `TransactionTests` — atomic purchases, refusals that move nothing, already-owned items, storage failure, step ordering, clamping.

**PlayMode** (`LiveEndpointTests`) — smoke tests against the real endpoint over `UnityWebRequest`, plus an unreachable-host case. Network-dependent by design; they are the only tests that can fail because of somebody else's outage.

### Known limitations

- **No authentication or server-side validation.** The sample endpoint echoes whatever it is sent, so "the backend accepted it" means "the request completed". A production economy needs signed requests, server-side reward validation and cross-device reconciliation, all of which sit behind `IVaultTransport`.
- **Tamper detection stops file edits, not rooted devices.** See [Tamper detection](#tamper-detection). A save moved to another device also fails its check.
- **Claim history grows without bound.** Every persistence operation serializes the whole document, and granted records accumulate. Fine at CoinRush's scale; a game with frequent claims and a long history should profile before shipping, and any compaction must preserve the duplicate-prevention information.
- **One vault per save, per process.** The guard is in memory, so it does not stop two processes — an editor and a standalone build, say — from sharing a save folder.
- **Backoff is per session.** There is no persistent retry schedule across launches — unsettled claims replay on the next open, or whenever the game calls `ResumePendingAsync()`.
