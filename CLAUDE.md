# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

MyTools is a static web application - a personal utility tool launcher built with pure HTML/CSS/JavaScript (no frameworks). It features a macOS-style dock interface with glassmorphism design, displaying utility apps that are auto-discovered from the repository.

## Development

**No build process required** - This is a pure static site. Open `index.html` directly in a browser to run locally.

**Deployment**: GitHub Pages from the master branch (repo: `anat-achadsiri/aj-design`)

**Local testing**: When running via `file://` protocol, the GitHub API is unavailable. The dock falls back to hardcoded apps in `loadAppsLocal()` within `index.html`. Update this fallback when adding new apps for local testing.

## Architecture

### App Launcher Pattern

- **index.html**: Main launcher page with dock component that auto-loads apps
- **Apps/**: Self-contained utility apps (one HTML file per app)
- **Apps/settings.html**: App management UI and developer guide (automatically excluded from dock)

### App Discovery Flow

1. Dock detects owner/repo from GitHub Pages URL (e.g., `{owner}.github.io/{repo}/`)
2. Fetches app list via GitHub API (`/contents/Apps`)
3. For each HTML file (except `settings.html`), fetches and parses meta tags for configuration
4. Falls back to hardcoded app list when running locally (file:// protocol)

### Adding New Apps

Create a new HTML file in `Apps/` with these meta tags:

```html
<meta name="app-name" content="App Display Name">  <!-- Optional: falls back to <title> -->
<meta name="app-icon" content="icon_name">         <!-- Material Icon name, emoji, image URL, or inline SVG -->
<meta name="app-color" content="#HEX">             <!-- Dock icon background -->
<meta name="app-order" content="1">                <!-- Sort order (lower = first, default: 99) -->
```

Each app is self-contained with inline CSS and JavaScript - no external dependencies.

## Code Conventions

- UI language: Thai
- Font: Kanit (Google Fonts)
- Icons: Google Material Icons (with image/SVG/emoji fallback)
- Design: Glassmorphism with gradient blues/cyan
- Responsive breakpoint: 768px
