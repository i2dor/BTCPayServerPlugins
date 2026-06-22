#nullable enable
namespace BTCPayServer.Plugins.AgentCheckout
{
    public class AgentCheckoutSettings
    {
        public bool Enabled { get; set; } = true;
        public bool AllowLightning { get; set; } = true;
        public bool AllowOnChain { get; set; } = true;
        public int RateLimitPerMinute { get; set; } = 30;
        public string? LlmsTxtAppend { get; set; }
    }
}
