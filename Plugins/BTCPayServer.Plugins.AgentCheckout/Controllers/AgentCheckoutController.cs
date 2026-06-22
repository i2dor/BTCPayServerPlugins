#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client.Models;
using BTCPayServer.Payments;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;

namespace BTCPayServer.Plugins.AgentCheckout.Controllers
{
    [ApiController]
    [AllowAnonymous]
    [IgnoreAntiforgeryToken]
    [EnableCors(CorsPolicies.All)]
    public class AgentCheckoutController : ControllerBase
    {
        private readonly InvoiceRepository _invoiceRepository;
        private readonly Dictionary<PaymentMethodId, IPaymentLinkExtension> _paymentLinkExtensions;
        private readonly AgentCheckoutService _agentCheckoutService;

        public AgentCheckoutController(
            InvoiceRepository invoiceRepository,
            Dictionary<PaymentMethodId, IPaymentLinkExtension> paymentLinkExtensions,
            AgentCheckoutService agentCheckoutService)
        {
            _invoiceRepository = invoiceRepository;
            _paymentLinkExtensions = paymentLinkExtensions;
            _agentCheckoutService = agentCheckoutService;
        }

        [HttpGet("/llms.txt")]
        public IActionResult GetLlmsTxt()
        {
            var host = $"{Request.Scheme}://{Request.Host}";
            var content = $@"# BTCPay Server Agent Checkout

> BTCPay Server lets LLM agents pay Bitcoin Lightning or on-chain invoices without authentication. Extract the invoiceId, request a quote, pay the returned URI, and poll for settlement.

Use these instructions only for invoices the user explicitly asked you to pay. Do not submit a payment without user intent and access to a wallet controlled by the user or agent.

Extract the `invoiceId` from a checkout URL like `{host}/i/{{invoiceId}}`.

Create a quote before paying:

```http
POST {host}/api/v1/agent/checkout/create-quote
Content-Type: application/json

{{
  ""invoiceId"": ""..."",
  ""transactionMethod"": ""LIGHTNING""
}}
```

`transactionMethod` is optional. When provided, must be `LIGHTNING` or `BITCOIN`. When omitted, LIGHTNING is tried first, then BITCOIN.

Response format:

```json
{{
  ""method"": ""LIGHTNING"",
  ""paymentUrl"": ""lightning:lnbc..."",
  ""btcAmount"": 1400
}}
```

`paymentUrl` is the URI to pass to a compatible wallet. `btcAmount` is in satoshis.

After paying, check the invoice status:

```http
POST {host}/api/v1/agent/checkout/order-status
Content-Type: application/json

{{
  ""invoiceId"": ""...""
}}
```

Status response:

```json
{{
  ""status"": ""PROCESSING"",
  ""paidAt"": null
}}
```

`status` values: PENDING (not yet paid), PROCESSING (payment seen, confirming), SETTLED (paid), EXPIRED, INVALID.
If PENDING or PROCESSING, wait and check again.

## Resources

- [Create Quote]({host}/api/v1/agent/checkout/create-quote): Request a Lightning or Bitcoin quote for an existing invoice.
- [Order Status]({host}/api/v1/agent/checkout/order-status): Check whether an invoice is paid, processing, or pending.
";
            return Content(content, "text/plain; charset=utf-8");
        }

