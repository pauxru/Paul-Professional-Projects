# Document Storage Adapters

`Northstar.Application` owns `IDocumentStore`. The default `LocalFileDocumentStore` writes below the configured `DocumentStore:RootPath`, generates a key independent of the original filename, and accepts only PDF/JPEG/PNG within the configured byte limit.

## Azure Blob production adapter design
An `AzureBlobDocumentStore` implementation would remain in `Northstar.Infrastructure` and implement the same port:

1. Bind a validated `AzureBlob` options section containing account endpoint, managed identity/client credentials reference, private container name, and maximum size.
2. Generate the same opaque storage key and upload to a private container using `BlobClient.UploadAsync` with a cancellation token.
3. Set content type, apply customer/tenant metadata only when justified, and avoid public URLs.
4. Require malware scan/quarantine before returning an application-visible document record.
5. Issue short-lived, scoped download URLs only after authorization; never persist SAS tokens.
6. Use encryption, soft delete/versioning, lifecycle retention, and audit events configured in the cloud account.

The Azure Blob adapter is intentionally documented rather than installed or exercised: no cloud credential, account, or external infrastructure is available on this host. Selecting an unimplemented provider must be treated as a deployment configuration error rather than silently falling back to local files.
