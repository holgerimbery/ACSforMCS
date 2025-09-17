# Release Notes Generation - Hybrid Approach

## Overview

The GitHub Actions release workflow now uses a hybrid approach for generating release notes that combines:

1. **CHANGELOG.md as primary source** (recommended)
2. **Automatic commit parsing** (supplement)
3. **Manual override capability** (for special releases)
4. **Fallback templates** (if other sources fail)

## How It Works

### Priority Order

1. **Custom Notes** (if provided via workflow input)
2. **CHANGELOG.md extraction** (for version-specific content)
3. **Static template + commits** (fallback with recent changes)
4. **Static template only** (complete fallback)

### CHANGELOG.md Integration

The workflow automatically extracts the relevant section from CHANGELOG.md for the current version:

```markdown
## [v1.0.0] - 2025-09-17
### Added
- New feature A
- New feature B

### Changed
- Updated component X
- Improved performance Y

### Fixed
- Bug fix Z
```

This section will be automatically included in the GitHub release notes.

## Workflow Inputs

### Manual Release Trigger

When manually triggering a release, you have these options:

- **version**: Release version (e.g., v1.0.0) - **Required**
- **release_type**: stable/beta/alpha - **Required**
- **custom_notes**: Override all other sources with custom text - **Optional**
- **include_commits**: Add recent commits to release notes - **Optional** (default: true)

### Examples

#### Standard Release (CHANGELOG.md + commits)
```
version: v1.0.0
release_type: stable
custom_notes: (leave empty)
include_commits: true
```

#### Custom Release Notes
```
version: v1.0.0
release_type: stable
custom_notes: "Special hotfix release addressing critical security issue..."
include_commits: false
```

#### CHANGELOG.md Only
```
version: v1.0.0
release_type: stable
custom_notes: (leave empty)
include_commits: false
```

## Best Practices

### Maintaining CHANGELOG.md

1. **Follow Keep a Changelog format**:
   ```markdown
   ## [version] - date
   ### Added
   ### Changed
   ### Deprecated
   ### Removed
   ### Fixed
   ### Security
   ```

2. **Use version tags that match releases**:
   - `[v1.0.0]` or `v1.0.0` (both supported)
   - Date format: `YYYY-MM-DD`

3. **Update before creating releases**:
   - Add new version section at the top
   - Document all significant changes
   - Use clear, user-friendly language

### Release Notes Quality

- **CHANGELOG.md**: Technical details, complete change list
- **Release notes**: User-focused, highlights, deployment info
- **Commits**: Recent technical changes since last release

## Troubleshooting

### CHANGELOG.md Not Found
- Workflow falls back to static template
- Recent commits are still included (if enabled)
- Consider creating CHANGELOG.md for better release notes

### Version Not Found in CHANGELOG.md
- Check version format: `[v1.0.0]` or `v1.0.0`
- Ensure exact match with release version
- Workflow falls back to template if not found

### Empty Release Notes
- Verify CHANGELOG.md section has content
- Check that commits exist since previous tag
- Use custom_notes input for manual override

### Formatting Issues
- Ensure proper Markdown in CHANGELOG.md
- Avoid special characters that break shell commands
- Test locally with `sed` commands if needed

## Advanced Usage

### Custom Commit Formatting

The workflow automatically formats commits as:
```
- Commit message (hash)
```

To improve commit appearance in release notes:
- Use conventional commit messages
- Write clear, descriptive commit messages
- Consider squashing related commits

### Multiple Release Types

- **stable**: Full release notes with all sources
- **beta**: Same as stable but marked as pre-release
- **alpha**: Same as stable but marked as pre-release

Pre-releases are automatically marked in GitHub based on release_type.

## Migration from Static Template

The workflow is backward compatible:
1. Existing releases continue to work
2. Static template used if CHANGELOG.md missing
3. Gradual migration possible by updating CHANGELOG.md

No breaking changes to existing workflow triggers or outputs.