        [HttpPost("/api/v1/agent/checkout/create-quote")]
        public async Task<IActionResult> CreateQuote([FromBody] CreateQuoteRequest? request)
        {
            if (string.IsNullOrEmpty(request?.InvoiceId))
                return BadRequest(new ErrorResponse("invoice-id-required", "invoiceId is required."));

            var invoice = await _invoiceRepository.GetInvoice(request.InvoiceId);
            if (invoice is null)
                return NotFound(new ErrorResponse("invoice-not-found", "The invoice was not found."));

            var settings = await _agentCheckoutService.GetSettings(invoice.StoreId);
            if (!settings.Enabled)
                return StatusCode(503, new ErrorResponse("agent-checkout-disabled", "Agent Checkout is not enabled for this store."));

            if (invoice.Status == InvoiceStatus.Expired || invoice.Status == InvoiceStatus.Invalid)
                return BadRequest(new ErrorResponse("invoice-expired", "The invoice is expired or invalid."));

            if (invoice.Status == InvoiceStatus.Settled)
                return BadRequest(new ErrorResponse("invoice-already-paid", "The invoice has already been paid."));

            var prompts = invoice.GetPaymentPrompts();

            var lnPmId = PaymentTypes.LN.GetPaymentMethodId("BTC");
            var btcPmId = PaymentTypes.CHAIN.GetPaymentMethodId("BTC");

            PaymentPrompt? selectedPrompt = null;
            string method;

            var requestedMethod = request.TransactionMethod?.ToUpperInvariant();
            if (requestedMethod == "BITCOIN")
            {
                if (!settings.AllowOnChain)
                    return BadRequest(new ErrorResponse("payment-method-disabled", "On-chain Bitcoin payments are not allowed by this store."));
                prompts.TryGetValue(btcPmId, out selectedPrompt);
                method = "BITCOIN";
            }
            else if (requestedMethod == "LIGHTNING")
            {
                if (!settings.AllowLightning)
                    return BadRequest(new ErrorResponse("payment-method-disabled", "Lightning payments are not allowed by this store."));
                prompts.TryGetValue(lnPmId, out selectedPrompt);
                method = "LIGHTNING";
            }
            else
            {
                // auto: prefer Lightning, fall back to on-chain
                if (settings.AllowLightning && prompts.TryGetValue(lnPmId, out selectedPrompt) && selectedPrompt?.Activated == true)
                    method = "LIGHTNING";
                else if (settings.AllowOnChain && prompts.TryGetValue(btcPmId, out selectedPrompt) && selectedPrompt?.Activated == true)
                    method = "BITCOIN";
                else
                    return BadRequest(new ErrorResponse("no-payment-method", "No activated payment method is available or permitted for this invoice."));
            }

            if (selectedPrompt is null || !selectedPrompt.Activated)
                return BadRequest(new ErrorResponse("payment-method-inactive", $"The {method} payment method is not activated for this invoice."));

            if (!_paymentLinkExtensions.TryGetValue(selectedPrompt.PaymentMethodId, out var linkExtension))
                return BadRequest(new ErrorResponse("no-payment-link-extension", "Payment link extension not found for this payment method."));

            var paymentUrl = linkExtension.GetPaymentLink(selectedPrompt, Url);
            if (string.IsNullOrEmpty(paymentUrl))
                return BadRequest(new ErrorResponse("payment-link-unavailable", "Payment link could not be generated. The invoice may not be activated yet."));

            var accounting = selectedPrompt.Calculate();
            var btcAmountSats = (long)(accounting.Due * 100_000_000m);

            return Ok(new CreateQuoteResponse(method, paymentUrl, btcAmountSats));
        }

        [HttpPost("/api/v1/agent/checkout/order-status")]
        public async Task<IActionResult> GetOrderStatus([FromBody] OrderStatusRequest? request)
        {
            if (string.IsNullOrEmpty(request?.InvoiceId))
                return BadRequest(new ErrorResponse("invoice-id-required", "invoiceId is required."));

            var invoice = await _invoiceRepository.GetInvoice(request.InvoiceId);
            if (invoice is null)
                return NotFound(new ErrorResponse("invoice-not-found", "The invoice was not found."));

            var settings = await _agentCheckoutService.GetSettings(invoice.StoreId);
            if (!settings.Enabled)
                return StatusCode(503, new ErrorResponse("agent-checkout-disabled", "Agent Checkout is not enabled for this store."));

            var status = invoice.Status switch
            {
                InvoiceStatus.New => "PENDING",
                InvoiceStatus.Processing => "PROCESSING",
                InvoiceStatus.Settled => "SETTLED",
                InvoiceStatus.Expired => "EXPIRED",
                InvoiceStatus.Invalid => "INVALID",
                _ => "PENDING"
            };

            DateTimeOffset? paidAt = null;
            if (invoice.Status is InvoiceStatus.Settled or InvoiceStatus.Processing)
            {
                var payments = invoice.GetPayments(false);
                foreach (var p in payments)
                {
                    if (paidAt is null || p.ReceivedTime > paidAt)
                        paidAt = p.ReceivedTime;
                }
            }

            return Ok(new OrderStatusResponse(status, paidAt));
        }
    }

    public class CreateQuoteRequest
    {
        [JsonProperty("invoiceId")]
        public string? InvoiceId { get; set; }

        [JsonProperty("transactionMethod")]
        public string? TransactionMethod { get; set; }
    }

    public class CreateQuoteResponse
    {
        [JsonProperty("method")]
        public string Method { get; }

        [JsonProperty("paymentUrl")]
        public string PaymentUrl { get; }

        [JsonProperty("btcAmount")]
        public long BtcAmount { get; }

        public CreateQuoteResponse(string method, string paymentUrl, long btcAmount)
        {
            Method = method;
            PaymentUrl = paymentUrl;
            BtcAmount = btcAmount;
        }
    }

    public class OrderStatusRequest
    {
        [JsonProperty("invoiceId")]
        public string? InvoiceId { get; set; }
    }

    public class OrderStatusResponse
    {
        [JsonProperty("status")]
        public string Status { get; }

        [JsonProperty("paidAt")]
        public DateTimeOffset? PaidAt { get; }

        public OrderStatusResponse(string status, DateTimeOffset? paidAt)
        {
            Status = status;
            PaidAt = paidAt;
        }
    }

    public class ErrorResponse
    {
        [JsonProperty("code")]
        public string Code { get; }

        [JsonProperty("message")]
        public string Message { get; }

        public ErrorResponse(string code, string message)
        {
            Code = code;
            Message = message;
        }
    }
}
