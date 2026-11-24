// Privileged entry point: only the controller loaded from the default branch may publish readiness status.
const WORKFLOW = 'azure-deploy.yml';
const WORKFLOW_PATH = `.github/workflows/${WORKFLOW}`;
const PIPELINE_FILES = [WORKFLOW_PATH, 'azure.yaml', 'scripts/check-coverage.py', 'scripts/test-resources.py',
  'scripts/check-webhook-configuration.py'];
// A single concrete next revision, reviewed as data on main before a feature can
// use it. This is deliberately bound to the complete predecessor pipeline: an
// unrelated future pipeline revision must never revive an old staged approval.
const REVIEWED_WORKFLOW_PATH = '.github/reviewed-pipelines/azure-deploy.yml';
const REVIEWED_WORKFLOW_SHA = '7a2cefa03c4868c8b56fe8bfba241c15d8a5dbb3';
const REVIEWED_PIPELINE_BASE = Object.freeze({
  [WORKFLOW_PATH]: '622675bc1c131a11268e49a09efc0d3eb4a7fca6',
  'azure.yaml': 'bcc11f088596a50c2c68b92d84553c84da3ceefd',
  'scripts/check-coverage.py': 'fe7e478c3d3feca4505dc6f824153fbdf4eff6bf',
  'scripts/test-resources.py': '8b84f121c4cce94a51b50e71102cacf4bc79902b',
  'scripts/check-webhook-configuration.py': '51bdf9ae176531f9f9d02fff2949b6a2f918569c',
});
const REQUIRED_JOBS = ['Build and local tests', 'Deploy test and run integration'];
const REQUIRED_STEP = 'Run deployed integration tests';
const TRUSTED_TEST_JOB = 'Verify trusted deployed integration';
const ACTIVE_RUN_STATES = new Set(['queued', 'in_progress', 'waiting', 'pending', 'requested']);

class VerificationPending extends Error {
  constructor() { super('The current feature verification has not completed.'); }
}

function featureRun(run, sha, repository) {
  return run.head_sha === sha && run.event === 'push' && run.head_branch !== 'main' &&
    run.head_repository?.full_name === repository && run.path === WORKFLOW_PATH;
}

function eligibleRun(run, sha, repository) {
  return featureRun(run, sha, repository) && run.status === 'completed' && run.conclusion === 'success';
}

function approvedHead(reviews, head) {
  const decisions = new Map();
  for (const review of [...reviews].sort((a, b) => a.id - b.id)) {
    if (['APPROVED', 'CHANGES_REQUESTED', 'DISMISSED'].includes(review.state)) decisions.set(review.user.login, review);
  }
  const latest = [...decisions.values()];
  return !latest.some(review => review.state === 'CHANGES_REQUESTED') &&
    latest.some(review => review.state === 'APPROVED' && review.commit_id === head);
}

async function authenticatePipeline(github, owner, repo, featureSha, trustedSha) {
  if (!/^[0-9a-f]{40}$/.test(trustedSha ?? '')) throw new Error('An immutable trusted main commit is required.');
  const observed = new Map();
  for (const path of PIPELINE_FILES) {
    // Read Git blob identities through the API. Never download or execute feature policy code.
    const [feature, trusted] = await Promise.all([
      github.rest.repos.getContent({ owner, repo, path, ref: featureSha }),
      github.rest.repos.getContent({ owner, repo, path, ref: trustedSha }),
    ]);
    if (feature.data.type !== 'file' || trusted.data.type !== 'file' || !feature.data.sha || !trusted.data.sha) {
      throw new Error(`Untrusted pipeline definition: ${path}. Pipeline changes require reviewed activation on main before automatic certification.`);
    }
    observed.set(path, { feature: feature.data.sha, trusted: trusted.data.sha });
  }
  const changed = PIPELINE_FILES.filter(path => observed.get(path).feature !== observed.get(path).trusted);
  if (changed.length === 0) return;
  const predecessorMatches = PIPELINE_FILES.every(path => observed.get(path).trusted === REVIEWED_PIPELINE_BASE[path]);
  if (changed.length !== 1 || changed[0] !== WORKFLOW_PATH || !predecessorMatches ||
      observed.get(WORKFLOW_PATH).feature !== REVIEWED_WORKFLOW_SHA) {
    throw new Error('Untrusted pipeline definition: no exact reviewed revision for this main pipeline.');
  }
  // Load only metadata of the complete reviewed definition at immutable main.
  // Never accept a staged file from the feature, execute it here or borrow it
  // from mutable main. Missing/malformed evidence cannot bootstrap itself.
  let staged;
  try {
    staged = await github.rest.repos.getContent({ owner, repo, path: REVIEWED_WORKFLOW_PATH, ref: trustedSha });
  } catch (error) {
    if (error.status !== 404) throw error;
    throw new Error('Untrusted pipeline definition: reviewed main workflow is missing.');
  }
  if (staged.data.type !== 'file' || staged.data.sha !== REVIEWED_WORKFLOW_SHA) {
    throw new Error('Untrusted pipeline definition: reviewed main workflow does not match its pinned revision.');
  }
}

