from __future__ import annotations

import json

from babyai.identity import Identity, IdentityStore


def test_identity_store_ignores_unknown_legacy_fields(tmp_path):
    path = tmp_path / "identity.json"
    path.write_text(
        json.dumps(
            {
                "name": "BabyAI",
                "owner": "KiRiYaN",
                "purpose": "Local assistant",
                "version": "0.1",
                "legacy_field": "must not crash newer desktop builds",
            }
        ),
        encoding="utf-8",
    )

    identity = IdentityStore(path).load_or_create(Identity())

    assert identity.name == "BabyAI"
    assert identity.owner == "KiRiYaN"
    assert identity.purpose == "Local assistant"
    assert identity.version == "0.1"
