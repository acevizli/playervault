using System;
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

        Vault _vault;

        async void Start()
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
            //    earlier session. An unreadable save throws instead of starting a new player, so
            //    the game can show an error and offer to retry.
            try
            {
                _vault = await Vault.OpenAsync(config);
            }
            catch (VaultStorageException exception)
            {
                Debug.LogError($"Could not load progress: {exception.Message}");
                return;
            }

            _vault.BalanceChanged += (resource, balance) => Debug.Log($"[vault] {resource} = {balance}");
            _vault.ClaimStateChanged += record => Debug.Log($"[vault] {record.RewardId}: {record.Describe()}");

            Debug.Log($"coins: {_vault.GetBalance("coins")}, lives: {_vault.GetBalance("lives")}/{_vault.GetMax("lives")}");

            await SpendSomething();
            await ClaimAReward();
            await BuyAHat();

            Debug.Log($"{_vault.PendingClaims.Count} claim(s) still settling.");
        }

        /// <summary>
        /// Spending. A failed spend returns a result instead of throwing.
        /// </summary>
        async System.Threading.Tasks.Task SpendSomething()
        {
            // Spend changes the balance now and saves in the background. Use it for small
            // changes such as a coin pickup or a lost life.
            var quick = _vault.Spend("lives", 1);
            Debug.Log(quick.Success ? $"spent a life, {quick.Balance} left" : $"refused: {quick.Failure}");

            // SpendAsync completes once the spend is saved, and undoes it if the save fails. Use
            // it when the game acts on the result.
            var durable = await _vault.SpendAsync("coins", 10);
            if (!durable.Success) Debug.LogWarning($"refused: {durable.Failure}");
        }

        /// <summary>
        /// Claiming. The backend is asked, and the reward is applied once no matter how often
        /// this runs or when the process is killed.
        /// </summary>
        async System.Threading.Tasks.Task ClaimAReward()
        {
            var result = await _vault.ClaimAsync("tutorial-complete", "coins", 250);

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
                    await _vault.ResumePendingAsync();
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
        async System.Threading.Tasks.Task BuyAHat()
        {
            var result = await _vault.TransactAsync(VaultTransaction.Purchase("coins", 50, "hat"));

            if (result.Success) Debug.Log($"bought a hat; {_vault.GetBalance("coins")} coins left");
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

        /// <summary>The game owns the vault, so the game closes it.</summary>
        async void OnDestroy()
        {
            if (_vault == null) return;

            var closing = _vault;
            _vault = null;
            await closing.CloseAsync();   // flushes, then disposes
        }
    }
}
