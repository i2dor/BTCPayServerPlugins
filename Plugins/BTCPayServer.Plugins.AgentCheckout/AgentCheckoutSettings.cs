#nullable enable
using System.ComponentModel.DataAnnotations;

namespace BTCPayServer.Plugins.AgentCheckout
{
    public class AgentCheckoutSettings
    {
        public bool Enabled { get; set; } = false;
        public bool OptedIn { get; set; } = false;
        public bool AllowLightning { get; set; } = true;
        public bool AllowOnChain { get; set; } = true;
        [Range(1, 300)]
        public int RateLimitPerMinute { get; set; } = 30;
        [MaxLength(4000)]
        public string? LlmsTxtAppend { get; set; }
        public string? ReceiptPrivateKeyHex { get; set; }

        public static bool IsActive(AgentCheckoutSettings s) => s.Enabled && s.OptedIn;
    }
}
