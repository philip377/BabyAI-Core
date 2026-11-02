import json
import re
import pytest
from test_ginger_review_feedback import feedback_fixture
from babyai.ginger_review_form import render_form, export_form


def test_inert_bundle_escapes_supplier_script_injection(tmp_path):
    _,_,bundle,_=feedback_fixture(tmp_path)
    hostile='</script><script>alert(1)</script><img src=x onerror=alert(1)>'
    bundle["rows"][0]["title"]=hostile
    page=render_form(bundle)
    assert hostile not in page
    data=re.search(r'<script id="bundle"[^>]*>(.*?)</script>',page,re.S).group(1)
    assert json.loads(data)["rows"][0]["title"] == hostile
    assert "connect-src 'none'" in page and "form-action 'none'" in page
    assert "innerHTML" not in page


def test_export_is_local_and_preserves_existing_form(tmp_path):
    journal,_,_,_=feedback_fixture(tmp_path)
    directory=tmp_path/"form"
    result=export_form(journal.directory,directory)
    assert not result["can_write"]
    original=(directory/"index.html").read_bytes()
    assert (directory/"bundle.json").is_file()
    with pytest.raises(FileExistsError): export_form(journal.directory,directory)
    assert (directory/"index.html").read_bytes() == original


def test_form_script_download_matches_feedback_import_schema(tmp_path):
    import shutil
    import subprocess
    from pathlib import Path
    from babyai.ginger_review_form import SCRIPT
    from babyai.ginger_review_feedback import validate_feedback
    node=shutil.which("node")
    if node is None: pytest.skip("Node required for offline script unit test")
    journal,_,bundle,_=feedback_fixture(tmp_path)
    harness=Path(__file__).with_name("ginger-review-form-unit.cjs")
    result=subprocess.run([node,str(harness)],input=json.dumps({"script":SCRIPT,"bundle":bundle}),
                          text=True,encoding="utf-8",capture_output=True,timeout=10)
    assert result.returncode == 0, result.stderr
    payload=validate_feedback(journal.directory,bundle,json.loads(result.stdout))
    assert payload["status"] == "annotation_only" and not payload["can_write"]


def test_export_preloads_exact_saved_annotations_as_inert_data(tmp_path):
    from babyai.ginger_review_feedback import import_feedback
    journal,_,bundle,feedback=feedback_fixture(tmp_path)
    hostile='</script><script>alert(2)</script>'
    feedback["entries"][0]["candidates"][0]["reason"]=hostile
    import_feedback(journal.directory,bundle,feedback)
    directory=tmp_path/"review-with-notes"
    export_form(journal.directory,directory)
    page=(directory/"index.html").read_text(encoding="utf-8")
    assert hostile not in page
    data=re.search(r'<script id="initial-feedback"[^>]*>(.*?)</script>',page,re.S).group(1)
    loaded=json.loads(data)
    assert loaded["entries"][0]["candidates"][0]["reason"] == hostile
    assert loaded["base_revision"] is not None
