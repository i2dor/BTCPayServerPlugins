using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.AgentCheckout
{
    public class AgentCheckoutPlugin : BaseBTCPayServerPlugin
    {
        public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
        {
            new() { Identifier = nameof(BTCPayServer), Condition = ">=2.3.7" }
        };

        public override void Execute(IServiceCollection services)
        {
            base.Execute(services);
            services.AddSingleton<AgentCheckoutService>();
            services.AddUIExtension("store-integrations-nav", "AgentCheckout/AgentCheckoutNav");
        }
    }
}
