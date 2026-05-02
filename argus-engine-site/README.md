# Argus Engine rewritten static site

This package contains a drop-in static documentation site for the Argus Engine repository.

## Files

- `index.html` — rewritten landing page with accurate architecture overview.
- `architecture.html` — deeper technical design overview.
- `usage.html` — responsible usage tutorial.
- `setup.html` — setup and local run instructions.
- `operations.html` — reliability and operations guidance.
- `responsible-use.html` — responsible use policy.
- `assets/styles.css` — shared responsive styling.
- `assets/site.js` — mobile nav and copy-button behavior.

## Suggested integration

Copy these files to the repository root to replace the current single-page GitHub Pages site. If GitHub Pages is served from the repository root, these relative links will work under `/Argus-Engine/`.

The copy intentionally avoids unsupported claims such as a finalized CLI installer or guaranteed vulnerability discovery. It describes the repo as a .NET event-driven reconnaissance platform with a Command Center, Gatekeeper, workers, Postgres, Redis, and RabbitMQ.
