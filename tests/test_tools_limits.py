import pytest

from babyai.permissions import Capability, PermissionStore
from babyai.tools import Toolset


def test_read_text_enforces_size_limit(tmp_path) -> None:
    store = PermissionStore(tmp_path / "permissions.json")
    store.grant(Capability.FILESYSTEM_READ)
    target = tmp_path / "large.txt"
    target.write_text("x" * 32, encoding="utf-8")
    with pytest.raises(ValueError, match="limit"):
        Toolset(store).read_text(target, max_bytes=8)


def test_list_directory_requires_permission(tmp_path) -> None:
    store = PermissionStore(tmp_path / "permissions.json")
    tools = Toolset(store)
    with pytest.raises(PermissionError):
        tools.list_directory(tmp_path)
    store.grant(Capability.FILESYSTEM_LIST)
    assert tools.list_directory(tmp_path) == ["permissions.json"]
