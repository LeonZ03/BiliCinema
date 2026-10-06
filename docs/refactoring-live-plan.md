# BiliCinema Release And Verification Policy

This document owns stable release, verification, completion and rollback
policy. It is not a current-work database. Do not record an active item, next
item, branch, commit SHA, CI state, progress checklist or completed history
here.

Owner-requested work that may survive the current Codex context is bookmarked
in GitHub Issue [#137](https://github.com/crazysmile-PhD/downkyicore/issues/137).
Each bookmark points to its existing PR or task-specific detail source. Product
PRs do not update this file merely because their work state changed.

## Release Policy

- Preserve true semantic dependencies and recheck a downstream change after its
  prerequisite changes; stale exact-head evidence does not transfer to a new
  base. Separate root causes may remain separate commits or review evidence,
  but do not grow an unmerged release stack beyond roughly two or three layers
  or material divergence from `main`. Consolidate accepted semantics onto one
  clean current-main integration branch and validate that exact head.
- BiliCinema ships one Windows x64 executable. Publish only from one clean
  final commit after strict quality, CodeQL, the Windows test selection and
  single-executable package validation pass for that exact commit. The inherited
  Linux/macOS package formats are not BiliCinema release artifacts; shared-code
  platform tests remain in the pull-request quality workflow.
- Preserve settings JSON, legacy SQLite, unfinished tasks, GID, partial-file
  maps, completed keys and resume fixtures unless an approved migration with
  rollback evidence explicitly changes them.
- Source and packages must not contain Cookie values, account data, local
  Config/Logs/Cache/Storage or developer artifacts.
- Existing tags and release assets are immutable. `version.txt` identifies a
  candidate; the release workflow creates its `bilicinema-v` tag and publishes
  assets only after all required gates pass. Do not publish locally around a
  failed gate or replace an existing release.

## Verification

The canonical commands, order and rollback procedure live in
`docs/operations/verification-and-rollback.md`. Run them sequentially in one
worktree; this policy intentionally does not duplicate the command list.

A result is valid only when its runtime, OS, architecture, exact commit and
dirty-worktree state are recorded. Cross-machine timings are not compared
directly.

## Completion And Rollback

Work is complete only after implementation, focused regressions, required
documentation, exact-head CI and review are green. Remove its bookmark from
#137; stable facts go to architecture, maintenance or release documentation.
Do not add a completed section to the workboard or this policy.

Before merge, rollback means closing the draft and deleting only the feature
branch. After merge, revert the complete change range without modifying user
data formats or reintroducing a security bypass. A migration requires its own
backup, rollback and reopen evidence.
