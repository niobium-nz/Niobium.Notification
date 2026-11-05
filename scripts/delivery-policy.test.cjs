const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { execFileSync } = require('node:child_process');
const { approvedHead, eligibleRun, testedRun, mergeEligibility, readiness, PIPELINE_FILES } = require('./delivery-policy.cjs');
const { contractsChanged } = require('./package-policy.cjs');
const trustedSha = 'a'.repeat(40);
const run = { id: 42, head_sha: 'head', event: 'push', head_branch: 'feature/test', conclusion: 'success',
  head_repository: { full_name: 'owner/repo' }, path: '.github/workflows/azure-deploy.yml',
  updated_at: '2026-10-30T12:00:00Z', html_url: 'https://github.com/owner/repo/actions/runs/42' };

function fixture() {
  const state = {
    pr: { number: 7, state: 'open', draft: false, base: { ref: 'main' }, head: { sha: 'head', ref: 'feature/test', repo: { full_name: 'owner/repo' } }, user: { login: 'author' } },
    reviews: [{ id: 1, state: 'APPROVED', commit_id: 'head', user: { login: 'author' } }],
    runs: [{ ...run }],
    jobs: [{ name: 'Build and local tests', conclusion: 'success' },
      { name: 'Deploy test and run integration', conclusion: 'success', steps: [{ name: 'Run deployed integration tests', conclusion: 'success' }] },
      { name: 'Verify trusted deployed integration', conclusion: 'success',
        steps: [{ name: `Run trusted deployed integration tests at ${trustedSha}`, conclusion: 'success' }] }],
    comparison: { status: 'ahead' },
    created: [], statuses: [], notices: [], contentReads: [], tamperedPath: null, missingTrustedPath: null,
  };
  const github = {
    paginate: async (method, args) => { if (method === 'runs') assert.equal(args.head_sha, 'head'); return state[method]; },
    rest: {
      actions: { listWorkflowRuns: 'runs', listJobsForWorkflowRun: 'jobs' },
      pulls: { get: async () => ({ data: state.pr }), listReviews: 'reviews',
        create: async args => { state.created.push(args); throw new Error('Readiness must never create a PR.'); } },
      repos: {
        getBranch: async () => ({ data: { commit: { sha: trustedSha } } }),
        getContent: async args => {
          state.contentReads.push(args);
          assert.ok(['head', trustedSha].includes(args.ref));
          if (args.ref === trustedSha && args.path === state.missingTrustedPath) throw new Error('Missing trusted file');
          return { data: { type: 'file', sha: args.ref === 'head' && args.path === state.tamperedPath ? 'no-op-pipeline' : `blob:${args.path}` } };
        },
        compareCommits: async () => ({ data: state.comparison }), createCommitStatus: async status => state.statuses.push(status),
      },
    },
  };
  return { state, github, pullNumber: 7, trustedSha,
    workflowRef: 'owner/repo/.github/workflows/pr-readiness.yml@refs/heads/main',
    context: { repo: { owner: 'owner', repo: 'repo' }, sha: trustedSha, ref: 'refs/heads/main', eventName: 'workflow_run', payload: { workflow_run: { ...run } } },
    core: { notice: message => state.notices.push(message) } };
}

