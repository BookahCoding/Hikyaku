"""Hermes entry point. Tests import bridge.py directly."""

if __package__:
    from .bridge import register  # noqa: F401
