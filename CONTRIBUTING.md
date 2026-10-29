# Contributing to Nova Globen Fravashi

Thank you for wanting to help. Issues and pull requests are welcome on
[github.com/nova-globen/fravashi](https://github.com/nova-globen/fravashi).

## Before your first pull request: the Contributor Licence Agreement

Fravashi is published under the GNU Affero General Public License, version 3 or later, and Nova Globen AB also offers it
under other licences. To keep that possible, every outside contribution needs a signed Contributor Licence Agreement
(CLA) before it can be merged. The CLA lets Nova Globen AB license your contribution under the AGPL and under other
terms; you keep the copyright to your work. A pull request from someone who has not signed is asked to do so before
review.

## How changes are made

- `dotnet build Fravashi.sln` and `dotnet test Fravashi.sln` must pass. Warnings are errors.
- Commit messages follow Conventional Commits: `type(scope): subject`, imperative, lower case, no trailing period.
- No em dash or en dash anywhere, code, comments and messages included: use a hyphen, a comma, a colon or parentheses.
  A test enforces it.
- A new dependency must have a licence compatible with the AGPL (MIT, Apache-2.0, BSD and the like) and an entry in
  `.githooks/licenses.allow`.
- Security problems go to the maintainers through GitHub's private vulnerability reporting on the repository, never in a
  public issue.
