"""Hermes entry point: the plugin lives in cron_output.py."""

if __package__:  # loaded by Hermes as a plugin package (the tests import cron_output directly)
    from .cron_output import register  # noqa: F401
