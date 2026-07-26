# Legend native mobile Entra setup

The checked-in mobile client deliberately has no tenant, application, scope, endpoint, or secret values. Until the values below are configured, the app remains on its configuration-required screen and the AgentPortal mobile bearer policy rejects every request.

## 1. Register the protected API

In Microsoft Entra ID, create (or identify) the **Legend MASTERAPP Mobile API** app registration used by AgentPortal.

1. Record its **Application (client) ID** and tenant **Directory (tenant) ID**.
2. Set an Application ID URI, such as `api://<mobile-api-client-id>`.
3. Expose one delegated scope, for example `mobile_access`.
4. The resulting full scope must be `api://<mobile-api-client-id>/mobile_access`.
5. Do not configure a client secret for the iOS client.

The protected API access token must have all of these claims:

- `iss` matching `https://login.microsoftonline.com/<tenant-id>/v2.0`
- `tid` matching the tenant ID
- `aud` matching the API application ID URI
- `scp` containing `mobile_access`
- `oid`, which is the only user identity input accepted by the mobile API

## 2. Register the iOS public client

Create a separate **Legend iOS** app registration.

1. Configure it as a public/native client; never add a client secret.
2. Add the iOS redirect URI in the exact form `<redirect-scheme>://oauth/callback`.
3. Add delegated permission to the Mobile API scope above.
4. Grant admin consent if your tenant requires it.
5. Record the iOS public-client application ID.

The redirect scheme must be unique to this application. The app validates the scheme, host (`oauth`), path (`/callback`), OAuth state, and authorization code before exchanging tokens.

## 3. Configure AgentPortal (App Service application settings)

Set these non-secret configuration settings on the AgentPortal host. Do not put any value in source control.

| Setting | Required value |
| --- | --- |
| `MobileAuth__TenantId` | Entra Directory (tenant) ID |
| `MobileAuth__Authority` | Exact issuer: `https://login.microsoftonline.com/<tenant-id>/v2.0` |
| `MobileAuth__Audience` | Mobile API application client ID, or its canonical Application ID URI `api://<mobile-api-client-id>`. The bearer validator normalizes this to the Entra v2 token audience (the API client ID GUID). |
| `MobileAuth__RequiredScope` | Full exposed delegated scope, e.g. `api://<mobile-api-client-id>/mobile_access` |

`MobileAuth__Authority` must be the exact issuer of tokens issued for the registration. The mobile API is intentionally fail-closed when any value is absent or invalid. It does not use browser cookies, e-mail addresses, UPNs, display names, or client-provided user IDs as identity authority.

## 4. Configure the iOS build securely

Supply the following non-secret build settings from an ignored local `.xcconfig` include or CI secret/configuration store. Do not commit tenant-specific values.

| Build setting | Required value |
| --- | --- |
| `LEGEND_API_BASE_URL` | Approved HTTPS AgentPortal origin for the target environment |
| `LEGEND_AUTHORIZATION_ENDPOINT` | `https://login.microsoftonline.com/<tenant-id>/oauth2/v2.0/authorize` |
| `LEGEND_TOKEN_ENDPOINT` | `https://login.microsoftonline.com/<tenant-id>/oauth2/v2.0/token` |
| `LEGEND_AUTH_CLIENT_ID` | iOS public-client application ID |
| `LEGEND_AUTH_REDIRECT_SCHEME` | The iOS redirect scheme configured in Entra |
| `LEGEND_AUTH_SCOPE` | `openid profile offline_access api://<mobile-api-client-id>/mobile_access` |
| `LEGEND_AUTH_AUDIENCE` | API Application ID URI, e.g. `api://<mobile-api-client-id>` |

The native bundle identifier is supplied by the target product build setting. Runtime identity is sourced only from `Bundle.main.bundleIdentifier`; no custom Info.plist bundle-ID key is used.

## 5. What the server permits

The only mobile host is AgentPortal, under `/api/v1/mobile`. It uses the `LegendMobileBearer` bearer-only authorization scheme and `LegendMobileApi` policy. The policy checks signature, issuer, tenant, audience, lifetime, not-before, and required delegated scope.

The server resolves the authenticated `oid` against stored `AgentProfile.AgentUserId` and `ClientProfile.ClientUserId`/`ExternalIdentityObjectId` independently. A dual-role user must explicitly select one server-authorized role. All messaging actions use the existing typed identity `(UserId, ParticipantType)` and existing `MessagingService`; the client cannot supply a sender identity.

## 6. Validation after administrators configure Entra

1. Build and install a signed non-production build with the non-secret iOS settings.
2. Sign in through the system browser and validate the exact redirect URI returns to the app.
3. Confirm `GET /api/v1/mobile/session` returns a JSON 401 without a bearer token and a typed session with a valid token.
4. For a dual-role account, confirm the server requires role selection and that each selected role produces the correct typed messaging identity.
5. Confirm Agent and Client conversations can only access existing authorized conversations.
6. Confirm no API request creates an AgentProfile, ClientProfile, tracking row, subscription, entitlement, or messaging participant implicitly.

No deployment, App Service change, Entra registration, client secret, production credential, database migration, or migration application is performed by this repository change.
