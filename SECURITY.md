# Security policy

## Supported versions

Bluestone has not had a stable release yet. Security fixes are made on the `main` branch.

## Reporting a vulnerability

Please do not report security vulnerabilities in public issues, discussions, or pull requests.

Report them privately through GitHub: open the repository's **Security** tab and choose **Report a vulnerability** ([direct link](https://github.com/djflan/Bluestone/security/advisories/new)).

Useful details include:

- the affected component (for example project file loading, MIDI or audio file reading, device profiles, plugin scanning or hosting, or a platform adapter)
- steps or a sample file that reproduces the problem
- the operating system and Bluestone commit you tested

You should receive an acknowledgement within a week. Once a fix is available, the advisory will be published with credit to the reporter unless you prefer to stay anonymous.

## Third-party plugins

Bluestone runs VST 3 and CLAP plugins in separate worker processes. This isolates crashes, not access: a plugin runs as your user, with the same permissions as Bluestone, and can read and write anything you can and use the network. There is no operating-system sandbox. Install only plugins you trust. Details are in [Security limitations](docs/plugin-hosting.md#security-limitations).
