# Removing the paid code from the public repository's history

**Status: a recommendation for the owner to decide. Nothing here has been run, and nothing in the repository was rewritten or force-pushed.**

The module extraction (PRs #43 to #54) moves the paid modules' code out of the *tip* of this repository. It stays in every earlier commit, tag, pull request branch and fork. If the goal is that someone cloning the public repository cannot obtain the paid code, the tip is not enough.

## The honest limits first

* **The repository is public. Anything that was ever pushed has already been cloned or can be.** Forks, clones, archives (GitHub, Software Heritage, search-engine caches), CI caches and pull request refs keep the old commits. Rewriting history removes the code from *new* clones of *this* repository; it does not recall copies. If the code must be considered secret, that has already failed, and what history rewriting buys is "not trivially browsable from the default branch's log from now on", plus a clear line for the future.
* GitHub keeps unreachable commits reachable by SHA (and through any open or closed pull request's `refs/pull/*/head`) until GitHub Support runs garbage collection. A rewrite plus a request to GitHub Support to purge cached views and run GC is needed; even then the pull request refs of merged PRs stay unless the PRs are deleted or the repository is recreated.
* The licensing model does not depend on secrecy: the key check is cryptographic and the modules are sold as supported, updated builds with a license. Treat history cleaning as hygiene and as a signal, not as protection.

## Option A: rewrite this repository's history (filter-repo)

Use [`git filter-repo`](https://github.com/newren/git-filter-repo) on a **fresh mirror clone** (never on the working repository).

```bash
git clone --mirror git@github.com:finepointcgi/Workbench.git workbench-clean.git
cd workbench-clean.git
git filter-repo --invert-paths --paths-from-file ../paid-paths.txt     # paid-paths.txt: one path or glob per line (below)
# review: git log --oneline | head; git count-objects -vH; build the stripped tree at a few commits
git remote add origin git@github.com:finepointcgi/Workbench.git
git push --force --mirror origin                                         # the point of no return: do it on a quiet day, after a backup
```

What `paid-paths.txt` holds is the set of paths the extraction PRs deleted, read from the PRs themselves (`git diff --name-only --diff-filter=D master...<tip>` for each of #43 to #54, plus the `docs/` pages that moved) and taking care of the files that stayed in core with the same name. Tests that moved go too. A first cut by directory: the `Module`-owned folders under `src/Workbench.Application`, `src/Workbench.Domain`, `src/Workbench.Contracts`, `src/Workbench.Api`, `src/Workbench.ApiClient`, `src/Workbench.Ui`, `src/Workbench.Infrastructure` that the PRs' file lists name, the paid pages under `docs/` and `site/`, and their tests under `tests/Workbench.Tests`. Do not guess: generate the list from the diffs and review it.

### What breaks

* **Every commit hash changes** after the first touched commit. All open pull requests, forks' upstream links, tags, releases and anything that cites a SHA (the extraction docs cite `253f114`, `14780e6` and others) point at objects that no longer exist on the default branch. Re-tag, and tell contributors to re-clone.
* **Old commits no longer build.** A commit from before the extraction that has the module code removed from history but still calls it (every commit between the introduction of a module and its removal references the code) will not compile. `git bisect` across the cut is useless. Mitigation: filter only the *files*, and accept that early history is broken; or filter and then replay the extraction commits so the tree is consistent from the cut onward (a larger project).
* **Migrations.** The EF migrations that created the paid tables live in core's migration projects. The extraction kept them (an existing install's history must still match) and released ownership with empty release migrations. Deleting those old migrations from history would break `dotnet ef` history and the upgrade path from any release before the cut. Keep the migration files; they hold table definitions, not business logic, but say so if the schema itself is also considered paid IP.
* **Submodules, CI caches and the demo/site build** that name moved paths fail until updated.
* **The private module repository's history is untouched** and already holds the code the files moved to, so nothing is lost; it only stops being derivable from the public history.
* Signed commits and tags lose their signatures when rewritten.

### Steps that make it safe

1. Announce a freeze; merge or close open pull requests first (they cannot be rebased onto rewritten history without effort).
2. Take a full mirror backup and keep it offline (it contains the code; that is the point).
3. Run the filter on a fresh mirror clone, build the result at the new tip and at a handful of older tags, and run the shipped suite and the private repository's CI against it.
4. Push with `--force --mirror` (protected branches must allow it temporarily), recreate releases and tags.
5. Ask GitHub Support to remove cached views and to run garbage collection; delete merged pull request branches; consider deleting and re-creating the repository's pull requests' refs by recreating the repository (Option B) if that matters.
6. Every contributor re-clones. Forks keep the old history unless their owners delete them.

## Option B: a fresh, clean public repository

Create a new public repository whose first commit is the extracted tip (the core without the paid code), keep this repository private (or archive it) as the full history, and point the documentation, container image names, the demo and the site at the new one.

* **Pros.** Clean, with no filtering errors; nothing reachable by SHA, no old pull request refs, no cached views of this repository to chase. The full history stays available to the owner in the private repository, including everything the extraction PRs documented. The simplest to get right.
* **Cons.** Loses stars, issues, discussions, pull request history and the repository URL (GitHub can redirect a *renamed* repository, so rename this one to something like `Workbench-full`, make it private, then create the new public `Workbench`; the old URL redirects to the renamed repository, which is now private, so links break rather than leak). Contributors and forks have to re-point. `git blame` and history in the public repository start at one commit; a *squashed* start is acceptable, or the tip history without the paid paths can be imported with Option A's filter run on a throwaway clone.
* The same honesty applies: clones and forks of the old public repository still exist.

## Recommendation

1. **Decide whether history cleaning is worth it.** If the paid code was public for months, a determined person has it; cleaning only prevents casual discovery. If that is enough, do it; if not, do not spend the effort and rely on the license and the support and update value of the built modules.
2. If yes: **Option B (rename the old repository private, publish a new clean one)**, because it avoids both the broken-history problem and the leftover pull request refs, and it is reversible until the old repository is deleted.
3. If the repository's stars, issues and URL matter more than cleanliness: Option A on a mirror, with the Support purge request, accepting the broken early history.
4. In either case, do it **after** the private repository has a remote with the history pushed, the module PRs have merged, and the private CI is green against the new public `master` (see [module-delivery.md](module-delivery.md)). Until then the history is the only complete record of how the modules were extracted.
5. Separately, decide the public documentation question (see [module-extraction-summary.md](module-extraction-summary.md)): pages in `docs/` and `site/` that still describe the paid modules are marketing, not code, and are usually worth keeping.
