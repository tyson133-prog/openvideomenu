# Contributing

Thank you for helping. Issues, proposals, conformance cases, implementations and fixes are all welcome.

## Changing the format

Read [GOVERNANCE.md](GOVERNANCE.md) first: format changes start as an issue, then a pull request against
`spec/` with the reasoning and the conformance cases that pin the change down. Changes must keep to the
[compatibility charter](spec/COMPATIBILITY.md): manifests that work today keep working.

## Changing the code

- `npm test` (conformance cases and unit tests) and `npm run typecheck` must pass.
- New behaviour in the navigator comes with a conformance case in `conformance/cases/`, so every
  implementation (TypeScript, BrightScript, …) is held to it.
- Match the style of the code around your change.

## Sign your work (Developer Certificate of Origin)

Every commit must be signed off, certifying that you wrote it or otherwise have the right to submit it under
the project's licenses, as set out in the [Developer Certificate of Origin 1.1](https://developercertificate.org/):

```sh
git commit -s -m "Your message"
```

This adds a `Signed-off-by: Your Name <you@example.com>` line. There's no separate agreement to sign.

## Licenses of what you contribute

What you contribute is licensed the same way as what it joins:

- **Code** (everything outside `spec/`): [Apache-2.0](LICENSE), which includes a patent grant.
- **Specification** (`spec/`): [CC BY 4.0](spec/LICENSE.md). By contributing to `spec/`, you also make the
  same royalty-free patent commitment for your contribution as the editor makes for the whole specification
  (the [Open Web Foundation Final Specification Agreement 1.0](https://www.openwebfoundation.org/the-agreements/the-owf-1-0-agreements-granted-claims/owfa-1-0),
  see [spec/LICENSE.md](spec/LICENSE.md)), so the format stays free to implement.

Contributors are credited in the git history, and anyone who shapes the format is welcome in
[AUTHORS.md](AUTHORS.md).
