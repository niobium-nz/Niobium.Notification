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

The predecessor contains the four existing pipeline files from main
`d3440994c7c8e50f775257438a0a45e33ffd95a2` plus the reviewed webhook preflight
added by this change. The current active workflow does not invoke that preflight:

| Protected path | Predecessor Git blob |
| --- | --- |
| `.github/workflows/azure-deploy.yml` | `622675bc1c131a11268e49a09efc0d3eb4a7fca6` |
| `azure.yaml` | `bcc11f088596a50c2c68b92d84553c84da3ceefd` |
| `scripts/check-coverage.py` | `fe7e478c3d3feca4505dc6f824153fbdf4eff6bf` |
| `scripts/test-resources.py` | `8b84f121c4cce94a51b50e71102cacf4bc79902b` |
| `scripts/check-webhook-configuration.py` | `51bdf9ae176531f9f9d02fff2949b6a2f918569c` |

The sole next workflow blob is
`7a2cefa03c4868c8b56fe8bfba241c15d8a5dbb3`. It starts from the receipt candidate workflow in local
commit `03563574da8ac3df7d9901704609ac6936bae1cb` and runs the preflight with
Python isolated mode (`-I`), so feature-local modules and `PYTHONPATH` cannot
replace its standard-library imports. This source reference is provenance, not
evidence of feature CI or deployment.

The preflight itself is protected on both the staged transition and every ordinary
exact-main comparison after activation. A feature cannot change or remove it while
retaining the authenticated workflow. Its tests use synthetic values; the helper
checks key-bundle format and does not establish that a provider endpoint or real
environment secret has been configured.

Any change to the trusted predecessor tuple expires the staged authorization.
Once the new workflow is installed on main, ordinary exact-main authentication
applies; this staged approval cannot later revive an older workflow. Changing this
review file alone does not authorize another revision: both its exact target pin
and complete predecessor pins in the main-owned controller must be reviewed.
Do not add wildcards, mutable refs, feature-provided manifests or skipped-test
exceptions. Remove obsolete staged material during subsequent pipeline maintenance.
