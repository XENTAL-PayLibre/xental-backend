#!/usr/bin/env node
/**
 * Xental MCP server.
 *
 * Exposes a Xental merchant account to any MCP-capable agent (Claude Desktop, etc.) as a set of
 * tools over the API plane. Authenticates with API-key client credentials, exchanges them for a
 * short-lived bearer token (POST /auth/token), caches it, and refreshes on expiry / 401.
 *
 * Env:
 *   XENTAL_API_BASE       default https://api.xental.online (use https://api.staging.xental.online for sandbox)
 *   XENTAL_CLIENT_ID      API key client id   (from the dashboard)
 *   XENTAL_CLIENT_SECRET  API key client secret (shown once on creation)
 */
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { StdioServerTransport } from '@modelcontextprotocol/sdk/server/stdio.js';
import { z } from 'zod';

const API_BASE = (process.env.XENTAL_API_BASE ?? 'https://api.xental.online').replace(/\/$/, '');
const CLIENT_ID = process.env.XENTAL_CLIENT_ID ?? '';
const CLIENT_SECRET = process.env.XENTAL_CLIENT_SECRET ?? '';

if (!CLIENT_ID || !CLIENT_SECRET) {
  console.error('XENTAL_CLIENT_ID and XENTAL_CLIENT_SECRET must be set.');
  process.exit(1);
}

// ---- Auth: cache a bearer token, refresh on expiry / 401 --------------------
let token: { value: string; expiresAt: number } | null = null;

async function getToken(force = false): Promise<string> {
  const now = Date.now();
  if (!force && token && token.expiresAt - 30_000 > now) return token.value;

  const res = await fetch(`${API_BASE}/api/v1/auth/token`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ clientId: CLIENT_ID, clientSecret: CLIENT_SECRET }),
  });
  if (!res.ok) throw new Error(`Auth failed (${res.status}). Check XENTAL_CLIENT_ID / XENTAL_CLIENT_SECRET.`);
  const json = (await res.json()) as { accessToken: string; expiresIn: number };
  token = { value: json.accessToken, expiresAt: now + json.expiresIn * 1000 };
  return token.value;
}

type Method = 'GET' | 'POST' | 'DELETE';

async function call(method: Method, path: string, body?: unknown): Promise<string> {
  const doFetch = async (bearer: string) =>
    fetch(`${API_BASE}/api/v1${path}`, {
      method,
      headers: {
        Authorization: `Bearer ${bearer}`,
        ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}),
      },
      body: body !== undefined ? JSON.stringify(body) : undefined,
    });

  let res = await doFetch(await getToken());
  if (res.status === 401) res = await doFetch(await getToken(true)); // token may have expired → refresh once

  const text = await res.text();
  if (!res.ok) throw new Error(`${method} ${path} → ${res.status}: ${text || res.statusText}`);
  return text || '(no content)';
}

/** Wrap an API call as an MCP tool result, surfacing errors as tool errors rather than crashes. */
function result(promise: Promise<string>) {
  return promise
    .then((text) => ({ content: [{ type: 'text' as const, text }] }))
    .catch((e: unknown) => ({
      content: [{ type: 'text' as const, text: `Error: ${e instanceof Error ? e.message : String(e)}` }],
      isError: true,
    }));
}

// ---- Server + tools ---------------------------------------------------------
const server = new McpServer({ name: 'xental', version: '0.1.0' });

server.tool(
  'get_agent_guide',
  'Read the Xental agent orientation (llms.txt): auth, core flow, and differentiators.',
  {},
  async () =>
    result(
      fetch(`${API_BASE}/.well-known/llms.txt`).then((r) => r.text())
    )
);

server.tool(
  'list_virtual_accounts',
  'List the merchant\'s dedicated virtual accounts (NUBANs) with balances and payment state.',
  {},
  async () => result(call('GET', '/virtual-accounts'))
);

server.tool(
  'get_virtual_account',
  'Get one virtual account by its reference, including balance and reconciliation state.',
  { accountRef: z.string().describe('The account reference (accountRef).') },
  async ({ accountRef }) => result(call('GET', `/virtual-accounts/${encodeURIComponent(accountRef)}`))
);

server.tool(
  'create_virtual_account',
  'Provision a dedicated virtual account (NUBAN) for a customer. On test-mode keys this creates a sandbox account. Money is integer kobo (₦1 = 100 kobo).',
  {
    name: z.string().describe('Customer / account holder name.'),
    accountRef: z.string().optional().describe('Optional unique reference; the server generates one if omitted.'),
    email: z.string().email().optional(),
    phone: z.string().optional(),
    expectedAmountKobo: z.number().int().nonnegative().optional().describe('Expected amount in kobo, for reconciliation.'),
  },
  async (args) => result(call('POST', '/virtual-accounts', args))
);

server.tool(
  'delete_virtual_account',
  'Delete a virtual account that has no payment activity.',
  { accountRef: z.string() },
  async ({ accountRef }) => result(call('DELETE', `/virtual-accounts/${encodeURIComponent(accountRef)}`))
);

server.tool(
  'list_transactions',
  'List reconciled inflows/outflows for the merchant.',
  {},
  async () => result(call('GET', '/transactions'))
);

server.tool(
  'transactions_summary',
  'Aggregate transaction totals and reconciliation breakdown.',
  {},
  async () => result(call('GET', '/transactions/summary'))
);

server.tool(
  'get_transaction',
  'Get one transaction by its reference.',
  { reference: z.string() },
  async ({ reference }) => result(call('GET', `/transactions/${encodeURIComponent(reference)}`))
);

server.tool(
  'list_banks',
  'List supported banks for payouts (name + bank code).',
  {},
  async () => result(call('GET', '/transfers/banks'))
);

server.tool(
  'lookup_bank_account',
  'Resolve an account name from an account number + bank code (name enquiry).',
  { accountNumber: z.string(), bankCode: z.string() },
  async (args) => result(call('POST', '/transfers/bank/lookup', args))
);

server.tool(
  'initiate_transfer',
  'Send a payout to a bank account. MOVES REAL MONEY on live keys. Idempotent on merchantTxRef. Amount in kobo.',
  {
    merchantTxRef: z.string().describe('Caller-supplied unique reference (idempotency key).'),
    amountKobo: z.number().int().positive(),
    accountNumber: z.string(),
    bankCode: z.string(),
    narration: z.string().optional(),
  },
  async (args) => result(call('POST', '/transfers/bank', args))
);

server.tool(
  'list_transfers',
  'List outbound transfers (payouts).',
  {},
  async () => result(call('GET', '/transfers'))
);

server.tool(
  'simulate_deposit',
  'Sandbox only (test-mode keys): drive a real reconciliation with no bank movement — create an account, simulate a payment, watch it reconcile end-to-end. Amount in kobo.',
  {
    accountRef: z.string(),
    amountKobo: z.number().int().positive(),
    senderName: z.string().optional(),
    reversal: z.boolean().optional(),
  },
  async (args) => result(call('POST', '/sandbox/simulate/deposit', args))
);

const transport = new StdioServerTransport();
await server.connect(transport);
console.error(`Xental MCP server running against ${API_BASE}`);
