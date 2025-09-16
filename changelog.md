# Changelog

All notable changes to ACS for MCS will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- GitHub Actions automated release pipeline
- Pre-built deployment packages for easy installation
- Comprehensive deployment documentation
- Release-specific deployment script with validation
- Configuration validation and setup tools
- Parallel WebSocket setup for improved performance
- Enhanced WebSocket buffer management (4KB → 8KB)

### Changed
- Optimized speech recognition timing (EndSilenceTimeoutMs: 2000ms → 1500ms)
- Improved initial response time (InitialSilenceTimeoutMs: 1000ms → 800ms)
- Faster TTS playback (speech rate: medium → fast)
- Reduced transfer operation delays (1000/2000ms → 500/1000ms)
- Enhanced HTTP timeout handling (30s → 10s)
- Improved WebSocket heartbeat timing (45s → 5s/30s intervals)

### Performance Improvements
- Reduced average response time by approximately 2 seconds
- Faster failure detection and recovery
- Better network throughput with optimized buffers
- Parallel processing for WebSocket setup and greeting

### Technical
- .NET 9.0 runtime support
- Enhanced error handling and logging
- Better connection pooling configuration
- Improved memory management with ArrayPool usage
- Compiled regex patterns for better performance

### Documentation
- Complete installation guide for end users
- Configuration reference documentation
- Troubleshooting guide with common issues
- Performance tuning recommendations
- Deployment best practices

### Security
- Enhanced Key Vault integration
- Improved secret management
- Better authentication handling

## [0.50] - 2025-09-21

### Added
- Azure Web App as the main deployment target for development and production
- Scripts to switch between development and production modes
- Development mode: monitoring endpoints and swagger interface (secured via API key)
- Production mode: minimal logging and unnecessary endpoints disabled

### Changed
- Improved deployment workflow with environment-specific configurations
- Enhanced security model for production deployments

## [0.40] - 2025-09-11

### Added
- Full Azure Web App compatibility
- Comprehensive wiki documentation
- Monitoring endpoints for health checking
- Swagger interface for API documentation
- Performance enhancements across the application

### Improved
- Overall system stability and reliability
- Documentation coverage and quality

## [0.30] - 2025-09-09

### Added
- Comprehensive error handling throughout the application
- Deployment guide specifically for Azure Web App

### Fixed
- Various stability issues
- Deployment process improvements

## [0.21] - 2025-08-xx

### Added
- Extensive in-line code comments for better maintainability
- Overview documentation file

### Improved
- Code readability and documentation

## [0.2] - 2025-08-xx

### Added
- Call forwarding to external users
- Azure Key Vault integration for all secrets
- Key Vault URI storage in user secrets (development) and GitHub Actions (production)

### Improved
- Error handling mechanisms
- Security posture with centralized secret management

## [0.1] - 2025-08-xx

### Added
- Initial release
- Detailed README.md with installation instructions
- Structured logging system

### Features
- Basic Azure Communication Services integration
- Microsoft Bot Framework connectivity
- Speech-to-text and text-to-speech capabilities

---

## Planned for Future Releases

### Performance Enhancements
- Bot response caching for improved response times
- TTS pre-warming for common phrases
- Advanced connection pooling optimizations
- Regional DirectLine endpoint optimization

### Features
- Health monitoring dashboards
- Configuration migration tools
- Multi-language support enhancements
- Advanced call analytics
- Custom voice model support

### DevOps
- Container deployment support
- Infrastructure as Code templates
- Automated testing improvements
- Performance benchmarking tools

---

## Version History Guidelines

### Release Types
- **Major** (X.0.0): Breaking changes, major new features, architectural changes
- **Minor** (X.Y.0): New features, performance improvements, backward compatible
- **Patch** (X.Y.Z): Bug fixes, security updates, minor improvements

### Support Policy
- **Current Release**: Full support and updates
- **Previous Minor**: Security updates only  
- **Older Versions**: Community support through GitHub issues

### Release Process
1. Changes are developed in feature branches
2. Pull requests are reviewed and merged to main
3. Releases are created using GitHub Actions
4. Pre-built packages are generated automatically
5. Documentation is updated with each release

For detailed commit-level changes, see the [GitHub commit history](https://github.com/holgerimbery/ACSforMCS/commits/main).
