# Reviewed deployment workflow revision

`azure-deploy.yml` in this directory is review material, not an Actions workflow.
The readiness controller may authenticate its exact Git blob only after this file
and its pinned predecessor/target identities have reached protected main through
the ordinary feature verification, native review and independent approval process.
The controller reads metadata at its immutable `github.workflow_sha`; it never
executes this file or reads a feature-owned approval file.

The proposed revision adds the receipt webhook signing-key bundle to the existing
test/prod Key Vault projection and runs the configuration preflight before `azd`.
It retains all jobs, test/production concurrency, trusted main checkout, read-only
production verification and package behavior. It grants no operator role and does
not configure environment secrets or a provider webhook.

The complete predecessor pipeline is main
`d3440994c7c8e50f775257438a0a45e33ffd95a2`:

| Protected path | Predecessor Git blob |
| --- | --- |
| `.github/workflows/azure-deploy.yml` | `622675bc1c131a11268e49a09efc0d3eb4a7fca6` |
| `azure.yaml` | `bcc11f088596a50c2c68b92d84553c84da3ceefd` |
| `scripts/check-coverage.py` | `fe7e478c3d3feca4505dc6f824153fbdf4eff6bf` |
| `scripts/test-resources.py` | `8b84f121c4cce94a51b50e71102cacf4bc79902b` |

The sole next workflow blob is
`9e69e51be2317bcca6edf74de7004281a0b9ed70`, captured from the receipt candidate's
workflow in local commit `03563574da8ac3df7d9901704609ac6936bae1cb`. This source
reference is provenance, not evidence of feature CI or deployment.

Any change to the trusted predecessor tuple expires the staged authorization.
Once the new workflow is installed on main, ordinary exact-main authentication
applies; this staged approval cannot later revive an older workflow. Changing this
review file alone does not authorize another revision: both its exact target pin
and complete predecessor pins in the main-owned controller must be reviewed.
Do not add wildcards, mutable refs, feature-provided manifests or skipped-test
exceptions. Remove obsolete staged material during subsequent pipeline maintenance.
