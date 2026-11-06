const { spawnSync } = require('node:child_process');

// Compare the merge tree with its first parent (main immediately before this merge).
// Git trees have no REST file-list cap; scoped diff also detects renames in either direction.
function contractsChanged(mergeSha, cwd = process.cwd()) {
  if (!/^[0-9a-f]{40}$/.test(mergeSha ?? '')) throw new Error('An immutable merged commit is required.');
  const result = spawnSync('git', ['diff', '--quiet', '--no-ext-diff', '--no-textconv',
    `${mergeSha}^`, mergeSha, '--', 'src/Niobium.Notification.Contracts/'], { cwd, encoding: 'utf8' });
  if (result.error) throw result.error;
  if (result.status === 0) return false;
  if (result.status === 1) return true;
  throw new Error('Cannot compare the merged Contracts tree with its first parent.');
}

module.exports = { contractsChanged };
