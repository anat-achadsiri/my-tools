# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

MyTools is a static web application - a personal utility tool launcher built with pure HTML/CSS/JavaScript (no frameworks). It features a macOS-style dock interface with glassmorphism design, displaying utility apps that are auto-discovered from the repository.

## Development

**No build process required** - This is a pure static site. Open `index.html` directly in a browser to run locally.

**Deployment**: GitHub Pages from the master branch (repo: `anat-achadsiri/aj-design`)

## Architecture

### App Launcher Pattern

- **index.html**: Main launcher page with dock component that auto-loads apps
- **Apps/**: Self-contained utility apps (one HTML file per app)
- **settings.html**: App management and developer guide

### App Discovery Flow

1. Dock detects owner/repo from GitHub Pages URL (or uses defaults)
2. Fetches app list via GitHub API (`/contents/Apps`)
3. For each HTML file, fetches and parses meta tags for configuration
4. Falls back to hardcoded app list when running locally (file:// protocol)

### Adding New Apps

Create a new HTML file in `Apps/` with these meta tags:

```html
<meta name="app-name" content="App Display Name">
<meta name="app-icon" content="icon_name">  <!-- Material Icon name, emoji, image URL, or inline SVG -->
<meta name="app-color" content="#HEX">       <!-- Dock icon background -->
<meta name="app-order" content="1">          <!-- Sort order (lower = first) -->
```

Each app is self-contained with inline CSS and JavaScript - no external dependencies.

### Current Apps

- **convert_to_sql.html**: Converts username lists to Oracle SQL WHERE IN clauses
- **text_counter.html**: Real-time text statistics (characters, words, lines, etc.)
- **vector_draw.html**: Vector drawing tool with image-to-vector conversion
- **settings.html**: App management interface (order: 99)

## Code Conventions

- UI language: Thai
- Font: Kanit (Google Fonts)
- Icons: Google Material Icons (with SVG/image fallback)
- Design: Glassmorphism with gradient blues/cyan
- Responsive breakpoint: 768px