async function testedRun(github, owner, repo, sha, trustedSha, runId, allowPending = false) {
  const runs = await github.paginate(github.rest.actions.listWorkflowRuns, {
    owner, repo, workflow_id: WORKFLOW, event: 'push', head_sha: sha, per_page: 100,
  });
  // A failed triggering run must not borrow success from an older run of the same SHA.
  const candidates = runId ? runs.filter(run => run.id === runId) :
    [...runs].sort((a, b) => Date.parse(b.updated_at) - Date.parse(a.updated_at)).slice(0, 1);
  // A PR synchronization may arrive before its push run is visible. Authenticate
  // the pipeline even while waiting; missing or changed trust is still failure.
  if (allowPending && !runId && candidates.length === 0) {
    await authenticatePipeline(github, owner, repo, sha, trustedSha);
    throw new VerificationPending();
  }
  for (const run of candidates) {
    if (!featureRun(run, sha, `${owner}/${repo}`)) continue;
    await authenticatePipeline(github, owner, repo, sha, trustedSha);
    if (allowPending && !runId && run.conclusion === null && ACTIVE_RUN_STATES.has(run.status)) {
      throw new VerificationPending();
    }
    if (!eligibleRun(run, sha, `${owner}/${repo}`)) continue;
    const jobs = await github.paginate(github.rest.actions.listJobsForWorkflowRun, {
      owner, repo, run_id: run.id, filter: 'latest', per_page: 100,
    });
    if (!REQUIRED_JOBS.every(name => jobs.some(job => job.name === name && job.conclusion === 'success'))) continue;
    const deployment = jobs.find(job => job.name === REQUIRED_JOBS[1]);
    if (!deployment.steps?.some(step => step.name === REQUIRED_STEP && step.conclusion === 'success')) continue;
    // A separate runner checks out only immutable main, including the harness, project references,
    // build configuration and read-only client. Feature test code is never certification evidence.
    const trustedTests = jobs.find(job => job.name === TRUSTED_TEST_JOB && job.conclusion === 'success');
    if (!trustedTests?.steps?.some(step => step.name === `Run trusted deployed integration tests at ${trustedSha}` &&
        step.conclusion === 'success')) continue;
    return run;
  }
  throw new Error('This feature commit has no successful authenticated build, test deployment and deployed integration run.');
}

// This is a pre-merge agent check, never a gate between a merged PR and production.
async function mergeEligibility({ github, context, pullNumber }) {
  const { owner, repo } = context.repo;
  const { data: pr } = await github.rest.pulls.get({ owner, repo, pull_number: pullNumber });
  if (pr.state !== 'open' || pr.draft || pr.base.ref !== 'main' || pr.head.repo?.full_name !== `${owner}/${repo}`) {
    throw new Error('Merge requires an open, ready, repository-owned PR to main.');
  }
  const reviews = await github.paginate(github.rest.pulls.listReviews, { owner, repo, pull_number: pullNumber, per_page: 100 });
  if (!approvedHead(reviews, pr.head.sha)) throw new Error('Approval of the current PR head is required.');
  const { data: main } = await github.rest.repos.getBranch({ owner, repo, branch: 'main' });
  const run = await testedRun(github, owner, repo, pr.head.sha, main.commit.sha);
  const { data: comparison } = await github.rest.repos.compareCommits({ owner, repo, base: main.commit.sha, head: pr.head.sha });
  if (!['ahead', 'identical'].includes(comparison.status)) throw new Error('Update the feature branch from main and repeat deployment/tests.');
  return { pr, run };
}

async function readiness({ github, context, core, trustedSha, workflowRef }) {
  const { owner, repo } = context.repo;
  const mainRef = 'refs/heads/main';
  if (!['workflow_run', 'pull_request_target'].includes(context.eventName) || context.ref !== mainRef ||
      context.sha !== trustedSha || !/^[0-9a-f]{40}$/.test(trustedSha ?? '') ||
      workflowRef !== `${owner}/${repo}/.github/workflows/pr-readiness.yml@${mainRef}`) {
    throw new Error('Repository writes require the controller defined by the immutable main workflow commit.');
  }
  const pr = context.payload.pull_request;
  const eventRun = context.payload.workflow_run;
  if (pr && pr.head.repo?.full_name !== `${owner}/${repo}`) return;
  if (eventRun && (eventRun.head_repository?.full_name !== `${owner}/${repo}` || eventRun.event !== 'push' || eventRun.head_branch === 'main')) return;
  const sha = pr?.head.sha ?? eventRun?.head_sha;
  if (!sha) throw new Error('Missing feature commit.');
  let run;
  try {
    run = await testedRun(github, owner, repo, sha, trustedSha, eventRun?.id,
      context.eventName === 'pull_request_target' && !!pr && !eventRun);
  } catch (error) {
    if (error instanceof VerificationPending) {
      await github.rest.repos.createCommitStatus({ owner, repo, sha, state: 'pending', context: 'Test deployment verified',
        description: 'Waiting for this commit to finish authenticated feature verification.' });
      core.notice(error.message);
      return;
    }
    await github.rest.repos.createCommitStatus({ owner, repo, sha, state: 'failure', context: 'Test deployment verified',
      description: 'A successful run with the trusted main pipeline definition is required.' });
    throw error;
  }
  await github.rest.repos.createCommitStatus({ owner, repo, sha, state: 'success', context: 'Test deployment verified',
    description: 'This commit passed the authenticated deployed integration workflow.', target_url: run.html_url });
  core.notice(`Feature commit is eligible for review: ${run.html_url}`);
}

module.exports = { mergeEligibility, readiness, testedRun, eligibleRun, approvedHead, authenticatePipeline, PIPELINE_FILES,
  REVIEWED_WORKFLOW_PATH, REVIEWED_WORKFLOW_SHA, REVIEWED_PIPELINE_BASE };
