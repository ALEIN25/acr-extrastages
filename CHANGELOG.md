# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this
project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Every DiRT Rally 2.0 stage that fits (106 of 156, start to finish at most 9 km) can be built.
  The launcher still starts with the recommended 30. In Choose stages, `all` selects every stage
  that fits and `default` restores the 30. The 50 longer stages are listed and cannot be built.

### Changed

- Better-looking objects: tree trunks, collider-shaped objects (rocks, fences, barriers) and the
  road-edge posts now use textures from the stage's own DiRT Rally 2.0 files (bark, rock, wood,
  ...) instead of flat debug colours, and trees get small textured crowns. Objects with no
  matching texture keep their old colour. Configured in `config/visuals.toml` `[object_textures]`.
- Medium and hard DiRT Rally 2.0 bushes no longer have colliders: in ACR they stopped the car like
  rocks, which made the verges unforgiving (about 490 per Finland stage, up to 3,600 on Greece).
  They are still drawn.
- UI launcher: a stage map (the route with start, splits and finish, and an elevation profile)
  for the stage you point at.

### Fixed

- Finland: the middle of some stages (Naarajärvi) had no detailed ground or road, so the far-off
  low-detail ground showed through with smeared textures and grass on the road. Those parts of
  the terrain are stored as `HIGH_` tiles, which are now read as well.

## [0.1.0-beta.1] - Unreleased

First public beta (not yet published). Brings stages from *DiRT Rally 2.0* into *Assetto Corsa
Rally*, built locally on your PC from your own copies of both games.

### Added

- 30 stages across 13 locations (Argentina, Australia, Finland, Monte Carlo, Germany, Greece,
  New Zealand, Poland, Scotland, Spain, Sweden, USA and Wales), listed in `config/stages.toml`.
  Stages appear next to the vanilla ones in the stage menus.
- Per-stage generated menu images (route map, track map, height profile) and loading backdrops.
- Pace notes generated for each stage, using the game's own call tokens.
- Offline verification of every built stage and of the menu registry before installing.
- Installer script that hard-links the generated files into the game's `~mods` folder, and an
  uninstall switch that removes only files this project created (`zz_acrxs_*`).
- Repository hygiene: dual MIT OR Apache-2.0 licensing, third-party notices, contribution and
  security policies, and `scripts/check-repo-content.ps1`, which refuses game-derived files in
  the repository.