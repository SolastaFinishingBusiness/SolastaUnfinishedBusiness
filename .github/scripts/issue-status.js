// Keeps the issue status labels in step with pull requests and releases (user direction, 2026-11-15: "Add forms and
// status bot. However, ensure that when new issues are created, they also have something like a "not-started" tag").
//
// Run by actions/github-script from two workflows:
// - issue-status.yml, on a new issue and on pull request events for `develop`;
// - dotnet.yml, as the last step of a version tag's run, after the release is published (a release created with the
//   workflow's own token starts no other workflow, so a separate `release` trigger would never fire).
//
// The status labels, at most one per issue, in the order work moves through them:
//   not-started -> in progress -> pending approval -> pending release -> released
// - not-started: a new issue nobody has worked on yet;
// - in progress: an open draft PR refers to it;
// - pending approval: an open PR refers to it and is ready for review;
// - pending release: a PR referring to it was merged into `develop`;
// - released: the release containing every merged PR that refers to it is published.
// An issue worked on in several PRs takes the label of the part furthest behind: an open draft PR keeps it in
// progress even when another PR for it was merged.
//
// A PR refers to an issue with a line such as `Issue: #42` (or `Issues: #42, #43`) in its description, or with the
// issue numbers in brackets at the end of its title, e.g. "Make settings guard what they say (#66)". `Closes #N` is
// deliberately not used: GitHub would close the issue at the `develop` merge.
//
// Sub-issues are closed as completed at the merge, keeping `pending release`; other issues stay open until the release
// (project rules, 2026-11-15). A parent issue with open sub-issues gets no status label. Closed issues are left alone
// by pull request events. Comments are never posted: the release comments stay with the agent doing the release, and
// the release step lists the issues it relabelled in the run's summary for that.
//
// Plain JavaScript because actions/github-script runs scripts as they are, with no build step.

'use strict';

const DEVELOPMENT_BRANCH = 'develop';
const STATUSES = ['not-started', 'in progress', 'pending approval', 'pending release', 'released'];
// Lower is further behind; used to pick the furthest-behind part of an issue worked on in several PRs.
const RANK = { 'in progress': 1, 'pending approval': 2, 'pending release': 3 };

/** The label names of an issue or pull request, whether GitHub sent them as objects or strings. */
function labelNames(issue) {
  return (issue.labels || []).map((label) => (typeof label === 'string' ? label : label.name));
}

/**
 * The issue numbers a pull request refers to: every `#N` on an `Issue:` / `Issues:` line of its description (bold,
 * quoted or listed lines included), and the `#N` in brackets at the very end of its title. The PR's own number is left
 * out.
 */
