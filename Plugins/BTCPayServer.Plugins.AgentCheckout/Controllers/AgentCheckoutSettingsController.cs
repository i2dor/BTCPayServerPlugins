#nullable enable
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.AgentCheckout.Controllers
{
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    [Route("stores/{storeId}/plugins/agent-checkout")]
    public class AgentCheckoutSettingsController : Controller
    {
        private readonly AgentCheckoutService _service;

        public AgentCheckoutSettingsController(AgentCheckoutService service)
        {
            _service = service;
        }

        [HttpGet("")]
        public async Task<IActionResult> UpdateSettings(string storeId)
        {
            var settings = await _service.GetSettings(storeId);
            return View("AgentCheckout/UpdateSettings", settings);
        }

        [HttpPost("")]
        public async Task<IActionResult> UpdateSettings(string storeId, AgentCheckoutSettings settings)
        {
            await _service.UpdateSettings(storeId, settings);
            TempData[WellKnownTempData.SuccessMessage] = "Agent Checkout settings saved.";
            return RedirectToAction(nameof(UpdateSettings), new { storeId });
        }
    }
}