function packageRepository(t) {
  const cwd = fs.mkdtempSync(path.join(os.tmpdir(), 'invoicing-package-'));
  t.after(() => fs.rmSync(cwd, { recursive: true, force: true }));
  const git = (...args) => execFileSync('git', args, { cwd, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();
  git('init', '--quiet'); git('config', 'user.name', 'Package test'); git('config', 'user.email', 'test@example.invalid');
  const write = (file, content = 'example\n') => {
    fs.mkdirSync(path.dirname(path.join(cwd, file)), { recursive: true });
    fs.writeFileSync(path.join(cwd, file), content);
  };
  const commit = () => { git('add', '.'); git('-c', 'core.hooksPath=/dev/null', 'commit', '--quiet', '--no-gpg-sign', '-m', 'Test tree'); return git('rev-parse', 'HEAD'); };
  write('README.md'); commit();
  return { cwd, git, write, commit };
}

test('package tree comparison includes additions, deletions and renames in either direction', t => {
  const repo = packageRepository(t);
  const contract = 'src/Niobium.Notification.Contracts/Invoice.cs';
  repo.write(contract); assert.equal(contractsChanged(repo.commit(), repo.cwd), true);
  repo.git('mv', contract, 'Invoice.cs'); assert.equal(contractsChanged(repo.commit(), repo.cwd), true);
  repo.git('mv', 'Invoice.cs', contract); assert.equal(contractsChanged(repo.commit(), repo.cwd), true);
  repo.git('rm', contract); assert.equal(contractsChanged(repo.commit(), repo.cwd), true);
});

test('unrelated tree changes do not publish Contracts', t => {
  const repo = packageRepository(t);
  repo.write('docs/product.md'); repo.write('src/Niobium.Notification.Contracts.Tests/InvoiceTests.cs');
  assert.equal(contractsChanged(repo.commit(), repo.cwd), false);
});

test('Contracts detection is complete beyond the 3000-file REST cap', t => {
  const repo = packageRepository(t);
  for (let i = 0; i < 3001; i++) repo.write(`docs/change-${i}.md`);
  repo.write('src/Niobium.Notification.Contracts/Invoice.cs');
  assert.equal(contractsChanged(repo.commit(), repo.cwd), true);
});

test('missing merge history and invalid revisions fail instead of skipping publication', t => {
  const repo = packageRepository(t);
  assert.throws(() => contractsChanged(repo.git('rev-parse', 'HEAD'), repo.cwd), /Cannot compare/);
  assert.throws(() => contractsChanged('b'.repeat(40), repo.cwd), /Cannot compare/);
  for (const value of [undefined, 'main', '--quiet']) assert.throws(() => contractsChanged(value, repo.cwd), /immutable merged commit/);
});

test('only a successful exact-repository feature run of the expected workflow qualifies', () => {
  assert.equal(eligibleRun(run, 'head', 'owner/repo'), true);
  for (const patch of [{ head_sha: 'old' }, { event: 'pull_request' }, { head_branch: 'main' }, { conclusion: 'skipped' },
    { head_repository: { full_name: 'fork/repo' } }, { path: '.github/workflows/forged.yml' }]) {
    assert.equal(eligibleRun({ ...run, ...patch }, 'head', 'owner/repo'), false);
  }
});

test('one permitted approval suffices, with no restriction on the PR author', () => {
  const approval = fixture().state.reviews[0];
  assert.equal(approvedHead([approval], 'head'), true);
  assert.equal(approvedHead([{ ...approval, user: { login: 'other' } }], 'head'), true);
  assert.equal(approvedHead([approval], 'new-head'), false);
  assert.equal(approvedHead([approval, { ...approval, id: 2, state: 'DISMISSED' }], 'head'), false);
  assert.equal(approvedHead([approval, { ...approval, id: 2, state: 'CHANGES_REQUESTED', user: { login: 'other' } }], 'head'), false);
  assert.equal(approvedHead([approval, { ...approval, id: 2, state: 'COMMENTED' }], 'head'), true);
});

test('green job names cannot authenticate a modified workflow or pipeline helper', async () => {
  for (const path of PIPELINE_FILES) {
    const input = fixture(); input.state.tamperedPath = path;
    await assert.rejects(readiness(input), /Untrusted pipeline definition/);
    assert.equal(input.state.statuses.at(-1).state, 'failure');
    assert.equal(input.state.created.length, 0);
  }
});

test('a missing trusted file fails closed without bootstrapping from feature code', async () => {
  const input = fixture(); input.state.missingTrustedPath = PIPELINE_FILES[0];
  await assert.rejects(readiness(input), /Missing trusted file/);
  assert.deepEqual(input.state.statuses.map(status => status.state), ['failure']);
  assert.equal(input.state.created.length, 0);
});

test('pipeline proof pins both API reads to exact immutable commits', async () => {
  const input = fixture(); await readiness(input);
  assert.equal(input.state.contentReads.length, PIPELINE_FILES.length * 2);
  assert.ok(input.state.contentReads.every(read => ['head', trustedSha].includes(read.ref)));
});

test('feature contexts cannot invoke privileged repository writes', async () => {
  for (const mutate of [x => { x.context.eventName = 'push'; }, x => { x.context.ref = 'refs/heads/feature/test'; },
    x => { x.workflowRef = 'owner/repo/.github/workflows/pr-readiness.yml@refs/heads/feature/test'; },
    x => { x.context.sha = 'head'; }, x => { x.trustedSha = 'main'; }]) {
    const input = fixture(); mutate(input);
    await assert.rejects(readiness(input), /Repository writes require/);
    assert.equal(input.state.statuses.length, 0); assert.equal(input.state.created.length, 0);
  }
});

test('skipped deployed tests and failed required jobs cannot qualify', async () => {
  const skipped = fixture(); skipped.state.jobs[1].steps[0].conclusion = 'skipped';
  await assert.rejects(testedRun(skipped.github, 'owner', 'repo', 'head', trustedSha));
  for (const job of [0, 1, 2]) for (const conclusion of ['failure', 'cancelled', 'skipped', null]) {
    const input = fixture(); input.state.jobs[job].conclusion = conclusion;
    await assert.rejects(testedRun(input.github, 'owner', 'repo', 'head', trustedSha));
  }
});

test('green candidate tests cannot substitute for the trusted main harness', async () => {
  for (const mutate of [s => { s.jobs.pop(); }, s => { s.jobs[2].steps = []; },
    s => { s.jobs[2].steps[0].conclusion = 'skipped'; },
    s => { s.jobs[2].steps[0].name = 'Run trusted deployed integration tests at feature'; },
    s => { s.jobs[2].steps[0].name = `Run trusted deployed integration tests at ${'b'.repeat(40)}`; }]) {
    const input = fixture(); mutate(input.state);
    await assert.rejects(readiness(input), /no successful authenticated/);
    assert.deepEqual(input.state.statuses.map(status => status.state), ['failure']);
    assert.equal(input.state.created.length, 0);
  }
});

test('a failed triggering run cannot reuse an older successful run for the same head', async () => {
  const input = fixture(); input.state.runs[0].conclusion = 'failure';
  input.state.runs.push({ ...run, id: 41, updated_at: '2026-10-29T12:00:00Z' });
  await assert.rejects(readiness(input));
  assert.equal(input.state.statuses.at(-1).state, 'failure');
  await assert.rejects(testedRun(input.github, 'owner', 'repo', 'head', trustedSha));
});

test('pre-merge validation accepts an approved, authenticated current head', async () => {
  assert.equal((await mergeEligibility(fixture())).run.id, 42);
});

test('pre-merge validation rejects stale approval, missing proof, and a newer main', async () => {
  for (const mutate of [s => { s.reviews[0].commit_id = 'old'; }, s => { s.runs = []; }, s => { s.comparison.status = 'diverged'; }]) {
    const input = fixture(); mutate(input.state); await assert.rejects(mergeEligibility(input));
  }
});

test('pre-merge validation rejects closed, draft, foreign, and wrong-base PRs', async () => {
  for (const mutate of [s => { s.pr.state = 'closed'; }, s => { s.pr.draft = true; }, s => { s.pr.head.repo.full_name = 'fork/repo'; }, s => { s.pr.base.ref = 'other'; }]) {
    const input = fixture(); mutate(input.state); await assert.rejects(mergeEligibility(input));
  }
});

test('successful workflow completion publishes verification without creating a PR', async () => {
  const input = fixture(); await readiness(input);
  assert.equal(input.state.created.length, 0);
  assert.equal(input.state.statuses.length, 1);
  assert.equal(input.state.statuses[0].state, 'success');
  assert.equal(input.state.statuses[0].target_url, run.html_url);
  assert.equal(input.state.statuses[0].sha, 'head'); assert.equal(input.state.statuses[0].context, 'Test deployment verified');
});

test('workflow verification works with no pull-request or branch-management API access', async () => {
  const input = fixture(); delete input.github.rest.pulls;
  await readiness(input);
  assert.equal(input.state.statuses[0].state, 'success');
  assert.equal(input.state.created.length, 0);
});

test('PR events update readiness without opening another PR', async () => {
  const input = fixture(); input.context.eventName = 'pull_request_target';
  input.context.payload = { pull_request: input.state.pr }; await readiness(input);
  assert.equal(input.state.statuses[0].state, 'success'); assert.equal(input.state.created.length, 0);
});

test('untrusted repository and non-feature events cannot mint status', async () => {
  for (const payload of [
    { workflow_run: { ...run, head_repository: { full_name: 'fork/repo' } } },
    { workflow_run: { ...run, event: 'pull_request' } },
    { workflow_run: { ...run, head_branch: 'main' } },
    { pull_request: { head: { repo: { full_name: 'fork/repo' } } } },
  ]) {
    const input = fixture(); input.context.payload = payload; await readiness(input);
    assert.equal(input.state.statuses.length, 0); assert.equal(input.state.created.length, 0);
  }
});
