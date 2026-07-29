*Meta: forum thread title suggestion — "MCP mod channel". Paste everything below the divider as the
thread's opening post.*

---

**MCP mod channel**

What it is: your local bridge to the running game mod, exposed as an MCP server. Run it on your
machine, point any MCP client (like Claude Desktop) at it, and you can read NetworkSense reports,
apply whitelisted config profiles, or check the netcode gates directly from your workspace. It is
strictly dev-only: it runs entirely on your own machine, and there is no hosted endpoint.

The gateway code sits in `network/mcp` in the repo, with the command contract at
`network/mcp/contracts/commands.json`. The one honest gap: the automated test surface is still
small — six tests covering the ledger, auth, and profile paths for a fairly wide tool list — and
you spin up your own local Python environment to run it.

One-pager: <ONEPAGER-URL>
Get it: <ACCESS-URL>

First things to try:

- **MC-1** — Run the gateway, list its tools from any MCP client, and post the list. Done when:
  the tool list from your own client is posted in this thread, along with which client you used
  and anything in the setup that wasn't obvious.

What a useful reply looks like:

- What you ran (which MCP client, on what OS, against what game/server setup).
- What actually happened — the tool list or the errors pasted verbatim, not summarized.
- What you expected instead.
