# Xental MCP server

A [Model Context Protocol](https://modelcontextprotocol.io) server that lets any MCP-capable agent
(Claude Desktop, Claude Code, etc.) operate a **Xental** merchant account — provision virtual
accounts, watch reconciled transactions, run payouts, and drive the sandbox — all with natural
language.

This is the "agent plane": instead of an agent scraping the dashboard, it talks to Xental through
typed tools backed by the public API.

## Tools

| Tool | What it does |
|------|--------------|
| `get_agent_guide` | Reads `/.well-known/llms.txt` — auth, core flow, differentiators |
| `list_virtual_accounts` / `get_virtual_account` | List / fetch NUBANs with balance + reconciliation state |
| `create_virtual_account` | Provision a NUBAN for a customer (sandbox on test keys) |
| `delete_virtual_account` | Remove an account with no activity |
| `list_transactions` / `get_transaction` / `transactions_summary` | Reconciled inflows/outflows + totals |
| `list_banks` / `lookup_bank_account` | Payout bank list + name enquiry |
| `initiate_transfer` / `list_transfers` | Send a payout (idempotent) / list payouts |
| `simulate_deposit` | Sandbox: drive a real reconciliation with no bank movement |

All money is **integer kobo** (₦1 = 100 kobo). Money-moving calls are idempotent on a caller-supplied
reference.

## Setup

```bash
cd clients/xental-mcp
npm install
npm run build
```

You need an API key from the dashboard (**Settings → Developers**). Copy the **client id** and
**client secret** (the secret is shown once). Use a **test-mode** key to start — it works immediately
and moves zero real money; live keys require approved KYC/KYB.

### Environment

| Var | Default | Notes |
|-----|---------|-------|
| `XENTAL_CLIENT_ID` | — | required |
| `XENTAL_CLIENT_SECRET` | — | required |
| `XENTAL_API_BASE` | `https://api.xental.online` | use `https://api.staging.xental.online` for sandbox |

## Use with Claude Desktop

Add to `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "xental": {
      "command": "node",
      "args": ["/absolute/path/to/clients/xental-mcp/build/index.js"],
      "env": {
        "XENTAL_API_BASE": "https://api.staging.xental.online",
        "XENTAL_CLIENT_ID": "your-client-id",
        "XENTAL_CLIENT_SECRET": "your-client-secret"
      }
    }
  }
}
```

Restart Claude Desktop, then try: *"Create a test virtual account for Ada, simulate a ₦5,000 deposit,
and show me the reconciliation."*

## Auth flow

The server exchanges your client credentials at `POST /api/v1/auth/token` for a short-lived bearer
token, caches it, and refreshes automatically on expiry or a 401. Your secret never leaves the
machine the server runs on.
