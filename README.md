# PlayerVault

PlayerVault is a Unity SDK that keeps track of player resources (coins, lives and so on) and
claims rewards from a backend. It ships as `PlayerVault.unitypackage` and was built on Unity
`6000.6.2f1`. The runtime only references `UnityEngine`.

| Folder | Contents |
| --- | --- |
| `PlayerVault/` | The SDK source, its tests and the package exporter. |
| `CoinRush/` | A small mobile game that uses the package. |
| `docs/REFERENCE.md` | The API reference. |

## Integration

Import the package with **Assets → Import Package → Custom Package…**. It adds `Runtime/` (the
SDK), `Editor/SaveMenu.cs` (a **Tools → PlayerVault** menu for deleting saves) and
`Samples/MinimalExample.cs`. You don't need to edit the manifest or add anything to a scene.
Then open a vault and use it:

```csharp
var vault = await Vault.OpenAsync(new VaultConfig
{
    PlayerId  = "test-player",
    ApiUrl    = "https://httpbin.org/anything",
    Resources = { new ResourceDefinition("coins", initial: 100),
                  new ResourceDefinition("lives", initial: 3, max: 5) }
});

vault.GetBalance("coins");                  // 100
vault.Spend("coins", 1000);                 // Success = false, Failure = InsufficientBalance

var claim = await vault.ClaimAsync("level-10-first-completion", "coins", 100);
// claim.Status is Granted, AlreadyGranted, Pending or Failed
```

If you prefer the Inspector, add a `VaultBehaviour` component instead. It also passes the
vault's events to the main thread. `MinimalExample.cs` goes through every possible result.

## Example: CoinRush

CoinRush is a Roll-a-Ball game for phones. You roll a ball around an arena, pick up every coin
and avoid the hazards. The game has no save code of its own; everything it remembers is in the
vault.

| In the game | PlayerVault call |
| --- | --- |
| Startup | A `VaultBehaviour` in the scene declares `coins`, `lives` (max 3) and one `level-N-unlocked` resource per level (max 1). The game gets the vault through `WhenOpen` and uses `IsDeclared` to check that every level has its unlock resource. |
| Picking up a coin | `GrantLocal("coins", n)`. Coins the game spawns itself don't go through the API. |
| Hitting a hazard or falling off | `Spend("lives", 1)`. When it returns `InsufficientBalance`, the run is over. |
| Starting a run | `GrantLocal("lives", 3)`. The max of 3 clamps it, and `AmountApplied` tells the HUD whether lives were already full. |
| Clearing a level | `ClaimRoutine("level-N-first-clear", "coins", reward)`. The HUD shows `Granted` the first time, `AlreadyGranted` after that, and `Pending` when the phone is offline. |
| Retry button | Appears while `PendingClaims` has entries and calls `ResumePendingRoutine`. |
| Buying a level | `TransactRoutine(VaultTransaction.Purchase("coins", cost, "level-N-unlocked"))`. Because of the max of 1, a level can't be bought twice. |
| HUD | `CanSpend` decides which level prices are shown as affordable. `BalanceChanged` and `ClaimStateChanged` update the labels, so a claim that goes through after a relaunch shows up too. A debug panel lists `Balances`, `GetMax`, `PendingClaims` and `GetClaim` for each level. |
| Edited save file | The `WhenOpen` failure callback gets a `VaultTamperedException`, and the game shows a screen the player can't get past. |

## Decisions

- The game creates and owns the `Vault` object; there is no singleton. `VaultBehaviour` is a
  thin wrapper for people who want a component.
- The network, save storage, key storage, clock and logger are all interfaces with a default
  implementation, so a game can plug in its own backend. The claim logic itself can't be
  replaced, because the order of its steps is what stops a reward being paid twice.
- httpbin echoes whatever it gets and can't reject a duplicate, so the SDK checks duplicates
  itself. It works out which rewards were granted from the stored claim records, so there is
  no second list that could get out of sync. Claiming a granted reward again returns
  `AlreadyGranted` and sends nothing. Two calls for the same reward at once share one request.
- A claim is saved as `Pending` before its request is sent. When the response comes back, the
  new balance and the claim record are saved in a single file write. If the app is killed at
  any point, the claim is either still pending and gets retried, or already fully granted.
  I didn't use `PlayerPrefs` because it saves keys one by one.
- A timeout doesn't mean the server said no. Network errors, timeouts, 429 and 5xx are retried
  with backoff. If every attempt fails, the claim stays `Pending` until the next launch or a
  call to `ResumePendingAsync()`. A claim only becomes `Failed` on a 4xx or invalid input.
- A pending claim doesn't hold any balance, so the player can keep spending while it runs.
- Running out of coins returns a result. A bad config throws as soon as the vault is created.
- If a reward would go over a resource's max, the extra is dropped, and `AmountApplied` and
  `WasClamped` tell the game how much landed.
- Save files are signed with HMAC-SHA256 and carry a write counter, which catches both edited
  files and old copies put back to claim a reward again. The keys are kept in the iOS Keychain
  and the Android Keystore. It's on by default and controlled by `DetectTampering`.

## Testing

Run the tests from **Window → General → Test Runner**, or from the command line:

```bash
Unity -batchmode -nographics -projectPath PlayerVault \
      -executeMethod PlayerVault.Tests.BatchTestRunner.RunEditMode
```

There are 103 EditMode tests, all passing. They swap in fakes for the network, the disk and the
clock, which lets them cover insufficient balance, caps, repeated and simultaneous claims,
offline, timeouts, 4xx against 5xx, restarting in the middle of a claim, a crash while saving,
unreadable and edited saves, and spending while a claim is running. The PlayMode tests call
the real httpbin endpoint through `UnityWebRequest`, and one points at a host that doesn't exist.

In CoinRush, clearing a level, restarting and clearing it again gives `AlreadyGranted` and no
extra coins. On Android, changing the coin balance in the save file over `adb` gets caught, and
the game refuses to load that save.
