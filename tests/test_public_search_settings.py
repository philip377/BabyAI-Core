import base64
import io
import json
import os
import time

import pytest

from babyai.config import BabyAIConfig
from babyai.desktop_commands import DesktopCommands
from babyai.desktop_worker import serve
from babyai.ginger_bottle_research import check_search, research, search_pages
from babyai.permissions import PermissionStore
from babyai.public_search_settings import PublicSearchSettings, SearchAccess, _crypt
from babyai.public_web import WebPage, WebReadError


KEY = 'test-only-not-a-real-api-key'


@pytest.fixture(autouse=True)
def no_environment_key(monkeypatch):
    monkeypatch.delenv('BABYAI_BRAVE_SEARCH_KEY', raising=False)


@pytest.fixture
def crypto(monkeypatch):
    secrets = {}
    def protect(data, *, decrypt=False):
        if decrypt:
            if data not in secrets:
                raise WebReadError('Invalid protected data')
            return secrets[data]
        ciphertext = ('test-cipher-' + str(len(secrets))).encode()
        secrets[ciphertext] = data
        return ciphertext
    monkeypatch.setattr('babyai.public_search_settings._crypt', protect)


def test_settings_never_expose_key_and_apply_to_new_instance(tmp_path, crypto):
    settings = PublicSearchSettings(tmp_path)
    assert settings.status()['mode'] == 'duckduckgo'
    assert not settings.path.exists()
    status = settings.save('brave', api_key=KEY)
    assert status['configured'] and status['verified'] is False
    assert status['key_source'] == 'saved'
    assert KEY not in json.dumps(status) + settings.path.read_text()
    assert KEY not in repr(PublicSearchSettings(tmp_path).access())
    assert PublicSearchSettings(tmp_path).access().key == KEY
    settings.save('duckduckgo')
    assert settings.access().key is None
    settings.save('brave')
    assert settings.access().key == KEY


def test_explicit_mode_overrides_environment_and_clear_does_not_reactivate_it(tmp_path, monkeypatch, crypto):
    monkeypatch.setenv('BABYAI_BRAVE_SEARCH_KEY', KEY)
    settings = PublicSearchSettings(tmp_path)
    assert settings.access().key_source == 'environment'
    settings.save('brave', api_key='saved-test-only-api-key')
    assert settings.access().key_source == 'saved'
    status = settings.save('links_only', clear_key=True)
    assert status['saved_key'] is False and settings.access().key is None
    with pytest.raises(WebReadError, match='Только по ссылке'):
        search_pages('Example perfume', lambda *a, **kw: pytest.fail('No network'), time.monotonic() + 5, settings.access())


@pytest.mark.parametrize('key', ['', 'short', 'x' * 513, 'valid-but\ninjected-header', 'только-кириллица', True, 123])
def test_invalid_key_is_not_stored_or_echoed(tmp_path, key):
    settings = PublicSearchSettings(tmp_path)
    with pytest.raises(WebReadError):
        settings.save('brave', api_key=key)
    assert not settings.path.exists()


def test_missing_and_corrupted_key_cannot_fall_back_to_another_provider(tmp_path, monkeypatch, crypto):
    settings = PublicSearchSettings(tmp_path)
    settings.save('brave')
    assert not settings.status()['configured']
    with pytest.raises(WebReadError, match='нужен ключ'):
        settings.access()
    settings.save('brave', api_key=KEY)
    data = json.loads(settings.path.read_text())
    data['protected_key'] = base64.b64encode(b'corrupted').decode()
    settings.path.write_text(json.dumps(data))
    monkeypatch.setenv('BABYAI_BRAVE_SEARCH_KEY', KEY)
    assert not settings.status()['configured']
    with pytest.raises(WebReadError, match='недоступен'):
        settings.access()
    settings.save('links_only', clear_key=True)
    assert settings.access().mode == 'links_only'


