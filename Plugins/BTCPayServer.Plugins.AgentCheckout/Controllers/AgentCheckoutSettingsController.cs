#nullable enable
using System;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NBitcoin;
using NBitcoin.Secp256k1;
using NNostr.Client;
using NNostr.Client.Protocols;

namespace BTCPayServer.Plugins.AgentCheckout.Controllers
{
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    [Route("plugins/{storeId}/agent-checkout")]
    [TypeFilter(typeof(AgentCheckoutExceptionFilter))]
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
            if (!string.IsNullOrEmpty(settings.ReceiptPrivateKeyHex))
            {
                try
                {
                    var key = NostrExtensions.ParseKey(settings.ReceiptPrivateKeyHex);
                    ViewBag.ReceiptNpub = AgentCheckoutService.GetReceiptNpub(key);
                }
                catch { }
            }
            return View(settings);
        }

        [HttpPost("")]
        public async Task<IActionResult> UpdateSettings(string storeId, AgentCheckoutSettings settings, string? importReceiptKey, string? keyAction)
        {
            if (!ModelState.IsValid)
            {
                TempData[WellKnownTempData.ErrorMessage] = "Invalid settings. Check the values and try again.";
                return RedirectToAction(nameof(UpdateSettings), new { storeId });
            }

            if (keyAction == "generate")
            {
                var newKey = ECPrivKey.Create(RandomUtils.GetBytes(32));
                settings.ReceiptPrivateKeyHex = newKey.ToHex();
            }
            else if (!string.IsNullOrWhiteSpace(importReceiptKey))
            {
                try
                {
                    ECPrivKey parsed;
                    var trimmed = importReceiptKey.Trim();
                    if (trimmed.StartsWith("nsec"))
                        parsed = trimmed.FromNIP19Nsec();
                    else
                        parsed = NostrExtensions.ParseKey(trimmed);
                    settings.ReceiptPrivateKeyHex = parsed.ToHex();
                }
                catch
                {
                    TempData[WellKnownTempData.ErrorMessage] = "Invalid key. Provide a valid nsec or 64-character hex private key.";
                    return RedirectToAction(nameof(UpdateSettings), new { storeId });
                }
            }
            else
            {
                var existing = await _service.GetSettings(storeId);
                settings.ReceiptPrivateKeyHex = existing.ReceiptPrivateKeyHex;
            }

            settings.RateLimitPerMinute = Math.Clamp(settings.RateLimitPerMinute, 1, 300);
            await _service.UpdateSettings(storeId, settings);
            TempData[WellKnownTempData.SuccessMessage] = "Agent Checkout settings saved.";
            return RedirectToAction(nameof(UpdateSettings), new { storeId });
        }
    }
}
