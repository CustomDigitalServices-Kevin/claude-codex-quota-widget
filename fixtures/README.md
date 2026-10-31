# Synthetic test fixtures

The fixtures in this directory are 100% constructed specifically for automated tests. No fixture was copied from a real account, session, transcript, response, or quota capture.

The Claude sample contains only synthetic timestamps and quota windows. The Codex samples contain minimal synthetic session metadata, one quota event, and an intentionally truncated final JSON line used to verify defensive journal reading. All UUIDs, paths, account identifiers, balances, percentages, and timestamps are fictional.

The `constructed` app-server examples are schema-based test documents. They are not captured service responses.
