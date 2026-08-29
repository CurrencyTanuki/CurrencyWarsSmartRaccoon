# Bundled CPython runtime

- Version: CPython 3.14.7, Windows embeddable package (64-bit)
- Upstream release: https://www.python.org/downloads/release/python-3147/
- Archive: `python-3.14.7-embed-amd64.zip`
- Upstream URL: https://www.python.org/ftp/python/3.14.7/python-3.14.7-embed-amd64.zip
- SHA-256: `d297e5ff019966817ad8502465176139f2d3d840fa4ed84b13bed399a6ab1f15`
- Release date: 2026-08-05

The unmodified archive is extracted to
`third_party/python-3.14.7-embed-amd64/` and copied into the application as
`runtime/python/`. The upstream `LICENSE.txt` remains beside the runtime.
The official Sigstore bundle and SPDX SBOM are retained next to the archive.

This private runtime only executes `gen_report.py`, which imports Python
standard-library modules and has no pip dependencies. The application invokes
the bundled `python.exe` by absolute path and does not fall back to PATH,
the Microsoft Store alias, or a system Python installation.
