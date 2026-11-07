#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.HostedServices;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using NBitcoin;
using NBXplorer;
using NBXplorer.Models;

// Compile-only stubs for the Electrum plugin types referenced by integration tests.
// The Electrum plugin source lives on the ff-ref-i2dor branch; this branch carries only
// AgentCheckout. These stubs satisfy the compiler so the test project builds without
// excluding these test files and without changing the .csproj.

namespace BTCPayServer.Plugins.Electrum
{
    public class ElectrumPlugin { }

    public class ElectrumClient
    {
        public bool IsConnected => false;
        public string ConnectedServer => string.Empty;
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync() => Task.CompletedTask;
        public Task<(string Software, string Protocol)> ServerVersionAsync(
            string clientName, string protocolVersion, CancellationToken ct = default) =>
            Task.FromResult((string.Empty, string.Empty));
    }

    public class BackendCoordinator
    {
        public WalletBackend GetActiveBackend(string walletId) => default;
        public Task EvaluateWalletAsync(string walletId, CancellationToken ct = default) => Task.CompletedTask;
    }

    public enum WalletBackend { Nbx, Electrum }

    public class NbxHealth
    {
        public bool Reachable { get; set; }
        public event Action? OnRecovered;
        public void NotifyRecovered() => OnRecovered?.Invoke();
    }

    public class ElectrumStatusMonitor
    {
        public NBXplorerState State { get; set; }
        public static bool EffectiveSynced(bool nbxSynced, bool electrumConnected) =>
            nbxSynced || electrumConnected;
    }

    public class ElectrumBalance
    {
        public Money Total { get; set; } = Money.Zero;
    }

    public class ElectrumWalletTracker
    {
        public Task<ElectrumBalance> GetBalanceAsync(string walletId, CancellationToken ct = default) =>
            Task.FromResult(new ElectrumBalance());
        public Task<UTXOChanges> GetUTXOChangesAsync(string walletId, CancellationToken ct = default) =>
            Task.FromResult(new UTXOChanges());
        public Task<KeyPathInformation?> GetNextUnusedAddressAsync(
            string walletId, bool exclude, bool reserve, CancellationToken ct = default) =>
            Task.FromResult<KeyPathInformation?>(null);
    }

    public class RealNbxGateway
    {
        public ExplorerClient GetClient(string cryptoCode) => null!;
    }
}

namespace BTCPayServer.Plugins.Electrum.Services
{
    public class ElectrumSettings
    {
        public string Server { get; set; } = string.Empty;
        public bool UseTls { get; set; }
        public string CryptoCode { get; set; } = string.Empty;
    }
}

namespace BTCPayServer.Plugins.Electrum.Controllers
{
    public class UIElectrumController : Controller
    {
        public Task<IActionResult> Settings() =>
            Task.FromResult<IActionResult>(View(new Services.ElectrumSettings()));
        public override void OnActionExecuting(ActionExecutingContext context) { }
        public override void OnActionExecuted(ActionExecutedContext context) { }
    }
}
