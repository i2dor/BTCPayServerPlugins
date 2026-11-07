#nullable enable
using System.Threading.Tasks;
using BTCPayServer.Plugins.AgentCheckout.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.AgentCheckout
{
    internal sealed class AgentCheckoutExceptionFilter : IAsyncExceptionFilter
    {
        private readonly ILogger<AgentCheckoutExceptionFilter> _logger;

        public AgentCheckoutExceptionFilter(ILogger<AgentCheckoutExceptionFilter> logger)
        {
            _logger = logger;
        }

        public Task OnExceptionAsync(ExceptionContext ctx)
        {
            _logger.LogError(ctx.Exception, "Unhandled exception in Agent Checkout endpoint");
            ctx.Result = new ObjectResult(new ErrorResponse("internal-error", "An internal error occurred."))
            {
                StatusCode = 500
            };
            ctx.ExceptionHandled = true;
            return Task.CompletedTask;
        }
    }
}
