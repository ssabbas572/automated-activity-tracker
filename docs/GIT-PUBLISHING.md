# Publish the repository to GitHub

This folder is ready to become the repository root: `README.md`, `.github` and the solution belong at the top level. The package has no remote URL, fabricated commit history or credentials. Publishing has not been performed automatically.

## 1. Use the correct copy

Extract this repository package into a new folder. It is the .NET 9 version prepared from the supplied implementation. If you made additional changes in your older local folder, compare and merge those changes before switching. Do not overwrite your only working copy.

Run from the new folder:

```bash
dotnet --version
dotnet restore AutomatedActivityTracker.sln --locked-mode
dotnet build AutomatedActivityTracker.sln -c Release --no-restore
dotnet test AutomatedActivityTracker.sln -c Release --no-build
```

## 2. Create an empty remote

On GitHub, create a repository named `automated-activity-tracker`.

Suggested description:

> ASP.NET Core activity tracking API with a durable SQLite outbox, idempotent requests, duration tracking, tenant isolation and integration tests.

Start private unless the client has approved a public submission. Leave GitHub's “Add README”, “Add .gitignore” and license options unchecked: these files are already prepared locally. No license is assigned in this package; agree it with the client if needed.

For a private repository, grant the evaluator access using the exact GitHub account they provide. Do not assume that sending a private URL alone grants access.

## 3. Initialize and inspect locally

Open a terminal in the extracted `automated-activity-tracker` folder:

```bash
git init -b main
git add .
git status --short
git diff --cached --stat
```

Review the staged list. It should contain source, docs, configuration, lock files and tests. It should not contain databases, credentials, `.vs`, `bin` or `obj`.

Check for tracked output/data:

```bash
git ls-files "*.db" "*.db-wal" "*.db-shm" "*/bin/*" "*/obj/*"
```

Expected output: no files. If sensitive files are already tracked in an existing repository, `.gitignore` does not remove them from history; stop and handle that case separately.

## 4. Commit and push

```bash
git commit -m "Implement automated case activity tracking with durable processing"
git remote add origin https://github.com/YOUR-USERNAME/automated-activity-tracker.git
git push -u origin main
```

Replace `YOUR-USERNAME` with the actual owner from the repository URL. Authenticate through your normal Git credential manager/browser flow. Do not put a token in the remote URL, source files or chat.

If Git asks for your identity, set your real name and GitHub-associated or GitHub no-reply email for this repository, then retry the commit:

```bash
git config user.name "Your Name"
git config user.email "YOUR_GITHUB_EMAIL"
```

If `origin` already exists, inspect `git remote -v` before changing anything. If the remote already contains commits, do not force-push this package over them; clone the remote and merge the intended changes instead.

## 5. Verify the remote submission

- Open the repository and confirm README links and the architecture diagram render.
- Open Actions and wait for the Windows and Linux verification jobs.
- Fix any failing jobs before claiming CI passes. Workflow configuration alone is not a passing run.
- Confirm the reviewer has access.
- Use the README walkthrough for the live demo. There is no public deployment URL in this package.

The workflow restores locked packages, builds Release, runs tests and validates API publishing. It uploads test-result files; it does not deploy the API or publish packages.

## 6. Later changes

```bash
git status
git add README.md docs/
git diff --cached
git commit -m "Clarify activity tracking behaviour and demonstration steps"
git push
```

Stage the actual source files as well when a change includes implementation updates. For dependency changes, intentionally regenerate and commit the affected `packages.lock.json` files, then run the tests.
