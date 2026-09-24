using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace PlayerVault.Samples
{
    /// <summary>
    /// The SDK in one file: configure a vault, read a balance, spend, claim a one-time reward,
    /// make a purchase and log what the vault reports.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Add this to an empty GameObject and press Play. It creates a <see cref="Vault"/> directly
    /// instead of using <see cref="VaultBehaviour"/>; CoinRush shows the component approach.
    /// </para>
    /// <para>
    /// Everything here runs on the main thread except the vault's events, which arrive on the
    /// thread that finished the work. This sample only logs from them, which is safe on any
    /// thread. For UI updates, subscribe through <see cref="VaultBehaviour"/>, which raises
    /// them on the main thread.
    /// </para>
    /// </remarks>
    public sealed class MinimalExample : MonoBehaviour
    {
        [SerializeField] string playerId = "demo-player";

        /// <summary>The sample endpoint. It echoes back whatever is posted to it.</summary>
        [SerializeField] string apiUrl = "https://httpbin.org/anything";

        /// <summary>Set once the vault is open, and cleared by <see cref="OnDestroy"/>.</summary>
        Vault _vault;

        /// <summary>
        /// Unity calls this without awaiting it, and an exception escaping an async void method
        /// is only logged, so everything happens in <see cref="RunAsync"/> and is caught here.
        /// </summary>
        async void Start()
        {
            try
            {
                await RunAsync(destroyCancellationToken);
            }
            catch (OperationCanceledException)
            {
                // The GameObject was destroyed partway through. OnDestroy has closed the vault.
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
            }
        }

        async Task RunAsync(CancellationToken destroyed)
        {
            // 1. Configure. A player id and a list of resources are required. Transport, storage,
            //    clock, logger and retry policy all have defaults.
            var config = new VaultConfig
            {
                PlayerId = playerId,
                ApiUrl = apiUrl,
                Resources =
                {
                    new ResourceDefinition("coins", initial: 100),
                    new ResourceDefinition("lives", initial: 3, max: 5),
                    new ResourceDefinition("hat", initial: 0, max: 1)
                }
            };

            // 2. Open. This loads the save and starts retrying any claims left unfinished by an
            //    earlier session. Opening can fail in three ways, and each needs a different
            //    message, so catch them separately.
            Vault vault;
            try
            {
                vault = await Vault.OpenAsync(config, destroyed);
            }
            catch (VaultTamperedException exception)
            {
                // The save was edited or an older copy was put back. Retrying fails the same way;
                // only Vault.DeleteSaveAsync clears it.
                Debug.LogError($"This save was changed outside the game: {exception.Reason}");
                return;
            }
            catch (VaultStorageException exception)
            {
                // The save could not be read. It is untouched, so the game can offer to retry.
                Debug.LogError($"Could not load progress: {exception.Message}");
                return;
            }
            catch (InvalidOperationException exception)
            {
                // The save is unreadable (with OnCorruptData = Throw), was written by a newer
                // build, or is already open in another vault.
                Debug.LogError($"Could not open progress: {exception.Message}");
                return;
            }

            // Destroyed while opening: OnDestroy ran before there was a vault to close, so this
            // method owns it and must close it.
            if (destroyed.IsCancellationRequested)
            {
                await CloseAsync(vault);
                return;
            }

            _vault = vault;

            vault.BalanceChanged += (resource, balance) => Debug.Log($"[vault] {resource} = {balance}");
            vault.ClaimStateChanged += record => Debug.Log($"[vault] {record.RewardId}: {record.Describe()}");

            Debug.Log($"coins: {vault.GetBalance("coins")}, lives: {vault.GetBalance("lives")}/{vault.GetMax("lives")}");

            // Each step uses the local vault, not the field: OnDestroy clears the field and
            // closes the vault, and the check after each await stops before using a closed one.
            await SpendSomething(vault);
            destroyed.ThrowIfCancellationRequested();

            await ClaimAReward(vault, destroyed);
            destroyed.ThrowIfCancellationRequested();

            await BuyAHat(vault);
            destroyed.ThrowIfCancellationRequested();

            Debug.Log($"{vault.PendingClaims.Count} claim(s) still settling.");
        }

        /// <summary>
        /// Spending. A failed spend returns a result instead of throwing.
        /// </summary>
        static async Task SpendSomething(Vault vault)
        {
            // Spend changes the balance now and saves in the background. Use it for small
            // changes such as a coin pickup or a lost life.
            var quick = vault.Spend("lives", 1);
            Debug.Log(quick.Success ? $"spent a life, {quick.Balance} left" : $"refused: {quick.Failure}");

            // SpendAsync completes once the spend is saved, and undoes it if the save fails. Use
            // it when the game acts on the result.
            var durable = await vault.SpendAsync("coins", 10);
            if (!durable.Success) Debug.LogWarning($"refused: {durable.Failure}");
        }

        /// <summary>
        /// Claiming. The backend is asked, and the reward is applied once no matter how often
        /// this runs or when the process is killed.
        /// </summary>
        static async Task ClaimAReward(Vault vault, CancellationToken destroyed)
        {
            // The token stops the retries when the GameObject is destroyed. The claim stays saved
            // as pending and is retried on the next launch.
            var result = await vault.ClaimAsync("tutorial-complete", "coins", 250, destroyed);

            switch (result.Status)
            {
                case ClaimStatus.Granted:
                    // Use AmountApplied, not AmountRequested. The maximum may have clamped the
                    // reward, and the part over the maximum is lost.
                    Debug.Log($"+{result.Record.AmountApplied} coins" +
                              (result.Record.WasClamped ? " (the rest overflowed the cap)" : string.Empty));
                    break;

                case ClaimStatus.AlreadyGranted:
                    Debug.Log("already claimed; nothing was sent");
                    break;

                case ClaimStatus.Pending:
                    // Offline, timed out, or out of retries. The claim is saved and is retried on
                    // the next launch, or now with ResumePendingAsync.
                    Debug.Log($"still settling — {result.Record.Describe()}");
                    await vault.ResumePendingAsync(destroyed);
                    break;

                case ClaimStatus.Failed:
                    // Final. The backend refused the claim, and retrying would fail the same way.
                    Debug.LogWarning($"refused: {result.Failure}");
                    break;
            }
        }

        /// <summary>
        /// Buying. The charge and the item are saved in one write, so a crash cannot leave the
        /// player charged without the item.
        /// </summary>
        static async Task BuyAHat(Vault vault)
        {
            var result = await vault.TransactAsync(VaultTransaction.Purchase("coins", 50, "hat"));

            if (result.Success) Debug.Log($"bought a hat; {vault.GetBalance("coins")} coins left");
            else Debug.Log($"no hat: {result.Failure} on '{result.FailedResource}'");
        }

        /// <summary>
        /// The last callback a mobile game can rely on before the OS kills it. With the default
        /// Immediate flush mode there is rarely anything left to save, but with Debounced there
        /// may be.
        /// </summary>
        async void OnApplicationPause(bool paused)
        {
            if (!paused || _vault == null) return;

            try { await _vault.FlushAsync(); }
            catch (VaultStorageException exception) { Debug.LogError(exception.Message); }
        }

        /// <summary>
        /// The game owns the vault, so the game closes it. A vault still opening is closed by
        /// <see cref="RunAsync"/> once it opens.
        /// </summary>
        async void OnDestroy()
        {
            if (_vault == null) return;

            var closing = _vault;
            _vault = null;
            await CloseAsync(closing);
        }

        /// <summary>
        /// Flushes, then disposes. A failed final write leaves the vault open so it can be
        /// retried; this object is going away, so it gives up and disposes instead.
        /// </summary>
        static async Task CloseAsync(Vault vault)
        {
            try
            {
                await vault.CloseAsync();
            }
            catch (VaultStorageException exception)
            {
                Debug.LogError($"Progress since the last save was lost: {exception.Message}");
                vault.Dispose();
            }
        }
    }
}
