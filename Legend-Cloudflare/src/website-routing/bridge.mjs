const PLATFORM_ZONE = "mylegnd.com";
const DEFAULT_ORIGIN = "https://masterapp-protect.azurewebsites.net";

function isPlatformHost(hostname) {
  const host = (hostname || "").toLowerCase();
  return host === PLATFORM_ZONE || host.endsWith("." + PLATFORM_ZONE);
}

function validatedOrigin(raw) {
  const value = (raw || DEFAULT_ORIGIN).trim();
  let url;
  try {
    url = new URL(value);
  } catch {
    return null;
  }
  if (url.protocol !== "https:" ||
      url.username || url.password ||
      url.pathname !== "/" ||
      url.search || url.hash ||
      !url.hostname.endsWith(".azurewebsites.net")) {
    return null;
  }
  return url;
}

export function buildBridgeRequest(request, env) {
  const incoming = new URL(request.url);
  const secret = (env?.LEGEND_WEBSITE_BRIDGE_SECRET || "").trim();
  if (secret.length < 32) {
    return { error: new Response("Website routing is temporarily unavailable.", { status: 503 }) };
  }

  const origin = validatedOrigin(env?.LEGEND_WEBSITE_ORIGIN);
  if (!origin) {
    return { error: new Response("Website routing is temporarily unavailable.", { status: 503 }) };
  }

  const target = new URL(incoming.pathname + incoming.search, origin);
  const headers = new Headers(request.headers);
  headers.delete("X-Legend-Original-Host");
  headers.delete("X-Legend-Website-Bridge");
  headers.set("X-Legend-Original-Host", incoming.hostname.toLowerCase());
  headers.set("X-Legend-Website-Bridge", secret);

  return {
    request: new Request(target.toString(), {
      method: request.method,
      headers,
      body: request.body,
      redirect: "manual"
    })
  };
}

export async function handleWebsiteRouting(request, env, fetchImpl = fetch) {
  const incoming = new URL(request.url);

  // The wildcard route exists only so Cloudflare-for-SaaS vanity domains reach
  // this transport. LEGEND-owned hosts retain their existing DNS/origin paths.
  if (isPlatformHost(incoming.hostname)) {
    return fetchImpl(request);
  }

  if (incoming.protocol !== "https:") {
    incoming.protocol = "https:";
    return Response.redirect(incoming.toString(), 308);
  }

  const bridged = buildBridgeRequest(request, env);
  if (bridged.error) return bridged.error;
  return fetchImpl(bridged.request);
}

export default {
  fetch(request, env) {
    return handleWebsiteRouting(request, env);
  }
};