function issueRefs(pr) {
  const numbers = new Set();
  const body = pr.body || '';
  for (const line of body.matchAll(/^[\s>*_-]*Issues?[*_]*\s*:\s*(.+)$/gim)) {
    for (const ref of line[1].matchAll(/#(\d+)/g)) numbers.add(Number(ref[1]));
  }
  const title = (pr.title || '').match(/\(\s*(#\d+(?:\s*,\s*#\d+)*)\s*\)\s*$/);
  if (title) {
    for (const ref of title[1].matchAll(/#(\d+)/g)) numbers.add(Number(ref[1]));
  }
  numbers.delete(pr.number);
  return [...numbers].sort((a, b) => a - b);
}

/** The status an issue gets from the open PRs that refer to it: the furthest behind, or null if none does. */
function statusFromOpenPrs(number, openPrs) {
  let best = null;
  for (const pr of openPrs) {
    if (!issueRefs(pr).includes(number)) continue;
    const status = pr.draft ? 'in progress' : 'pending approval';
    if (best === null || RANK[status] < RANK[best]) best = status;
  }
  return best;
}

module.exports = async function run({ github, context, core }) {
  const { owner, repo } = context.repo;

  /** The issue, or null if it doesn't exist (a typo in a PR's `Issue:` line). */
  async function getIssue(number) {
    try {
      return (await github.rest.issues.get({ owner, repo, issue_number: number })).data;
    } catch (error) {
      if (error.status === 404 || error.status === 410) return null;
      throw error;
    }
  }

  /** Gives the issue exactly one status label (or none, for `status` null), removing the others it has. */
  async function setStatus(issue, status) {
    const names = labelNames(issue);
    for (const label of STATUSES) {
      if (label !== status && names.includes(label)) {
        await github.rest.issues.removeLabel({ owner, repo, issue_number: issue.number, name: label });
      }
    }
    if (status && !names.includes(status)) {
      await github.rest.issues.addLabels({ owner, repo, issue_number: issue.number, labels: [status] });
    }
    core.info(`#${issue.number}: ${status || 'no status label'}`);
  }

  /** The number of the issue's parent, or null if it isn't a sub-issue. */
  async function parentOf(number) {
    const result = await github.graphql(
      `query($owner: String!, $repo: String!, $number: Int!) {
        repository(owner: $owner, name: $repo) { issue(number: $number) { parent { number } } }
      }`,
      { owner, repo, number, headers: { 'GraphQL-Features': 'sub_issues' } },
    );
    const parent = result.repository && result.repository.issue && result.repository.issue.parent;
    return parent ? parent.number : null;
  }

  // The PRs merged into `develop` (the 300 most recently updated closed ones, far more than one release holds), read
  // once per run and only when needed.
  let mergedPrs = null;
  async function getMergedPrs() {
    if (mergedPrs === null) {
      const closed = await github.paginate(
        github.rest.pulls.list,
        { owner, repo, state: 'closed', base: DEVELOPMENT_BRANCH, sort: 'updated', direction: 'desc', per_page: 100 },
        (response, done) => {
          if (response.data.length < 100 || response.url.includes('page=3')) done();
          return response.data;
        },
      );
      mergedPrs = closed.filter((pr) => pr.merged_at && pr.merge_commit_sha);
    }
    return mergedPrs;
  }

  /** True when a PR already merged into `develop` refers to the issue. */
  async function hasMergedPr(number) {
    return (await getMergedPrs()).some((pr) => issueRefs(pr).includes(number));
  }

  /** A parent whose sub-issues aren't all done carries no status label (project rules, 2026-11-15). */
  function hasOpenSubIssues(issue) {
    const summary = issue.sub_issues_summary;
    return Boolean(summary && summary.total > summary.completed);
  }

  /**
   * Sets an open issue's status after a PR event. `merged` is true when the PR that was just merged into `develop`
   * refers to it.
   */
  async function syncIssue(number, openPrs, merged) {
    const issue = await getIssue(number);
    if (!issue || issue.pull_request) return;
    if (issue.state !== 'open') {
      core.info(`#${number}: closed, left as it is`);
      return;
    }
    if (hasOpenSubIssues(issue)) {
      core.info(`#${number}: a parent with open sub-issues, left as it is`);
      return;
    }

    const fromOpenPrs = statusFromOpenPrs(number, openPrs);
    let status;
    if (fromOpenPrs) {
      status = fromOpenPrs;
    } else if (merged || labelNames(issue).includes('pending release') || (await hasMergedPr(number))) {
      // Merged now, or one of its PRs was merged earlier (while another was still open) and the last open one just
      // closed or stopped referring to it.
      status = 'pending release';
    } else {
      // Its PR was closed without merging, or no longer refers to it: it was worked on, so not `not-started` either.
      status = null;
    }
    await setStatus(issue, status);

    // A sub-issue is closed once all its work is merged, whichever event shows that last.
    if (status === 'pending release' && (await parentOf(number)) !== null) {
      await github.rest.issues.update({
        owner,
        repo,
        issue_number: number,
        state: 'closed',
        state_reason: 'completed',
      });
      core.info(`#${number}: a sub-issue, closed at the merge`);
    }
  }

  /** A new issue starts as `not-started` unless whoever opened it already gave it a status. */
  async function onIssueOpened() {
    const issue = context.payload.issue;
    if (labelNames(issue).some((label) => STATUSES.includes(label))) return;
    await github.rest.issues.addLabels({ owner, repo, issue_number: issue.number, labels: ['not-started'] });
    core.info(`#${issue.number}: not-started`);
  }

  /** A PR into `develop` was opened, edited, marked ready or draft, reopened, closed or merged. */
  async function onPullRequest() {
    const pr = context.payload.pull_request;
    if (pr.base.ref !== DEVELOPMENT_BRANCH) return;

    const refs = issueRefs(pr);
    const all = new Set(refs);
    // An edit can drop a reference: the issue it no longer refers to is updated too.
    const changes = context.payload.changes || {};
    if (context.payload.action === 'edited' && (changes.body || changes.title)) {
      const before = {
        number: pr.number,
        title: changes.title ? changes.title.from : pr.title,
        body: changes.body ? changes.body.from : pr.body,
      };
      for (const number of issueRefs(before)) all.add(number);
    }
    if (all.size === 0) {
      core.info(`PR #${pr.number} refers to no issue`);
      return;
    }

    const openPrs = await github.paginate(github.rest.pulls.list, {
      owner,
      repo,
      state: 'open',
      base: DEVELOPMENT_BRANCH,
      per_page: 100,
    });
    const merged = context.payload.action === 'closed' && pr.merged === true;
    for (const number of [...all].sort((a, b) => a - b)) {
      await syncIssue(number, openPrs, merged && refs.includes(number));
    }
  }

  /**
   * After a release is published from a version tag: every issue labelled `pending release` whose merged PRs are all
   * in the tag becomes `released`, and is closed if still open. The run's summary lists them for the release comments,
   * and the ones no merged PR refers to, to check by hand.
   */
  async function onRelease(tag) {
    const pending = (
      await github.paginate(github.rest.issues.listForRepo, {
        owner,
        repo,
        labels: 'pending release',
        state: 'all',
        per_page: 100,
      })
    ).filter((issue) => !issue.pull_request);
    if (pending.length === 0) {
      core.info('No issue is labelled pending release');
      return;
    }

    // The merge commits of the PRs merged into `develop`, by the issue they refer to.
    const mergesByIssue = new Map();
    for (const pr of await getMergedPrs()) {
      for (const number of issueRefs(pr)) {
        if (!mergesByIssue.has(number)) mergesByIssue.set(number, []);
        mergesByIssue.get(number).push(pr.merge_commit_sha);
      }
    }

    /** True when the commit is the tag's commit or one of its ancestors. */
    async function inTag(sha) {
      const comparison = await github.rest.repos.compareCommitsWithBasehead({
        owner,
        repo,
        basehead: `${sha}...${tag}`,
        per_page: 1,
      });
      return comparison.data.status === 'ahead' || comparison.data.status === 'identical';
    }

    const released = [];
    const unmatched = [];
    for (const issue of pending) {
      const merges = mergesByIssue.get(issue.number) || [];
      if (merges.length === 0) {
        unmatched.push(issue);
        continue;
      }
      let all = true;
      for (const sha of merges) {
        if (!(await inTag(sha))) {
          all = false;
          break;
        }
      }
      if (!all) {
        core.info(`#${issue.number}: a merged PR isn't in ${tag}, left as pending release`);
        continue;
      }
      await setStatus(issue, 'released');
      if (issue.state === 'open') {
        await github.rest.issues.update({
          owner,
          repo,
          issue_number: issue.number,
          state: 'closed',
          state_reason: 'completed',
        });
      }
      released.push(issue);
    }

    const row = (issue) => [
      `#${issue.number}`,
      issue.title,
      labelNames(issue).includes('upstream') ? 'yes (needs the @-mention)' : '',
    ];
    core.summary.addHeading(`Issues released in ${tag}`, 2);
    if (released.length > 0) {
      core.summary.addRaw('Relabelled `released` and closed. Each still needs its release comment.', true);
      core.summary.addTable([
        [
          { data: 'Issue', header: true },
          { data: 'Title', header: true },
          { data: 'Upstream', header: true },
        ],
        ...released.map(row),
      ]);
    } else {
      core.summary.addRaw('None relabelled.', true);
    }
    if (unmatched.length > 0) {
      core.summary.addHeading('Labelled pending release, but no merged PR refers to them: check by hand', 3);
      core.summary.addList(unmatched.map((issue) => `#${issue.number} ${issue.title}`));
    }
    await core.summary.write();
  }

  if (context.eventName === 'issues' && context.payload.action === 'opened') {
    await onIssueOpened();
  } else if (context.eventName === 'pull_request_target' || context.eventName === 'pull_request') {
    await onPullRequest();
  } else if (context.eventName === 'push' && context.ref.startsWith('refs/tags/')) {
    await onRelease(context.ref.slice('refs/tags/'.length));
  } else {
    core.info(`Nothing to do for ${context.eventName}`);
  }
};

module.exports.issueRefs = issueRefs;
module.exports.statusFromOpenPrs = statusFromOpenPrs;
