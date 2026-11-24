# Set up Notification operator identity

Use these steps when the deferred operator feature resumes. The owner approved
the design on 25 November 2026 (Pacific/Auckland). Until then, use the existing
manual process for operator access to data. [Operator recovery design](operator-recovery.md)

## Prepare the identity

1. Choose the existing Niobium management application and approved operator
   accounts for each environment. Record the management app GUID and each
   Niobium user GUID in hyphenated `D` format. Choose one dedicated approved
   test principal for CI. Use separate grants in `test` and `prod`.
2. Confirm the actual central Identity issuer URL for sign-in and its ID-token
   trust values with the Identity administrator. The current issuance path uses
   the management app GUID as both `aud` and `GroupSid`. The framework sets
   `iss` from the central service's `AccessTokenIssuer`, including for RSA ID
   tokens. Use the issued `iss` value in Notification. Obtain the full
   corresponding RSA public PEM, at least 2048 bits, from the trusted
   configuration. Keep the private key with central Identity and use a
   secret managed issuer key for privileged test trust.
   [Token builder](https://github.com/niobium-nz/Niobium/blob/e82065df679af28d78b02ce1b25d78b970c57172/Niobium.Platform.Identity/BearerTokenBuilder.cs)
3. Open Notification's GitHub repository **Settings → Environments**. Select
   each of `test` and `prod` and create the following environment variables and
   secret. The proposed access-token issuer and audience values identify the
   Notification service and environment explicitly.
   [GitHub environments](https://github.com/niobium-nz/Niobium.Notification/settings/environments)

| GitHub environment name | Type | Value |
| --- | --- | --- |
| `APPSETTING_IDENTITYSERVICEOPTIONS__IDTOKENISSUER` | Variable | Actual central ID-token `iss` |
| `APPSETTING_IDENTITYSERVICEOPTIONS__IDTOKENAUDIENCE` | Variable | Management app GUID, matching central `aud` |
| `APPSETTING_IDENTITYSERVICEOPTIONS__IDTOKENPUBLICKEY` | Variable | Complete trusted public RSA PEM |
| `APPSETTING_IDENTITYSERVICEOPTIONS__ACCESSTOKENISSUER` | Variable | For example, `niobium.notification.test` or `niobium.notification.prod` |
| `APPSETTING_IDENTITYSERVICEOPTIONS__ACCESSTOKENAUDIENCE` | Variable | For example, `niobium.notification.operators.test` or `niobium.notification.operators.prod` |
| `APPSETTING_NOTIFICATIONOPERATOROPTIONS__MANAGEMENTGROUP` | Variable | Management app GUID in `D` format, matching central `GroupSid` |
| `APPSETTING_IDENTITYSERVICEOPTIONS__ACCESSTOKENSECRET` | Secret | Text encoding of at least 32 random bytes from the approved secret manager, independently generated per environment |

The future workflow must project all seven values explicitly. The public values use
the existing `APPSETTING_` hook. Project the access secret as runner variable
`APPSETTING_KV_IDENTITYSERVICEOPTIONS__ACCESSTOKENSECRET`. Deployment writes
`IDENTITYSERVICEOPTIONS--ACCESSTOKENSECRET` in Key Vault and refreshes the Function
App's `IDENTITYSERVICEOPTIONS__ACCESSTOKENSECRET` reference. Use the GitHub
secret name in the table above. Keep secret values and tokens in the approved
administration systems. [Existing deployment configuration](delivery-and-operations.md#provisioning-and-credentials)

## Provision resources and assign the role

1. Deliver additive provisioning for the framework's `Role` and `Entitlement`
   tables, `NotificationRecoveryAction` and the typed copy dispatch queue through
   the normal review process. Use the existing Notification data account and
   Service Bus namespace. Preserve all required resources in the name lists.
   Plan this bootstrap ahead of the guarded deployment test so the administrator
   has a ready table for the grant.
2. In each environment, have the authorized administrator apply the approved
   grant using the established administration process. If that process uses
   Storage Explorer or the portal Storage browser, open **Tables → Role** in
   Notification's data account, identified by the deployment output
   `dataStorageAccountName`.
3. Create or update the approved row using the physical mapping below. On an
   existing row, preserve its fields and roles and use its current ETag for a
   conditional update. The application's Role model uses business property
   names, which the framework maps to the physical keys.

| Physical field | Framework property | Value |
| --- | --- | --- |
| `PartitionKey` | `Role.Tenant` | Management app GUID in `D` format |
| `RowKey` | `Role.User` | Approved Niobium user GUID in `D` format |
| `Roles` | `Role.Roles` | Comma separated role names including exactly `NOTIFICATION_RECOVERY_OPERATOR` |

4. Leave the `Entitlement` table available for the framework lookup. Recovery
   authorization requires only the role row. Keep the resulting application grant
   independent of the administrator's manual backend access.
   [Framework Role model](https://github.com/niobium-nz/Niobium/blob/e82065df679af28d78b02ce1b25d78b970c57172/Niobium.Platform.Identity/Role.cs)

## Wire sign-in and verify before release

1. Sign in through the real central Identity service using the approved account
   within the selected application. Use its existing supported client and login
   method. Retrieve the ID token from the Authorization response header.
2. Exchange it over HTTPS at the Notification host using
   `GET /operators/token`, Authorization Bearer header and an empty request
   body. Use the Authorization response token for subsequent recovery calls. In
   the shared client, set `AccessTokenHost` to the Notification HTTPS origin and
   `AccessTokenEndpoint` to `/operators/token`. Check the response code and
   token presence from `RefreshAccessTokenAsync`.
3. Implement fresh central sign-in before each test suite using the approved
   CI mechanism for the dedicated test principal. Project
   `TEST_NOTIFICATION_OPERATOR_ID_TOKEN`,
   `TEST_NOTIFICATION_OPERATOR_ACTOR`,
   `APPSETTING_NOTIFICATIONOPERATOROPTIONS__MANAGEMENTGROUP` and
   `APPSETTING_IDENTITYSERVICEOPTIONS__ACCESSTOKENISSUER` into both the
   candidate suite and the trusted job that runs the harness from main. Supply
   the actor as a Niobium user GUID in `D` format. Mask tokens, keep them in
   runner scope and fail closed on missing or expired input. The GitHub Azure
   login provides the Azure deployment identity. The central sign-in supplies
   Niobium identity and the administrator supplies the Role grant.
4. Stage the exact new workflow, its predecessor and helper pins, and the
   independently reviewed verifier before turning on the writer. Test version 4
   evidence, history, intentional copies and previously stored version 2 and
   version 3 submissions. Apply the normal feature deployment, native review,
   separate formal approval and protected merge process.
   [Reviewed workflow activation](delivery-and-operations.md#reviewed-workflow-activation)
5. Validate in test against owned fixtures, using the existing approved sender
   and recipient for any delivery check. Production verification uses
   `GET /health` and checks the deployed merge SHA.

| Case | Required evidence |
| --- | --- |
| Wrong issuer, signing key, audience or lifetime | Exchange fails before reading roles |
| Valid central identity from a different app scope | Exchange rejects the group before reading roles |
| Valid management identity without local role | Ordinary token cannot inspect or act on a submission |
| Approved role through released shared client | GET exchange succeeds, guarded read succeeds, POST exchange is rejected |
| Role removed | Newly exchanged token lacks operator privileges |
| Owned Held submission | Assessments and history preserve transport facts and zero attempts for an unsent fixture |
| Stale revision, missing saved content or replay | Conflict where appropriate, with one stable child for an identical successful copy request |
| Real original receipt and deliberate copy | Distinct Published Delivered evidence for each operation |
| Terminal delivery | Required feature and trusted checks pass, production GET report names actual merge, package result is recorded |
