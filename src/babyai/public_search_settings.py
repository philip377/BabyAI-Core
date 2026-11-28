"""Desktop-only search preferences; credentials are never returned to the UI/model."""
import base64
import ctypes
from dataclasses import dataclass, field
import json
import os
from pathlib import Path
import tempfile

from .public_web import WebReadError


MODES = ('duckduckgo', 'brave', 'links_only')


def validate_key(value):
    if not isinstance(value, str) or not 12 <= len(value) <= 512 or any(
            not 33 <= ord(c) <= 126 for c in value):
        raise WebReadError('Ключ поиска должен содержать от 12 до 512 символов без пробелов.')
    return value


def _crypt(data, *, decrypt=False):
    """Windows DPAPI, current user only. No machine-wide/plaintext fallback."""
    if os.name != 'nt':
        raise WebReadError('Защищённое сохранение ключа доступно в Windows.')
    class Blob(ctypes.Structure):
        _fields_ = [('size', ctypes.c_uint32), ('data', ctypes.POINTER(ctypes.c_ubyte))]

    buffer = ctypes.create_string_buffer(data)
    source = Blob(len(data), ctypes.cast(buffer, ctypes.POINTER(ctypes.c_ubyte)))
    output = Blob()
    entropy_buffer = ctypes.create_string_buffer(b'BabyAI public search Brave key v1')
    entropy = Blob(len(entropy_buffer) - 1, ctypes.cast(entropy_buffer, ctypes.POINTER(ctypes.c_ubyte)))
    crypt32 = ctypes.WinDLL('crypt32', use_last_error=True)
    kernel32 = ctypes.WinDLL('kernel32', use_last_error=True)
    operation = crypt32.CryptUnprotectData if decrypt else crypt32.CryptProtectData
    operation.argtypes = [ctypes.POINTER(Blob), ctypes.c_void_p, ctypes.POINTER(Blob),
                         ctypes.c_void_p, ctypes.c_void_p, ctypes.c_uint32, ctypes.POINTER(Blob)]
    operation.restype = ctypes.c_int
    kernel32.LocalFree.argtypes = [ctypes.c_void_p]
    kernel32.LocalFree.restype = ctypes.c_void_p
    try:
        if not operation(ctypes.byref(source), None, ctypes.byref(entropy), None, None, 1, ctypes.byref(output)):
            raise WebReadError('Windows не смогла открыть или защитить ключ поиска. Введи его заново.')
        if not 0 < output.size <= 8192:
            raise WebReadError('Windows вернула недопустимые защищённые данные.')
        return ctypes.string_at(output.data, output.size)
    finally:
        ctypes.memset(buffer, 0, len(buffer))
        if output.data:
            ctypes.memset(output.data, 0, output.size)
            kernel32.LocalFree(ctypes.cast(output.data, ctypes.c_void_p))


@dataclass(frozen=True)
class SearchAccess:
    mode: str
    key: str | None = field(default=None, repr=False)
    key_source: str = 'none'


class PublicSearchSettings:
    def __init__(self, directory):
        self.path = Path(directory) / 'public-search.json'

    def _read(self):
        try:
            with self.path.open('rb') as stream:
                body = stream.read(16385)
        except FileNotFoundError:
            # Compatibility with the earlier process environment setting.
            return dict(version=1, mode='brave' if os.getenv('BABYAI_BRAVE_SEARCH_KEY', '').strip() else 'duckduckgo',
                        protected_key='')
        except OSError:
            raise WebReadError('Не удалось прочитать настройки интернет-поиска.') from None
        try:
            if len(body) > 16384:
                raise ValueError()
            data = json.loads(body)
            if (not isinstance(data, dict) or set(data) != {'version', 'mode', 'protected_key'}
                    or type(data['version']) is not int or data['version'] != 1
                    or data['mode'] not in MODES or not isinstance(data['protected_key'], str)
                    or len(data['protected_key']) > 12000):
                raise ValueError()
            return data
        except (ValueError, TypeError):
            raise WebReadError('Настройки интернет-поиска повреждены. Сохрани их заново.') from None

    def access(self):
        data = self._read()
        if data['mode'] != 'brave':
            return SearchAccess(data['mode'])
        try:
            if data['protected_key']:
                key = _crypt(base64.b64decode(data['protected_key'], validate=True), decrypt=True).decode('ascii')
                return SearchAccess('brave', validate_key(key), 'saved')
        except (ValueError, UnicodeError):
            raise WebReadError('Сохранённый ключ недоступен. Введи ключ заново в настройках интернет-поиска.') from None
        key = os.getenv('BABYAI_BRAVE_SEARCH_KEY', '').strip()
        if key:
            return SearchAccess('brave', validate_key(key), 'environment')
        raise WebReadError('Для Brave нужен ключ: Настройки → Интернет-поиск. Или укажи готовую ссылку.')

    def status(self):
        try:
            data = self._read()
            mode, saved = data['mode'], bool(data['protected_key'])
        except WebReadError as exc:
            return dict(mode='links_only', saved_key=False, key_source='none', configured=False,
                        problem=str(exc), verified=False)
        try:
            access = self.access()
            return dict(mode=mode, saved_key=saved, key_source=access.key_source, configured=True,
                        problem='', verified=False)
        except WebReadError as exc:
            return dict(mode=mode, saved_key=saved, key_source='unavailable', configured=False,
                        problem=str(exc), verified=False)

    def save(self, mode, *, api_key=None, clear_key=False):
        if mode not in MODES or type(clear_key) is not bool or (api_key is not None and clear_key):
            raise WebReadError('Некорректное изменение настроек поиска.')
        if api_key is not None:
            validate_key(api_key)
            if mode != 'brave':
                raise WebReadError('Новый ключ можно сохранить только для Brave.')
        try:
            old = self._read()
        except WebReadError:
            # Repair needs an explicit replacement/clear, never silently lose a key.
            if api_key is None and not clear_key:
                raise
            old = dict(protected_key='')
        protected = old['protected_key']
        if clear_key:
            protected = ''
        elif api_key is not None:
            protected = base64.b64encode(_crypt(api_key.encode('ascii'))).decode('ascii')
        data = dict(version=1, mode=mode, protected_key=protected)
        temporary = None
        try:
            self.path.parent.mkdir(parents=True, exist_ok=True)
            fd, temporary = tempfile.mkstemp(prefix='.public-search-', suffix='.tmp', dir=self.path.parent)
            with os.fdopen(fd, 'w', encoding='utf-8') as stream:
                json.dump(data, stream)
                stream.flush()
                os.fsync(stream.fileno())
            os.replace(temporary, self.path)
        except OSError:
            raise WebReadError('Не удалось сохранить настройки интернет-поиска. Прежние настройки сохранены.') from None
        finally:
            if temporary is not None:
                Path(temporary).unlink(missing_ok=True)
        return self.status()
