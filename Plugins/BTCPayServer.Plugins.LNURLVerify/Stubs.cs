#nullable enable
using System.Net.Http;
using BTCPayServer.Lightning;
using Microsoft.Extensions.Logging;
using NBitcoin;

// Compile-only stubs for the LNURLVerify plugin types referenced by integration tests.
// The LNURLVerify plugin source lives on the ff-ref-i2dor branch; this branch carries only
// AgentCheckout. These stubs satisfy the compiler so the test project builds without
// excluding these test files and without changing the .csproj.

namespace BTCPayServer.Plugins.LNURLVerify
{
    public class LNURLVerifyConnectionStringHandler
    {
        public LNURLVerifyConnectionStringHandler(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory) { }

        public ILightningClient? Create(string connectionString, Network network, out string? error)
        {
            error = null;
            return null;
        }
    }
}
