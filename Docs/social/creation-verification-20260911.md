# Social creation ingress evidence — 2026-09-11

Base: 59ebd76c8e19521f6530c01502c271718b0a6dd7. Independent pre-amendment review integrated as 2176e68b. Local application changes affect only AgentPortal/web.config and the reviewed structural assertion in MobileIntegrationTests.

## Reproduction and first demonstrated failure

The supplied native log records three media creation POSTs returning 404 with 103-byte bodies. An empty unauthenticated POST to the production media route also returned the same IIS missing-resource text; posts and feed routes reached the application and returned authentication-required JSON. No user content was created by these probes.

Read-only existing IIS records contained 18 matching media-path requests with 404.0 and Win32 status 2. Detailed error records 000246, 000247, 000248, 000251 and 000255 identified IIS Web Core, MapRequestHandler, StaticFile and 0x80070002, resolving the API path beneath site/wwwroot as a physical file. This demonstrates incorrect handler selection before the shared social controller executes. It does not prove downstream storage, processing or persistence failed independently.

The inspected deployed web.config SHA256 was 2d472c525b6869b64d4e5c6078eec6d56ffcc793299488edd5bf75092c2b8c12. It had a root ASP.NET Core handler location and a separate media-path request-limit location. Current production was 262428f38c8e1dd0ee72a362983b277f493fd362, not the unpublished messaging candidate. No credentials or private request contents are retained here.

## Repair and scope

Move the existing 106954752-byte IIS ceiling into the canonical application handler scope and remove the virtual media-path scope. Keep the shared controller routes, multipart fields, action/form limits, authentication and media validation unchanged. The root transport ceiling applies application-wide; independent application limits remain separate. This is a focused candidate correction, not proof of its deployed effect.

The structural test amendment was independently reviewed before editing. It now checks single root scope, inheritance control, wildcard ASP.NET Core handler and the unchanged central numeric ceiling. Both direct and staged upload action/form limit assertions remain intact.

## Executed verification

Focused local build/test: 80 passed, zero failed, zero skipped. Filter: MobileIntegrationTests, SocialFeedServiceTests, SocialMediaStorageTests and SocialMediaProcessingWorkerTests. Results: /private/tmp/legend-social-results/social-ingress.trx. No unrelated held-out assertions were changed. Local dotnet publish completed; the packaged web.config also passed XML checks for the single root handler scope and unchanged ceiling. No package was uploaded.

## Still required

After authorized deployment, verify direct and staged media POSTs reach application authentication rather than StaticFile. Then test authorized post, story and Hac upload, processing, persisted feed visibility and actual media playback. A successful 401 probe proves routing only. Windows IIS is not executed by these Mac tests, so live resolution remains unverified. Messaging/translation deployment remains explicitly held by the user; no release was performed here.
