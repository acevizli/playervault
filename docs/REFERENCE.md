# PlayerVault API Reference

Everything is in the `PlayerVault` namespace.

- [Opening a vault](#opening-a-vault): `VaultConfig`, `ResourceDefinition`, `Vault.OpenAsync`
- [Resources](#resources): reading, spending, granting
- [Transactions](#transactions): `VaultTransaction`
- [Reward claims](#reward-claims): `ClaimAsync`, `ClaimResult`, `ClaimRecord`
- [Events](#events)
- [Lifecycle and saves](#lifecycle-and-saves)
- [VaultBehaviour](#vaultbehaviour)
- [Extension points](#extension-points): transport, storage, key store, clock, logger

---

## Opening a vault

```csharp
var vault = await Vault.OpenAsync(new VaultConfig
{
    PlayerId  = "test-player",
    ApiUrl    = "https://httpbin.org/anything",
    Resources = { new ResourceDefinition("coins", initial: 100),
                  new ResourceDefinition("lives", initial: 3, max: 5) }
});
```

`Vault.OpenAsync(config, cancellationToken)` creates the vault, loads the save and starts retrying unfinished claims in the background. It does not wait for the network. `new Vault(config)` followed by `LoadAsync()` does the same in two steps.

An invalid config throws `ArgumentException` from `OpenAsync` or the constructor. Create the vault on Unity's main thread unless the config sets its own `Transport`, `Storage` and `KeyStore`: the defaults read `Application.persistentDataPath` and capture the main thread's context, so creating them elsewhere throws `InvalidOperationException`. `VaultBehaviour` handles this for you.

| Exception from opening | Meaning |
| --- | --- |
| `VaultStorageException` | A save exists but could not be read. Nothing was written; opening can be retried. |
| `VaultTamperedException` | The save was edited or an older copy was put back (`OnTampered = Block`). `Reason` is `Unsigned`, `SignatureMismatch` or `RolledBack`. |
| `InvalidOperationException` | A vault is already open on this save, the save is corrupt (an empty file counts) with `OnCorruptData = Throw`, it was written by a newer SDK, or it belongs to another player. |

### VaultConfig

| Property | Default | |
| --- | --- | --- |
| `PlayerId` | required | Identifies the player and keys the save. |
| `ApiUrl` | `https://httpbin.org/anything` | Where claims are POSTed. |
| `Resources` | empty | `ResourceDefinition` list. |
| `AllowUndeclaredResources` | `false` | Create unknown resources on first use instead of refusing them. |
| `ResumePendingOnOpen` | `true` | Retry pending claims in the background after opening. |
| `Retry` | `new RetryPolicy()` | See below. |
| `FlushMode` | `Immediate` | `Debounced` coalesces writes within `DebounceInterval` (1 s). |
| `OnCorruptData` | `Quarantine` | `Quarantine` moves an unreadable save aside and starts fresh; `Throw` fails the open. An empty or truncated file is unreadable; only a missing file is a new player. |
| `DetectTampering` | `true` | Sign saves and check them on open. |
| `OnTampered` | `Block` | `Block` fails the open; `Quarantine` moves the save aside and starts fresh. |
| `Transport`, `Storage`, `KeyStore`, `Clock`, `Logger` | Unity defaults | See [Extension points](#extension-points). |

### RetryPolicy

| Property | Default | |
| --- | --- | --- |
| `MaxAttempts` | `3` | Attempts per claim per session. |
| `BaseDelay` | 500 ms | First backoff delay, doubled each attempt. |
| `MaxDelay` | 10 s | Backoff ceiling. |
| `Jitter` | `0.25` | Random spread applied to each delay, 0 to 1. |
| `Timeout` | 15 s | Per request. |

### ResourceDefinition

`new ResourceDefinition(key, initial = 0, max = null)`. `Key`, `Initial` and `Max` are also settable properties. A definition converts implicitly to its key, so it can be passed anywhere a resource name is expected:

```csharp
static readonly ResourceDefinition Coins = new ResourceDefinition("coins", initial: 100);

config.Resources.Add(Coins);
vault.Spend(Coins, 30);
```

---

## Resources

| Member | Returns |
| --- | --- |
| `GetBalance(resource)` | `long`. |
| `GetMax(resource)` | `long?`, null when uncapped. |
| `Balances` | A copy of every balance. |
| `IsDeclared(resource)` | Whether the key was declared in the config. |
| `CanSpend(resource, amount)` | Whether `Spend` would succeed right now. |
| `Spend(resource, amount)` | `SpendResult`. Applies immediately; the save is written in the background. |
| `SpendAsync(resource, amount)` | `Task<SpendResult>`. Completes once the change is saved. |
| `GrantLocal(resource, amount)` | `GrantResult`. Adds without contacting the backend, clamped to the max. |
| `GrantAsync(resource, amount)` | `Task<GrantResult>`. Completes once the change is saved. |

Use the `Async` forms for anything the game acts on afterwards. If their write fails, the change is undone and the result is `StorageUnavailable`.

None of these throw at runtime. A refusal comes back in the result:

```csharp
var result = vault.Spend("coins", 50);
if (!result.Success)
    ShowNotice(result.Failure);   // InsufficientBalance, UnknownResource, ...
```

| Result | Members |
| --- | --- |
| `SpendResult` | `Success`, `Failure`, `Balance` |
| `GrantResult` | `Success`, `Failure`, `AmountApplied`, `Balance` |

`SpendFailure`: `None`, `UnknownResource`, `InvalidAmount`, `InsufficientBalance`, `AtMaximum` (only from `GrantExact`), `StorageUnavailable` (only from `Async` methods).

A pending claim never holds or reserves a balance, so spending works while claims are in flight.

---

## Transactions

Several changes applied and saved together, or not at all.

```csharp
var result = await vault.TransactAsync(VaultTransaction.Purchase("coins", 250, "level-3-unlocked"));
if (result.Success) ShowLevel(3);
else                ShowNotice(result.Failure);   // nothing was charged
```

| `VaultTransaction` | |
| --- | --- |
| `.Spend(resource, amount)` | Subtract. Fails the transaction if the balance is too low. |
| `.Grant(resource, amount)` | Add, clamped to the max. |
| `.GrantExact(resource, amount)` | Add. Fails the transaction with `AtMaximum` if the max would clamp any of it. |
| `VaultTransaction.Purchase(price resource, price, item resource, quantity = 1)` | `.Spend(price).GrantExact(item)`. |

Steps run in order against the running balance. `TransactionResult` has `Success`, `Failure`, `FailedResource`, `Changes` (a `ResourceChange` per step: `Resource`, `Applied`, `Balance`) and `AppliedTo(resource)`.

A resource with `max: 1` works as an owned/not-owned flag: `Purchase` refuses to sell it twice.

---

## Reward claims

```csharp
var result = await vault.ClaimAsync("level-10-first-completion", "coins", 100);

switch (result.Status)
{
    case ClaimStatus.Granted:        ShowReward(result.Record.AmountApplied); break;
    case ClaimStatus.AlreadyGranted: ShowAlreadyClaimed(); break;
    case ClaimStatus.Pending:        ShowWillRetry(); break;
    case ClaimStatus.Failed:         ShowError(result.Failure); break;
}
```

`ClaimAsync(rewardId, resource, amount, cancellationToken)` POSTs

```json
{ "player_id": "test-player", "reward_id": "level-10-first-completion", "resource": "coins", "amount": 100 }
```

to `ApiUrl` and applies the amount after a successful response. A reward id is granted at most once per player, across restarts. Calling again returns `AlreadyGranted` without sending a request, and concurrent calls for the same id share one request. A concurrent call with the same id but a different resource or amount gets `Conflict` instead.

| `ClaimStatus` | |
| --- | --- |
| `Granted` | Accepted and applied. |
| `AlreadyGranted` | Granted earlier. Nothing was sent or applied. |
| `Pending` | Not finished: offline, timed out or out of attempts. Retried on next open and by `ResumePendingAsync()`. |
| `Failed` | Refused for good (4xx, unparseable response, invalid input). |

`ClaimFailure` gives the reason: `None`, `Rejected`, `Network`, `Parse`, `Cancelled`, `Invalid`, `Storage`, `Conflict` (the id is already pending or in flight with a different resource or amount; retry with the original values).

### ClaimResult

`Status`, `Failure`, `Success` (`Status == Granted`) and `Record`. `Status` describes this call; `Record.Status` describes the stored claim, which is `Granted` when this call returned `AlreadyGranted`.

### ClaimRecord

| Member | |
| --- | --- |
| `RewardId`, `Resource` | As claimed. |
| `AmountRequested`, `AmountApplied` | Applied is lower when the max clamped the reward. |
| `WasClamped` | `Granted` and `AmountApplied < AmountRequested`. The overflow is discarded. |
| `Status`, `Failure` | Stored state. |
| `Attempts`, `IsAwaitingRetry` | Attempt count; pending with at least one attempt made. |
| `LastStatusCode`, `LastError` | Last HTTP status (-1 if none) and error text. |
| `CreatedAt`, `UpdatedAt` | UTC timestamps. |
| `Describe()` | One-line summary for a HUD or log, e.g. `granted 100 coins`. |

### Querying and retrying

| Member | |
| --- | --- |
| `GetClaim(rewardId)` | The stored record, or null if never claimed. |
| `PendingClaims` | Every pending record. |
| `ResumePendingAsync(cancellationToken)` | Retry every pending claim now. Safe to call repeatedly. Call it on reconnect, on resume from background, or from a retry button. |

---

## Events

| Event | Raised |
| --- | --- |
| `BalanceChanged(string resource, long balance)` | After a balance change is saved. |
| `ClaimStateChanged(ClaimRecord record)` | Whenever a claim changes state. |

`Vault` raises these on the thread that finished the work, which after a claim is a thread-pool thread. To update UI, subscribe on `VaultBehaviour` instead: it raises the same events on the main thread. Each handler is called separately, and one that throws is logged without stopping the others.

---

## Lifecycle and saves

| Member | |
| --- | --- |
| `PlayerId`, `ApiUrl` | Fixed when the vault is created. To switch player, close and open a new vault. |
| `FlushAsync()` | Write now; completes when saved. |
| `CloseAsync()` | Flush, dispose and complete once the last write is on disk. Use on logout or teardown. If the final write fails it throws `VaultStorageException` and the vault stays open with everything in memory: call `CloseAsync()` again to retry, or `Dispose()` to give up the unsaved changes. |
| `Dispose()` | Cancel in-flight work and return at once. Unwritten debounced changes are dropped. |
| `Vault.DeleteSaveAsync(playerId, storage = null, keyStore = null)` | Delete a player's save and its tamper-check data. Throws `InvalidOperationException` while a vault is open on it. |

Only one vault can be open per save. Opening a save whose previous vault is still closing waits for its last write.

Durable operations (`SpendAsync`, `GrantAsync`, `TransactAsync`, and the writes inside a claim) commit one at a time: each one's change and write finish, or are undone, before the next one takes its snapshot. An operation reported as failed therefore never reaches disk through a later write. Network waits are not part of a commit, so a pending claim never blocks a spend.

Saves are stored at `Application.persistentDataPath/playervault/`. In the editor, **Tools → PlayerVault** shows the folder or deletes every save.

---

## VaultBehaviour

An optional `MonoBehaviour` that configures and opens a vault from the Inspector.

```csharp
[SerializeField] VaultBehaviour vaultBehaviour;
IDisposable _hook;

void Start()     => _hook = vaultBehaviour.WhenOpen(OnVaultOpened, OnVaultOpenFailed);
void OnDestroy() => _hook?.Dispose();

void OnVaultOpened(Vault vault) => coinsLabel.text = vault.GetBalance("coins").ToString();
```

**Inspector:** player id, API URL, resources (key, initial, optional max), Open On Awake, Allow Undeclared Resources, Flush On Application Pause, Persist Across Scenes, retry settings, Resume Pending On Open, flush mode, corrupt-data and tamper policies, Detect Tampering.

| Member | |
| --- | --- |
| `Vault`, `IsOpen` | The open vault, or null. |
| `WhenOpen(onOpen, onFailed = null)` | Run `onOpen` now if open, otherwise when it opens. Dispose the returned handle to unsubscribe. |
| `WhenOpenAsync()` | Awaitable form of `WhenOpen`. |
| `Opened`, `OpenFailed`, `OpenError`, `HasFailedToOpen` | Open notifications and the last failure. Call `OpenAsync()` again to retry. A handler that throws is logged and does not stop the others. |
| `BalanceChanged`, `ClaimStateChanged` | The vault's events, raised on the main thread. |
| `PlayerId` | Settable before opening, e.g. after sign-in with Open On Awake off. |
| `OpenAsync()` / `OpenAsync(config)` | Open with Inspector settings, or with a config you adjusted. |
| `CreateConfig()` | A `VaultConfig` built from the Inspector, to modify before `OpenAsync(config)`. |
| `Attach(vault, takeOwnership = true)` | Use a vault you created yourself. |
| `VaultBehaviour.Find(playerId = null)` | Find an existing component, e.g. from a scene without its own. |
| `RunOnMainThread(action)` | Queue work onto the main thread. |
| `ClaimRoutine`, `SpendRoutine`, `TransactRoutine`, `ResumePendingRoutine`, `OpenRoutine` | Coroutine forms that take an `onComplete` callback. |

```csharp
// Custom setup, keeping the Inspector values
vaultBehaviour.PlayerId = await SignIn();
var config = vaultBehaviour.CreateConfig();
config.Transport = new MyBackendTransport();
await vaultBehaviour.OpenAsync(config);

// Coroutine style
StartCoroutine(vaultBehaviour.ClaimRoutine("daily-bonus", "coins", 50, r => ShowResult(r.Status)));
```

Components with the same player id share one vault. It is closed with a final save when the last of them is destroyed, unless **Persist Across Scenes** is ticked on a root object. The component also saves when the app goes to the background. In the editor, **Delete Save** in its context menu deletes its player's save.

---

## Extension points

Set any of these on `VaultConfig`. Each has a default.

### IVaultTransport

Default: `UnityWebRequestTransport`. Implement it to talk to your own backend.

```csharp
public sealed class MyBackendTransport : IVaultTransport
{
    public async Task<TransportResponse> PostAsync(string url, string jsonBody, CancellationToken ct)
    {
        // send the request, then map the result:
        //   2xx                      -> TransportResponse.Success(status, body)
        //   4xx                      -> TransportResponse.Rejected(status, body)
        //   5xx / 429                -> TransportResponse.Retryable(status, body)
        //   no response, timeout ... -> TransportResponse.Indeterminate(error)
    }
}
```

`Rejected` fails the claim. `Retryable` and `Indeterminate` are retried and leave the claim pending if attempts run out.

### IVaultStorage

Default: `JsonFileStorage(rootDirectory = null)`. Methods: `ReadAsync(key)` (null when there is no save), `WriteAsync(key, payload)`, `QuarantineAsync(key)`, and optional `DeleteAsync(key)`. **`WriteAsync` must be atomic**: fully replace the stored payload or change nothing.

### IVaultKeyStore

Stores the per-player signing key and save counter used by tamper detection. Defaults: iOS Keychain, Android Keystore, and `FileKeyStore` in the editor and standalone builds. Methods: `GetOrCreateKeyAsync(playerId)` (the same 32+ bytes every call), `ReadCounterAsync(playerId)`, `WriteCounterAsync(playerId, counter)` (must never lower the stored value), `DeleteAsync(playerId)`.

### IVaultClock and IVaultLogger

`IVaultClock`: `UtcNow`, `DelayAsync(delay, ct)`. Default `SystemClock`.
`IVaultLogger`: `Info`, `Warn`, `Error(message, exception)`. Default `UnityLogger`.
