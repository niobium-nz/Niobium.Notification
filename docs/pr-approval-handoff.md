# PR review and approval handoff

The owner reaffirmed this process on 2026-11-12 (Pacific/Auckland) after the
implementation agent stopped at approval instead of invoking the agreed separate
reviewer. This procedure is stored with the repository so a missing workspace
`AGENTS.md`, resumed goal or another checkout cannot remove the handoff.

## Responsibilities

| Role | Responsibility |
| --- | --- |
| Implementation agent, normally `niobium-nz` | Implement, test, create/reuse the PR, monitor findings, invoke the approver, fix issues, merge after approval and verify production/package results |
| Native Codex background review | Automatically review every PR/push and publish code/security findings for the current head |
| Separate `pr_approver` agent | Independently inspect the change and evidence, then submit a formal GitHub review through the authorized reviewer identity |

For a PR authored by `niobium-nz`, the owner-authorized reviewer is `5he11`.
The approver must verify the authenticated connection and repository permission.
The implementation agent must not use that identity to submit its own verdict.
A GitHub reviewer request does not start the `pr_approver` agent; invoke the agent
explicitly. Native review completion, a comment or a reaction is not formal approval.

## Required handoff

1. Follow the repository's delivery guide: validate the exact feature commit,
   including actual test deployment and required integration suites, then create
   or reuse the ready PR and request the designated GitHub reviewer. Ordinary
   feature pushes, PR creation and deployment remain authorized.
2. Monitor native code/security review on the actual head and address actionable
   findings from all review submissions, inline threads and conversation comments.
   Do not replace native automatic review with the approval agent or a polling chat.
3. If a valid formal approval of the current head is already recorded, verify it
   and proceed; do not require an additional approval. Otherwise explicitly start
   or reuse a **separate `pr_approver` agent/session**. Supply the
   repository/PR URL, exact head and base SHAs, source location, current-head test
   run/artifact links, native receipts and any unresolved issues. Give it an
   independent review task, not a predetermined approval. A fresh review context
   is preferred; the reviewer must inspect the actual change and evidence itself.
4. The approver checks correctness, scope, tests, native/human findings and the
   final diff. It may begin reading while CI runs, but must not approve until the
   required current-head verification and review evidence are complete. Submit
   `APPROVE` only if warranted, or `REQUEST_CHANGES` with concrete findings. Bind
   the review to the exact commit, then verify its recorded author, state and SHA.
   The approver does not edit implementation, fabricate status or merge the PR.
5. The implementation agent handles findings and repeats tests/native review and
   the approval handoff after a changed head. A prior approval or a reviewer task's
   text summary cannot substitute for GitHub's formal review of the current commit.
6. With the formal approval recorded, check all repository protections, resolved
   threads, current main ancestry and equality of the tested/proposed merge trees.
   Merge using the expected head SHA without asking for another permission.
   Monitor production deployment, read-only verification and package workflows
   through terminal results. Investigate failures through the same feature process.

This is genuine independent approval, not delegation of a rubber stamp. It does
not weaken branch protection, allow self-approval, enable Actions to approve PRs,
or authorize production data writes/migrations. Keep implementation and review
decisions in their separate sessions.

## Continuation and blockers

Awaiting native review, an active approver, review fixes or deployment completion
is **work in progress**, not a completed task or a reason to mark the goal blocked.
Keep the implementation session responsible for the next action and continue
independent authorized work when available. Do not stop at "reviewer requested"
or "ready for approval", and do not ask the owner to repeat the standing authority.

Before reporting an approval blocker, verify that the separate handoff was actually
attempted and record its concrete failure. Examples are an unavailable authorized
reviewer connection, insufficient GitHub permission, or an explicit reviewer issue
requiring a missing business decision. Report that specific dependency and the
attempted recovery. Do not call pending review itself an external blocker or create
an approval merely to end the wait. Preserve the PR/head, reviewer task/result,
native receipts, test evidence and next action in the handoff or PR description.
