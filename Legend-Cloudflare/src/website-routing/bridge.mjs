const PLATFORM_ZONE = "mylegnd.com";
const DEFAULT_WEBSITE_ORIGIN = "https://masterapp-protect.azurewebsites.net";
const DEFAULT_COMMERCE_ORIGIN = "https://masterapp-parfait.azurewebsites.net";

const COMMERCE_PLATFORM_HOSTS = new Set([
  "mylegnd.com",
  "www.mylegnd.com",
  "protect.mylegnd.com"
]);

function isPlatformHost(hostname) {
  const host = (hostname || "").toLowerCase();
  return host === PLATFORM_ZONE || host.endsWith("." + PLATFORM_ZONE);
}

function isCommerceTransportPath(pathname) {
  const path = pathname || "/";
  return path === "/store" ||
    path.startsWith("/store/") ||
    path.startsWith("/store-assets/") ||
    path.startsWith("/uploads/parfait-products/") ||
    path.startsWith("/parfait-analytics/");
}

function validatedOrigin(raw, fallback) {
  const value = (raw || fallback).trim();
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

  const commerce = isCommerceTransportPath(incoming.pathname);
  const origin = commerce
    ? validatedOrigin(env?.LEGEND_COMMERCE_ORIGIN, DEFAULT_COMMERCE_ORIGIN)
    : validatedOrigin(env?.LEGEND_WEBSITE_ORIGIN, DEFAULT_WEBSITE_ORIGIN);
  if (!origin) {
    return { error: new Response("Website routing is temporarily unavailable.", { status: 503 }) };
  }

  const target = new URL(incoming.pathname + incoming.search, origin);
  const upstream = new Request(target.toString(), request);
  const headers = new Headers(upstream.headers);
  headers.delete("Host");
  headers.delete("X-Legend-Original-Host");
  headers.delete("X-Legend-Website-Bridge");
  headers.set("X-Legend-Original-Host", incoming.hostname.toLowerCase());
  headers.set("X-Legend-Website-Bridge", secret);

  return {
    commerce,
    request: new Request(upstream, {
      headers,
      redirect: "manual"
    })
  };
}

export async function handleWebsiteRouting(request, env, fetchImpl = fetch) {
  const incoming = new URL(request.url);
  const hostname = incoming.hostname.toLowerCase();
  const commerce = isCommerceTransportPath(incoming.pathname);

  if (incoming.protocol !== "https:") {
    incoming.protocol = "https:";
    return Response.redirect(incoming.toString(), 308);
  }

  // Normal LEGEND-owned traffic retains its existing origins. The only first-party
  // routes transported to the commerce authority are the explicit public storefront
  // hosts. Portal/client are never redirected through commerce.
  if (isPlatformHost(hostname) &&
      !(commerce && COMMERCE_PLATFORM_HOSTS.has(hostname))) {
    return fetchImpl(request);
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
