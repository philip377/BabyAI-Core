"""Bounded public HTTPS reads; no cookies, proxy, script execution or local hosts."""
from dataclasses import dataclass
import http.client
import ipaddress
import socket
import ssl
import time
from urllib.parse import urljoin, urlsplit, urlunsplit


class WebReadError(ValueError):
    pass


def public_url(value):
    if (not isinstance(value, str) or len(value) > 2048
            or any(ord(c) < 33 or c in '\\' for c in value)):
        raise WebReadError('Нужна публичная ссылка HTTPS без пробелов.')
    try:
        parts = urlsplit(value)
        if (parts.scheme != 'https' or not parts.hostname or parts.username is not None
                or parts.password is not None or parts.port not in (None, 443)):
            raise ValueError()
        host = parts.hostname.encode('idna').decode('ascii').lower().rstrip('.')
        if ':' in host or '.' not in host or host.endswith(('.local', '.localhost', '.internal')):
            raise ValueError()
        try:
            address = ipaddress.ip_address(host)
        except ValueError:
            address = None
        if address is not None and not address.is_global:
            raise ValueError()
        return urlunsplit(('https', host, parts.path or '/', parts.query, ''))
    except (ValueError, UnicodeError) as exc:
        raise WebReadError('Разрешены только публичные HTTPS-страницы.') from exc


def public_addresses(host):
    addresses = list(dict.fromkeys(info[4][0] for info in
        socket.getaddrinfo(host, 443, type=socket.SOCK_STREAM)))
    if not addresses or any(not ipaddress.ip_address(ip).is_global for ip in addresses):
        raise WebReadError('Адрес сайта ведёт в локальную или служебную сеть.')
    return addresses


@dataclass(frozen=True)
class WebPage:
    url: str
    content_type: str
    body: bytes


def read_public(url, *, limit=1_000_000, types=('text/html',), deadline=None, api_key=None):
    """Resolve every redirect, then pin TLS to a validated IP (DNS cannot rebind).

    A Brave key may only go to its exact API host; that request cannot redirect.
    """
    deadline = deadline or time.monotonic() + 25
    url = public_url(url)
    for _ in range(4):
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise WebReadError('Истекло время чтения сайтов.')
        parts = urlsplit(url)
        if api_key is not None and parts.hostname != 'api.search.brave.com':
            raise WebReadError('Ключ поиска нельзя отправить этому сайту.')
        connection = http.client.HTTPSConnection(parts.hostname, timeout=min(8, remaining))
        raw_socket = None
        try:
            addresses = public_addresses(parts.hostname)
            raw_socket = socket.create_connection((addresses[0], 443), timeout=min(8, remaining))
            connection.sock = ssl.create_default_context().wrap_socket(raw_socket, server_hostname=parts.hostname)
            headers = {'User-Agent': 'BabyAI/0.1 (public product research)',
                       'Accept': ', '.join(types), 'Accept-Encoding': 'identity'}
            if api_key is not None:
                headers['X-Subscription-Token'] = api_key
            connection.request('GET', urlunsplit(('', '', parts.path, parts.query, '')), headers=headers)
            response = connection.getresponse()
            if response.status in (301, 302, 303, 307, 308):
                if api_key is not None:
                    raise WebReadError('Поисковый API перенаправил запрос; ключ не передан дальше.')
                location = response.getheader('Location')
                if not location:
                    raise WebReadError('Некорректное перенаправление сайта.')
                url = public_url(urljoin(url, location))
                continue
            if response.status != 200:
                raise WebReadError(f'Сайт не предоставил данные (HTTP {response.status}).')
            content_type = response.getheader('Content-Type', '').split(';')[0].strip().lower()
            if content_type not in types or response.getheader('Content-Encoding', 'identity') != 'identity':
                raise WebReadError('Сайт вернул неподдерживаемый формат.')
            body = bytearray()
            while len(body) <= limit:
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise WebReadError('Истекло время чтения сайтов.')
                if connection.sock is not None:
                    connection.sock.settimeout(min(8, remaining))
                chunk = response.read1(min(65536, limit + 1 - len(body)))
                if not chunk:
                    return WebPage(url, content_type, bytes(body))
                body.extend(chunk)
            raise WebReadError('Страница или фотография слишком большая.')
        except (OSError, http.client.HTTPException) as exc:
            # Do not include headers, credentials or a remote response in errors.
            raise WebReadError('Не удалось прочитать публичный сайт. Проверь подключение.') from exc
        finally:
            connection.close()
            if raw_socket is not None:
                raw_socket.close()
    raise WebReadError('Слишком много перенаправлений сайта.')
