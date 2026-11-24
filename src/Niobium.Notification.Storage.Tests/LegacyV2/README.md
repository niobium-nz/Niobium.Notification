# Prior production writer regression fixture

These two files are the exact model/domain source from Notification commit
`d34bffbc49b980d6c1c372f90a57c21846eccad4`, with only the namespace changed and a header/import added
to compile beside the current types. The current unchanged retry policy and frozen
payload type are reused. Do not synchronize these files with new application code.

`MixedVersionReceiptTests` executes this old writer through the pinned Table/Blob
adapters against owned loopback storage. Its version-2 control reproduces the
metadata loss caused by replace-mode writes; version 3 must reject that writer
before any row change. This fixture is never a runtime assembly or deployment.

Original source digests (before the namespace/header transformation):

- `NotificationSubmission.cs`: `e698f2d5e1cbbd77d4331bcaec770e6522e660e5cba3b36cd290b429fcb945b2`
- `NotificationSubmissionDomain.cs`: `589138fd23ddea78fc36d96cb0741d9bd488758175747743fc0edfab199edde3`