def test_corrupted_settings_repair_requires_explicit_replacement(tmp_path, crypto):
    settings = PublicSearchSettings(tmp_path)
    settings.path.write_text('{broken')
    assert not settings.status()['configured']
    with pytest.raises(WebReadError):
        settings.save('duckduckgo')
    settings.save('brave', api_key=KEY)
    assert settings.access().key == KEY


def test_atomic_replace_failure_keeps_previous_settings(tmp_path, monkeypatch, crypto):
    settings = PublicSearchSettings(tmp_path)
    settings.save('duckduckgo')
    old = settings.path.read_bytes()
    def fail(*args, **kwargs):
        raise OSError('simulated disk failure')
    monkeypatch.setattr('babyai.public_search_settings.os.replace', fail)
    with pytest.raises(WebReadError):
        settings.save('brave', api_key=KEY)
    assert settings.path.read_bytes() == old
    assert list(tmp_path.iterdir()) == [settings.path]


def test_encryption_failure_does_not_save_plaintext_or_erase_old_key(tmp_path, monkeypatch, crypto):
    settings = PublicSearchSettings(tmp_path)
    settings.save('brave', api_key=KEY)
    old = settings.path.read_bytes()
    def fail(*args, **kwargs):
        raise WebReadError('Protection unavailable')
    monkeypatch.setattr('babyai.public_search_settings._crypt', fail)
    with pytest.raises(WebReadError):
        settings.save('brave', api_key='replacement-test-only-key')
    assert settings.path.read_bytes() == old
    assert list(tmp_path.iterdir()) == [settings.path]


@pytest.mark.skipif(os.name != 'nt', reason='Real Windows current-user DPAPI')
def test_real_windows_protection_round_trip_and_tamper_rejection(tmp_path):
    settings = PublicSearchSettings(tmp_path)
    settings.save('brave', api_key=KEY)
    assert KEY not in settings.path.read_text()
    assert PublicSearchSettings(tmp_path).access().key == KEY
    data = bytearray(_crypt(KEY.encode()))
    data[-1] ^= 1
    with pytest.raises(WebReadError):
        _crypt(bytes(data), decrypt=True)


@pytest.mark.parametrize(('code', 'message'), [(401, 'не принял'), (403, 'не принял'), (429, 'число запросов')])
def test_search_provider_errors_have_no_retry_or_fallback(code, message):
    calls = []
    def fetch(url, **kwargs):
        calls.append(url)
        raise WebReadError('HTTP failure', status=code)
    with pytest.raises(WebReadError, match=message):
        search_pages('Example perfume', fetch, time.monotonic() + 5, SearchAccess('brave', KEY))
    assert len(calls) == 1 and calls[0].startswith('https://api.search.brave.com/')


def test_connection_check_fetches_only_search_with_key_and_does_not_persist_success(tmp_path, crypto):
    settings = PublicSearchSettings(tmp_path)
    settings.save('brave', api_key=KEY)
    calls = []
    def fetch(url, **kwargs):
        calls.append((url, kwargs))
        return WebPage(url, 'application/json', json.dumps(dict(web=dict(results=[
            dict(url='https://example.com/product', title='Product')]))).encode())
    result = check_search(settings, fetch=fetch)
    assert result['connected'] and result['result_count'] == 1 and len(calls) == 1
    assert calls[0][1]['api_key'] == KEY
    assert 'spellcheck=false' in calls[0][0]
    assert KEY not in json.dumps(result) and not settings.status()['verified']


@pytest.mark.parametrize('body', [{}, {'error': 'invalid'}, {'web': {}}, {'web': {'results': None}}, []])
def test_http_200_with_wrong_api_shape_does_not_mean_connected(tmp_path, crypto, body):
    settings = PublicSearchSettings(tmp_path)
    settings.save('brave', api_key=KEY)
    result = check_search(settings, fetch=lambda url, **kw: WebPage(url, 'application/json', json.dumps(body).encode()))
    assert not result['connected'] and result['result_count'] == 0


