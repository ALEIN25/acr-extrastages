# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this
project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Every DiRT Rally 2.0 stage that fits (106 of 156, start to finish at most 9 km) can be built.
  The launcher still starts with the recommended 30. In Choose stages, `all` selects every stage
  that fits and `default` restores the 30. The 50 longer stages are listed and cannot be built.
- Reverse variants: a route and its reverse build into one stage, with its own menu map and
  elevation profile (`config/variants.toml`).
- Real DiRT Rally 2.0 rocks near the road, and real tree models for the nearest trees.
- Many more real trees: full DiRT Rally 2.0 tree models within 12 m of the road, the same trees with
  thinned foliage out to 25 m, flat cards only beyond that (`[vegetation]` near_m / mid_m). Bushes
  near the road use their real models too. Finland 01 r2 runs at about 80 FPS on the test PC.
- Tree trunk and object colliders are fitted to their visible models, so no thick trunk pillars or
  collider boxes are drawn over trees, houses and vehicles that are walled in at car height.
- A build check that fails a stage if something solid near the road cannot be seen. Colliders
  with nothing drawn on them are now drawn.

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

- The car no longer starts among the parked start-area vehicles: it waits 12 m before the start
  line, on every stage.
- Tree trunks no longer stand 20 m tall in white birch bark, and buildings no longer pick up each
  other's textures when two models share a material name.
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