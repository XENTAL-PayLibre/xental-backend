using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Xental.Api.Controllers;

/// <summary>
/// Agent-facing discovery (agent layer). A compact, LLM-readable capability map so a developer can
/// point their AI agent at Xental and have it wire the integration. The full contract is the
/// OpenAPI document at <c>/swagger/v1/swagger.json</c>; this is the orientation on top of it.
/// </summary>
[ApiController]
[AllowAnonymous]
public sealed class AgentDiscoveryController : ControllerBase
{
    [HttpGet("/.well-known/llms.txt")]
    [Produces("text/plain")]
    public ContentResult Llms() => Content(LlmsTxt, "text/plain");

    private const string LlmsTxt = """
        # Xental

        Reusable dedicated virtual accounts (DVAs / NUBANs) + automatic reconciliation on Nomba.
        Issue a persistent bank account number per customer; inbound transfers are auto-reconciled
        (exact / underpaid / overpaid / duplicate / reversal) and pushed as enriched webhook events.

        ## Base URL
        - Production:  https://api.xental.online
        - Sandbox:     https://api.staging.xental.online  (test-mode keys, zero real money)

        ## Auth
        1. Create an API key in the dashboard (test or live). Copy the client secret (shown once).
        2. POST /api/v1/auth/token {clientId, clientSecret} -> { accessToken }.
        3. Send `Authorization: Bearer <accessToken>` on every /api/v1 call.
        Live keys require an approved KYC/KYB onboarding; test keys work immediately.

        ## Core flow
        - POST /api/v1/virtual-accounts            provision a NUBAN for a customer (optional expectedAmountKobo)
        - GET  /api/v1/virtual-accounts/{ref}      balance + payment state
        - GET  /api/v1/transactions                reconciled inflows/outflows
        - Subscribe outbound webhooks (POST /api/v1/webhook-endpoints) for `deposit.reconciled`.

        ## Build + verify with zero money (recommended for agents)
        - POST /api/v1/sandbox/simulate/deposit {accountRef, amountKobo}   (test-mode keys only)
          Drives a real reconciliation with no bank movement — create an account, simulate a
          payment, and observe it reconcile, split, and trigger rules end-to-end.

        ## Differentiators
        - Live Checkout:   POST /api/v1/checkout/sessions  ->  GET /api/v1/checkout/{token}/stream (SSE)
        - Split & Escrow:  PUT  /api/v1/settings/splits ; POST /api/v1/settlements/{ref}/hold|release
        - Money Rules:     GET/POST/DELETE /api/v1/rules   (overpaid->hold, high-risk->hold, notify)
        - Payment Flows:   GET/POST/PUT/DELETE /api/v1/flows ; GET /api/v1/flows/runs
                           Multi-step automation on reconciled deposits: trigger (deposit/overpaid/
                           underpaid/fully-paid/high-risk) + conditions -> ordered actions
                           (hold/release/notify/review-flag), with an audit trail. (dashboard plane)
        - Collections Intelligence: GET /api/v1/insights[/aging|/forecast|/customers]
                           Receivables aging, a cash-flow forecast, and per-customer collection
                           scores (0-100). (dashboard plane)
        - Copilot:         POST /api/v1/copilot/ask {prompt}
                           A grounded, natural-language assistant over your live account data.
                           (dashboard plane)

        ## MCP (agent plane)
        An MCP server (clients/xental-mcp) exposes the API-plane operations above as typed tools for
        any MCP-capable agent (Claude Desktop, etc.) — provision accounts, watch transactions, run
        payouts, and drive the sandbox with natural language.

        ## Conventions
        - All money is integer kobo (₦1 = 100 kobo). Never floats.
        - Money-moving POSTs are idempotent on a caller-supplied reference.
        - Errors: JSON problem details with a stable code + message.

        ## Full contract
        OpenAPI: /swagger/v1/swagger.json
        """;
}