def test_http_202_captcha_is_actionable_but_never_retried(tmp_path):
    calls = []
    def fetch(url, **kwargs):
        calls.append(url)
        raise WebReadError('HTTP 202', status=202)
    result = check_search(PublicSearchSettings(tmp_path), fetch=fetch)
    assert len(calls) == 1 and not result['connected']
    assert 'Настройки' in result['message'] and 'Brave' in result['message']


def test_explicit_link_works_with_unusable_search_settings_and_no_model_on_failure(tmp_path):
    settings = PublicSearchSettings(tmp_path)
    settings.save('brave')
    result = research('Example perfume', source_url='https://example.com/product', settings=settings,
                      fetch=lambda url, **kw: WebPage(url, 'text/html', b'<p>Product</p>'))
    assert result['status'] == 'sources'
    failed = research('Example perfume', settings=settings,
                      provider_factory=lambda: pytest.fail('No model for blocked search'),
                      fetch=lambda *a, **kw: pytest.fail('No network without key'))
    assert failed['status'] == 'unavailable' and failed['can_match'] is False


def test_model_starts_after_a_page_is_read_and_does_not_see_search_key(tmp_path, crypto):
    settings = PublicSearchSettings(tmp_path)
    settings.save('brave', api_key=KEY)
    events, prompts = [], []
    class Model:
        def generate(self, prompt):
            prompts.append(prompt)
            return '{"excerpts":[]}'
    def factory():
        events.append('model')
        return Model()
    def fetch(url, **kwargs):
        if url.startswith('https://api.search.brave.com/'):
            events.append('search')
            return WebPage(url, 'application/json', json.dumps(dict(web=dict(results=[
                dict(url='https://example.com/product', title='Example')]))).encode())
        events.append('product')
        assert 'api_key' not in kwargs
        return WebPage(url, 'text/html', b'<p>Example perfume 100ml.</p>')
    result = research('Example perfume', settings=settings, provider_factory=factory, fetch=fetch)
    assert events == ['search', 'product', 'model'] and len(prompts) == 1
    assert KEY not in prompts[0] + json.dumps(result)
    assert result['status'] == 'sources' and result['can_match'] is False


def test_connection_command_needs_permission_without_loading_model_or_core(tmp_path, monkeypatch):
    commands = DesktopCommands(BabyAIConfig(data_dir=tmp_path, provider='echo'))
    monkeypatch.setattr(commands, '_provider', lambda: pytest.fail('No model'))
    monkeypatch.setattr(commands, '_core', lambda: pytest.fail('No Ginger or agent'))
    monkeypatch.setattr('babyai.ginger_bottle_research.check_search', lambda settings: dict(connected=True))
    assert commands.execute('web.search.status')['ok']
    for value in (None, False, 'true', 1):
        with pytest.raises(PermissionError):
            commands.execute('web.search.check', dict(allow_public_web=value))
    assert commands.execute('web.search.check', dict(allow_public_web=True))['connected']
    assert PermissionStore(commands.config.permissions_file).list() == []
    assert not commands.config.permissions_file.exists()


def test_worker_never_echoes_key_to_reply_or_trace(tmp_path, crypto, monkeypatch, capsys):
    commands = DesktopCommands(BabyAIConfig(data_dir=tmp_path, provider='echo'))
    traces = []
    monkeypatch.setattr('babyai.desktop_worker.trace', lambda *args, **kwargs: traces.append((args, kwargs)))
    requests = [dict(id=1, command='web.search.configure', payload=dict(mode='brave', api_key=KEY)),
                dict(id=2, command='web.search.status'), dict(id=3, command='worker.shutdown')]
    output = io.StringIO()
    serve(commands, stdin=io.StringIO('\n'.join(map(json.dumps, requests)) + '\n'), stdout=output)
    assert all(json.loads(line)['ok'] for line in output.getvalue().splitlines())
    captured = capsys.readouterr()
    assert KEY not in output.getvalue() + repr(traces) + captured.out + captured.err
