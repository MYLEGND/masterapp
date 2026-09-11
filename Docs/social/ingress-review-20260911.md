# Independent social ingress review — 2026-09-11

Review baseline: `59ebd76c`. This commit contains documentation only, before any test amendment. No application configuration or test changes, builds, live writes, or deployment were performed by this reviewer.

## Decision

The proposed repair is justified for implementation and validation: move the existing bounded IIS request limit into the single canonical application-root location that already owns the ASP.NET Core handler. Remove the media-route-specific location. Correct the source-contract test accordingly, preserving the numeric and action-level limit assertions. This is not evidence that deployed IIS is already fixed.

## Evidence and limits of the conclusion

Source inspection confirms `AgentPortal/web.config` currently has a root `location path="." inheritInChildApplications="false"` with the wildcard `aspNetCore` handler and in-process application configuration, plus a separate virtual route location containing only request filtering limits. `MobileSocialController` declares the correct POST media route and routes ordinary creation failures to 400, 403, or 503. Native iOS and Android requests match that route, multipart fields and authenticated participant contract.

The lead supplied live diagnostics for independent interpretation: empty POST to the media route reproduces the same 103-byte 404 as uploads; posts/feed probes reach authentication and return 401; 18 matching IIS records report 404.0; detailed-error records 000246/247/248/251/255 identify IISWebCore, MapRequestHandler, StaticFile, error 0x80070002, and a physical filesystem lookup ending in the API route. These are lead-provided observations, not a live probe or raw-log inspection performed by this reviewer.

Those details establish that the failed requests selected StaticFile rather than reaching the intended application handler. An empty request also rules out the media byte limit as the explanation for that reproduction. They do not establish that every nested location is invalid on every IIS installation, or independently prove the exact inheritance mechanism. Removing this route-specific configuration scope is a focused, testable correction; successful application routing after deployment remains required.

Microsoft documents the application-root web.config and canonical ASP.NET Core handler configuration, including the root location wrapper and child-application inheritance control. The proposed structure follows that documented application configuration model. [ASP.NET Core IIS hosting](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/iis/?view=aspnetcore-9.0), [web.config source documentation](https://github.com/dotnet/AspNetCore.Docs/blob/main/aspnetcore/host-and-deploy/iis/web-config.md).

Microsoft defines `maxAllowedContentLength` in bytes, with a default of 30,000,000, and documents request filtering separately from handler dispatch. The 404.0 StaticFile finding should not be relabeled as a proven content-length rejection. [IIS request limits](https://learn.microsoft.com/en-us/iis/configuration/system.webserver/security/requestfiltering/requestlimits/).

## Scope and retained boundaries

Keep `106954752` bytes, aligned to `SocialMediaUploadLimits.MaximumMultipartRequestBytes`. Keep the existing handler name, wildcard path/verbs, module, resource type, application process/arguments and in-process hosting configuration unchanged. No alternate route, duplicate handler, rewrite, storage path, authentication exemption, or media validation bypass is justified.

Moving the IIS limit to root broadens that transport allowance to application routes, rather than just the media URL. This is a real scope change and must be stated in the change description. It does not itself change MVC action limits, form parsing limits, authentication, actor resolution, media-type/content validation, or persistence rules. Preserve those limits; do not claim every unrelated route retains the old IIS ceiling. IIS processes its filter before application body limits, so application limits remain a separate layer. [ASP.NET Core IIS hosting](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/iis/?view=aspnetcore-9.0).

## Correct test amendment

`MobileIntegrationTests.IisIngress_AdmitsTheSamePayloadAsMobileMediaEndpoints` currently requires the route-specific location and explicitly rejects a root request limit. That encodes the configuration shape implicated by the runtime evidence, not a proven security invariant. Amending it is warranted; deleting it or reducing it to a numeric substring check is not.

The amended structural test should require one canonical root location with child-application inheritance disabled; one expected ASP.NET Core wildcard handler and application configuration; exactly one intended requestLimits value in the same system.webServer scope; equality with the central maximum; and no route-specific media location. Keep the existing reflection assertions for BOTH CreateMediaPost and StageMediaPost request-body/form limits and form-value bounds. Verify the published web.config as well as source, since publishing transforms application hosting attributes.

## Required confirmation before claiming resolution

Run the focused configuration/controller tests and existing social validation tests. Source/XML tests do not execute Windows IIS handler selection. When a deployment is separately authorized, first repeat the empty unauthenticated media POST and compare it with the existing posts/feed authentication responses: the media request must reach the application's authentication boundary instead of StaticFile. Check direct and stage routes, then use an authorized controlled upload to establish end-to-end persistence and processing. A 401 routing probe alone cannot prove upload success. Keep existing negative authentication and payload validation behavior. Do not publish raw diagnostics or account data in test artifacts.

Messaging and other deployment holds remain in effect; this review authorizes neither publication nor production mutation.
