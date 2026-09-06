# Connecting AL Dev Toolbox to your GitHub organisation

A short guide for the three people involved: the **operator** who runs the server, the **org Admin** who connects the organisation, and every **member** who wants the GitHub features. Do the parts in order; each one takes a few minutes.

## 1. Operator: register the GitHub App (once per server)

The toolbox talks to GitHub as a GitHub App that you register once. Every organisation that uses this server installs that one App.

1. Sign in to GitHub as yourself and go to Settings, then Developer settings, then GitHub Apps, then New GitHub App. Any name will do; it is what people see when they install it.
2. Open `/site-admin/settings/github` in the toolbox. Copy its three read-only addresses into the App's boxes of the same names:
   - **Setup URL** (`/github/setup`).
   - **Callback URL** (`/signin-github`). While you are there, tick "Expire user authorization tokens".
   - **Webhook URL** (`/github/webhook`), tick "Active", and make up a **webhook secret**. Paste the same secret into GitHub and into the toolbox's Webhook secret field.
   If the server sits behind a reverse proxy, make sure the toolbox knows its public https address; GitHub reaches the webhook from the internet.
3. Under "Where can this GitHub App be installed?" choose **Any account**.
4. Grant these permissions. Anything you leave out turns off only the feature that needs it.

   | Permission | Level | What it enables |
   | --- | --- | --- |
   | Repository: Administration | Read and write | Creating repositories from New Workspace, and applying branch rules |
   | Repository: Contents | Read and write | Reading files, committing, and publishing Releases |
   | Repository: Metadata | Read | Listing repositories (always required) |
   | Repository: Pull requests | Read and write | Opening pull requests |
   | Repository: Checks | Read and write | Posting build results on pull requests |
   | Organization: Members | Read | Knowing who is in the organisation |

5. Under "Subscribe to events", tick **Pull request**. Without it the toolbox never hears about pull requests and nothing else changes.
6. Create the App, then on its page: note the **App ID** and **Client ID**, generate a **client secret**, and generate a **private key** (GitHub downloads a `.pem` file once).
7. Back on `/site-admin/settings/github`, fill in the App ID, the App's name from its URL (`github.com/apps/<name>`), the client ID, the client secret, the webhook secret, and paste the private key. Save.

GitHub sends a ping when the webhook is saved; the App's "Advanced" tab shows whether the toolbox answered.

## 2. Org Admin: connect the organisation

1. Link your own GitHub account first: Account, then Repository access, then Connect GitHub. The connection step needs it to prove you administer the installation.
2. Go to Administration, then Repositories. Tick GitHub under "Where your code lives", then press **Connect**. GitHub asks where to install the App; choose your **organisation** (a personal account is refused) and which repositories it may see. All repositories is simplest.
3. Back on the Repositories tab, "What the toolbox may do there" lists the granted permissions. Use **Check the connection** after changing the App's permissions on GitHub.
4. Optional: **Repository standards** lets you keep files (for example `.github/workflows/build.yml`, `CODEOWNERS`) and branch rules that every repository the toolbox creates starts with.

You must be an owner of the GitHub organisation to connect it.

## 3. Members: link your GitHub account

Everyone who wants to create repositories, open pull requests, or pick repositories in the toolbox links their own account once: Account, then Repository access, then Connect GitHub. Writes into an existing repository always go out under the linked account, so GitHub applies that person's own permissions and the pull request is theirs.

## What runs on its own once connected

- **Repository discovery**: once a day the toolbox lists the organisation's repositories and offers the AL ones no solution tracks yet on the Solutions page.
- **Pull-request builds**: opening or updating a pull request on a tracked repository compiles it and posts a check run with the compiler's findings inline. Pull requests from forks are built only when the author is a member of the organisation and the fork is their own.
- **Translation memory**: once a day the `.xlf` files in tracked repositories feed the Translator's suggestions.
- **Dependency drift**: after a new Business Central release is imported, the Solutions page shows which repositories still target an older version and can open update pull requests.

Scheduled parts can be switched off per server with the `DISABLE_*` variables listed in the README.

## If something does not work

- **"GitHub is not set up on this server"**: part 1 is incomplete; the App ID, name and private key are all required.
- **Connect is replaced by a message on the Repositories tab**: the Admin has not linked their own GitHub account, or GitHub does not list them as an owner of that organisation.
- **A repository is missing from the picker**: the App was installed on a subset of repositories, or the person cannot open that repository on GitHub themselves.
- **Pull requests get no check run**: the App is not subscribed to the Pull request event, the webhook secret differs between GitHub and the toolbox, or the repository is not tracked by any solution. GitHub's "Recent deliveries" tab on the App shows the toolbox's answer to each delivery.
- **"Not published to GitHub" on a build**: the App lacks Contents: write, or a tag rule on the repository blocks tag creation.
