// Privileged entry point: only the controller loaded from the default branch may publish readiness status.
const WORKFLOW = 'azure-deploy.yml';
const WORKFLOW_PATH = `.github/workflows/${WORKFLOW}`;
const PIPELINE_FILES = [WORKFLOW_PATH, 'azure.yaml', 'scripts/check-coverage.py', 'scripts/test-resources.py'];
const REQUIRED_JOBS = ['Build and local tests', 'Deploy test and run integration'];
const REQUIRED_STEP = 'Run deployed integration tests';
const TRUSTED_TEST_JOB = 'Verify trusted deployed integration';

function eligibleRun(run, sha, repository) {
  return run.head_sha === sha && run.event === 'push' && run.head_branch !== 'main' &&
    run.conclusion === 'success' && run.head_repository?.full_name === repository && run.path === WORKFLOW_PATH;
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
  for (const path of PIPELINE_FILES) {
    // Read Git blob identities through the API. Never download or execute feature policy code.
    const [feature, trusted] = await Promise.all([
      github.rest.repos.getContent({ owner, repo, path, ref: featureSha }),
      github.rest.repos.getContent({ owner, repo, path, ref: trustedSha }),
    ]);
    if (feature.data.type !== 'file' || trusted.data.type !== 'file' ||
        !feature.data.sha || feature.data.sha !== trusted.data.sha) {
      throw new Error(`Untrusted pipeline definition: ${path}. Pipeline changes require reviewed activation on main before automatic certification.`);
    }
  }
}

async function testedRun(github, owner, repo, sha, trustedSha, runId) {
  const runs = await github.paginate(github.rest.actions.listWorkflowRuns, {
    owner, repo, workflow_id: WORKFLOW, event: 'push', head_sha: sha, per_page: 100,
  });
  // A failed triggering run must not borrow success from an older run of the same SHA.
  const candidates = runId ? runs.filter(run => run.id === runId) :
    [...runs].sort((a, b) => Date.parse(b.updated_at) - Date.parse(a.updated_at)).slice(0, 1);
  for (const run of candidates) {
    if (!eligibleRun(run, sha, `${owner}/${repo}`)) continue;
    await authenticatePipeline(github, owner, repo, sha, trustedSha);
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
    run = await testedRun(github, owner, repo, sha, trustedSha, eventRun?.id);
  } catch (error) {
    await github.rest.repos.createCommitStatus({ owner, repo, sha, state: 'failure', context: 'Test deployment verified',
      description: 'A successful run with the trusted main pipeline definition is required.' });
    throw error;
  }
  await github.rest.repos.createCommitStatus({ owner, repo, sha, state: 'success', context: 'Test deployment verified',
    description: 'This commit passed the authenticated deployed integration workflow.', target_url: run.html_url });
  core.notice(`Feature commit is eligible for review: ${run.html_url}`);
}

module.exports = { mergeEligibility, readiness, testedRun, eligibleRun, approvedHead, authenticatePipeline, PIPELINE_FILES };
