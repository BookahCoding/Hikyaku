# Security policy

## Supported versions

Only the latest release receives security fixes. The in-app updater (Settings → Updates) installs it after verifying its
signature.

## Reporting a vulnerability

Please **do not open a public issue** for a security problem.

Report it privately through GitHub: open the repository's **Security** tab and choose **Report a vulnerability**
(GitHub private vulnerability reporting). If that is not available to you, write to the maintainer at the address in
[README.md](README.md#credits-and-licence) with "Hikyaku security" in the subject.

Please include what you found, how to reproduce it, the Hikyaku version and your Windows version. Do not include real
passwords, tokens, gateway addresses or chat contents; a description or a synthetic example is enough.

You can expect an acknowledgement within a week. Once a fix is released you will be credited in the release notes unless
you prefer not to be.

## Scope

In scope: this Windows client, its update and signature checks, how it stores credentials on the PC, and anything it sends
to a gateway. Problems in the Hermes Agent gateway itself belong with that project.

How Hikyaku protects credentials and verifies updates is described in [README.md](README.md#security-and-privacy) and
[docs/UPDATES.md](docs/UPDATES.md).
