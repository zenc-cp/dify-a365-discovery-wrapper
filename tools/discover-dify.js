/* Dify 0.15.4 Studio metadata collector. Read this file before running it.
   Paste into your own signed-in Studio console, then call:
   await difyA365Discover("https://YOUR-DIFY-ORIGIN")
   Returns metadata only. Does not upload, download, write to Dify, or print a token. */
(function (root) {
  "use strict";
  const guid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
  function need(ok, reason) { if (!ok) throw new Error(reason); }

  async function collect({ origin, expectedOrigin, token, fetchImpl, now = () => new Date() }) {
    const base = new URL(expectedOrigin);
    need(base.origin === expectedOrigin && origin === expectedOrigin && !base.username && !base.password,
      "Dify origin mismatch");
    need(base.protocol === "https:" || (base.protocol === "http:" && ["localhost", "127.0.0.1", "[::1]"].includes(base.hostname)),
      "Use HTTPS or a loopback lab tunnel");
    need(typeof token === "string" && token.length > 0, "Sign in to your Dify 0.15.4 Studio first");
    const reads = [];
    async function get(path) {
      const response = await fetchImpl(origin + path, {
        method: "GET", credentials: "include", redirect: "error", cache: "no-store",
        headers: { Authorization: "Bearer " + token, Accept: "application/json" },
        signal: AbortSignal.timeout(15000),
      });
      need(response.status === 200, "Dify GET failed with HTTP " + response.status);
      need((response.headers.get("content-type") || "").includes("application/json"), "Expected JSON, not a login page");
      const raw = await response.text();
      need(raw.length <= 2000000, "Dify response exceeds collector limit");
      let body;
      try { body = JSON.parse(raw); } catch { throw new Error("Invalid Dify JSON response"); }
      reads.push({ method: "GET", path, status: 200 });
      return body;
    }
    const workspace = await get("/console/api/workspaces/current");
    need(guid.test(workspace.id || ""), "Missing current workspace id");
    const apps = [], pages = [], ids = new Set();
    let total = null, finished = false;
    for (let page = 1; page <= 20; page++) {
      const body = await get(`/console/api/apps?page=${page}&limit=100&mode=all&is_created_by_me=false`);
      need(Array.isArray(body.data) && body.data.length <= 100 && Number.isInteger(body.total) && body.total >= 0 &&
        typeof body.has_more === "boolean", "Unexpected Dify apps response");
      need(body.page === undefined || body.page === page, "Unexpected response page");
      need(body.limit === undefined || body.limit === 100, "Unexpected response limit");
      if (total === null) total = body.total;
      need(body.total === total, "Inventory changed during paging; collect again before approval");
      for (const item of body.data) {
        need(guid.test(item.id || "") && !ids.has(item.id), "Duplicate or invalid app id");
        need(typeof item.name === "string" && item.name.trim().length > 0 && item.name.length <= 256 &&
          !/[\u0000-\u001f\u007f]/.test(item.name), "Invalid app name");
        need(["chat", "advanced-chat", "agent-chat", "workflow", "completion"].includes(item.mode), "Unknown app mode");
        ids.add(item.id);
        apps.push({ id: item.id, name: item.name, mode: item.mode });
      }
      pages.push({ page, limit: 100, total, count: body.data.length, has_more: body.has_more });
      if (!body.has_more) { finished = true; break; }
      need(body.data.length > 0, "Empty page with has_more=true");
    }
    need(finished && apps.length === total, "Incomplete inventory; no usable checkpoint produced");
    const finalWorkspace = await get("/console/api/workspaces/current");
    need(finalWorkspace.id === workspace.id, "Workspace changed during discovery");
    return { schema: 1, status: "dify_platform_inventory_discovered", source: "Dify 0.15.4 console API",
      instance_url: origin, observed_at_utc: now().toISOString(), workspace_id: workspace.id,
      selection_filters: { created_by_me: false, mode: "all", name: null, source_ids: null },
      pages, total, apps, reads, complete: true, credentials_returned: false };
  }
  if (typeof module !== "undefined" && module.exports) module.exports = { collect };
  else root.difyA365Discover = expectedOrigin => collect({
    origin: root.location.origin, expectedOrigin,
    token: root.localStorage.getItem("console_token"), fetchImpl: root.fetch.bind(root),
  });
})(globalThis);
