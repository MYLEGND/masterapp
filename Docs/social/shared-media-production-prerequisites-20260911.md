# Shared media production prerequisites — 2026-09-11

This is a prepared release checklist, not a deployed configuration. The user explicitly prohibits deployment. No Azure settings, identities, roles, containers or media objects were changed during inspection.

## Verified existing resources

Read-only Azure inspection found `masterappstorage1221` and its existing `legend-social-media` container. The portal and client app settings contain no configured Social:Media Blob connection, container URL or explicit root. Deployed appsettings also contain no effective Blob configuration. The existing implementation consequently selects each app's own persistent home/data/legend-social-media directory. The client website cannot read the portal's files from its own disk.

The portal has a system-assigned identity with Storage Blob Data Contributor inherited at the existing social container. The client app has no managed identity (identity property null). No account keys or credential values were retrieved or recorded. The existing container reports publicAccess null (private). The existing App Service plan has one hosting instance. Multiple concurrent processing workers have not been validated.

## Prepared code

The existing SocialMediaStorage authority supports the BlobContainerUrl setting with DefaultAzureCredential. The candidate extends its existing video processor to operate on a bounded temporary download and conditionally replace the same Blob using the observed ETag. Local disk processing remains supported. There is one canonical post/media visibility resolver shared by the agent and client websites and mobile APIs. Only the portal registers the processing worker; the client registers the same social read authority without another worker.

The source transition must use the existing container. It must not create another media database, anonymous download bypass, second processor or duplicate content authority.

## Required release actions, not executed

1. Establish the client app's managed identity and grant only the existing container access needed by its actual social usage. The new client surface only reads posts/media; Storage Blob Data Reader at the container is the initial minimum. Do not grant it account-wide contributor privileges. If any client operation requires mutation, demonstrate that operation and review the minimum permission separately.
2. Configure the existing `Social__Media__BlobContainerUrl` for both apps to the same existing private container URL. Keep connection-string credentials absent and use their identities. This changes live storage behavior and remains held with deployment.
3. Before routing clients to Blob-backed media, migrate and verify all retained portal disk assets, including previews, into the same container under their unchanged database storage keys. The existing portal read-through migration is available, but lazy migration alone cannot prove that the client can open every previously shared asset. Inventory the authoritative existing asset records, verify bytes/checksums and readiness, and coordinate in-progress uploads/processing so the final inventory has no gaps. Retain original files until the migration and rollback decision are verified; do not delete to resolve uncertainty.
4. Apply the candidate's additive messaging migration through the existing release authority, then deploy the matching source/artifacts only after approval. No migration has been applied by this repair operation.
5. Verify post/story/Hac creation, processing and playback through the authenticated production routes. Verify the same retained asset from portal, client, iOS and Android, including expired/private/deleted refusal and clean attachment download. Verify production FFmpeg execution separately from local process-orchestration tests.

## Open evidence gates

Storage configuration and migration are not executed; client managed-identity access is not established. Actual production media parity, corrected IIS ingress and end-to-end latency therefore remain unverified. These are explicit release prerequisites, not successful checks. Local test success does not close them and no new deployment is authorized by this document.

A memory-only production FFmpeg-to-FFprobe synthetic codec probe through the existing Kudu command endpoint timed out after 30 seconds without a result. A subsequent process inspection showed no ffmpeg or ffprobe processes. This is unverified codec execution, not a pass; local orchestration tests do not replace the production codec gate.
